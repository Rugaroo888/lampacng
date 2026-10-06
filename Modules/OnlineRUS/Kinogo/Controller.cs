using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared;
using Shared.Attributes;
using Shared.Models;
using Shared.Models.Base;
using Shared.Models.Online.Settings;
using Shared.Models.Templates;
using Shared.PlaywrightCore;
using Shared.Services;
using Shared.Services.HTTP;
using Shared.Services.RxEnumerate;
using Shared.Services.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Kinogo;

public class KinogoController : BaseOnlineController
{
    public KinogoController() : base(ModInit.conf) { }

    [HttpGet, Staticache(manually: true)]
    [Route("lite/kinogo")]
    async public Task<ActionResult> Index(string title, string original_title, short year, bool rjson, string href, bool similar, short s = -1, int t = -1)
    {
        if (await IsRequestBlocked(rch: true))
            return badInitMsg;

        #region search
        if (string.IsNullOrEmpty(href))
        {
            if (string.IsNullOrEmpty(title))
                return OnError("search params");

        reset_search:
            var search = await InvokeCacheResult<SearchModel>($"kinogo:search:{title}:{year}", TimeSpan.FromHours(4), async e =>
            {
                SearchModel result = null;

                if (rch?.enable == true)
                {
                    await rch.GetSpan($"{init.host}/search/{title}", html =>
                    {
                        result = SearchResult(html, title, year);
                    });
                }
                else
                {
                    await PlaywrightHttp.GetSpan(init.plugin, $"{init.host}/search/{title}", html =>
                    {
                        result = SearchResult(html, title, year);
                    }, proxy: proxy_data);
                }

                if (result == null)
                    return e.Fail("search-result", refresh_proxy: true);

                return e.Success(result);
            });

            if (similar || string.IsNullOrEmpty(search.Value?.link))
                return ContentTpl(search, () => search.Value.similar);

            if (string.IsNullOrEmpty(search.Value?.link))
            {
                if (IsRhubFallback(search))
                    goto reset_search;

                return OnError();
            }

            href = search.Value?.link;
        }
        #endregion

        if (string.IsNullOrEmpty(href))
            return OnError("href");

        var embed = await GetEmbed(href);
        if (!embed.IsSuccess)
            return OnError(embed.ErrorMsg);

        var cache = await GetPlaylist(href, embed.Value);
        return ContentTpl(cache,
            () => BuildResult(cache.Value, title, original_title, year, s, t, rjson, href)
        );
    }

    async Task<CacheResult<string>> GetEmbed(string href)
    {
        #region embed
    reset_embed:

        var embed = await InvokeCacheResult<string>($"kinogo:{href}", TimeSpan.FromHours(4), async e =>
        {
            string iframeUri = null;
            string targetHref = $"{init.host}/{href}";

            if (rch?.enable == true)
            {
                await rch.GetSpan(init.cors(targetHref), html =>
                {
                    iframeUri = Rx.Match(html, "<iframe [^>]+data-src=\"([^\"]+)\"");
                });
            }
            else
            {
                await PlaywrightHttp.GetSpan(init.plugin, init.cors(targetHref), html =>
                {
                    iframeUri = Rx.Match(html, "<iframe [^>]+data-src=\"([^\"]+)\"");
                }, proxy: proxy_data);
            }

            if (iframeUri == null)
                return e.Fail("iframeUri", refresh_proxy: true);

            string embedUrl = iframeUri.StartsWith("//") ? $"https:{iframeUri}" : iframeUri;

            if (string.IsNullOrEmpty(embedUrl))
                return e.Fail("embedUrl", refresh_proxy: true);

            return e.Success(embedUrl);
        });

        if (IsRhubFallback(embed))
            goto reset_embed;

        return embed;
        #endregion
    }

    async Task<CacheResult<List<PlaylistItem>>> GetPlaylist(string href, string embedUrl)
    {
        #region iframe
    reset_iframe:

        // v2 keeps old cached entries without id/data out of the playback path.
        var cache = await InvokeCacheResult<List<PlaylistItem>>(ipkey($"kinogo:playlist:v2:{embedUrl}"), 20, async e =>
        {
            string fileEncode = null;
            var embedHeaders = httpHeaders(init, HeadersModel.Init("referer", $"{init.host}/{href}"));

            if (rch?.enable == true)
            {
                await rch.GetSpan(init.cors(embedUrl), html =>
                {
                    fileEncode = Rx.Match(html, "\"file\":\"([^\"]+)\"");
                }, embedHeaders);
            }
            else
            {
                await PlaywrightHttp.GetSpan(init.plugin, init.cors(embedUrl), html =>
                {
                    fileEncode = Rx.Match(html, "\"file\":\"([^\"]+)\"");
                }, headers: embedHeaders, proxy_data);
            }

            if (string.IsNullOrEmpty(fileEncode))
                return e.Fail("fileEncode", refresh_proxy: true);

            string playlistJson = await DecodeFile(init, fileEncode);
            if (string.IsNullOrEmpty(playlistJson))
                return e.Fail("playlistJson");

            try
            {
                var playlist = JsonConvert.DeserializeObject<List<PlaylistItem>>(playlistJson);
                if (playlist == null || playlist.Count == 0)
                    return e.Fail("playlist");

                return e.Success(playlist);
            }
            catch
            {
                return e.Fail("DeserializeObject");
            }
        });

        if (IsRhubFallback(cache))
            goto reset_iframe;
        #endregion

        return cache;
    }

    #region Video
    [HttpGet, Staticache(manually: true)]
    [Route("lite/kinogo/video")]
    [Route("lite/kinogo/video.m3u8")]
    async public Task<ActionResult> Video(string href, string id, string title, bool play = false)
    {
        if (await IsRequestBlocked(rch: true))
            return badInitMsg;

        if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(id))
            return OnError("video params");

        var embed = await GetEmbed(href);
        if (!embed.IsSuccess)
            return OnError(embed.ErrorMsg);

        var playlist = await GetPlaylist(href, embed.Value);
        if (!playlist.IsSuccess)
            return OnError(playlist.ErrorMsg);

        var item = FindItem(playlist.Value, id);
        if (string.IsNullOrEmpty(item?.data))
            return OnError("playlist item");

        if (!Uri.TryCreate(embed.Value, UriKind.Absolute, out var embedUri)
            || (embedUri.Scheme != "https" && embedUri.Scheme != "http"))
            return OnError("embed uri");

        string origin = embedUri.GetLeftPart(UriPartial.Authority);
        var streamHeaders = HeadersModel.Init(("referer", embed.Value), ("origin", origin));

        var cache = await InvokeCacheResult<PlaylistItem>(ipkey($"kinogo:video:v2:{embed.Value}:{id}"), 5, async e =>
        {
            // The provider expects the opaque data value as a JSON string.
            var apiHeaders = httpHeaders(init, HeadersModel.Init(("referer", embed.Value), ("origin", origin), ("content-type", "application/json")));
            string apiUrl = init.cors($"{origin}/api/playlist/load", apiHeaders, requestInfo);
            string body = JsonConvert.SerializeObject(item.data);
            JObject response;

            if (rch?.enable == true)
                response = await rch.Post<JObject>(apiUrl, body, apiHeaders);
            else
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                response = await Http.Post<JObject>(apiUrl, content,
                    timeoutSeconds: init.httptimeout,
                    headers: apiHeaders.Where(h => !h.name.Equals("content-type", StringComparison.OrdinalIgnoreCase)).ToList(),
                    proxy: init.useproxy ? proxy : null, httpversion: init.httpversion);
            }

            if (response == null || response.Value<bool?>("success") == false)
                return e.Fail("playlist-load");

            var payload = response["file"] != null ? response : response["data"] as JObject;
            if (payload == null || payload["file"]?.Type != JTokenType.String)
                return e.Fail("playlist-load-file");

            var media = payload.ToObject<PlaylistItem>();
            if (!IsMediaFile(media?.file))
                return e.Fail("playlist-load-file");

            return e.Success(media);
        });

        if (!cache.IsSuccess)
            return OnError(cache.ErrorMsg);

        var streams = new StreamQualityTpl();
        string file = NormalizeFile(cache.Value.file);
        foreach (Match match in Regex.Matches(file, "\\[([^\\]]+)\\]((?:https?:)?//[^,\\[\\s]+)"))
            streams.Append(HostStreamProxy(NormalizeFile(match.Groups[2].Value), headers: streamHeaders), match.Groups[1].Value);

        if (streams.IsEmpty)
            streams.Append(HostStreamProxy(file, headers: streamHeaders), "auto");

        string stream = streams.Firts().link;
        if (play)
            return RedirectToPlay(stream);

        return ContentTo(VideoTpl.ToJson(
            "play", stream, title ?? item.title,
            streamquality: streams,
            subtitles: BuildSubtitles(cache.Value.subtitle ?? item.subtitle, streamHeaders),
            vast: init.vast,
            headers: stream.Contains("/proxy/") ? null : streamHeaders,
            httpContext: HttpContext
        ));
    }

    static PlaylistItem FindItem(List<PlaylistItem> playlist, string id)
    {
        if (playlist == null)
            return null;

        foreach (var item in playlist)
        {
            if (item.id == id && item.folder == null)
                return item;

            var found = FindItem(item.folder, id);
            if (found != null)
                return found;
        }

        return null;
    }

    static string NormalizeFile(string file)
        => file?.StartsWith("//") == true ? "https:" + file : file;

    static bool IsMediaFile(string file)
        => !string.IsNullOrWhiteSpace(file) && (file.StartsWith("https://") || file.StartsWith("http://")
            || file.StartsWith("//") || Regex.IsMatch(file, "^\\[[^\\]]+\\](?:https?:)?//"));

    static bool NeedsLoad(PlaylistItem item)
        => !string.IsNullOrEmpty(item?.id) && !string.IsNullOrEmpty(item.data);

    string VideoLink(PlaylistItem item, string href, string title)
        => $"{host}/lite/kinogo/video?href={HttpUtility.UrlEncode(href)}&id={HttpUtility.UrlEncode(item.id)}&title={HttpUtility.UrlEncode(title)}";

    SubtitleTpl BuildSubtitles(string subtitle, IReadOnlyList<HeadersModel> headers = null)
    {
        var subtitles = new SubtitleTpl();
        if (!string.IsNullOrEmpty(subtitle))
        {
            foreach (Match match in Regex.Matches(subtitle, "\\[([^\\]]+)\\]([^\\[\\,]+)"))
                subtitles.Append(match.Groups[1].Value, HostStreamProxy(NormalizeFile(match.Groups[2].Value), headers: headers));
        }
        return subtitles;
    }
    #endregion

    #region BuildResult
    ITplResult BuildResult(List<PlaylistItem> playlist, string title, string original_title, short year, short s, int t, bool rjson, string href)
    {
        if (playlist.FirstOrDefault()?.folder == null)
        {
            var mtpl = new MovieTpl(title, original_title);

            foreach (var source in playlist)
            {
                string voice = source.title;
                string file = source.file;

                if (string.IsNullOrEmpty(voice) || (!NeedsLoad(source) && !IsMediaFile(file)))
                    continue;

                if (NeedsLoad(source))
                {
                    string link = VideoLink(source, href, $"{title ?? original_title} ({Regex.Replace(voice, "<[^>]+>", "")})");
                    mtpl.Append(
                        Regex.Replace(voice, "<[^>]+>", ""), link, "call",
                        accsArgs($"{link.Replace("/video?", "/video.m3u8?")}&play=true"),
                        vast: init.vast
                    );
                    continue;
                }

                if (file.StartsWith("//"))
                    file = "https:" + file;

                #region subtitle
                var subtitles = new SubtitleTpl();
                string _subs = source.subtitle;
                if (!string.IsNullOrEmpty(_subs))
                {
                    var match = new Regex("\\[([^\\]]+)\\]([^\\[\\,]+)").Match(_subs);
                    while (match.Success)
                    {
                        string srt = match.Groups[2].Value;
                        if (srt.StartsWith("//"))
                            srt = "https:" + srt;

                        subtitles.Append(match.Groups[1].Value, HostStreamProxy(srt));
                        match = match.NextMatch();
                    }
                }
                #endregion

                mtpl.Append(
                    Regex.Replace(voice, "<[^>]+>", ""),
                    HostStreamProxy(file),
                    subtitles: subtitles,
                    vast: init.vast
                );
            }

            return mtpl;
        }
        else
        {
            string enc_title = HttpUtility.UrlEncode(title);
            string enc_original_title = HttpUtility.UrlEncode(original_title);
            string enc_href = HttpUtility.UrlEncode(href);

            if (s == -1)
            {
                var tpl = new SeasonTpl(playlist.Count);
                foreach (var season in playlist)
                {
                    string _s = Regex.Match(season.title ?? string.Empty, " ([0-9]+)$").Groups[1].Value;
                    if (!string.IsNullOrEmpty(_s))
                    {
                        tpl.Append(
                            $"{_s} сезон",
                            $"{host}/lite/kinogo?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&href={enc_href}&s={_s}",
                            _s
                        );
                    }
                }

                return tpl;
            }
            else
            {
                var episodes = playlist.FirstOrDefault(i => (i.title ?? string.Empty).EndsWith($" {s}"))?.folder;
                if (episodes == null)
                    return new EpisodeTpl();

                #region Перевод
                var vtpl = new VoiceTpl();
                var hashSet = new HashSet<int>();

                foreach (var episode in episodes)
                {
                    if (episode.folder == null)
                        continue;

                    foreach (var voice in episode.folder)
                    {
                        int voice_id = voice.voice_id;
                        if (hashSet.Add(voice_id))
                        {
                            if (t == -1)
                                t = voice_id;

                            vtpl.Append(
                                voice.title,
                                t == voice_id,
                                $"{host}/lite/kinogo?rjson={rjson}&title={enc_title}&original_title={enc_original_title}&year={year}&href={enc_href}&s={s}&t={voice_id}"
                            );
                        }
                    }
                }
                #endregion

                var etpl = new EpisodeTpl(vtpl, episodes.Count);

                foreach (var episode in episodes)
                {
                    string name = episode.title;
                    var source = episode.folder?.FirstOrDefault(i => i.voice_id == t);
                    string file = source?.file;

                    if (!NeedsLoad(source) && !IsMediaFile(file))
                        continue;

                    if (NeedsLoad(source))
                    {
                        string link = VideoLink(source, href, $"{title ?? original_title} ({name}, {source.title})");
                        etpl.Append(
                            name, title ?? original_title, s,
                            Regex.Match(name, " ([0-9]+)$").Groups[1].Value,
                            link, "call",
                            streamlink: accsArgs($"{link.Replace("/video?", "/video.m3u8?")}&play=true"),
                            vast: init.vast
                        );
                        continue;
                    }

                    if (file.StartsWith("//"))
                        file = "https:" + file;

                    #region subtitle
                    var subtitles = new SubtitleTpl();
                    string _subs = episode.subtitle;

                    if (!string.IsNullOrEmpty(_subs))
                    {
                        var match = new Regex("\\[([^\\]]+)\\]([^\\[\\,]+)").Match(_subs);
                        while (match.Success)
                        {
                            string srt = match.Groups[2].Value;
                            if (srt.StartsWith("//"))
                                srt = "https:" + srt;

                            subtitles.Append(match.Groups[1].Value, HostStreamProxy(srt));
                            match = match.NextMatch();
                        }
                    }
                    #endregion

                    etpl.Append(
                        name,
                        title ?? original_title,
                        s,
                        Regex.Match(name, " ([0-9]+)$").Groups[1].Value,
                        HostStreamProxy(file),
                        subtitles: subtitles,
                        vast: init.vast
                    );
                }

                return etpl;
            }
        }
    }
    #endregion

    #region SearchResult
    SearchModel SearchResult(ReadOnlySpan<char> html, string title, int year)
    {
        if (html.IsEmpty)
            return null;

        var rx = Rx.Matches("<div id=\"[0-9]+\" class=\"shortstory\">(.*?)<div class=\"shortstory__meta\">", html, 0, RegexOptions.Singleline);
        if (rx.Count == 0)
            return null;

        string link = null;
        string stitle = SearchNameTo.Convert(title);

        var similar = new SimilarTpl(rx.Count);

        foreach (var row in rx.Rows())
        {
            string href = row.Match("<a href=\"https?://[^/]+/([^\"#]+)");
            if (string.IsNullOrEmpty(href))
                continue;

            string name = row.Match("<h2>([^<]+)</h2>");
            if (string.IsNullOrEmpty(name))
                continue;

            string blockYear = row.Match("Год выпуска:</b><a[^>]*>([0-9]{4})");

            string img = row.Match("<img\\s+data-src=\"([^\"]+)\"");
            if (!string.IsNullOrEmpty(img))
                img = ModInit.conf.host + img;

            string uri = $"{host}/lite/kinogo?href={HttpUtility.UrlEncode(href)}";
            similar.Append(name, blockYear, string.Empty, uri, PosterApi.Size(img));

            if (SearchNameTo.Contains(name, stitle) && blockYear == year.ToString())
                link = href;
        }

        if (string.IsNullOrEmpty(link) && similar.IsEmpty)
            return null;

        return new SearchModel()
        {
            link = link,
            similar = similar
        };
    }
    #endregion

    #region DecodeFile
    async Task<string> DecodeFile(OnlinesSettings init, string fileEncode)
    {
        try
        {
            using (var browser = new PlaywrightBrowser())
            {
                var page = await browser.NewPageAsync(init.plugin).ConfigureAwait(false);
                if (page == null)
                    return null;

                await page.AddScriptTagAsync(new()
                {
                    Content = ModInit.playerjs
                });

                return await page.EvaluateAsync<string>("(input) => decodePlayerjsFile(input)", fileEncode);
            }
        }
        catch { return null; }
    }
    #endregion
}
