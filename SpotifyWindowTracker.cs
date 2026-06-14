using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SpotifyLinearVolume;

/// <summary>Locates and tracks the Spotify main window so the control panel can dock to it.</summary>
public static class SpotifyWindowTracker
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// Expensive: enumerate Spotify processes and return the largest visible, non-minimized
    /// player window (deterministic), or Zero if none. Call rarely and cache the handle.
    /// </summary>
    public static IntPtr FindWindow(out uint processId)
    {
        processId = 0;
        var procs = Process.GetProcessesByName("Spotify");
        try
        {
            var pids = new HashSet<uint>();
            foreach (var p in procs) pids.Add((uint)p.Id);

            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            uint bestPid = 0;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (!pids.Contains(pid) || GetClassName(h) != "Chrome_WidgetWin_1") return true;

                if (GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top)
                {
                    long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area > bestArea) { bestArea = area; best = h; bestPid = pid; }
                }
                return true;
            }, IntPtr.Zero);
            processId = bestPid;
            return best;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    /// <summary>Cheap: current bounds of a known handle; false if gone / minimized / hidden.</summary>
    public static bool TryGetBounds(IntPtr hWnd, uint expectedProcessId, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd) || IsIconic(hWnd) || !IsWindowVisible(hWnd))
            return false;
        // Guard against HWND reuse: the handle must still belong to the Spotify process we found.
        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid != expectedProcessId) return false;
        if (GetWindowRect(hWnd, out var r) && r.Right > r.Left && r.Bottom > r.Top)
        {
            bounds = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return true;
        }
        return false;
    }

    private static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        _ = GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
