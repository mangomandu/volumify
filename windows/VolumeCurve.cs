namespace SpotifyLinearVolume;

/// <summary>
/// Perceptual volume curve: maps a UI slider position (0..1) to an actual gain (0..1)
/// via gain = position ^ p.  p &lt; 1 spreads the audible range toward the low end and
/// makes the top of the slider gentler (tames Spotify's top-heavy native curve).
/// </summary>
public static class VolumeCurve
{
    public static float Gain(float position, float p)
    {
        p = SanitizeP(p);
        position = Math.Clamp(position, 0f, 1f);
        return (float)Math.Pow(position, p);
    }

    public static float PositionFromGain(float gain, float p)
    {
        p = SanitizeP(p);
        gain = Math.Clamp(gain, 0f, 1f);
        if (gain <= 0f) return 0f;
        return (float)Math.Pow(gain, 1.0 / p);
    }

    // Spotify applies its OWN steep, top-heavy curve to the value we set: slider value -> amplitude is
    // ≈ value^4 (per community reports + measurement), so the perceived loudness ≈ amplitude^0.6 ≈
    // value^2.4. These estimate what you actually HEAR, so the panel/graph can show felt loudness rather
    // than the raw value we send (which, plotted alone, looks bowed-up even when the result is even).
    public const float SpotifyAmpExp = 4f;
    public const float FeltExp = SpotifyAmpExp * 0.6f; // 2.4

    /// <summary>Estimated perceived loudness (0..1) you actually hear at this slider position + p.</summary>
    public static float FeltLoudness(float position, float p) =>
        (float)Math.Pow(Math.Clamp(position, 0f, 1f), SanitizeP(p) * FeltExp);

    /// <summary>Estimated actual output amplitude (0..1) — for the real dB readout.</summary>
    public static float Amplitude(float position, float p) =>
        (float)Math.Pow(Math.Clamp(position, 0f, 1f), SanitizeP(p) * SpotifyAmpExp);

    // Guard against p &lt;= 0 / NaN / Infinity, which would invert or break the curve math.
    private static float SanitizeP(float p) => float.IsFinite(p) && p > 0f ? p : 1f;
}
