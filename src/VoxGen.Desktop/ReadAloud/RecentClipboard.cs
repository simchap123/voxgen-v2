using System;
using System.Runtime.InteropServices;

namespace VoxGen.Desktop.ReadAloud;

/// <summary>
/// Notices when another app puts text on the clipboard, so a quick tap can read "what I just
/// highlighted/copied" — apps that draw their own highlight (Claude Code in a terminal) only hand
/// it over by copying. VoxGen's own clipboard writes (dictation paste, the Copy button) are ignored.
/// Win32 only, so it works from the background hover thread (no STA needed).
/// </summary>
public sealed class RecentClipboard
{
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);

    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    private readonly object _lock = new();
    private uint _seenSeq = GetClipboardSequenceNumber();
    private uint _changedSeq;
    private uint _takenSeq;
    private DateTime _changedAt = DateTime.MinValue;

    /// <summary>Call often (each hover poll). Records when another app last changed the clipboard.</summary>
    public void Poll()
    {
        lock (_lock)
        {
            var seq = GetClipboardSequenceNumber();
            if (seq == _seenSeq) return;
            _seenSeq = seq;

            var owner = GetClipboardOwner();
            if (owner != IntPtr.Zero && GetWindowThreadProcessId(owner, out var pid) != 0 && pid == OwnPid) return;
            _changedSeq = seq;
            _changedAt = DateTime.UtcNow;
        }
    }

    /// <summary>The clipboard text if another app copied it within <paramref name="window"/> and it
    /// hasn't been read yet. Each copy is read once, so later taps go back to hover.</summary>
    public string? TakeRecent(TimeSpan window)
    {
        Poll();
        lock (_lock)
        {
            if (_changedSeq == 0 || _changedSeq == _takenSeq || DateTime.UtcNow - _changedAt > window) return null;
            if (GetClipboardSequenceNumber() != _changedSeq) return null; // changed again since (by us)
            _takenSeq = _changedSeq;
        }
        return ReadText();
    }

    private static string? ReadText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var h = GetClipboardData(CF_UNICODETEXT);
                    if (h == IntPtr.Zero) return null;
                    var p = GlobalLock(h);
                    if (p == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(p); }
                    finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
            }
            System.Threading.Thread.Sleep(20); // another app holds the clipboard
        }
        return null;
    }
}
