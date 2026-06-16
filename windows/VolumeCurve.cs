namespace Volumify;

/// <summary>
/// Maps a UI slider position (0..1) to the value we set on Spotify's own slider. Spotify then applies
/// its OWN steep curve (≈ value^4), so what you HEAR is value^4 in amplitude and ≈ value^2.4 in perceived
/// loudness. We pre-distort the value so the net result matches the curve a preset asks for.
///
/// A preset's curve is encoded in one float <c>p</c>:
///   • <c>p &gt; 0</c> — power law: value = position^p (net amplitude = position^(4p)).
///       p=0.25 → net amplitude ∝ position (amplitude-linear, like web YouTube).
///       p≈0.42 → perceived loudness ∝ position (perceptually even).
///       p=1.0  → Spotify's raw top-heavy curve.
///   • <c>p &lt; 0</c> — logarithmic "audio taper" over (−p) dB, like iPhone/Discord: net amplitude
///       follows an equal-dB ramp (each slider step ≈ equal loudness). Can't be done with any single
///       power exponent — it's a different (exponential) family — hence the separate encoding.
/// </summary>
public static class VolumeCurve
{
    public const float SpotifyAmpExp = 4f;             // Spotify applies ≈ value^4 to what we set
    public const float FeltExp = SpotifyAmpExp * 0.6f; // 2.4 — perceived loudness ≈ amplitude^0.6 (Stevens)
    private const double DbToValue = 20.0 * SpotifyAmpExp; // dB → value exponent divisor (80): value = 10^(dB/80)

    /// <summary>The value to set on Spotify's slider for this UI position.</summary>
    public static float Gain(float position, float p)
    {
        position = Math.Clamp(position, 0f, 1f);
        p = Sanitize(p);
        if (p > 0f) return (float)Math.Pow(position, p);   // power law
        if (position <= 0f) return 0f;                     // log taper: hard mute at the very bottom
        double rangeDb = -p;                               // e.g. 50 → ramps −50 dB (bottom) … 0 dB (top)
        return (float)Math.Pow(10.0, rangeDb * (position - 1.0) / DbToValue);
    }

    /// <summary>Inverse of <see cref="Gain"/> — the UI position that produces this Spotify value.</summary>
    public static float PositionFromGain(float gain, float p)
    {
        gain = Math.Clamp(gain, 0f, 1f);
        p = Sanitize(p);
        if (gain <= 0f) return 0f;
        if (p > 0f) return (float)Math.Pow(gain, 1.0 / p);
        double rangeDb = -p; // invert gain = 10^(rangeDb·(pos−1)/80) → pos = 1 + 80·log10(gain)/rangeDb
        return (float)Math.Clamp(1.0 + DbToValue * Math.Log10(gain) / rangeDb, 0.0, 1.0);
    }

    /// <summary>Estimated perceived loudness (0..1) you actually hear — derives from the value we send.</summary>
    public static float FeltLoudness(float position, float p) => (float)Math.Pow(Gain(position, p), FeltExp);

    /// <summary>Estimated actual output amplitude (0..1).</summary>
    public static float Amplitude(float position, float p) => (float)Math.Pow(Gain(position, p), SpotifyAmpExp);

    // Allow positive (power exponent) or negative (log dB range); reject 0 / NaN / Infinity.
    private static float Sanitize(float p) => float.IsFinite(p) && p != 0f ? p : 1f;
}
