using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Volumify;

/// <summary>One lyric line. <see cref="TimeMs"/> is -1 for unsynced (plain) lyrics.</summary>
public readonly record struct LyricLine(long TimeMs, string Text)
{
    public bool Synced => TimeMs >= 0;
}

public sealed record LyricsResult(IReadOnlyList<LyricLine> Lines, bool Synced, bool Instrumental, bool Found, string Source)
{
    public static readonly LyricsResult None = new(Array.Empty<LyricLine>(), false, false, false, "");
    public static LyricsResult Inst(string src) => new(Array.Empty<LyricLine>(), false, true, true, src);
}

/// <summary>
/// Fetches lyrics from free third-party sources — never Spotify's API, never patching Spotify.
///   1. LRCLIB (synced LRC; what gives the Spotify-style line sync)
///   2. Genius (plain text; covers songs Spotify/LRCLIB lack — e.g. brand-new releases)
/// Results are cached per track for the session.
/// </summary>
public static class LyricsProvider
{
    private const string AppUa = "Volumify/0.3 (+https://github.com/mangomandu/volumify)";
    private const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(8),
    })
    { Timeout = TimeSpan.FromSeconds(14) };

    private static readonly object _cacheGate = new();
    private static readonly Dictionary<string, LyricsResult> _cache = new();

    public static async Task<LyricsResult> GetAsync(NowPlaying.TrackInfo track, CancellationToken ct)
    {
        if (track.IsEmpty) return LyricsResult.None;
        string key = track.Key;
        lock (_cacheGate) if (_cache.TryGetValue(key, out var hit)) return hit;

        LyricsResult result = LyricsResult.None;
        try { result = await FetchAsync(track, ct); }
        catch (OperationCanceledException) { return LyricsResult.None; } // don't cache a cancellation
        catch { result = LyricsResult.None; }

        lock (_cacheGate)
        {
            if (_cache.Count > 120) _cache.Clear();
            _cache[key] = result;
        }
        return result;
    }

    private static async Task<LyricsResult> FetchAsync(NowPlaying.TrackInfo t, CancellationToken ct)
    {
        var lrclib = await TryLrclibAsync(t, ct);
        if (lrclib.Found) return lrclib;

        var genius = await TryGeniusAsync(t, ct);
        if (genius.Found) return genius;

        return LyricsResult.None;
    }

    // ---------- LRCLIB (synced) ----------
    private static async Task<LyricsResult> TryLrclibAsync(NowPlaying.TrackInfo t, CancellationToken ct)
    {
        // exact match first (artist + track + album + duration)
        var get = $"https://lrclib.net/api/get?artist_name={Enc(t.Artist)}&track_name={Enc(t.Title)}"
                + $"&album_name={Enc(t.Album)}&duration={t.DurationMs / 1000}";
        var json = await GetStringAsync(get, AppUa, ct, allow404: true);
        if (json != null)
        {
            var r = ParseLrclibObject(json, "lrclib");
            if (r.Found) return r;
        }

        // fuzzy search fallback
        var search = $"https://lrclib.net/api/search?q={Enc(t.Artist + " " + t.Title)}";
        var arr = await GetStringAsync(search, AppUa, ct, allow404: true);
        if (arr == null) return LyricsResult.None;
        try
        {
            using var doc = JsonDocument.Parse(arr);
            JsonElement? bestSynced = null, bestPlain = null;
            int wantSec = (int)(t.DurationMs / 1000);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                bool synced = e.TryGetProperty("syncedLyrics", out var sl) && sl.ValueKind == JsonValueKind.String && sl.GetString()!.Length > 0;
                bool plain = e.TryGetProperty("plainLyrics", out var pl) && pl.ValueKind == JsonValueKind.String && pl.GetString()!.Length > 0;
                bool durOk = wantSec <= 0 || !e.TryGetProperty("duration", out var d) || d.ValueKind != JsonValueKind.Number || Math.Abs(d.GetDouble() - wantSec) <= 8;
                if (synced && durOk && bestSynced == null) bestSynced = e;
                if (plain && durOk && bestPlain == null) bestPlain = e;
            }
            var chosen = bestSynced ?? bestPlain;
            if (chosen != null) return ParseLrclibElement(chosen.Value, "lrclib");
        }
        catch { }
        return LyricsResult.None;
    }

    private static LyricsResult ParseLrclibObject(string json, string src)
    {
        try { using var doc = JsonDocument.Parse(json); return ParseLrclibElement(doc.RootElement, src); }
        catch { return LyricsResult.None; }
    }

    private static LyricsResult ParseLrclibElement(JsonElement e, string src)
    {
        if (e.TryGetProperty("instrumental", out var inst) && inst.ValueKind == JsonValueKind.True)
            return LyricsResult.Inst(src);

        if (e.TryGetProperty("syncedLyrics", out var sl) && sl.ValueKind == JsonValueKind.String && sl.GetString() is { Length: > 0 } lrc)
        {
            var lines = ParseLrc(lrc);
            if (lines.Count > 0) return new LyricsResult(lines, true, false, true, src);
        }
        if (e.TryGetProperty("plainLyrics", out var pl) && pl.ValueKind == JsonValueKind.String && pl.GetString() is { Length: > 0 } plain)
        {
            var lines = ParsePlain(plain);
            if (lines.Count > 0) return new LyricsResult(lines, false, false, true, src);
        }
        return LyricsResult.None;
    }

    // ---------- Genius (plain, for the long tail) ----------
    private static async Task<LyricsResult> TryGeniusAsync(NowPlaying.TrackInfo t, CancellationToken ct)
    {
        var search = $"https://genius.com/api/search/multi?q={Enc(t.Artist + " " + t.Title)}";
        var json = await GetStringAsync(search, BrowserUa, ct);
        if (json == null) return LyricsResult.None;

        string? url = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var sec in doc.RootElement.GetProperty("response").GetProperty("sections").EnumerateArray())
            {
                if (!sec.TryGetProperty("hits", out var hits)) continue;
                foreach (var h in hits.EnumerateArray())
                {
                    if (h.TryGetProperty("type", out var ty) && ty.GetString() == "song"
                        && h.GetProperty("result").TryGetProperty("url", out var u))
                    { url = u.GetString(); break; }
                }
                if (url != null) break;
            }
        }
        catch { }
        if (url == null) return LyricsResult.None;

        var html = await GetStringAsync(url, BrowserUa, ct);
        if (html == null) return LyricsResult.None;
        var lines = ExtractGeniusLyrics(html);
        return lines.Count > 0 ? new LyricsResult(lines, false, false, true, "genius") : LyricsResult.None;
    }

    private static List<LyricLine> ExtractGeniusLyrics(string html)
    {
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(html, "<div[^>]*data-lyrics-container=\"true\"[^>]*>"))
        {
            int i = m.Index + m.Length, depth = 1;
            foreach (Match t in Regex.Matches(html.Substring(i), "<(/?)div"))
            {
                depth += t.Groups[1].Value == "/" ? -1 : 1;
                if (depth == 0) { sb.Append(html.AsSpan(i, t.Index)); sb.Append('\n'); break; }
            }
        }
        string text = Regex.Replace(sb.ToString(), "<br\\s*/?>", "\n");
        text = Regex.Replace(text, "<[^>]+>", "");
        text = WebUtility.HtmlDecode(text);

        var outLines = new List<LyricLine>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (IsGeniusJunk(line)) continue;
            outLines.Add(new LyricLine(-1, line));
        }
        return outLines;
    }

    private static bool IsGeniusJunk(string line) =>
        line.Contains("Contributors") || line.Contains("Translations") || line.Contains("Romanization")
        || line.Contains("You might also like") || line.EndsWith("Embed") || line.EndsWith(" Lyrics");

    // ---------- parsing helpers ----------
    private static List<LyricLine> ParseLrc(string lrc)
    {
        var list = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int i = 0; var times = new List<long>();
            while (i < line.Length && line[i] == '[')
            {
                int close = line.IndexOf(']', i);
                if (close < 0) break;
                if (TryParseLrcTime(line.Substring(i + 1, close - i - 1), out long ms)) { times.Add(ms); i = close + 1; }
                else break; // metadata tag like [ar:...] → not a timestamp
            }
            if (times.Count == 0) continue;
            string txt = line[i..].Trim();
            foreach (var ms in times) list.Add(new LyricLine(ms, txt));
        }
        list.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return list;
    }

    private static bool TryParseLrcTime(string tag, out long ms)
    {
        ms = 0;
        var m = Regex.Match(tag, @"^(\d+):(\d{1,2})(?:[.:](\d{1,3}))?$");
        if (!m.Success) return false;
        long min = long.Parse(m.Groups[1].Value), sec = long.Parse(m.Groups[2].Value);
        long frac = m.Groups[3].Success ? long.Parse(m.Groups[3].Value.PadRight(3, '0')) : 0;
        ms = (min * 60 + sec) * 1000 + frac;
        return true;
    }

    private static List<LyricLine> ParsePlain(string text)
    {
        var list = new List<LyricLine>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
            list.Add(new LyricLine(-1, raw.Trim()));
        // trim leading/trailing blank lines
        while (list.Count > 0 && list[0].Text.Length == 0) list.RemoveAt(0);
        while (list.Count > 0 && list[^1].Text.Length == 0) list.RemoveAt(list.Count - 1);
        return list;
    }

    private static string Enc(string s) => Uri.EscapeDataString(s ?? "");

    private static async Task<string?> GetStringAsync(string url, string ua, CancellationToken ct, bool allow404 = false)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", ua);
            req.Headers.TryAddWithoutValidation("Accept-Language", "ko,en;q=0.8");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound && allow404) return null;
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
