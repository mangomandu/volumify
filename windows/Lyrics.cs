using System.IO;
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
/// The synced sources race in parallel and the first synced hit wins; plain text is only a fallback
/// (so we never "google" first).
///   1. Musixmatch — line-synced. This is the SAME database Spotify licenses for its own lyrics, so we
///                   get exactly what Spotify shows (identical timing) without touching Spotify or its token.
///   2. LRCLIB     — line-synced, community-sourced; covers some songs Musixmatch lacks.
///   3. Genius     — plain text only, last resort for the long tail (songs nobody has timed —
///                   the same reason Spotify itself shows no lyrics for them).
/// Results are cached per track for the session.
/// </summary>
public static class LyricsProvider
{
    private const string AppUa = "Volumify/0.3 (+https://github.com/mangomandu/volumify)";
    private const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly object _cacheGate = new();
    private static readonly Dictionary<string, LyricsResult> _cache = new();
    private static readonly string DiskCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Volumify", "lyrics");

    // Musixmatch needs a "user token". token.get is heavily rate-limited (a few mints trip a captcha),
    // so we mint ONE and reuse it — persisted across launches via InitToken. null = unknown,
    // "" = a definitive failure (captcha/limit) so we stop hammering, else the token.
    private static readonly SemaphoreSlim _tokenGate = new(1, 1);
    private static volatile string? _mxmToken;
    private static long _tokenRetryAfter;   // TickCount64 before which we won't re-mint after a failed token.get (anti-hammer)
    private static Action<string>? _onTokenMinted;
    private static int _warmed;

    /// <summary>Seed a Musixmatch token saved from a previous run, plus a sink that persists a freshly minted one.</summary>
    public static void InitToken(string? saved, Action<string> onMinted)
    {
        if (!string.IsNullOrEmpty(saved)) _mxmToken = saved;
        _onTokenMinted = onMinted;
    }

    /// <summary>Pre-mint the Musixmatch token so the first lookup isn't slowed by it. Call when lyrics turn on.</summary>
    public static void WarmUp()
    {
        if (Interlocked.Exchange(ref _warmed, 1) != 0) return;
        _ = Task.Run(async () => { try { await GetMxmTokenAsync(CancellationToken.None); } catch { } });
    }

    public static async Task<LyricsResult> GetAsync(NowPlaying.TrackInfo track, CancellationToken ct)
    {
        if (track.IsEmpty) return LyricsResult.None;
        string key = track.Key;
        lock (_cacheGate) if (_cache.TryGetValue(key, out var hit)) return hit;

        // disk cache — a song we've fetched before is instant on replay or after a restart.
        // Only successful results are persisted; misses stay in-memory so they're retried next session.
        // Read off the UI thread (%APPDATA% can be roaming / AV-scanned, so the read can stall).
        var disk = await Task.Run(() => ReadDisk(key));
        if (disk is { Found: true }) { lock (_cacheGate) _cache[key] = disk; return disk; }

        LyricsResult result = LyricsResult.None;
        try { result = await FetchAsync(track, ct); }
        catch (OperationCanceledException) { return LyricsResult.None; } // don't cache a cancellation
        catch { result = LyricsResult.None; }

        lock (_cacheGate)
        {
            if (_cache.Count > 120) _cache.Clear();
            _cache[key] = result;
        }
        if (result.Found) { var r = result; var k = key; _ = Task.Run(() => WriteDisk(k, r)); } // persist off the UI thread
        return result;
    }

    // ---------- persistent (disk) cache ----------
    private static LyricsResult? ReadDisk(string key)
    {
        try
        {
            var path = Path.Combine(DiskCacheDir, KeyHash(key) + ".json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<LyricsResult>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static void WriteDisk(string key, LyricsResult r)
    {
        try
        {
            Directory.CreateDirectory(DiskCacheDir);
            var path = Path.Combine(DiskCacheDir, KeyHash(key) + ".json");
            var tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp"; // unique tmp so concurrent writes don't collide
            File.WriteAllText(tmp, JsonSerializer.Serialize(r));
            File.Move(tmp, path, overwrite: true); // atomic replace so a crash can't leave a truncated cache file
        }
        catch { }
    }

    private static string KeyHash(string s)
    {
        ulong h = 14695981039346656037UL;            // FNV-1a 64-bit → stable filename across runs (no GetHashCode)
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h.ToString("x16");
    }

    private static async Task<LyricsResult> FetchAsync(NowPlaying.TrackInfo t, CancellationToken ct)
    {
        // Musixmatch is fast (~1s) and authoritative — it's the database Spotify itself uses. Try it first;
        // if it has synced lyrics we're done. (Measured: LRCLIB is currently 6–21s and often errors, so we
        // must never block the fallback on it.)
        var mxm = await Safe(TryMusixmatchAsync(t, ct));
        if (mxm.Synced) return mxm;

        // No synced from Musixmatch → run Genius (plain, ~0.6s) and a hard-capped LRCLIB (synced, slow)
        // together. Use the fast plain the moment it's ready; only wait on LRCLIB when the fast one finds nothing.
        var lrc = CappedLrclibAsync(t, ct, 2500);
        var gen = Safe(TryGeniusAsync(t, ct));

        var firstDone = await Task.WhenAny(lrc, gen);
        var first = await firstDone;
        if (first.Synced) return first;     // a fast LRCLIB synced actually beat Genius
        if (first.Found) return first;      // Genius (or LRCLIB) plain is ready → show it now, don't wait on the slow one

        // The fast source found nothing → fall back to the other (this is when LRCLIB earns its keep).
        var other = await (ReferenceEquals(firstDone, lrc) ? gen : lrc);
        if (other.Found) return other;
        if (mxm.Found) return mxm;          // Musixmatch plain / instrumental marker
        return LyricsResult.None;
    }

    // LRCLIB with a hard cap — it's a useful synced source but currently very slow, so it must never stall us.
    private static async Task<LyricsResult> CappedLrclibAsync(NowPlaying.TrackInfo t, CancellationToken ct, int capMs)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(capMs);
        try { return await TryLrclibAsync(t, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return LyricsResult.None; } // our cap, not the caller's
        catch { return LyricsResult.None; }
    }

    private static async Task<LyricsResult> Safe(Task<LyricsResult> task)
    {
        try { return await task; }
        catch (OperationCanceledException) { throw; }
        catch { return LyricsResult.None; }
    }

    // ---------- Musixmatch (synced; the same database Spotify licenses) ----------
    private const string MxmBase = "https://apic-desktop.musixmatch.com/ws/1.1/";
    private const string MxmApp = "web-desktop-app-v1.0";

    private static async Task<LyricsResult> TryMusixmatchAsync(NowPlaying.TrackInfo t, CancellationToken ct)
    {
        string? token = await GetMxmTokenAsync(ct);
        if (string.IsNullOrEmpty(token)) return LyricsResult.None;

        long durSec = t.DurationMs / 1000;
        var url = MxmBase + "macro.subtitles.get?format=json&namespace=lyrics_richsynched&subtitle_format=mxm"
                + "&app_id=" + MxmApp
                + "&q_track=" + Enc(t.Title)
                + "&q_artist=" + Enc(t.Artist)
                + "&q_album=" + Enc(t.Album)
                + (durSec > 0 ? "&q_duration=" + durSec + "&f_subtitle_length=" + durSec : "")
                + "&usertoken=" + Enc(token);
        var json = await MxmGetAsync(url, ct);
        if (json == null) return LyricsResult.None;

        var (result, authFailed) = ParseMusixmatch(json);
        if (authFailed && _mxmToken == token) _mxmToken = null; // invalidate only the token THIS request used → re-mint next lookup
        return result;
    }

    private static async Task<string?> GetMxmTokenAsync(CancellationToken ct)
    {
        var cached = _mxmToken;
        if (cached != null) return cached.Length == 0 ? null : cached;

        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_mxmToken != null) return _mxmToken.Length == 0 ? null : _mxmToken;
            if (Environment.TickCount64 < _tokenRetryAfter) return null; // backing off after a recent failure

            var json = await MxmGetAsync(MxmBase + "token.get?app_id=" + MxmApp + "&format=json", ct);
            // No response (network down / 401 / 429 / 5xx): back off 60s so queued + later callers don't hammer the
            // rate-limited endpoint. Leave _mxmToken null so it can recover once the cooldown passes.
            if (json == null) { _tokenRetryAfter = Environment.TickCount64 + 60_000; return null; }

            string? token = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var msg = doc.RootElement.GetProperty("message");
                int status = msg.GetProperty("header").GetProperty("status_code").GetInt32();
                if (status == 200 && msg.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object
                    && body.TryGetProperty("user_token", out var ut) && ut.ValueKind == JsonValueKind.String)
                {
                    var s = ut.GetString();
                    if (!string.IsNullOrEmpty(s) && s.Length > 20) token = s; // real tokens are ~54 chars
                }
            }
            catch { }

            _mxmToken = token ?? "";                                       // "" = definitive failure → stop hammering
            if (token != null) { try { _onTokenMinted?.Invoke(token); } catch { } }
            return token;
        }
        finally { _tokenGate.Release(); }
    }

    /// <summary>Parse macro.subtitles.get. Returns (result, authFailed); authFailed flags a stale token to re-mint.</summary>
    private static (LyricsResult result, bool authFailed) ParseMusixmatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var msg = doc.RootElement.GetProperty("message");
            int top = msg.GetProperty("header").GetProperty("status_code").GetInt32();
            if (top == 401 || top == 403) return (LyricsResult.None, true);
            if (!msg.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object) return (LyricsResult.None, false);
            if (!body.TryGetProperty("macro_calls", out var macro) || macro.ValueKind != JsonValueKind.Object) return (LyricsResult.None, false);

            // track metadata — instrumental tracks have no lyrics by definition
            if (macro.TryGetProperty("matcher.track.get", out var matcher)
                && TryBody(matcher, out var mBody)
                && mBody.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object
                && track.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.Number && ins.GetInt32() == 1)
                return (LyricsResult.Inst("musixmatch"), false);

            // synced subtitles → subtitle_list[0].subtitle.subtitle_body is itself a JSON array string
            if (macro.TryGetProperty("track.subtitles.get", out var subs)
                && TryBody(subs, out var sBody)
                && sBody.TryGetProperty("subtitle_list", out var slist) && slist.ValueKind == JsonValueKind.Array && slist.GetArrayLength() > 0
                && slist[0].TryGetProperty("subtitle", out var subtitle)
                && subtitle.TryGetProperty("subtitle_body", out var sb) && sb.ValueKind == JsonValueKind.String)
            {
                var lines = ParseMxmSubtitle(sb.GetString()!);
                if (lines.Count > 0) return (new LyricsResult(lines, true, false, true, "musixmatch"), false);
            }

            // plain lyrics fallback
            if (macro.TryGetProperty("track.lyrics.get", out var lyr)
                && TryBody(lyr, out var lBody)
                && lBody.TryGetProperty("lyrics", out var lyrics) && lyrics.ValueKind == JsonValueKind.Object)
            {
                bool restricted = lyrics.TryGetProperty("restricted", out var rr) && rr.ValueKind == JsonValueKind.Number && rr.GetInt32() == 1;
                if (!restricted && lyrics.TryGetProperty("lyrics_body", out var lb) && lb.ValueKind == JsonValueKind.String
                    && lb.GetString() is { Length: > 0 } plain)
                {
                    var lines = ParsePlain(StripMxmFooter(plain));
                    if (lines.Count > 0) return (new LyricsResult(lines, false, false, true, "musixmatch"), false);
                }
            }
        }
        catch { }
        return (LyricsResult.None, false);
    }

    // macro_calls[name].message.body as a JSON object; false when absent (body stays default and is never read,
    // avoiding InvalidOperationException from reading .ValueKind on a default JsonElement).
    private static bool TryBody(JsonElement call, out JsonElement body)
    {
        body = default;
        return call.TryGetProperty("message", out var m)
            && m.TryGetProperty("body", out body)
            && body.ValueKind == JsonValueKind.Object;
    }

    private static List<LyricLine> ParseMxmSubtitle(string body)
    {
        var list = new List<LyricLine>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string text = e.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() ?? "" : "";
                long ms = 0;
                if (e.TryGetProperty("time", out var tm) && tm.TryGetProperty("total", out var tot) && tot.ValueKind == JsonValueKind.Number)
                    ms = (long)Math.Round(tot.GetDouble() * 1000);
                list.Add(new LyricLine(ms, text.Trim()));
            }
        }
        catch { }
        list.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
        return list;
    }

    private static string StripMxmFooter(string s)
    {
        int i = s.IndexOf("***", StringComparison.Ordinal); // "******* This Lyrics is NOT for Commercial use *******"
        return i > 0 ? s[..i] : s;
    }

    private static async Task<string?> MxmGetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
            req.Headers.TryAddWithoutValidation("Cookie", "x-mxm-token-guid=");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
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
        || line.Contains("You might also like") || line.EndsWith("Embed") || line.EndsWith(" Lyrics")
        || IsSectionHeader(line);

    // [Verse], [Chorus], [Pre-Chorus 1: …], [Bridge] — structural markers, not lyrics
    private static bool IsSectionHeader(string line) =>
        line.Length > 1 && line[0] == '[' && line[^1] == ']';

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
        {
            var line = raw.Trim();
            if (IsSectionHeader(line)) continue; // drop [Verse]/[Chorus]/… structural markers
            list.Add(new LyricLine(-1, line));
        }
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
