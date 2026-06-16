namespace Volumify;

public enum AppLang { Korean, English }

/// <summary>
/// Minimal two-language UI text. <see cref="T"/> returns the active language's string; both
/// languages live inline at each call site, which suits a small two-locale app (no resx/keys).
/// </summary>
public static class Loc
{
    public static AppLang Lang { get; set; } = AppLang.Korean;

    public static string T(string ko, string en) => Lang == AppLang.English ? en : ko;

    /// <summary>Best guess from the OS UI culture: Korean if the UI is Korean, otherwise English.</summary>
    public static AppLang Detect() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("ko", StringComparison.OrdinalIgnoreCase) ? AppLang.Korean : AppLang.English;

    /// <summary>Parse a persisted "ko"/"en" setting; anything else (unset) falls back to <see cref="Detect"/>.</summary>
    public static AppLang FromSetting(string? s) => s switch
    {
        "en" => AppLang.English,
        "ko" => AppLang.Korean,
        _ => Detect(),
    };

    public static string ToSetting(AppLang lang) => lang == AppLang.English ? "en" : "ko";
}

/// <summary>
/// A curve preset: a localized name plus the curve code <c>p</c> (see <see cref="VolumeCurve"/> —
/// p&gt;0 is a power law, p&lt;0 a log/dB taper). Both the tray menu and the panel pill show the name.
/// </summary>
public sealed record Preset(string Ko, string En, float P)
{
    public string Label => Loc.T(Ko, En);
    public string Pill => Loc.T(Ko, En);
}
