using Windows.Foundation;
using Windows.Media.Control;
using WinSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using WinManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace Volumify;

/// <summary>
/// Reads "what's playing" from Windows' System Media Transport Controls — the OS now-playing API that
/// Spotify already reports to (media flyout, lock screen, hardware media keys). No Spotify API, no
/// patching. Exposes the current track and a live, interpolated playback position so lyrics can sync.
///
/// WinRT events fire on a thread-pool thread, so <see cref="TrackChanged"/>/<see cref="Tick"/> may be
/// raised off the UI thread — consumers must marshal. Position reads are lock-guarded and safe to call
/// from the UI timer.
/// </summary>
public sealed class NowPlaying : IDisposable
{
    public sealed record TrackInfo(string Artist, string Title, string Album, long DurationMs, string SpotifyId = "")
    {
        public string Key => (SpotifyId.Length > 0 ? "id:" + SpotifyId : Artist + "" + Title).ToLowerInvariant();
        public bool IsEmpty => Artist.Length == 0 && Title.Length == 0;
    }

    private WinManager? _mgr;
    private WinSession? _session;

    private readonly object _gate = new();
    private long _baseMs, _baseTick, _durMs;
    private volatile bool _playing;
    private volatile bool _disposed;

    // Stored delegates so WinRT add/remove pair up correctly (method groups would create new instances).
    private readonly TypedEventHandler<WinSession, MediaPropertiesChangedEventArgs> _onMedia;
    private readonly TypedEventHandler<WinSession, PlaybackInfoChangedEventArgs> _onPlayback;
    private readonly TypedEventHandler<WinSession, TimelinePropertiesChangedEventArgs> _onTimeline;

    public TrackInfo? Current { get; private set; }
    public bool IsPlaying => _playing;

    /// <summary>Live playback position (ms), interpolated from the last SMTC timeline report.</summary>
    public long PositionMs
    {
        get
        {
            lock (_gate)
            {
                long p = _baseMs + (_playing ? Environment.TickCount64 - _baseTick : 0);
                return _durMs > 0 ? Math.Clamp(p, 0, _durMs) : Math.Max(0, p);
            }
        }
    }

    public long DurationMs { get { lock (_gate) return _durMs; } }

    public event Action? TrackChanged; // a different song is now current
    public event Action? Tick;         // play/pause/seek changed

    public NowPlaying()
    {
        _onMedia = (s, _) => RefreshMedia(s);
        _onPlayback = (s, _) => { RefreshPlayback(s); RefreshTimeline(s); Tick?.Invoke(); };
        _onTimeline = (s, _) => { RefreshTimeline(s); Tick?.Invoke(); };
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            _mgr = await WinManager.RequestAsync();
            if (_disposed) return;
            _mgr.SessionsChanged += (_, _) => RehookSafe();
            RehookSafe();
        }
        catch { /* SMTC unavailable (very old Windows) — Current stays null */ }
    }

    private void RehookSafe() { try { Rehook(); } catch { } }

    private void Rehook()
    {
        if (_mgr == null || _disposed) return;

        WinSession? spot = null;
        foreach (var s in _mgr.GetSessions())
            if ((s.SourceAppUserModelId ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase)) { spot = s; break; }

        if (ReferenceEquals(spot, _session)) { if (spot != null) RefreshAll(spot); return; }

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= _onMedia;
            _session.PlaybackInfoChanged -= _onPlayback;
            _session.TimelinePropertiesChanged -= _onTimeline;
        }
        _session = spot;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += _onMedia;
            _session.PlaybackInfoChanged += _onPlayback;
            _session.TimelinePropertiesChanged += _onTimeline;
            RefreshAll(_session);
        }
        else if (Current != null) { Current = null; TrackChanged?.Invoke(); }
    }

    private void RefreshAll(WinSession s) { RefreshPlayback(s); RefreshTimeline(s); RefreshMedia(s); }

    private void RefreshPlayback(WinSession s)
    {
        try { _playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch { }
    }

    private void RefreshTimeline(WinSession s)
    {
        try
        {
            var tl = s.GetTimelineProperties();
            lock (_gate)
            {
                _baseMs = (long)tl.Position.TotalMilliseconds;
                _baseTick = Environment.TickCount64;
                _durMs = (long)tl.EndTime.TotalMilliseconds;
            }
        }
        catch { }
    }

    private async void RefreshMedia(WinSession s)
    {
        try
        {
            var mp = await s.TryGetMediaPropertiesAsync();
            if (mp == null || _disposed) return;
            var t = new TrackInfo(mp.Artist ?? "", mp.Title ?? "", mp.AlbumTitle ?? "", DurationMs);
            if (Current == null || Current.Key != t.Key)
            {
                Current = t;
                TrackChanged?.Invoke();
            }
        }
        catch { }
    }

    /// <summary>Re-anchor the interpolated position from the latest SMTC timeline (cheap; call from a timer).</summary>
    public void Resync() { var s = _session; if (s != null) RefreshTimeline(s); }

    /// <summary>
    /// Seek the active session to <paramref name="ms"/> — used by click-to-seek on synced lyrics.
    /// Goes through SMTC, so it drives Spotify's own transport (and syncs to phone/Connect). Position
    /// is optimistically re-anchored so the highlight jumps immediately; Spotify's next timeline event corrects it.
    /// </summary>
    public async void TrySeek(long ms)
    {
        var s = _session;
        if (s == null) return;
        long dur; lock (_gate) dur = _durMs;
        if (dur > 0) ms = Math.Clamp(ms, 0, dur);
        try
        {
            bool ok = await s.TryChangePlaybackPositionAsync(ms * 10000L); // ms → 100-ns ticks
            if (ok) lock (_gate) { _baseMs = ms; _baseTick = Environment.TickCount64; }
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= _onMedia;
                _session.PlaybackInfoChanged -= _onPlayback;
                _session.TimelinePropertiesChanged -= _onTimeline;
            }
        }
        catch { }
    }
}
