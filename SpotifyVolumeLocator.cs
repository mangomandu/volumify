using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace SpotifyLinearVolume;

/// <summary>
/// Locates Spotify's native volume slider for the overlay, using only UI Automation.
///
/// UIA reports the slider's <b>X</b> reliably, but its box is a loose ~129px hit-area that now also spans
/// the speaker/mute icon, so its left edge is NOT the drawn rail — pixel calibration across window sizes
/// shows the rail starts a stable +65px inside the box (left edge constant relative to slider.X). The
/// right edge sits a stable 8px before the mini-player button, which slides left as Spotify compresses
/// the playbar at narrow widths — so the rail keeps its left and shrinks from the right. The returned
/// rectangle is one overlay knob-radius wider than the measured rail on each side;
/// <see cref="VolumeBar.EdgePad"/> insets the drawn track back onto Spotify's rail.
///
/// UIA's <b>Y</b> is unreliable in the playbar, so the vertical centre is a tuned offset from the
/// window's bottom edge.
/// </summary>
public static class SpotifyVolumeLocator
{
    private const int SliderHeight = 20;          // a bit taller than the rail so it fully hides it vertically
    public const int OverlayEdgePad = 8;
    public const int NormalRailWidth = 93;        // measured drawn-rail width at full window width
    private const int RailStartFromSlider = 65;   // measured resting rail left = slider.X + this
    private const int RailEndAfterRightButton = -8; // measured resting rail right = mini-player.left + this
    private const int DefaultRailEndFromSlider = 157; // fallback rail right when the mini-player button isn't found
    private const int WindowEdgeGap = 8;          // never poke past the window's right edge
    private const int PlaybarCenterOffset = 43;   // rail centre this far above the window's bottom edge

    // Accessible-name fragments for the controls immediately right of the volume rail
    // (mini-player / fullscreen) in the locales we support.
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

            // The rail ends a fixed gap before the mini-player / fullscreen button. Match that button by
            // NAME only: a position-based "leftmost small button right of the rail" heuristic grabs phantom
            // per-track save ("추가하기") buttons that UIA reports inside the rail zone, throwing the right edge off.
            int nearestButtonX = int.MaxValue;  // leftmost mini-player / fullscreen button = rail end
            foreach (AutomationElement e in elements)
            {
                if (e.Current.ControlType != ControlType.Button) continue;
                var bb = e.Current.BoundingRectangle;
                if (bb.IsEmpty) continue;
                int bx = (int)bb.X;
                if (bx <= x + RailStartFromSlider) continue; // must sit right of the rail's left end

                string name = e.Current.Name ?? "";
                bool nameMatch = false;
                foreach (var key in RightButtonNames)
                    if (name.Length > 0 && name.Contains(key, StringComparison.OrdinalIgnoreCase)) { nameMatch = true; break; }
                if (nameMatch && bx < nearestButtonX)
                    nearestButtonX = bx;
            }

            int railLeft = x + RailStartFromSlider - OverlayEdgePad;
            int railRight = (nearestButtonX != int.MaxValue
                ? nearestButtonX + RailEndAfterRightButton
                : x + DefaultRailEndFromSlider) + OverlayEdgePad;

            if (!GetWindowRect(spotifyHwnd, out var win))
                return new Rectangle(railLeft, (int)r.Y, Math.Max(24, railRight - railLeft), (int)r.Height);

            railRight = Math.Min(railRight, win.Right - WindowEdgeGap);
            if (railLeft < x) railLeft = x;
            int width = Math.Max(24, railRight - railLeft);

            // Vertical centre: UIA's Y is unreliable in the playbar, so anchor to the window bottom.
            int centerY = win.Bottom - PlaybarCenterOffset;
            return new Rectangle(railLeft, centerY - SliderHeight / 2, width, SliderHeight);
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
