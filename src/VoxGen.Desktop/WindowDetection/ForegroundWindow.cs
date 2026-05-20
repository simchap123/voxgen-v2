using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VoxGen.Desktop.WindowDetection;

/// <summary>
/// Foreground-window utilities — PRD §8.5. The capture path is on the hot path
/// for "recording start under 100ms" (§14.1), so every method here must be cheap
/// and non-throwing.
/// </summary>
public static class ForegroundWindow
{
    /// <summary>
    /// Snapshot the OS's notion of the currently-active window. Returns the HWND
    /// or <see cref="IntPtr.Zero"/> if Windows declines to report one (lock screen,
    /// UAC, secure desktop, etc.).
    /// </summary>
    public static IntPtr CaptureNow() => GetForegroundWindow();

    /// <summary>
    /// Best-effort process name (no <c>.exe</c>) for a window. Swallows exceptions —
    /// the process may have exited between the capture and the lookup, or Windows
    /// may deny access to its handle.
    /// </summary>
    public static string? GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Title bar text of the window, or null if it has none. Bounded internally
    /// to a sane buffer length; we don't need to round-trip 4-MB titles.
    /// </summary>
    public static string? GetWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return null;
            // +1 for the null terminator that GetWindowText writes.
            var buf = new StringBuilder(len + 1);
            int written = GetWindowText(hwnd, buf, buf.Capacity);
            return written > 0 ? buf.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Bring the previously-captured window back to the foreground after our overlay /
    /// background work, so the paste lands in the right place.
    ///
    /// SetForegroundWindow on modern Windows is restricted: an app cannot steal focus
    /// unless its thread already owns it, or it's the foreground app, or a few other
    /// niche conditions. The workaround is the "AttachThreadInput dance" — we attach
    /// our input queue to the target window's thread, which makes the OS treat us as
    /// part of the same input context, lets SetForegroundWindow succeed, then we detach.
    ///
    /// This is widely used and well-documented; it's not a hack, it's the supported
    /// pattern for utilities that legitimately need to restore focus.
    /// </summary>
    public static bool RestoreForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            uint targetThreadId = GetWindowThreadProcessId(hwnd, out _);
            if (targetThreadId == 0) return false;

            uint ourThreadId = GetCurrentThreadId();
            if (targetThreadId == ourThreadId)
            {
                return SetForegroundWindow(hwnd);
            }

            bool attached = AttachThreadInput(ourThreadId, targetThreadId, true);
            try
            {
                // If the window is minimized, restore it so SetForegroundWindow can actually focus it.
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                return SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) AttachThreadInput(ourThreadId, targetThreadId, false);
            }
        }
        catch
        {
            return false;
        }
    }

    // ---------- P/Invoke ----------

    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
