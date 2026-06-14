using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace SpotifyLinearVolume;

/// <summary>
/// Locates Spotify's native volume slider for the overlay, using only UI Automation.
///
/// UIA reports button/slider <b>X</b> reliably but the slider's box is a loose ~129px hit-area that
/// covers the speaker / mute icon on its left and overlaps the mini-player button on its right, and
/// Spotify <i>compresses</i> the drawn rail as the window narrows — so fixed pixel offsets don't hold.
/// Instead we bound the rail by its neighbours: <b>left edge = the right edge of the icon that sits in
/// the slider's left hit-area</b> (the speaker/mute button), <b>right edge = the left edge of the
/// nearest mini-player / fullscreen button</b>. Both are reliable UIA X coordinates that reposition
/// with the window, so the overlay tracks the rail at any width. The overlay (opaque) covers exactly
/// that span, so Spotify's round knob is always hidden inside it (no doubled knob) yet the box never
/// reaches the speaker icon or the next button.
///
/// UIA's <b>Y</b> is unreliable in the playbar, so the vertical centre is a tuned offset from the
/// window's bottom edge.
/// </summary>
public static class SpotifyVolumeLocator
{
    private const int SliderHeight = 20;          // a bit taller than the rail so it fully hides it vertically
    // The rail is inset from its neighbouring buttons by a fixed gap (Spotify CSS — constant px at any
    // resolution). These two tune the overlay's left/right edges relative to those button edges.
    // Calibrated by comparing our green fill against Spotify's own green fill at the same window
    // (the UIA button boxes carry hidden padding, so their edges aren't the rail ends). The box reaches
    // one knob-radius past the rail so the white knob never clips; the track inside it is the rail's length.
    private const int RailStartInset = -11;       // overlay left edge = speaker.right + this  (rail start - EdgePad)
    private const int RailEndInset = 1;           // overlay right edge = miniplayer.left - this (rail end + EdgePad)
    private const int WindowEdgeGap = 8;          // never poke past the window's right edge
    private const int PlaybarCenterOffset = 43;   // rail centre this far above the window's bottom edge
    private const int LeftIconReach = 84;         // a left-hit-area icon's right edge no further than this past slider.X
    private const int DefaultLeftInset = 62;      // fallback rail start if the speaker icon isn't found
    private const int DefaultRightInset = 150;    // fallback rail end if no right button is found

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

            // Bound the rail by its neighbours: the speaker icon on the left, the mini-player button on the right.
            int leftIconRight = int.MinValue;   // rightmost icon ending inside the slider's left hit-area = rail start
            int nearestButtonX = int.MaxValue;  // leftmost mini-player / fullscreen button = rail end
            foreach (AutomationElement e in elements)
            {
                if (e.Current.ControlType != ControlType.Button) continue;
                var bb = e.Current.BoundingRectangle;
                if (bb.IsEmpty) continue;
                int bx = (int)bb.X, br = (int)(bb.X + bb.Width);

                if (br > x && br - x <= LeftIconReach && br > leftIconRight)
                    leftIconRight = br;

                string name = e.Current.Name ?? "";
                bool nameMatch = false;
                foreach (var key in RightButtonNames)
                    if (name.Length > 0 && name.Contains(key, StringComparison.OrdinalIgnoreCase)) { nameMatch = true; break; }
                bool posMatch = bx - x > LeftIconReach + 4 && bb.Width < 44; // an icon sitting right of the rail
                if ((nameMatch || posMatch) && bx < nearestButtonX)
                    nearestButtonX = bx;
            }

            int railLeft = (leftIconRight > int.MinValue ? leftIconRight : x + DefaultLeftInset) + RailStartInset;
            int railRight = (nearestButtonX != int.MaxValue ? nearestButtonX : x + DefaultRightInset) - RailEndInset;

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
