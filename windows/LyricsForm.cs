using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Volumify;

/// <summary>
/// Floating, always-on-top lyrics window. Unlike Spotify's built-in lyrics (which take over the whole
/// view), this stays up while you browse playlists. Synced lyrics (LRCLIB) highlight + auto-scroll the
/// current line like Spotify's; plain lyrics (Genius fallback) scroll with the wheel.
/// </summary>
public sealed class LyricsForm : Form
{
    private static readonly Color Bg = Color.FromArgb(18, 18, 18);
    private static readonly Color Accent = Color.FromArgb(30, 215, 96);
    private const int HeaderH = 38;
    private const int Pad = 18;

    private static readonly Font LineFont = new("Segoe UI", 13f);
    private static readonly Font HeaderFont = new("Segoe UI Semibold", 9.5f);
    private static readonly Font StatusFont = new("Segoe UI", 11f);
    private static readonly StringFormat WrapFmt = new() { FormatFlags = 0 };
    private static readonly StringFormat CenterFmt = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    private readonly NowPlaying _np;
    private readonly Label _close = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 110 };

    private LyricsResult _lyrics = LyricsResult.None;
    private NowPlaying.TrackInfo? _track;
    private string _status = "";
    private CancellationTokenSource? _fetchCts;

    private readonly List<(int line, float top, float height)> _rows = new();
    private int _layoutWidth = -1;
    private bool _layoutDirty = true;

    private int _activeIdx = -1;
    private float _scroll, _targetScroll;
    private bool _userScrolled;          // plain lyrics: wheel-controlled
    private long _userScrollTick;

    public event Action? CloseRequested;

    public LyricsForm(NowPlaying np)
    {
        _np = np;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(360, 440);
        MinimumSize = new Size(240, 200);
        BackColor = Bg;
        ShowInTaskbar = false;
        TopMost = true;

        SetupClose();
        Controls.Add(_close);

        _timer.Tick += (_, _) => OnTick();
        _np.TrackChanged += OnTrackChangedAsync;
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); } catch { }
    }

    private void SetupClose()
    {
        _close.Text = "✕";
        _close.Font = new Font("Segoe UI", 9f);
        _close.ForeColor = Color.FromArgb(150, 150, 150);
        _close.TextAlign = ContentAlignment.MiddleCenter;
        _close.Size = new Size(26, 24);
        _close.Cursor = Cursors.Hand;
        _close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _close.Location = new Point(ClientSize.Width - 30, 7);
        _close.MouseEnter += (_, _) => _close.ForeColor = Color.White;
        _close.MouseLeave += (_, _) => _close.ForeColor = Color.FromArgb(150, 150, 150);
        _close.Click += (_, _) => CloseRequested?.Invoke();
    }

    public void SetActive(bool on)
    {
        if (on)
        {
            _timer.Start();
            Show();
            KickFetch(); // (re)load lyrics for whatever's playing now
        }
        else
        {
            _timer.Stop();
            _fetchCts?.Cancel();
            Hide();
        }
    }

    // ----- track / fetch -----
    private void OnTrackChangedAsync()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(KickFetch); } catch { }
    }

    private async void KickFetch()
    {
        if (IsDisposed) return;
        var track = _np.Current;
        _track = track;
        _fetchCts?.Cancel();
        _lyrics = LyricsResult.None;
        _rows.Clear(); _layoutDirty = true;
        _scroll = _targetScroll = 0; _activeIdx = -1; _userScrolled = false;

        if (track == null || track.IsEmpty) { _status = Loc.T("재생 중인 곡이 없어요", "Nothing playing"); Invalidate(); return; }
        _status = Loc.T("가사 찾는 중…", "Finding lyrics…");
        Invalidate();

        var cts = new CancellationTokenSource();
        _fetchCts = cts;
        LyricsResult res;
        try { res = await LyricsProvider.GetAsync(track, cts.Token); }
        catch { res = LyricsResult.None; }
        if (cts.IsCancellationRequested || IsDisposed || !ReferenceEquals(_fetchCts, cts)) return;

        _lyrics = res;
        _layoutDirty = true;
        _status = res.Instrumental ? Loc.T("연주곡 (가사 없음)", "Instrumental")
               : !res.Found ? Loc.T("가사를 찾지 못했어요", "No lyrics found")
               : "";
        Invalidate();
    }

    // ----- tick: advance the synced highlight -----
    private void OnTick()
    {
        if (_track != null && (_np.Current == null || _np.Current.Key != _track.Key)) { /* TrackChanged will fire */ }
        if (!_lyrics.Synced || _lyrics.Lines.Count == 0) { return; }

        _np.Resync();
        long pos = _np.PositionMs;
        int idx = -1;
        var lines = _lyrics.Lines;
        for (int i = 0; i < lines.Count; i++) { if (lines[i].TimeMs <= pos) idx = i; else break; }
        if (idx != _activeIdx) { _activeIdx = idx; _userScrolled = false; }

        if (!_userScrolled && _rows.Count > 0)
        {
            float vpH = ClientSize.Height - HeaderH - Pad;
            float centerY = _activeIdx >= 0 && _activeIdx < _rows.Count
                ? _rows[_activeIdx].top + _rows[_activeIdx].height / 2f
                : 0;
            _targetScroll = centerY - vpH / 2f;
        }
        _scroll += (_targetScroll - _scroll) * 0.22f;
        if (Math.Abs(_targetScroll - _scroll) < 0.4f) _scroll = _targetScroll;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // manual scroll (plain lyrics, or peek ahead on synced)
        _targetScroll -= e.Delta * 0.5f;
        ClampScroll();
        _userScrolled = true;
        _userScrollTick = Environment.TickCount64;
        if (_lyrics.Synced) { /* re-center after a few seconds */ }
        Invalidate();
        base.OnMouseWheel(e);
    }

    private void ClampScroll()
    {
        if (_rows.Count == 0) { _targetScroll = 0; return; }
        float content = _rows[^1].top + _rows[^1].height;
        float vpH = ClientSize.Height - HeaderH - Pad;
        float max = Math.Max(0, content - vpH + Pad);
        _targetScroll = Math.Clamp(_targetScroll, 0, max);
    }

    // ----- paint -----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        // header
        using (var tb = new SolidBrush(Color.FromArgb(235, 235, 235)))
        using (var sb = new SolidBrush(Color.FromArgb(140, 140, 140)))
        {
            string title = _track?.Title ?? "Volumify";
            string artist = _track?.Artist ?? "";
            g.DrawString(Ellipsize(g, title, HeaderFont, ClientSize.Width - 70), HeaderFont, tb, 16, 8);
            if (artist.Length > 0)
                g.DrawString(Ellipsize(g, artist, StatusFont, ClientSize.Width - 70), new Font("Segoe UI", 8.5f), sb, 16, 22);
        }

        var vp = new Rectangle(0, HeaderH, ClientSize.Width, ClientSize.Height - HeaderH);
        if (vp.Height < 10) return;

        if (_lyrics.Lines.Count == 0)
        {
            using var stb = new SolidBrush(Color.FromArgb(150, 150, 150));
            g.DrawString(_status, StatusFont, stb, new RectangleF(Pad, vp.Top, vp.Width - 2 * Pad, vp.Height), CenterFmt);
            return;
        }

        EnsureLayout(g, vp.Width - 2 * Pad);
        g.SetClip(new Rectangle(Pad, vp.Top + 2, vp.Width - 2 * Pad, vp.Height - 4));

        for (int r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            float y = vp.Top + Pad + row.top - _scroll;
            if (y + row.height < vp.Top || y > vp.Bottom) continue;

            int alpha; Color col;
            if (_lyrics.Synced)
            {
                int d = Math.Abs(r - _activeIdx);
                col = r == _activeIdx ? Color.White : Color.FromArgb(150, 150, 150);
                alpha = r == _activeIdx ? 255 : d == 1 ? 165 : d == 2 ? 120 : 85;
            }
            else { col = Color.FromArgb(205, 205, 205); alpha = 230; }

            // soft fade near the viewport edges
            float ly = y + row.height / 2f;
            float edge = Math.Min(ly - vp.Top, vp.Bottom - ly);
            if (edge < 40) alpha = (int)(alpha * Math.Clamp(edge / 40f, 0f, 1f));

            using var b = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 255), col));
            g.DrawString(_lyrics.Lines[row.line].Text, LineFont, b,
                new RectangleF(Pad, y, vp.Width - 2 * Pad, row.height + 2), WrapFmt);
        }
        g.ResetClip();
    }

    private void EnsureLayout(Graphics g, int width)
    {
        if (!_layoutDirty && _layoutWidth == width) return;
        _rows.Clear();
        float top = 0;
        for (int i = 0; i < _lyrics.Lines.Count; i++)
        {
            string text = _lyrics.Lines[i].Text;
            float h;
            if (text.Length == 0) h = LineFont.GetHeight(g) * 0.6f;
            else h = g.MeasureString(text, LineFont, Math.Max(10, width), WrapFmt).Height;
            _rows.Add((i, top, h));
            top += h + 8; // line gap
        }
        _layoutWidth = width;
        _layoutDirty = false;
        if (!_lyrics.Synced) ClampScroll();
    }

    private static string Ellipsize(Graphics g, string s, Font f, float maxW)
    {
        if (g.MeasureString(s, f).Width <= maxW) return s;
        while (s.Length > 1 && g.MeasureString(s + "…", f).Width > maxW) s = s[..^1];
        return s + "…";
    }

    // ----- borderless drag / resize -----
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084, WM_EXITSIZEMOVE = 0x0232;
        if (m.Msg == WM_NCHITTEST)
        {
            long lp = m.LParam.ToInt64();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            m.Result = (IntPtr)HitTest(p);
            return;
        }
        base.WndProc(ref m);
        if (m.Msg == WM_EXITSIZEMOVE) { _layoutDirty = true; BoundsChanged?.Invoke(Bounds); Invalidate(); }
    }

    public event Action<Rectangle>? BoundsChanged;

    private int HitTest(Point p)
    {
        const int grip = 6;
        const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12,
                  HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        bool l = p.X < grip, r = p.X >= ClientSize.Width - grip, t = p.Y < grip, b = p.Y >= ClientSize.Height - grip;
        if (t && l) return HTTOPLEFT;
        if (t && r) return HTTOPRIGHT;
        if (b && l) return HTBOTTOMLEFT;
        if (b && r) return HTBOTTOMRIGHT;
        if (l) return HTLEFT;
        if (r) return HTRIGHT;
        if (t) return HTTOP;
        if (b) return HTBOTTOM;
        if (p.Y < HeaderH) return HTCAPTION; // drag by the header
        return HTCLIENT;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; CloseRequested?.Invoke(); }
        else base.OnFormClosing(e);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); _fetchCts?.Cancel(); _np.TrackChanged -= OnTrackChangedAsync; }
        base.Dispose(disposing);
    }
}
