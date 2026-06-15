using System.Drawing.Drawing2D;

namespace SpotifyLinearVolume;

/// <summary>
/// Live volume curve: a green line for felt loudness vs slider position, a dashed diagonal
/// "even" reference, and a simple marker at the current position. Drag horizontally to set it.
/// </summary>
public sealed class CurveGraphPanel : Panel
{
    private const int Pad = 12;
    private static readonly Color Accent = Color.FromArgb(30, 215, 96);
    private static readonly Font LabelFont = new("Segoe UI", 7.5f); // cached — was reallocated each repaint

    private float _p = 0.5f;
    private float _position = 0.5f;
    private bool _dragging;

    public event Action<float>? PositionPicked;

    public CurveGraphPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(26, 26, 26);
        Cursor = Cursors.Hand;
    }

    public void Set(float p, float position)
    {
        _p = p;
        _position = position;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e) { _dragging = true; Pick(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) Pick(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; }

    private void Pick(int mouseX)
    {
        int width = Math.Max(1, Width - 2 * Pad);
        PositionPicked?.Invoke(Math.Clamp((float)(mouseX - Pad) / width, 0f, 1f));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Reserve a thin strip top + bottom for the axis labels.
        var area = new Rectangle(Pad, Pad + 13, Width - 2 * Pad, Height - 2 * Pad - 26);
        if (area.Width < 8 || area.Height < 8) return;

        // Plot frame.
        using (var frame = new Pen(Color.FromArgb(48, 48, 48)))
            g.DrawRectangle(frame, area.Left, area.Top, area.Width, area.Height);

        // "Even" reference: a straight diagonal — felt loudness rising evenly with the slider.
        using (var lin = new Pen(Color.FromArgb(70, 70, 70)) { DashStyle = DashStyle.Dash })
            g.DrawLine(lin, area.Left, area.Bottom, area.Right, area.Top);

        // The curve: felt loudness vs slider position (≈ position^(2.4·p) — "고름" reads as a straight line).
        const int n = 128;
        var pts = new PointF[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float x = i / (float)n;
            float y = VolumeCurve.FeltLoudness(x, _p);
            pts[i] = new PointF(area.Left + x * area.Width, area.Bottom - y * area.Height);
        }
        using (var pen = new Pen(Accent, 2.4f) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, pts);

        // Current-position marker: a vertical guide + white dot with a green ring.
        float felt = VolumeCurve.FeltLoudness(_position, _p);
        float mx = area.Left + _position * area.Width;
        float my = area.Bottom - felt * area.Height;
        using (var guide = new Pen(Color.FromArgb(70, 255, 255, 255)) { DashStyle = DashStyle.Dot })
            g.DrawLine(guide, mx, area.Bottom, mx, my);
        using (var dot = new SolidBrush(Color.White))
            g.FillEllipse(dot, mx - 4.5f, my - 4.5f, 9, 9);
        using (var ring = new Pen(Accent, 2f))
            g.DrawEllipse(ring, mx - 4.5f, my - 4.5f, 9, 9);

        // Axis labels: what each direction means, kept small and out of the way.
        using var labelBrush = new SolidBrush(Color.FromArgb(150, 150, 150));
        g.DrawString("↑ " + Loc.T("체감 음량", "loudness"), LabelFont, labelBrush, area.Left - 2, Pad - 3);
        string xLabel = Loc.T("슬라이더 위치", "slider") + " →";
        float xWidth = g.MeasureString(xLabel, LabelFont).Width;
        g.DrawString(xLabel, LabelFont, labelBrush, area.Right - xWidth + 2, area.Bottom + 5);
    }
}
