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
    private static readonly Color Bg = Color.FromArgb(28, 27, 25);         // warm near-black (Claude-ish)
    private static Color Accent => Theme.Accent;                           // shared, user-customizable
    private static readonly Color TextActive = Color.FromArgb(245, 243, 239);
    private static readonly Color TextDim = Color.FromArgb(150, 145, 138);
    private const int HeaderH = 38;
    private const int Pad = 18;
    private const int HighlightLeadMs = 130; // small perceptual lead; the real-time sync now comes from extrapolating SMTC's position, not this

    private static readonly Font LineFont = new("Segoe UI Semibold", 13.5f);             // non-active lines: soft semibold
    private static readonly Font LineFontActive = new("Segoe UI", 13.5f, FontStyle.Bold); // current line: heavier, Spotify-style
    private static readonly Font HeaderFont = new("Segoe UI Semibold", 9.5f);
    private static readonly Font ArtistFont = new("Segoe UI", 8.5f);
    private static readonly Font StatusFont = new("Segoe UI", 11f);
    private static readonly Font EmojiFont = new("Segoe UI Emoji", 30f);
    private static readonly Font IconFont = new("Segoe MDL2 Assets", 11f);      // prev/next glyphs
    private static readonly StringFormat WrapFmt = new() { FormatFlags = 0 };
    private static readonly StringFormat CenterFmt = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    private readonly NowPlaying _np;
    private readonly Label _close = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 }; // ~25fps; OnTick only repaints when something moves
    private readonly SpotifyDock _dock;

    private LyricsResult _lyrics = LyricsResult.None;
    private NowPlaying.TrackInfo? _track;
    private string _status = "";
    private CancellationTokenSource? _fetchCts;

    private readonly List<(int line, float top, float height)> _rows = new();
    private int _layoutWidth = -1;
    private bool _layoutDirty = true;

    private int _activeIdx = -1;
    private int _hoverRow = -1;           // synced lyrics: line under the cursor (click-to-seek affordance)
    private bool _canSearch;              // not-found state → offer a one-click web search (the long tail lives on niche sites)
    private bool _searchHover;
    private RectangleF _searchBtnRect;
    private float _scroll, _targetScroll;
    private bool _userScrolled;          // plain lyrics: wheel-controlled
    private long _userScrollTick;

    private bool _albumTint = true;      // tint the backdrop from album art, Spotify-style
    private Color _artColor = Color.Empty;

    private bool _pinned;                 // pinned → stay up when Spotify is minimized; else drop with it
    private bool _pinHover;
    public event Action<bool>? PinnedChanged; // user clicked the pin → persist + mirror in the menu

    private int _transHover;              // 0=none, 1=prev, 2=play/pause, 3=next — transport bar, shown when pinned
    private bool _lastPlaying;            // so OnTick repaints the play/pause glyph when playback flips
    private int FooterH => _pinned ? 46 : 0; // transport strip reserved at the bottom while pinned

    public event Action? CloseRequested;
    public event Action<Point>? DockOffsetChanged; // user dragged while docked → persist the new offset
    public Func<string, long, CancellationToken, Task<string?>>? TrackIdProvider; // optional: (title, durationMs, ct) → exact Spotify track id
    public Func<CancellationToken, Task<NowPlaying.TrackInfo?>>? NextTrackProvider; // optional: next queued track → prefetch its lyrics

    public void SetDockOffset(Point? offset) => _dock.SetOffset(offset);
    public void SetPinned(bool pinned) { _pinned = pinned; _dock.SetHideWhenAbsent(!pinned); _layoutDirty = true; Invalidate(); }
    private void TogglePin() { _pinned = !_pinned; _dock.SetHideWhenAbsent(!_pinned); _layoutDirty = true; Invalidate(); PinnedChanged?.Invoke(_pinned); }
    public void SetAlbumTint(bool on) { if (_albumTint == on) return; _albumTint = on; Invalidate(); }

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
        // Not TopMost — the dock keeps it just above Spotify in the z-order so it follows Spotify's layer
        // but never floats over other apps you bring to the front.

        SetupClose();
        Controls.Add(_close);

        _timer.Tick += (_, _) => OnTick();
        _np.TrackChanged += OnTrackChangedAsync;
        _np.ArtColorChanged += OnArtColorChanged;
        _artColor = _np.ArtColor;

        // Dock default: sit just off Spotify's right edge, bottom-aligned (near the volume controls).
        _dock = new SpotifyDock(this, (r, size) => new Point(r.Right + 8, r.Bottom - size.Height));
        _dock.OffsetChanged += o => DockOffsetChanged?.Invoke(o);
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
        _close.BackColor = Color.Transparent; // composite over the painted backdrop (album gradient or warm-black)
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
            LyricsProvider.WarmUp(); // mint the Musixmatch token now so the first lookup isn't slowed by it
            _timer.Start();
            _dock.SetEnabled(true); // the dock shows the window (next to Spotify, or kept up when minimized) + follows it
            KickFetch(); // (re)load lyrics for whatever's playing now
        }
        else
        {
            _timer.Stop();
            _fetchCts?.Cancel();
            _dock.SetEnabled(false);
            Hide();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && _timer.Enabled) KickFetch(); // (re)shown beside Spotify → load the current track's lyrics
    }

    // ----- track / fetch -----
    private void OnTrackChangedAsync()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(KickFetch); } catch { }
    }

    private void OnArtColorChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => { _artColor = _np.ArtColor; if (_albumTint) Invalidate(); }); } catch { }
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
        _canSearch = false; _searchHover = false; _searchBtnRect = RectangleF.Empty;

        if (track == null || track.IsEmpty) { _status = Loc.T("재생 중인 곡이 없어요", "Nothing playing"); Invalidate(); return; }
        _status = Loc.T("가사 찾는 중…", "Finding lyrics…");
        Invalidate();

        var cts = new CancellationTokenSource();
        _fetchCts = cts;
        LyricsResult res;
        try
        {
            // Fast path: a prefetch (or earlier play) already cached this track under its plain title key →
            // show instantly, skipping the Spotify-API id round trip entirely. This is what makes it feel like Spotify.
            var peek = LyricsProvider.PeekCache(track.Key);
            if (peek is { Found: true })
            {
                res = peek;
            }
            else
            {
                if (TrackIdProvider != null)
                {
                    var id = await TrackIdProvider(track.Title, track.DurationMs, cts.Token);
                    if (!string.IsNullOrEmpty(id)) track = track with { SpotifyId = id }; // exact Spotify match (verified same track)
                }
                res = await LyricsProvider.GetAsync(track, cts.Token);
            }
        }
        catch { res = LyricsResult.None; }
        if (cts.IsCancellationRequested || IsDisposed || !ReferenceEquals(_fetchCts, cts)) return;

        _lyrics = res;
        _layoutDirty = true;
        _status = res.Instrumental ? Loc.T("연주곡 (가사 없음)", "Instrumental")
               : !res.Found ? Loc.T("가사를 찾지 못했어요", "No lyrics found")
               : "";
        _canSearch = (!res.Found && !res.Instrumental) || (res.Instrumental && res.Guess); // missing, or a *guessed* instrumental
        Invalidate();

        // Prefetch the next queued track's lyrics so the next change is near-instant (like Spotify prefetches).
        if (NextTrackProvider != null) _ = PrefetchNextAsync();
    }

    private async Task PrefetchNextAsync()
    {
        try
        {
            var next = await NextTrackProvider!(CancellationToken.None);
            if (next != null && !next.IsEmpty) _ = LyricsProvider.PrefetchAsync(next); // cache under BOTH id + title keys → instant on play
        }
        catch { }
    }

    // ----- tick: advance the synced highlight -----
    private void OnTick()
    {
        bool needPaint = false;
        if (_pinned && _np.IsPlaying != _lastPlaying) { _lastPlaying = _np.IsPlaying; needPaint = true; } // play/pause glyph

        if (_lyrics.Synced && _lyrics.Lines.Count > 0)
        {
            // resume auto-follow a few seconds after a manual peek (wheel scroll), like Spotify
            if (_userScrolled && Environment.TickCount64 - _userScrollTick > 4000) _userScrolled = false;

            _np.Resync();
            long pos = _np.PositionMs + HighlightLeadMs;
            int idx = -1;
            var lines = _lyrics.Lines;
            for (int i = 0; i < lines.Count; i++) { if (lines[i].TimeMs <= pos) idx = i; else break; }
            if (idx != _activeIdx) { _activeIdx = idx; _userScrolled = false; needPaint = true; }

            if (!_userScrolled && _rows.Count > 0)
            {
                float vpH = ClientSize.Height - HeaderH - FooterH - Pad;
                float centerY = _activeIdx >= 0 && _activeIdx < _rows.Count
                    ? _rows[_activeIdx].top + _rows[_activeIdx].height / 2f
                    : 0;
                _targetScroll = centerY - vpH / 2f;
            }
        }

        // Animate scroll toward target — runs for BOTH the synced auto-scroll AND plain wheel-scrolling.
        // (This used to sit after an early return for plain lyrics, so plain lyrics never scrolled at all.)
        if (Math.Abs(_targetScroll - _scroll) > 0.4f)
        {
            _scroll += (_targetScroll - _scroll) * 0.22f;
            if (Math.Abs(_targetScroll - _scroll) < 0.4f) _scroll = _targetScroll;
            needPaint = true;
        }

        if (needPaint) Invalidate();
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
        float vpH = ClientSize.Height - HeaderH - FooterH - Pad;
        float max = Math.Max(0, content - vpH + Pad);
        _targetScroll = Math.Clamp(_targetScroll, 0, max);
    }

    // ----- click-to-seek (synced lyrics): click a line → Spotify jumps there, like its own lyrics -----
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (PinHit().Contains(e.Location)) { TogglePin(); return; } // pin/unpin (keep up when Spotify is minimized)
        if (_pinned)
        {
            var (pv, pl, nx) = TransportRects();
            if (pv.Contains(e.Location)) { _np.SkipPrevious(); Invalidate(); return; }
            if (pl.Contains(e.Location)) { _np.TogglePlayPause(); Invalidate(); return; }
            if (nx.Contains(e.Location)) { _np.SkipNext(); Invalidate(); return; }
        }
        if (_canSearch && _searchBtnRect.Contains(e.Location)) { OpenWebSearch(); return; } // not-found → web search
        if (!_lyrics.Synced) return;
        int r = RowAt(e.Location);
        if (r < 0) return;
        var line = _lyrics.Lines[_rows[r].line];
        if (line.TimeMs < 0) return;
        _np.TrySeek(line.TimeMs);
        _activeIdx = _rows[r].line;
        _userScrolled = false;       // re-anchor auto-follow to the clicked line
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool onSearch = _canSearch && _searchBtnRect.Contains(e.Location);
        if (onSearch != _searchHover) { _searchHover = onSearch; Invalidate(); }

        bool onPin = PinHit().Contains(e.Location);
        if (onPin != _pinHover) { _pinHover = onPin; Invalidate(); }

        int th = 0;
        if (_pinned)
        {
            var (pv, pl, nx) = TransportRects();
            if (pv.Contains(e.Location)) th = 1; else if (pl.Contains(e.Location)) th = 2; else if (nx.Contains(e.Location)) th = 3;
        }
        if (th != _transHover) { _transHover = th; Invalidate(); }

        int r = (_lyrics.Synced && e.Y >= HeaderH) ? RowAt(e.Location) : -1;
        bool clickable = onPin || th > 0 || (r >= 0 && _lyrics.Lines[_rows[r].line].TimeMs >= 0) || onSearch;
        Cursor = clickable ? Cursors.Hand : Cursors.Default;
        if (r != _hoverRow) { _hoverRow = r; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = Cursors.Default;
        bool inv = _hoverRow != -1 || _searchHover || _pinHover || _transHover != 0;
        _hoverRow = -1; _searchHover = false; _pinHover = false; _transHover = 0;
        if (inv) Invalidate();
    }

    /// <summary>Row index at a client point (or -1). Mirrors the Y math in OnPaint.</summary>
    private int RowAt(Point p)
    {
        if (_rows.Count == 0 || p.Y < HeaderH || p.Y >= ClientSize.Height - FooterH) return -1;
        float baseY = HeaderH + Pad - _scroll;
        for (int r = 0; r < _rows.Count; r++)
        {
            float y = baseY + _rows[r].top;
            if (p.Y >= y && p.Y < y + _rows[r].height) return r;
        }
        return -1;
    }

    private void OpenWebSearch()
    {
        var t = _track;
        if (t == null) return;
        string q = Uri.EscapeDataString($"{t.Artist} {t.Title} " + Loc.T("가사", "lyrics"));
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://www.google.com/search?q={q}") { UseShellExecute = true }); }
        catch { }
    }

    // Coral pill button for the not-found state (the "send me to the web" affordance).
    private void DrawSearchButton(Graphics g, Rectangle vp)
    {
        string label = Loc.T("🔍  웹에서 검색", "🔍  Search the web");
        var sz = g.MeasureString(label, StatusFont);
        float bw = Math.Min(vp.Width - 2 * Pad, sz.Width + 44), bh = 36;
        float bx = vp.Left + (vp.Width - bw) / 2f, by = vp.Top + vp.Height / 2f + 6;
        _searchBtnRect = new RectangleF(bx, by, bw, bh);

        using var path = RoundedRect(_searchBtnRect, bh / 2f);
        if (_searchHover) { using var fill = new SolidBrush(Accent); g.FillPath(fill, path); }
        using (var pen = new Pen(Accent, 1.5f)) g.DrawPath(pen, path);
        using var tb = new SolidBrush(_searchHover ? Color.White : Accent);
        g.DrawString(label, StatusFont, tb, _searchBtnRect, CenterFmt);
    }

    private bool AlbumMode => _albumTint && !_artColor.IsEmpty;

    // Pin sits just left of the ✕, in the header strip. PinHit is a roomier target for the mouse.
    private RectangleF PinBox() => new(ClientSize.Width - 57, 7, 19, 23);
    private RectangleF PinHit() => RectangleF.Inflate(PinBox(), 6, 5);

    // Pin toggle: the crisp Segoe MDL2 pushpin glyph, rotated to point DOWN, on a soft "active" chip.
    private void DrawPin(Graphics g, RectangleF box, bool pinned, bool hover)
    {
        if (pinned || hover)
        {
            const float cs = 22f;
            var chip = new RectangleF(box.X + box.Width / 2f - cs / 2f, box.Y + box.Height / 2f - cs / 2f, cs, cs);
            Color chipC = pinned ? (AlbumMode ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(56, Accent))
                                 : Color.FromArgb(30, 255, 255, 255);
            using var cb = new SolidBrush(chipC);
            using var cp = RoundedRect(chip, 6f);
            g.FillPath(cb, cp);
        }

        Color icol = pinned ? (AlbumMode ? Color.White : Accent) : Color.FromArgb(hover ? 225 : 170, 178, 174, 168);
        using var b = new SolidBrush(icol);
        var st = g.Save();
        g.TranslateTransform(box.X + box.Width / 2f, box.Y + box.Height / 2f);
        g.RotateTransform(270f); // E718 points up by default \u2192 flip it to point down (tweak this angle if it ends up tilted)
        g.DrawString("\ue718", IconFont, b, new RectangleF(-box.Width / 2f, -box.Height / 2f, box.Width, box.Height), CenterFmt);
        g.Restore(st);
    }

    // Transport bar (prev / play-pause / next), shown only while pinned — Spotify may be minimized then.
    private (RectangleF prev, RectangleF play, RectangleF next) TransportRects()
    {
        float cy = ClientSize.Height - FooterH / 2f;
        float cx = ClientSize.Width / 2f;
        const float gap = 48f, s = 30f;
        RectangleF R(float x) => new(x - s / 2f, cy - s / 2f, s, s);
        return (R(cx - gap), R(cx), R(cx + gap));
    }

    private void DrawTransport(Graphics g)
    {
        var (pv, pl, nx) = TransportRects();
        float top = ClientSize.Height - FooterH;
        using (var sep = new Pen(Color.FromArgb(26, 255, 255, 255)))
            g.DrawLine(sep, 18, top, ClientSize.Width - 18, top);

        DrawGlyphBtn(g, "\uE892", pv, _transHover == 1); // previous
        DrawGlyphBtn(g, "\uE893", nx, _transHover == 3); // next

        // center: filled accent disc + hand-drawn play/pause (so the triangle's centroid sits dead-centre)
        float d = _transHover == 2 ? 31f : 29f;
        float dcx = pl.X + pl.Width / 2f, dcy = pl.Y + pl.Height / 2f;
        var disc = new RectangleF(dcx - d / 2f, dcy - d / 2f, d, d);
        using (var b = new SolidBrush(AlbumMode ? Color.White : Accent)) g.FillEllipse(b, disc);
        using (var gb = new SolidBrush(Color.FromArgb(238, 26, 24, 22)))
        {
            if (_np.IsPlaying)
            {
                const float bw = 3.2f, gp = 2.6f, hh = 6.2f;
                g.FillRectangle(gb, dcx - gp / 2f - bw, dcy - hh, bw, 2 * hh);
                g.FillRectangle(gb, dcx + gp / 2f, dcy - hh, bw, 2 * hh);
            }
            else
            {
                const float w = 9f, h = 6.6f; // base at dcx-w/3, apex at dcx+2w/3 \u2192 centroid exactly at dcx
                g.FillPolygon(gb, new[] { new PointF(dcx - w / 3f, dcy - h), new PointF(dcx - w / 3f, dcy + h), new PointF(dcx + 2 * w / 3f, dcy) });
            }
        }
    }

    private void DrawGlyphBtn(Graphics g, string glyph, RectangleF rect, bool hover)
    {
        Color col = hover ? (AlbumMode ? Color.White : Accent) : Color.FromArgb(212, 214, 210, 203);
        using var b = new SolidBrush(col);
        g.DrawString(glyph, IconFont, b, rect, CenterFmt);
    }

    // Derive a deep, slightly-muted backdrop shade from the album colour so light lyrics stay readable.
    private static Color Backdrop(Color c, float keep, int lift)
    {
        int Mix(int v) => Math.Clamp((int)(v * keep) + lift, 0, 255);
        return Color.FromArgb(Mix(c.R), Mix(c.G), Mix(c.B));
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ----- paint -----
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        if (AlbumMode)
        {
            using var bg = new LinearGradientBrush(ClientRectangle,
                Backdrop(_artColor, 0.30f, 12), Backdrop(_artColor, 0.14f, 8), LinearGradientMode.Vertical);
            g.FillRectangle(bg, ClientRectangle);
        }
        else g.Clear(Bg);

        // header
        using (var tb = new SolidBrush(TextActive))
        using (var sb = new SolidBrush(TextDim))
        {
            string title = _track?.Title ?? "Volumify";
            string artist = _track?.Artist ?? "";
            g.DrawString(Ellipsize(g, title, HeaderFont, ClientSize.Width - 96), HeaderFont, tb, 16, 8);
            if (artist.Length > 0)
                g.DrawString(Ellipsize(g, artist, ArtistFont, ClientSize.Width - 96), ArtistFont, sb, 16, 22);
        }
        DrawPin(g, PinBox(), _pinned, _pinHover);
        if (_pinned) DrawTransport(g);

        var vp = new Rectangle(0, HeaderH, ClientSize.Width, ClientSize.Height - HeaderH - FooterH);
        if (vp.Height < 10) return;

        if (_lyrics.Lines.Count == 0)
        {
            if (_lyrics.Instrumental) // cat at the piano 🎹🐈
            {
                using var eb = new SolidBrush(Color.FromArgb(210, 206, 200));
                using var ib = new SolidBrush(TextDim);
                bool guess = _lyrics.Guess;
                float midY = vp.Top + vp.Height / 2f;
                g.DrawString("🎹🐈", EmojiFont, eb, new RectangleF(Pad, midY - (guess ? 86 : 58), vp.Width - 2 * Pad, 54), CenterFmt);
                g.DrawString(guess ? Loc.T("연주곡인 듯 · 가사 없음", "Looks instrumental · no lyrics")
                                   : Loc.T("연주곡 · 가사 없음", "Instrumental · no lyrics"),
                    StatusFont, ib, new RectangleF(Pad, guess ? midY - 30 : midY + 6, vp.Width - 2 * Pad, 26), CenterFmt);
                if (guess && _canSearch) DrawSearchButton(g, vp); else _searchBtnRect = RectangleF.Empty;
                return;
            }
            using (var stb = new SolidBrush(TextDim))
                g.DrawString(_status, StatusFont, stb,
                    new RectangleF(Pad, vp.Top, vp.Width - 2 * Pad, _canSearch ? vp.Height - 28 : vp.Height), CenterFmt);
            if (_canSearch) DrawSearchButton(g, vp); else _searchBtnRect = RectangleF.Empty;
            return;
        }

        EnsureLayout(g, vp.Width - 2 * Pad);
        g.SetClip(new Rectangle(Pad, vp.Top + 2, vp.Width - 2 * Pad, vp.Height - 4));

        for (int r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            float y = vp.Top + Pad + row.top - _scroll;
            if (y + row.height < vp.Top || y > vp.Bottom) continue;

            bool album = AlbumMode;
            int alpha; Color col;
            if (_lyrics.Synced)
            {
                int d = Math.Abs(r - _activeIdx);
                bool act = r == _activeIdx;
                col = album ? Color.White : (act ? Accent : TextDim);     // album mode: bright-white sung line; else accent
                alpha = act ? 255 : d == 1 ? 170 : d == 2 ? 125 : 90;
                if (r == _hoverRow && !act) { col = album ? Color.White : Color.FromArgb(224, 220, 213); alpha = Math.Max(alpha, album ? 235 : 225); } // click-to-seek hint
            }
            else { col = album ? Color.White : Color.FromArgb(202, 197, 190); alpha = album ? 235 : 230; }

            // soft fade near the viewport edges
            float ly = y + row.height / 2f;
            float edge = Math.Min(ly - vp.Top, vp.Bottom - ly);
            if (edge < 40) alpha = (int)(alpha * Math.Clamp(edge / 40f, 0f, 1f));

            var font = (_lyrics.Synced && r == _activeIdx) ? LineFontActive : LineFont;
            using var b = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 255), col));
            g.DrawString(_lyrics.Lines[row.line].Text, font, b,
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
            if (text.Length == 0) h = LineFontActive.GetHeight(g) * 0.6f;
            else h = g.MeasureString(text, LineFontActive, Math.Max(10, width), WrapFmt).Height; // measure with the boldest weight so a line never re-wraps/clips when it becomes active
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
        const int WM_NCHITTEST = 0x0084, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
        if (m.Msg == WM_NCHITTEST)
        {
            long lp = m.LParam.ToInt64();
            var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
            m.Result = (IntPtr)HitTest(p);
            return;
        }
        if (m.Msg == WM_ENTERSIZEMOVE) _dock.NotifyDragStart();
        base.WndProc(ref m);
        if (m.Msg == WM_EXITSIZEMOVE) { _dock.NotifyDragEnd(); _layoutDirty = true; BoundsChanged?.Invoke(Bounds); Invalidate(); }
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
        if (PinHit().Contains(p)) return HTCLIENT; // the pin sits in the header strip but must take clicks, not drag
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
        if (disposing) { _timer.Stop(); _timer.Dispose(); _fetchCts?.Cancel(); _np.TrackChanged -= OnTrackChangedAsync; _np.ArtColorChanged -= OnArtColorChanged; _dock.Dispose(); }
        base.Dispose(disposing);
    }
}
