using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Volumify;

/// <summary>
/// Optional Spotify login — Authorization Code + PKCE, read-only "user-read-currently-playing" scope — used ONLY
/// to read the exact track ID of what's playing. With that ID we ask Musixmatch by track_spotify_id and get
/// exactly the lyrics Spotify shows, with no fuzzy title/artist matching. The client is never patched and lyrics
/// still come from Musixmatch; this just removes the guesswork in matching.
/// </summary>
public sealed class SpotifyAuth
{
    private const int Port = 8888;
    public const string RedirectUri = "http://127.0.0.1:8888/callback";
    private const string Scope = "user-read-currently-playing";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string _clientId;
    private string? _refreshToken;
    private string? _accessToken;
    private long _accessExpiry;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);

    public event Action? RefreshTokenChanged; // _refreshToken changed → persist it

    public SpotifyAuth(string? clientId, string? refreshToken)
    {
        _clientId = (clientId ?? "").Trim();
        _refreshToken = string.IsNullOrEmpty(refreshToken) ? null : refreshToken;
    }

    public bool HasClientId => !string.IsNullOrWhiteSpace(_clientId);
    public bool IsLinked => !string.IsNullOrEmpty(_refreshToken);
    public string RefreshToken => _refreshToken ?? "";
    public string ClientId => _clientId;

    public void SetClientId(string id) => _clientId = (id ?? "").Trim();

    public void Unlink()
    {
        _refreshToken = null; _accessToken = null; _accessExpiry = 0;
        RefreshTokenChanged?.Invoke();
    }

    /// <summary>Open the browser, capture the redirect on a loopback socket, exchange the code for tokens.</summary>
    public async Task<bool> LoginAsync(CancellationToken ct)
    {
        if (!HasClientId) return false;
        string verifier = RandUnreserved(64);
        string challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = RandUnreserved(16);

        var listener = new TcpListener(IPAddress.Loopback, Port);
        try { listener.Start(); }
        catch { return false; } // port already in use

        try
        {
            string authUrl = "https://accounts.spotify.com/authorize?client_id=" + Uri.EscapeDataString(_clientId)
                + "&response_type=code&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
                + "&scope=" + Uri.EscapeDataString(Scope)
                + "&code_challenge_method=S256&code_challenge=" + challenge + "&state=" + state;
            OpenBrowser(authUrl);

            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromMinutes(3));
            using var client = await listener.AcceptTcpClientAsync(to.Token);
            string? code = await ReadCodeAndRespond(client, state);
            if (code == null) return false;

            var tok = await PostTokenAsync(new()
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = _clientId,
                ["code_verifier"] = verifier,
            }, ct);
            if (tok == null) return false;
            ApplyTokens(tok.Value);
            return IsLinked;
        }
        catch { return false; }
        finally { try { listener.Stop(); } catch { } }
    }

    /// <summary>Base-62 track ID of what's playing right now, or null (nothing playing / not linked / error).</summary>
    public async Task<string?> GetCurrentTrackIdAsync(CancellationToken ct)
    {
        var token = await EnsureAccessAsync(ct);
        if (token == null) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing?market=from_token");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NoContent || !resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                return id.GetString();
        }
        catch { }
        return null;
    }

    private async Task<string?> EnsureAccessAsync(CancellationToken ct)
    {
        if (_accessToken != null && Environment.TickCount64 < _accessExpiry) return _accessToken;
        if (string.IsNullOrEmpty(_refreshToken) || !HasClientId) return null;
        await _tokenGate.WaitAsync(ct);
        try
        {
            if (_accessToken != null && Environment.TickCount64 < _accessExpiry) return _accessToken;
            var tok = await PostTokenAsync(new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _refreshToken!,
                ["client_id"] = _clientId,
            }, ct);
            if (tok == null) return null;
            ApplyTokens(tok.Value);
            return _accessToken;
        }
        finally { _tokenGate.Release(); }
    }

    private void ApplyTokens((string access, string? refresh, int expires) t)
    {
        _accessToken = t.access;
        _accessExpiry = Environment.TickCount64 + Math.Max(30, t.expires - 60) * 1000L;
        if (!string.IsNullOrEmpty(t.refresh) && t.refresh != _refreshToken) { _refreshToken = t.refresh; RefreshTokenChanged?.Invoke(); }
    }

    private static async Task<(string access, string? refresh, int expires)?> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form), ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String) return null;
            string? refresh = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
            int expires = root.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number ? ex.GetInt32() : 3600;
            return (at.GetString()!, refresh, expires);
        }
        catch { return null; }
    }

    private static async Task<string?> ReadCodeAndRespond(TcpClient client, string expectedState)
    {
        try
        {
            using var stream = client.GetStream();
            var buf = new byte[4096];
            int n = await stream.ReadAsync(buf);
            string reqLine = Encoding.ASCII.GetString(buf, 0, n).Split('\n').FirstOrDefault() ?? ""; // "GET /callback?code=..&state=.. HTTP/1.1"
            string? code = null, state = null;
            int q = reqLine.IndexOf('?');
            if (q >= 0)
            {
                int sp = reqLine.IndexOf(' ', q);
                string query = reqLine.Substring(q + 1, (sp < 0 ? reqLine.Length : sp) - q - 1);
                foreach (var kv in query.Split('&'))
                {
                    int eq = kv.IndexOf('=');
                    if (eq < 0) continue;
                    string key = kv[..eq];
                    string val = Uri.UnescapeDataString(kv[(eq + 1)..]);
                    if (key == "code") code = val; else if (key == "state") state = val;
                }
            }
            string html = "<html><body style='font-family:sans-serif;background:#191414;color:#eee;text-align:center;padding-top:64px'>"
                + "<h2 style='color:#1ed760'>Volumify</h2><p>로그인 완료! 이 창을 닫아도 됩니다.</p></body></html>";
            string resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\nContent-Length: "
                + Encoding.UTF8.GetByteCount(html) + "\r\n\r\n" + html;
            await stream.WriteAsync(Encoding.UTF8.GetBytes(resp));
            return state == expectedState ? code : null;
        }
        catch { return null; }
    }

    private static void OpenBrowser(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static string RandUnreserved(int len)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
        var bytes = RandomNumberGenerator.GetBytes(len);
        var sb = new StringBuilder(len);
        foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }

    private static string B64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
