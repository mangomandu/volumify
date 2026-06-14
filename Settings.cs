using System.IO;
using System.Text.Json;

namespace SpotifyLinearVolume;

public sealed class AppSettings
{
    public float P { get; set; } = 1.0f; // neutral baseline; lower = boost low end, up to 2.0 = Spotify's default feel
    public bool DockToSpotify { get; set; }
    public bool HasDockOffset { get; set; }
    public int DockOffsetX { get; set; }
    public int DockOffsetY { get; set; }
    public int PanelWidth { get; set; }
    public int PanelHeight { get; set; }
    public bool OverlayOnVolume { get; set; }
    public bool OverlayPopup { get; set; } = true; // hover the overlay → roomy fly-out slider above the playbar
    public string Language { get; set; } = ""; // "ko" / "en"; empty = auto-detect from the OS on first run
}

/// <summary>Persists settings to %APPDATA%\SpotifyLinearVolume\settings.json (best-effort).</summary>
public static class SettingsStore
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyLinearVolume");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
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
