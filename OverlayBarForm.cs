using System.Runtime.InteropServices;

namespace SpotifyLinearVolume;

/// <summary>
/// A tiny volume bar that overlays Spotify's own volume slider — matched to its position and
/// size (found via UI Automation, off the UI thread) and following the Spotify window via a
/// move-event hook. The UIA query runs as a generation-guarded state machine so a stuck or
/// stale query can never leave the overlay permanently hidden.
/// </summary>
public sealed class OverlayBarForm : Form
{
    private const long ResizeDebounceMs = 100; // settle wait after a resize before re-locating (snappier reappear)
    private const int PopupCompressionMargin = 2;

    private readonly VolumeModel _model;
    private readonly VolumeBar _bar = new();

    private readonly System.Windows.Forms.Timer _presenceTimer = new() { Interval = 250 };
    private readonly System.Windows.Forms.Timer _resizeSettleTimer = new() { Interval = 75 };
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 130 };
    private readonly WinEventProc _winEventProc;

    private VolumePopupForm? _popup; // optional fly-out for comfortable control when the rail is tiny
    private bool _popupEnabled;
    private long _hoverLeftTick;
    private IntPtr _winEventHook;
    private uint _hookedPid;

    private IntPtr _spotifyHwnd;
    private uint _spotifyPid;
    private Size _spotifySize;
    private Size _relSize;        // window size that _relRect was computed for (rect is stale if it differs)
    private Rectangle? _relRect; // volume slider rect relative to the Spotify window top-left
    private bool _active;
    private volatile bool _querying;
    private bool _pendingRequery;
    private int _queryGeneration;
    private long _lastResizeTick;
    private int _resizeProbeBudget;

    private ContextMenuStrip? _menu;
    private readonly Form _menuOwner = new()
    {
        ShowInTaskbar = false,
        FormBorderStyle = FormBorderStyle.None,
        StartPosition = FormStartPosition.Manual,
        Size = new Size(1, 1),
        Opacity = 0,
    };

    public OverlayBarForm(VolumeModel model)
    {
        _model = model;
        _winEventProc = OnWinEvent;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black; // sit invisibly on Spotify's (near-black) playbar — no grey box
        ClientSize = new Size(120, 22);

        _bar.Dock = DockStyle.Fill;
        _bar.BackColor = Color.Black; // the bar fills the form, so this is what shows around the track
        _bar.EdgePad = SpotifyVolumeLocator.OverlayEdgePad; // track spans Spotify's rail; the outer pad hides the knob edge
        _bar.PositionPicked += pos => _model.SetPosition(pos);
        _bar.Set(_model.Position); // initialize from the current model (not 0)
        Controls.Add(_bar);

        _presenceTimer.Tick += (_, _) => PresenceTick();
        _resizeSettleTimer.Tick += (_, _) => ResizeSettleTick();
        _hoverTimer.Tick += (_, _) => HoverTick();
        _model.Changed += OnModelChanged;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    private void OnModelChanged()
    {
        if (!IsDisposed) _bar.Set(_model.Position);
    }

    public void SetActive(bool on)
    {
        _active = on;
        _queryGeneration++; // invalidate any in-flight query
        _pendingRequery = false;
        _relRect = null;
        _relSize = Size.Empty;
        _spotifyHwnd = IntPtr.Zero;
        _spotifyPid = 0;
        _spotifySize = Size.Empty;
        _lastResizeTick = 0;

        if (on)
        {
            if (!IsHandleCreated) { _ = Handle; } // ensure a native handle so BeginInvoke works before first Show
            _querying = false;
            _resizeSettleTimer.Stop();
            _presenceTimer.Start();
            if (_popupEnabled) _hoverTimer.Start();
            PresenceTick();
        }
        else
        {
            _presenceTimer.Stop();
            _resizeSettleTimer.Stop();
            _hoverTimer.Stop();
            HidePopup();
            UninstallHook();
            Hide();
        }
    }

    /// <summary>Enable/disable the hover fly-out popup (a roomy slider above the playbar).</summary>
    public void SetPopupEnabled(bool on)
    {
        _popupEnabled = on;
        if (on)
        {
            if (_active) _hoverTimer.Start();
        }
        else
        {
            _hoverTimer.Stop();
            HidePopup();
        }
    }

    private void HoverTick()
    {
        if (!_active || !_popupEnabled || IsDisposed) { _hoverTimer.Stop(); return; }

        // Keep the popup open whenever the cursor is on it — even if the overlay momentarily blinks
        // (e.g. Spotify reflows as we set its volume). Otherwise dragging the popup slider would
        // dismiss it mid-drag. Re-assert its position each tick so it tracks the overlay.
        bool overPopup = _popup?.ContainsCursor(6) ?? false;
        if (overPopup) { _hoverLeftTick = 0; ShowPopup(); return; }

        // Only fly out when the rail is too small to drag comfortably. On a normal-width rail,
        // hovering does nothing. Once open, staying on the popup keeps it open regardless of width.
        var hot = Bounds; hot.Inflate(4, 4);
        bool overOverlay = Visible && IsCompressedForPopup() && hot.Contains(Cursor.Position);
        if (overOverlay)
        {
            _hoverLeftTick = 0;
            ShowPopup();
        }
        else if (_popup is { Visible: true })
        {
            // grace period so crossing the overlay→popup gap (or a brief overlay blink) doesn't flicker it shut
            if (_hoverLeftTick == 0) _hoverLeftTick = Environment.TickCount64;
            else if (Environment.TickCount64 - _hoverLeftTick > 220) HidePopup();
        }
    }

    private void ShowPopup()
    {
        if (_popup == null || _popup.IsDisposed) _popup = new VolumePopupForm(_model);
        _popup.ShowAbove(Bounds);
    }

    private void UpdatePopup()
    {
        if (_popupEnabled && _popup is { Visible: true }) _popup.ShowAbove(Bounds);
    }

    private void HidePopup()
    {
        if (_popup is { IsDisposed: false, Visible: true }) _popup.Hide();
        _hoverLeftTick = 0;
    }

    private bool IsCompressedForPopup()
    {
        int railWidth = Math.Max(0, Width - SpotifyVolumeLocator.OverlayEdgePad * 2);
        return railWidth < SpotifyVolumeLocator.NormalRailWidth - PopupCompressionMargin;
    }

    /// <summary>
    /// Right-click menu. The overlay is WS_EX_NOACTIVATE, so a normal ContextMenuStrip won't
    /// dispatch reliably — show it manually from a tiny foreground-capable owner window.
    /// </summary>
    public void SetContextMenu(ContextMenuStrip menu)
    {
        _menu = menu;
        _bar.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || _menu == null) return;
            _menuOwner.Location = Cursor.Position;
            if (!_menuOwner.Visible) _menuOwner.Show();
            _menuOwner.Activate();
            _menu.Closed += HideOwnerOnce;
            _menu.Show(_menuOwner, _menuOwner.PointToClient(Cursor.Position));
        };
    }

    private void HideOwnerOnce(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        if (sender is ContextMenuStrip m) m.Closed -= HideOwnerOnce;
        _menuOwner.Hide();
    }

    private void PresenceTick()
    {
        if (!_active) return;

        bool ok = SpotifyWindowTracker.TryGetBounds(_spotifyHwnd, _spotifyPid, out var r);
        if (!ok)
        {
            _spotifyHwnd = SpotifyWindowTracker.FindWindow(out _spotifyPid);
            _relRect = null;
            ok = SpotifyWindowTracker.TryGetBounds(_spotifyHwnd, _spotifyPid, out r);
        }
        if (!ok) { UninstallHook(); if (Visible) Hide(); return; }

        if (_hookedPid != _spotifyPid) InstallHook();

        // (Re)locate the volume slider when we have no cached rect or the window resized.
        if (_relRect == null || _spotifySize != r.Size)
        {
            if (_spotifySize != r.Size)
                NoteSpotifySizeChanged(r.Size);
            else if (!IsResizeDebouncing(r.Size) && !_resizeSettleTimer.Enabled)
                QueryRelRectAsync(_spotifyHwnd, _spotifyPid);
        }
        Reposition(r);
    }

    private void NoteSpotifySizeChanged(Size size)
    {
        _spotifySize = size;
        _lastResizeTick = Environment.TickCount64;
        _relRect = null;
        _relSize = Size.Empty;
        _resizeProbeBudget = 6;
        if (Visible) Hide();
        if (!_resizeSettleTimer.Enabled) _resizeSettleTimer.Start();
    }

    private bool IsResizeDebouncing(Size size)
    {
        return size == _spotifySize && _lastResizeTick != 0
            && Environment.TickCount64 - _lastResizeTick < ResizeDebounceMs;
    }

    private void ResizeSettleTick()
    {
        if (!_active || IsDisposed) { _resizeSettleTimer.Stop(); return; }
        if (!SpotifyWindowTracker.TryGetBounds(_spotifyHwnd, _spotifyPid, out var r))
        {
            _resizeSettleTimer.Stop();
            if (Visible) Hide();
            return;
        }

        if (_spotifySize != r.Size)
        {
            NoteSpotifySizeChanged(r.Size);
            Reposition(r);
            return;
        }

        if (IsResizeDebouncing(r.Size))
        {
            Reposition(r);
            return;
        }

        bool currentRectMissing = _relRect == null || _relSize != r.Size;
        if (currentRectMissing || _resizeProbeBudget > 0)
        {
            if (_querying)
            {
                _pendingRequery = true;
                Reposition(r);
                return;
            }

            if (!currentRectMissing && Environment.TickCount64 - _lastResizeTick >= ResizeDebounceMs)
                _resizeProbeBudget--;
            QueryRelRectAsync(_spotifyHwnd, _spotifyPid);
            Reposition(r);
            return;
        }

        _resizeSettleTimer.Stop();
    }

    private void QueryRelRectAsync(IntPtr hwnd, uint pid)
    {
        if (_querying) { _pendingRequery = true; return; }
        _querying = true;
        int generation = _queryGeneration;
        try
        {
            Task.Run(() =>
            {
                Rectangle? rel = null;
                Size sizeAtQuery = Size.Empty;
                try
                {
                    if (SpotifyWindowTracker.TryGetBounds(hwnd, pid, out var winBefore))
                    {
                        var abs = SpotifyVolumeLocator.FindVolumeRect(hwnd);
                        if (abs is Rectangle a && SpotifyWindowTracker.TryGetBounds(hwnd, pid, out var winAfter)
                            && winAfter == winBefore)
                        {
                            rel = new Rectangle(a.X - winAfter.Left, a.Y - winAfter.Top, a.Width, a.Height);
                            sizeAtQuery = winAfter.Size;
                        }
                    }
                }
                catch { /* UIA can throw cross-process — treat as not-found */ }
                PostQueryResult(hwnd, pid, generation, rel, sizeAtQuery);
            });
        }
        catch
        {
            FinishQueryAndMaybeRetry();
        }
    }

    private void PostQueryResult(IntPtr hwnd, uint pid, int generation, Rectangle? rel, Size sizeAtQuery)
    {
        if (!IsHandleCreated || IsDisposed) { _querying = false; return; }
        try { BeginInvoke(() => ApplyRel(hwnd, pid, generation, rel, sizeAtQuery)); }
        catch { _querying = false; }
    }

    private void ApplyRel(IntPtr hwnd, uint pid, int generation, Rectangle? rel, Size sizeAtQuery)
    {
        if (!_active || generation != _queryGeneration || hwnd != _spotifyHwnd || pid != _spotifyPid)
        {
            FinishQueryAndMaybeRetry();
            return;
        }
        if (rel is Rectangle rr && SpotifyWindowTracker.TryGetBounds(_spotifyHwnd, _spotifyPid, out var cur))
        {
            if (cur.Size == sizeAtQuery && !IsResizeDebouncing(cur.Size))
            {
                _relRect = rr;
                _relSize = cur.Size;
                _spotifySize = cur.Size;
                Reposition(cur);
            }
            else
            {
                _pendingRequery = true; // window resized during the query → retry with fresh data
                if (!_resizeSettleTimer.Enabled) _resizeSettleTimer.Start();
            }
        }
        FinishQueryAndMaybeRetry();
    }

    // Central completion: always clears _querying and drains a pending requery from every exit path.
    private void FinishQueryAndMaybeRetry()
    {
        _querying = false;
        if (!_active || IsDisposed) { _pendingRequery = false; return; }
        if (_pendingRequery)
        {
            _pendingRequery = false;
            if (SpotifyWindowTracker.TryGetBounds(_spotifyHwnd, _spotifyPid, out var r))
            {
                if (_spotifySize != r.Size)
                    NoteSpotifySizeChanged(r.Size);
                if (IsResizeDebouncing(r.Size))
                {
                    if (!_resizeSettleTimer.Enabled) _resizeSettleTimer.Start();
                }
                else
                {
                    QueryRelRectAsync(_spotifyHwnd, _spotifyPid);
                }
            }
        }
    }

    private void Reposition(Rectangle win)
    {
        if (!_active || IsDisposed) { if (Visible) Hide(); return; }
        if (IsResizeDebouncing(win.Size)) { if (Visible) Hide(); return; }
        // Never show with a slider rect captured for a DIFFERENT window size (stale after resize).
        if (_relRect is not Rectangle rel || win.Size != _relSize) { if (Visible) Hide(); return; }

        int h = Math.Max(rel.Height, 18); // a touch taller than the 16px slider for easier grabbing
        // Hug the rail exactly: [X .. X+Width] ⊂ Spotify's reserved slider hit-area, so the overlay can
        // never spill onto the speaker icon (left) or the mini-player/fullscreen buttons (right), even
        // when the window is narrow and those gaps shrink. The knob self-clamps inside instead.
        int w = Math.Max(24, rel.Width);
        Bounds = new Rectangle(win.Left + rel.X, win.Top + rel.Y + (rel.Height - h) / 2, w, h);

        UpdatePopup();

        IntPtr above = GetWindow(_spotifyHwnd, GW_HWNDPREV);
        if (above != Handle)
            SetWindowPos(Handle, above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (!Visible) Show();
    }

    private void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_active || IsDisposed || hwnd != _spotifyHwnd) return;
        if (SpotifyWindowTracker.TryGetBounds(_spotifyHwnd, _spotifyPid, out var r))
        {
            if (idObject == 0 && idChild == 0 && _spotifySize != r.Size) // window resized → cached slider rect is stale until settle
            {
                NoteSpotifySizeChanged(r.Size);
            }
            Reposition(r);
        }
    }

    private void InstallHook()
    {
        UninstallHook();
        if (_spotifyPid == 0) return;
        _winEventHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _winEventProc, _spotifyPid, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _hookedPid = _winEventHook != IntPtr.Zero ? _spotifyPid : 0;
    }

    private void UninstallHook()
    {
        if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
        _hookedPid = 0;
    }

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, GW_HWNDPREV = 3;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 0x0002;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _active = false;
            _queryGeneration++;
            _presenceTimer.Stop();
            _resizeSettleTimer.Stop();
            _hoverTimer.Stop();
            _presenceTimer.Dispose();
            _resizeSettleTimer.Dispose();
            _hoverTimer.Dispose();
            _model.Changed -= OnModelChanged;
            _popup?.Dispose();
            UninstallHook();
            _menuOwner.Dispose();
        }
        base.Dispose(disposing);
    }
}
