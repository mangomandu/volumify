using System.Drawing.Drawing2D;

namespace Volumify;

/// <summary>A row of rounded "pill" buttons for picking a curve preset. Active pill = green.</summary>
public sealed class PresetBar : Panel
{
    private static Color Accent => Theme.Accent; // shared, user-customizable
    private static readonly Font PillFont = new("Segoe UI", 8.5f, FontStyle.Bold); // cached — was reallocated each repaint
    private static readonly StringFormat CenterFmt = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    private readonly string[] _labels;
    private int _active = -1;
    private int _hover = -1;

    public event Action<int>? PresetSelected;

    public PresetBar(string[] labels)
    {
        _labels = labels;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(20, 20, 20);
        Cursor = Cursors.Hand;
    }

    public void SetActive(int index)
    {
        if (_active == index) return;
        _active = index;
        Invalidate();
    }

    private int IndexAt(int x)
    {
        if (_labels.Length == 0) return -1;
        return Math.Clamp(x * _labels.Length / Math.Max(1, Width), 0, _labels.Length - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        int i = IndexAt(e.X);
        if (i >= 0) PresetSelected?.Invoke(i);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = IndexAt(e.X);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        int n = _labels.Length;
        if (n == 0) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = (float)Width / n;

        for (int i = 0; i < n; i++)
        {
            var cell = new RectangleF(i * w + 3, 2, w - 6, Height - 4);
            bool active = i == _active;
            Color bg = active ? Accent : i == _hover ? Color.FromArgb(50, 50, 50) : Color.FromArgb(36, 36, 36);
            using (var path = Rounded(cell, 7))
            using (var b = new SolidBrush(bg))
                g.FillPath(b, path);

            using var tb = new SolidBrush(active ? Color.FromArgb(12, 12, 12) : Color.FromArgb(205, 205, 205));
            g.DrawString(_labels[i], PillFont, tb, cell, CenterFmt);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
