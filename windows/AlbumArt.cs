using System.Drawing.Drawing2D;

namespace Volumify;

/// <summary>
/// Pulls a single representative colour out of album art — the seed for the lyrics backdrop, the way
/// Spotify tints its "Now Playing" view. Favours a vibrant, mid-bright pixel; falls back to the plain
/// average when the art is greyscale, so monochrome covers still get a sensible (neutral) tint.
/// </summary>
public static class AlbumArt
{
    public static Color Dominant(Bitmap src)
    {
        const int N = 32; // album thumbnails are small already; 32×32 is plenty and keeps GetPixel cheap
        using var small = new Bitmap(N, N);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, N, N);
        }

        double vr = 0, vg = 0, vb = 0, wsum = 0; // saturation-weighted (vibrant) accumulator
        double ar = 0, ag = 0, ab = 0; int n = 0; // plain average (greyscale fallback)
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                var p = small.GetPixel(x, y);
                ar += p.R; ag += p.G; ab += p.B; n++;

                float mx = Math.Max(p.R, Math.Max(p.G, p.B)) / 255f;
                float mn = Math.Min(p.R, Math.Min(p.G, p.B)) / 255f;
                if (mx < 0.15f || mx > 0.97f) continue;            // skip near-black / near-white pixels
                float sat = mx <= 0 ? 0 : (mx - mn) / mx;
                double w = sat * sat * mx;                          // weight: vivid and reasonably bright
                vr += p.R * w; vg += p.G * w; vb += p.B * w; wsum += w;
            }

        if (wsum > 1e-3) return Color.FromArgb((int)(vr / wsum), (int)(vg / wsum), (int)(vb / wsum));
        if (n > 0) return Color.FromArgb((int)(ar / n), (int)(ag / n), (int)(ab / n));
        return Color.FromArgb(44, 42, 48);
    }
}
