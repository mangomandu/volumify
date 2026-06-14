namespace SpotifyLinearVolume;

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
/// A curve preset: a localized word label plus the exponent <c>p</c> (gain = position^p). The menus
/// show "&lt;word&gt; (p)"; the control panel's pill shows just the number.
/// </summary>
public sealed record Preset(string Ko, string En, float P)
{
    public string Label => $"{Loc.T(Ko, En)} ({P:0.0})";
    public string Number => $"{P:0.0}";
}
