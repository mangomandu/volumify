using System.IO;
using System.Text.Json;

namespace Volumify;

public sealed class AppSettings
{
    public float P { get; set; } = 0.4f; // "고름/Even" — perceived loudness ≈ slider; lower = flatter, 1.0 = Spotify's raw top-heavy
    public bool DockToSpotify { get; set; }
    public bool HasDockOffset { get; set; }
    public int DockOffsetX { get; set; }
    public int DockOffsetY { get; set; }
    public int PanelWidth { get; set; }
    public int PanelHeight { get; set; }
    public bool OverlayOnVolume { get; set; }
    public bool OverlayPopup { get; set; } = true; // hover the overlay → roomy fly-out slider above the playbar
    public bool LyricsEnabled { get; set; } // floating synced-lyrics window
    public bool HasLyricsBounds { get; set; }
    public int LyricsX { get; set; }
    public int LyricsY { get; set; }
    public int LyricsW { get; set; }
    public int LyricsH { get; set; }
    public bool HasLyricsDockOffset { get; set; }
    public int LyricsDockOffsetX { get; set; }
    public int LyricsDockOffsetY { get; set; }
    public bool LyricsKeepWhenMinimized { get; set; } = true; // keep the lyrics up when Spotify is minimized
    public bool LyricsAlbumTint { get; set; } = true; // tint the lyrics backdrop from the album-art colour, like Spotify
    public int AccentArgb { get; set; } // 0 = use the default coral; else a custom accent color
    public string MusixmatchToken { get; set; } = ""; // minted once (token.get is rate-limited), reused across runs
    public string SpotifyClientId { get; set; } = "";     // optional: user's Spotify app, for exact-lyrics login
    public string SpotifyRefreshToken { get; set; } = ""; // optional: read-only "currently playing" refresh token
    public string Language { get; set; } = ""; // "ko" / "en"; empty = auto-detect from the OS on first run
}

/// <summary>Persists settings to %APPDATA%\Volumify\settings.json (best-effort).</summary>
public static class SettingsStore
{
    private static readonly string AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Dir = Path.Combine(AppData, "Volumify");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    // Pre-rename location (the app used to be "SpotifyLinearVolume") — read it when the new file is absent so upgrades keep settings.
    private static readonly string LegacyFilePath = Path.Combine(AppData, "SpotifyLinearVolume", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            string path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
                if (loaded != null) return loaded;
            }
        }
        catch { /* missing / corrupt / unreadable → fall back to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true); // atomic replace so a crash can't corrupt settings.json
        }
        catch { /* best effort */ }
    }
}
