using System.Drawing.Drawing2D;

namespace SpotifyLinearVolume;

/// <summary>Slim horizontal volume bar. Drag to set position. The track is inset from the control
/// edges by <see cref="EdgePad"/> and the knob centre travels the full track [pad, W-pad], so it
/// lines up with the rail it overlays. The overlay sets EdgePad to the knob radius — the box reaches
/// a radius past the drawn rail only so the knob never clips, while the track stays the rail's length.
/// The popup sets a larger EdgePad for a roomy slider.</summary>
public sealed class VolumeBar : Control
{
    private const int KnobR = 6; // knob is a 12px circle

    private static readonly Color Accent = Color.FromArgb(30, 215, 96); // Spotify green

    private int _pad = 14;
    private float _position;
    private bool _dragging;

    public event Action<float>? PositionPicked;

    /// <summary>Horizontal inset of the track (and the knob's travel) from the control edges.</summary>
    public int EdgePad
    {
        get => _pad;
        set { _pad = Math.Max(0, value); Invalidate(); }
    }

    public VolumeBar()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(20, 20, 20);
        Cursor = Cursors.Hand;
        Height = 28;
    }

    public void Set(float position)
    {
        _position = Math.Clamp(position, 0f, 1f);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { Capture = true; _dragging = true; Pick(e.X); }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Pick(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _dragging = false; Capture = false; }
        base.OnMouseUp(e); // raises MouseUp so the overlay can show its right-click menu
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        _dragging = false; // capture lost (menu opened / hidden / focus change) → stop dragging
        base.OnMouseCaptureChanged(e);
    }

    private void Pick(int mouseX)
    {
        int x0 = _pad, x1 = Width - _pad;
        int width = Math.Max(1, x1 - x0);
        PositionPicked?.Invoke(Math.Clamp((float)(mouseX - x0) / width, 0f, 1f));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int y = Height / 2;
        int x0 = _pad, x1 = Width - _pad;
        if (x1 <= x0) return;

        // The knob centre travels the full track [x0, x1] so it lines up with the rail's own knob.
        int kx = Math.Clamp(x0 + (int)Math.Round((x1 - x0) * _position), x0, x1);

        using (var track = new Pen(Color.FromArgb(60, 60, 60), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, x0, y, x1, y);

        if (kx > x0)
            using (var fill = new Pen(Accent, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(fill, x0, y, kx, y);

        // Plain white knob (no green ring) so it matches Spotify's own knob and the fill ends exactly
        // where Spotify's does.
        using (var dot = new SolidBrush(Color.White))
            g.FillEllipse(dot, kx - KnobR, y - KnobR, 2 * KnobR, 2 * KnobR);
    }
}
