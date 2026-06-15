using System.Globalization;
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
    // The "v2" segment is a cache version — bump it whenever match/parse logic changes so stale results
    // (e.g. a wrong song cached before match validation existed) are ignored instead of served forever.
    private static readonly string DiskCacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Volumify", "lyrics", "v3");

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
        // Obvious instrumentals (the title says so) → don't waste any lookups, just say "instrumental".
        if (LooksInstrumental(t.Title) || LooksInstrumental(t.Album)) return LyricsResult.Inst("title");

        // Musixmatch is fast (~1s) and authoritative — it's the database Spotify itself uses. Try it first;
        // synced → done, and it also flags instrumentals (so many piano/score tracks resolve here, no search).
        var mxm = await Safe(TryMusixmatchAsync(t, ct));
        if (mxm.Synced || mxm.Instrumental) return mxm;

        // No synced yet → race the Korean synced services (Bugs + Genie license lyrics directly, so they cover
        // the K-pop / Korean long tail Musixmatch misses; fast), Genius (fast, plain) and a hard-capped LRCLIB
        // (synced but currently very slow). First synced wins; else take a plain once both Korean sources have
        // settled — never wait on slow LRCLIB.
        var bugs = Safe(TryBugsAsync(t, ct));
        var genie = Safe(TryGenieAsync(t, ct));
        var gen = Safe(TryGeniusAsync(t, ct));
        var lrc = CappedLrclibAsync(t, ct, 2000);

        LyricsResult plain = (mxm.Found && !mxm.Instrumental) ? mxm : LyricsResult.None;
        int koreanLeft = 2; // Bugs + Genie — the fast synced sources we prefer over plain
        var pending = new List<Task<LyricsResult>> { bugs, genie, gen, lrc };
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            var r = await done;
            if (r.Synced) return r;                                           // Bugs/Genie (or a fast LRCLIB) synced wins
            if (ReferenceEquals(done, bugs) || ReferenceEquals(done, genie)) koreanLeft--;
            if (r.Found && !r.Instrumental && !plain.Found) plain = r;
            if (koreanLeft == 0 && plain.Found) return plain;                 // both Korean sources done, no synced → use plain
        }
        return plain.Found ? plain : LyricsResult.None;
    }

    private static bool LooksInstrumental(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        string n = s.ToLowerInvariant();
        return n.Contains("instrumental") || n.Contains("(inst.)") || n.Contains("(inst)") || n.Contains("[inst]")
            || n.Contains("karaoke") || n.Contains("off vocal") || n.Contains("backing track")
            || n.Contains("연주곡") || n.Contains("반주");
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

        var (result, authFailed) = ParseMusixmatch(json, t);
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
    private static (LyricsResult result, bool authFailed) ParseMusixmatch(string json, NowPlaying.TrackInfo t)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var msg = doc.RootElement.GetProperty("message");
            int top = msg.GetProperty("header").GetProperty("status_code").GetInt32();
            if (top == 401 || top == 403) return (LyricsResult.None, true);
            if (!msg.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object) return (LyricsResult.None, false);
            if (!body.TryGetProperty("macro_calls", out var macro) || macro.ValueKind != JsonValueKind.Object) return (LyricsResult.None, false);

            // Validate the fuzzy match — Musixmatch will happily return a *different* popular song for an
            // obscure query. Require the matched track's duration (or, failing that, its title) to line up.
            if (macro.TryGetProperty("matcher.track.get", out var matcher)
                && TryBody(matcher, out var mBody)
                && mBody.TryGetProperty("track", out var track) && track.ValueKind == JsonValueKind.Object)
            {
                if (!MxmTrackMatches(track, t)) return (LyricsResult.None, false); // wrong song → don't show its lyrics
                if (track.TryGetProperty("instrumental", out var ins) && ins.ValueKind == JsonValueKind.Number && ins.GetInt32() == 1)
                    return (LyricsResult.Inst("musixmatch"), false);
            }

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

    // ---------- Bugs (synced; Korean services license lyrics directly, so they cover the K-pop / Korean
    //            long tail Musixmatch misses — e.g. brand-new B-sides. Fast: search ~0.5s, lyrics ~instant) ----------
    private static async Task<LyricsResult> TryBugsAsync(NowPlaying.TrackInfo t, CancellationToken ct)
    {
        var html = await GetStringAsync($"https://music.bugs.co.kr/search/track?q={Enc(t.Artist + " " + t.Title)}", BrowserUa, ct);
        if (html == null) return LyricsResult.None;

        // Pair each result's (trackId, title) with the artist that follows it; take the first that matches.
        var titles = Regex.Matches(html, @"bugs\.music\.listen\('(\d+)'.*?\btitle=""([^""]+)""", RegexOptions.Singleline);
        var artists = Regex.Matches(html, @"<p class=""artist"">\s*<a\b[^>]*?\btitle=""([^""]+)""");
        string? id = null;
        for (int i = 0; i < titles.Count; i++)
        {
            string gotTitle = WebUtility.HtmlDecode(titles[i].Groups[2].Value);
            string gotArtist = i < artists.Count ? WebUtility.HtmlDecode(artists[i].Groups[1].Value) : "";
            if (!RoughTitleMatch(t.Title, gotTitle)) continue;
            if (t.Artist.Length > 0 && gotArtist.Length > 0 && !RoughTitleMatch(t.Artist, gotArtist)) continue;
            id = titles[i].Groups[1].Value; break;
        }
        if (id == null) return LyricsResult.None;

        var synced = await GetStringAsync($"https://music.bugs.co.kr/player/lyrics/T/{id}", BrowserUa, ct);
        var r = ParseBugsLyrics(synced, true);
        if (r.Found) return r;
        var plain = await GetStringAsync($"https://music.bugs.co.kr/player/lyrics/N/{id}", BrowserUa, ct);
        return ParseBugsLyrics(plain, false);
    }

    // Bugs lyrics JSON: {"lyrics":"<sec>|<text>＃<sec>|<text>…"} synced, or {"lyrics":"<line>\r\n<line>…"} plain.
    private static LyricsResult ParseBugsLyrics(string? json, bool synced)
    {
        if (json == null) return LyricsResult.None;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("lyrics", out var ly) || ly.ValueKind != JsonValueKind.String) return LyricsResult.None;
            string s = ly.GetString() ?? "";
            if (s.Trim().Length == 0) return LyricsResult.None;

            if (synced)
            {
                var lines = new List<LyricLine>();
                foreach (var part in s.Split('＃'))
                {
                    int bar = part.IndexOf('|');
                    if (bar <= 0) continue;
                    if (double.TryParse(part.AsSpan(0, bar), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
                        lines.Add(new LyricLine((long)Math.Round(sec * 1000), part[(bar + 1)..].Trim()));
                }
                lines.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
                return lines.Count > 0 ? new LyricsResult(lines, true, false, true, "bugs") : LyricsResult.None;
            }
            var plainLines = ParsePlain(s.Replace("\r\n", "\n"));
            return plainLines.Count > 0 ? new LyricsResult(plainLines, false, false, true, "bugs") : LyricsResult.None;
        }
        catch { return LyricsResult.None; }
    }

    // ---------- Genie (지니; Korean, synced. Clean JSON search API → more robust than Bugs' HTML, good backup) ----------
    private static async Task<LyricsResult> TryGenieAsync(NowPlaying.TrackInfo t, CancellationToken ct)
    {
        var json = await GetStringAsync($"https://www.genie.co.kr/search/searchAuto?query={Enc(t.Artist + " " + t.Title)}", BrowserUa, ct);
        if (json == null) return LyricsResult.None;

        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("song", out var songs) || songs.ValueKind != JsonValueKind.Array) return LyricsResult.None;
            foreach (var s in songs.EnumerateArray())
            {
                string gotArtist = s.TryGetProperty("field1", out var a) ? a.GetString() ?? "" : ""; // field1 = artist
                string gotTitle = s.TryGetProperty("word", out var w) ? w.GetString() ?? "" : "";    // word   = title
                if (!RoughTitleMatch(t.Title, gotTitle)) continue;
                if (t.Artist.Length > 0 && gotArtist.Length > 0 && !RoughTitleMatch(t.Artist, gotArtist)) continue;
                if (s.TryGetProperty("id", out var i)) { id = i.GetString(); break; }
            }
        }
        catch { }
        if (id == null) return LyricsResult.None;

        var msl = await GetStringAsync($"https://dn.genie.co.kr/app/purchase/get_msl.asp?path=a&songid={id}", BrowserUa, ct);
        return ParseGenieMsl(msl);
    }

    // Genie get_msl is JSONP: null({"<ms>":"<line>", …}) — keys are millisecond timestamps.
    private static LyricsResult ParseGenieMsl(string? jsonp)
    {
        if (jsonp == null) return LyricsResult.None;
        int open = jsonp.IndexOf('{'), close = jsonp.LastIndexOf('}');
        if (open < 0 || close <= open) return LyricsResult.None;
        try
        {
            using var doc = JsonDocument.Parse(jsonp[open..(close + 1)]);
            var lines = new List<LyricLine>();
            foreach (var p in doc.RootElement.EnumerateObject())
                if (long.TryParse(p.Name, out long ms) && p.Value.ValueKind == JsonValueKind.String)
                    lines.Add(new LyricLine(ms, (p.Value.GetString() ?? "").Trim()));
            lines.Sort((a, b) => a.TimeMs.CompareTo(b.TimeMs));
            return lines.Count > 0 ? new LyricsResult(lines, true, false, true, "genie") : LyricsResult.None;
        }
        catch { return LyricsResult.None; }
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
                    if (!(h.TryGetProperty("type", out var ty) && ty.GetString() == "song")) continue;
                    var res = h.GetProperty("result");
                    string gotTitle = res.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                    string gotArtist = res.TryGetProperty("primary_artist", out var pa) && pa.TryGetProperty("name", out var pn) ? pn.GetString() ?? "" : "";
                    if (!RoughTitleMatch(t.Title, gotTitle)) continue; // wrong title → skip
                    if (t.Artist.Length > 0 && gotArtist.Length > 0 && !RoughTitleMatch(t.Artist, gotArtist)) continue; // different artist → skip
                    if (res.TryGetProperty("url", out var u)) { url = u.GetString(); break; }
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

    // ---------- match validation (don't show a different song's lyrics for an obscure query) ----------
    private static bool MxmTrackMatches(JsonElement track, NowPlaying.TrackInfo t)
    {
        long wantSec = t.DurationMs / 1000;
        long gotSec = track.TryGetProperty("track_length", out var tl) && tl.ValueKind == JsonValueKind.Number ? tl.GetInt64() : 0;
        string gotTitle = track.TryGetProperty("track_name", out var tn) ? tn.GetString() ?? "" : "";
        string gotArtist = track.TryGetProperty("artist_name", out var an) ? an.GetString() ?? "" : "";

        bool durOk = wantSec > 0 && gotSec > 0 && Math.Abs(wantSec - gotSec) <= 12;
        bool titleOk = gotTitle.Length == 0 || RoughTitleMatch(t.Title, gotTitle);
        bool artistOk = t.Artist.Length == 0 || gotArtist.Length == 0 || RoughTitleMatch(t.Artist, gotArtist);

        // Trust a near-exact duration (then one name lining up is enough); otherwise both names must match.
        return durOk ? (titleOk || artistOk) : (titleOk && artistOk);
    }

    private static bool RoughTitleMatch(string want, string got)
    {
        want = NormalizeTitle(want); got = NormalizeTitle(got);
        if (want.Length == 0 || got.Length == 0) return false;
        if (want.Contains(got) || got.Contains(want)) return true;
        var a = want.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = new HashSet<string>(got.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        int shared = a.Count(b.Contains);
        return shared >= Math.Max(1, (int)Math.Ceiling(Math.Min(a.Length, b.Count) * 0.6));
    }

    private static string NormalizeTitle(string s)
    {
        s = (s ?? "").ToLowerInvariant();
        s = Regex.Replace(s, @"\(.*?\)|\[.*?\]", " ");                              // drop (feat …), [remix] …
        s = Regex.Replace(s, @"\b(feat|ft|featuring|prod|remix|inst|instrumental)\b\.?", " ");
        s = Regex.Replace(s, @"[^a-z0-9가-힣\s]", " ");                             // keep letters / digits / Hangul
        return Regex.Replace(s, @"\s+", " ").Trim();
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
