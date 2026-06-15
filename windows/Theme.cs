namespace Volumify;

/// <summary>
/// The app's accent color — shared and user-customizable. Every surface (overlay bar, hover popup, curve
/// graph, preset pills, lyrics) reads <see cref="Accent"/> at paint time, so changing it repaints them all.
/// </summary>
public static class Theme
{
    public static readonly Color DefaultAccent = Color.FromArgb(204, 120, 92); // Claude coral #CC785C

    public static Color Accent { get; private set; } = DefaultAccent;

    /// <summary>Raised after the accent changes so live windows can invalidate.</summary>
    public static event Action? AccentChanged;

    public static void SetAccent(Color c)
    {
        Accent = Color.FromArgb(255, c.R, c.G, c.B); // force opaque
        AccentChanged?.Invoke();
    }
}
