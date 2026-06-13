using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace SpotifyLinearVolume;

/// <summary>
/// Locates Spotify's native volume slider for the overlay, using only UI Automation.
///
/// Spotify is Chromium and UIA only tells the truth about <b>X</b> coordinates — it tracks the
/// slider's and buttons' left edges to ~1px across resizes. Two things it lies about:
///  • The slider <b>Width</b> is the hit‑area (~129px), wider than the drawn rail; and at narrow
///    window widths Spotify visually <b>compresses</b> the rail far below that, so any fixed trim
///    eventually spills the overlay onto the mini‑player / fullscreen buttons next to it.
///  • Every playbar element's <b>Y</b> is unreliable (off‑screen / inconsistent).
///
/// PrintWindow can't help (a backgrounded Chromium window renders nothing to capture). So we anchor
/// on the slider's X and <b>clamp the width to the nearest button on the right</b> — those buttons
/// are found by accessible name and their X is reliable, and they slide toward the slider precisely
/// as the rail compresses, which is exactly the bound we need. Y stays a tuned offset from the
/// window's bottom edge (the geometry is stable even though UIA's Y isn't).
/// </summary>
public static class SpotifyVolumeLocator
{
    private const int PlaybarSliderOffset = 54; // slider top above the window's bottom edge
    private const int SliderHeight = 16;
    private const int RailRightInset = 37;      // UIA slider width overshoots the drawn rail by ~37px
    private const int ButtonGap = 10;           // keep this many px clear of the next button
    private const int WindowEdgeGap = 8;        // never poke past the window's right edge

    // Accessible-name fragments for the controls immediately right of the volume rail
    // (mini-player / fullscreen) in the locales we support. Used to bound the overlay's right edge.
    private static readonly string[] RightButtonNames =
    {
        "전체 화면", "전체화면", "미니플레이어", "미니 플레이어",
        "full screen", "fullscreen", "mini player", "miniplayer",
    };

    public static Rectangle? FindVolumeRect(IntPtr spotifyHwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(spotifyHwnd);
            if (root == null) return null;

            // One descendants walk for both sliders and buttons (the tree is large; don't walk twice).
            var elements = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));

            AutomationElement? volume = null;
            foreach (AutomationElement e in elements)
            {
                if (e.Current.ControlType != ControlType.Slider) continue;
                string name = e.Current.Name ?? "";
                if (name.Contains("볼륨") || name.Contains("Volume", StringComparison.OrdinalIgnoreCase))
                {
                    var rr = e.Current.BoundingRectangle;
                    if (!rr.IsEmpty && rr.Width >= 1) { volume = e; break; }
                }
            }
            if (volume == null) return null;

            var r = volume.Current.BoundingRectangle;
            int x = (int)r.X;
            int width = Math.Max(24, (int)r.Width - RailRightInset);

            // Nearest mini-player / fullscreen button to the right of the slider (by name; X is reliable).
            int nearestButtonX = int.MaxValue;
            foreach (AutomationElement e in elements)
            {
                if (e.Current.ControlType != ControlType.Button) continue;
                string name = e.Current.Name ?? "";
                if (name.Length == 0) continue;
                bool match = false;
                foreach (var key in RightButtonNames)
                    if (name.Contains(key, StringComparison.OrdinalIgnoreCase)) { match = true; break; }
                if (!match) continue;
                var bb = e.Current.BoundingRectangle;
                if (bb.IsEmpty) continue;
                int bx = (int)bb.X;
                if (bx > x + 10 && bx < nearestButtonX) nearestButtonX = bx;
            }

            if (!GetWindowRect(spotifyHwnd, out var win))
                return new Rectangle(x, (int)r.Y, width, (int)r.Height);

            if (nearestButtonX != int.MaxValue)
                width = Math.Min(width, nearestButtonX - x - ButtonGap); // don't reach the button
            width = Math.Min(width, win.Right - x - WindowEdgeGap);      // don't leave the window
            width = Math.Max(20, width);

            return new Rectangle(x, win.Bottom - PlaybarSliderOffset, width, SliderHeight);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
}
