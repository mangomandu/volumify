using System.Threading;

namespace SpotifyLinearVolume;

/// <summary>
/// Shared volume state (position + curve p) and the single source of truth that the tray menu,
/// overlay, popup and control panel all read from and mutate. Raising <see cref="Changed"/> keeps
/// every surface in sync.
///
/// The write to Spotify's slider goes through UI Automation, which can occasionally be slow (a
/// re-locate when Spotify re-renders). So <see cref="Changed"/> fires first — every UI surface
/// updates instantly — and the actual write runs on a dedicated background thread that always
/// applies the latest value. Dragging the overlay or popup therefore never stutters the UI.
/// </summary>
public sealed class VolumeModel : IDisposable
{
    private readonly SpotifyVolumeController _controller = new();

    private readonly Thread _pushThread;
    private readonly AutoResetEvent _pushSignal = new(false);
    private readonly object _pushGate = new();
    private float _pushTarget;
    private bool _pushPending;
    private volatile bool _disposed;
    private volatile bool _sessionFound;
    private volatile float _externalGain = -1f; // latest Spotify slider value seen by the background poll (-1 = none)

    private const int PollIntervalMs = 200;
    private const float SyncEpsilon = 0.005f;

    public float Position { get; private set; } = 0.5f;
    public float P { get; private set; } = 1f;
    public bool SessionFound => _sessionFound;

    public float Gain => VolumeCurve.Gain(Position, P);

    public event Action? Changed;

    public VolumeModel(float initialP)
    {
        P = float.IsFinite(initialP) && initialP > 0f ? initialP : 1f;
        var g = _controller.GetGain();
        if (g.HasValue)
        {
            Position = VolumeCurve.PositionFromGain(g.Value, P);
            _sessionFound = true;
        }

        _pushThread = new Thread(PushLoop) { IsBackground = true, Name = "SpotifyVolumePush" };
        _pushThread.Start();
    }

    public void Nudge(float delta) => SetPosition(Position + delta);

    public void SetPosition(float position)
    {
        Position = float.IsFinite(position) ? Math.Clamp(position, 0f, 1f) : 0f;
        Apply();
    }

    public void SetP(float p)
    {
        // Guard against non-finite / <= 0 p so the stored P (read directly by the graph painter)
        // can never produce NaN/Infinity points.
        p = float.IsFinite(p) && p > 0f ? p : 1f;

        // Keep the resulting gain continuous so the volume doesn't jump when the curve strength
        // changes — only the shape (and where the marker sits) moves.
        float gain = VolumeCurve.Gain(Position, P);
        P = p;
        Position = VolumeCurve.PositionFromGain(gain, P);
        Apply();
    }

    // Update every UI surface immediately, then hand the latest gain to the background pusher so a slow
    // UI-Automation write to Spotify's slider can never stutter the overlay/popup while you drag.
    private void Apply()
    {
        Changed?.Invoke();
        lock (_pushGate) { _pushTarget = Gain; _pushPending = true; }
        _pushSignal.Set();
    }

    private void PushLoop()
    {
        while (true)
        {
            _pushSignal.WaitOne(PollIntervalMs); // wake on a queued write, else every PollIntervalMs to poll
            if (_disposed) return;

            float? toPush = null;
            lock (_pushGate)
            {
                if (_pushPending) { toPush = _pushTarget; _pushPending = false; } // collapse a burst to the latest
            }

            if (toPush.HasValue)
            {
                _sessionFound = _controller.SetGain(toPush.Value);
                _externalGain = toPush.Value; // we own this value now; a stale poll mustn't snap us back to it
                continue;
            }

            // No queued write → read Spotify's slider so an external change (its own bar, a hotkey, the
            // phone) can be folded into our model by PumpExternal on the UI thread. All UI Automation
            // access stays on this one thread, so there's no cross-thread contention on the controller.
            var g = _controller.GetGain();
            if (g.HasValue) { _sessionFound = true; _externalGain = g.Value; }
        }
    }

    /// <summary>
    /// Fold the latest externally-observed Spotify volume into the model. Call on the UI thread (a tray
    /// timer) so moving Spotify's own slider / hotkeys / the phone updates every Volumify surface too.
    /// </summary>
    public void PumpExternal()
    {
        lock (_pushGate) { if (_pushPending) return; } // a write we just queued wins over a stale reading
        float g = _externalGain;
        if (g < 0f || Math.Abs(g - Gain) <= SyncEpsilon) return; // nothing polled yet, or already matches us
        Position = VolumeCurve.PositionFromGain(g, P);
        Changed?.Invoke(); // refresh UI only — Spotify is already at this value, so don't write it back
    }

    public void Dispose()
    {
        _disposed = true;
        _pushSignal.Set();
        _pushThread.Join(500);
        _controller.Dispose();
        _pushSignal.Dispose();
    }
}
