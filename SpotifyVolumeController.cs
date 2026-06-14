using System.Diagnostics;
using System.Windows.Automation;
using NAudio.CoreAudioApi;

namespace SpotifyLinearVolume;

/// <summary>
/// Drives Spotify's OWN volume slider via UI Automation (the RangeValue pattern), so the change
/// is reflected everywhere — the phone, other Connect devices, and the Windows mixer all stay in
/// sync — instead of being a separate OS-level gain. The value is a float 0..1 (fine control) and
/// it is local (no Web API / OAuth / latency) and never patches the client, so it stays lossless-
/// and update-safe.
///
/// The Windows per-app session for Spotify is reset to 100% once, so this slider is the real knob.
/// The slider's RangeValue pattern is cached (the descendants walk is only paid on a cold/stale
/// cache); each <see cref="SetGain"/> after that is a ~1ms SetValue.
/// </summary>
public sealed class SpotifyVolumeController : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private RangeValuePattern? _cached;   // Spotify's volume slider, located lazily
    private bool _sessionReset;

    public bool IsSpotifyRunning
    {
        get
        {
            var procs = Process.GetProcessesByName("Spotify");
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
    }

    /// <summary>Set Spotify's own volume slider to the given value (0..1).</summary>
    public bool SetGain(float gain)
    {
        gain = Math.Clamp(gain, 0f, 1f);
        EnsureSessionFull(); // keep the per-app session at 100% so the slider is the true volume
        return TrySetValue(gain);
    }

    /// <summary>Spotify's current slider value (0..1), or null if it can't be read.</summary>
    public float? GetGain()
    {
        var rvp = Pattern();
        if (rvp == null) return null;
        try { return (float)rvp.Current.Value; }
        catch { _cached = null; return null; }
    }

    private bool TrySetValue(float gain)
    {
        var rvp = Pattern();
        if (rvp == null) return false;
        try { rvp.SetValue(gain); return true; }
        catch
        {
            _cached = null;             // element went stale (Spotify re-rendered) → relocate once
            rvp = Pattern();
            if (rvp == null) return false;
            try { rvp.SetValue(gain); return true; } catch { _cached = null; return false; }
        }
    }

    private RangeValuePattern? Pattern()
    {
        if (_cached != null) return _cached;
        try
        {
            IntPtr hwnd = FindSpotifyMainWindow();
            if (hwnd == IntPtr.Zero) return null;
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return null;

            var sliders = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider));
            foreach (AutomationElement s in sliders)
            {
                string name = s.Current.Name ?? "";
                if ((name.Contains("볼륨") || name.Contains("Volume", StringComparison.OrdinalIgnoreCase))
                    && s.TryGetCurrentPattern(RangeValuePattern.Pattern, out var pat))
                {
                    _cached = (RangeValuePattern)pat;
                    return _cached;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One-time: put Spotify's Windows session back to 100% so our slider is the real volume.</summary>
    private void EnsureSessionFull()
    {
        if (_sessionReset) return;
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            bool found = false;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (IsSpotify(s)) { s.SimpleAudioVolume.Volume = 1f; found = true; }
            }
            if (found) _sessionReset = true; // only latch once we've actually reset a live session
        }
        catch
        {
            // No render device / no Spotify session yet — try again on the next SetGain.
        }
    }

    private static IntPtr FindSpotifyMainWindow()
    {
        var procs = Process.GetProcessesByName("Spotify");
        try
        {
            foreach (var p in procs)
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            return IntPtr.Zero;
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    private static bool IsSpotify(AudioSessionControl session)
    {
        try
        {
            uint pid = session.GetProcessID;
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int)pid);
            return string.Equals(p.ProcessName, "Spotify", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _enumerator?.Dispose();
}
