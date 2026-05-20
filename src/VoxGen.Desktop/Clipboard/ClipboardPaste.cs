using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using VoxGen.Desktop.Logging;
using VoxGen.Desktop.WindowDetection;

namespace VoxGen.Desktop.Clipboard;

/// <summary>
/// Paste pipeline (PRD §8.8). Snapshots the clipboard, places the final text on it, restores focus
/// to the window the hotkey captured, and synthesizes Ctrl+V via <c>SendInput</c>.
///
/// <para><b>The transcript is never lost (PRD §5.3, §8.8).</b> On any failure — clipboard locked,
/// <c>SendInput</c> rejected, target window gone — the text is left on the clipboard and the result is
/// <see cref="PasteOutcome.LeftOnClipboard"/> so the user can paste it manually. This method never throws.</para>
///
/// <para><b>Threading.</b> Must be called on an STA / UI thread — the WPF <see cref="System.Windows.Clipboard"/>
/// requires STA. The controller marshals here. We do not assert STA at runtime so a misuse can't crash the
/// app; instead a clipboard failure degrades gracefully to <see cref="PasteOutcome.LeftOnClipboard"/>.</para>
/// </summary>
public sealed class ClipboardPaste : IClipboardPaste
{
    private readonly ILogger _logger;

    // Setting the clipboard can transiently fail if another app holds the clipboard open
    // (e.g. a clipboard manager). Retry a few times with a short backoff.
    private const int ClipboardRetries = 5;
    private const int ClipboardRetryDelayMs = 30;

    public ClipboardPaste(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public PasteResult PasteInto(IntPtr targetWindow, string text)
    {
        text ??= string.Empty;

        // 1) Snapshot the current clipboard text (best-effort; may be empty or non-text).
        string? priorText = TryGetClipboardText();

        // 2) Put the final text on the clipboard. This is the safety net: if everything after this
        //    fails, the transcript is still on the clipboard for a manual paste.
        if (!TrySetClipboardText(text, out string? setError))
        {
            // We could not even set the clipboard — there is nothing further we can do, and the
            // transcript is NOT on the clipboard. Report failure so the caller surfaces a message
            // and keeps the recording. (The temp WAV is preserved upstream per §8.4.)
            _logger.Error("Paste failed: could not set clipboard text", new()
            {
                ["error"] = setError,
                ["textLength"] = text.Length,
            });
            return new PasteResult(PasteOutcome.LeftOnClipboard, setError ?? "Could not access the clipboard.");
        }

        // 3) Restore focus to the window we captured before any VoxGen UI appeared (PRD §8.5).
        bool focused = ForegroundWindow.RestoreForeground(targetWindow);
        if (!focused)
        {
            // The target window may have closed, or Windows refused the focus change. The text is on
            // the clipboard, so the user can paste manually — that's the correct, lossless fallback.
            _logger.Warning("Paste fell back to clipboard: could not restore foreground window", new()
            {
                ["targetWindow"] = targetWindow.ToInt64(),
            });
            return new PasteResult(PasteOutcome.LeftOnClipboard,
                "VoxGen couldn't focus the original window. Your text is on the clipboard — press Ctrl+V to paste.");
        }

        // 4) Synthesize Ctrl+V.
        if (!TrySendCtrlV(out string? sendError))
        {
            _logger.Warning("Paste fell back to clipboard: SendInput(Ctrl+V) failed", new()
            {
                ["error"] = sendError,
            });
            return new PasteResult(PasteOutcome.LeftOnClipboard,
                sendError ?? "VoxGen couldn't send the paste keystroke. Your text is on the clipboard.");
        }

        // 5) Prior-clipboard restore is deliberately NOT done.
        //    Restoring the user's previous clipboard contents would require waiting until the Ctrl+V
        //    above has actually been consumed by the target app. That timing is app-dependent and
        //    unbounded (slow apps, IME composition, focus-change debounce), so a restore on a fixed
        //    delay races the paste and risks pasting the OLD clipboard instead of the transcript —
        //    which would silently lose the user's words (violates §5.3 / §8.8). We log the prior
        //    contents' presence for diagnostics and leave the transcript on the clipboard, which is
        //    also the most useful thing to have there right after a dictation.
        _logger.Debug("Paste succeeded", new()
        {
            ["textLength"] = text.Length,
            ["hadPriorClipboardText"] = priorText is not null,
        });

        return new PasteResult(PasteOutcome.Pasted);
    }

    // ============ Clipboard helpers ============

    private string? TryGetClipboardText()
    {
        try
        {
            // ContainsText guards against non-text payloads (images, files) without throwing.
            return System.Windows.Clipboard.ContainsText()
                ? System.Windows.Clipboard.GetText()
                : null;
        }
        catch (Exception ex)
        {
            // Non-fatal — we only wanted this for an optional restore, which we don't perform anyway.
            _logger.Debug("Could not snapshot prior clipboard text", new() { ["error"] = ex.Message });
            return null;
        }
    }

    private bool TrySetClipboardText(string text, out string? error)
    {
        error = null;

        // An empty string is a legitimate (if odd) transcript; SetText throws on null/empty, so
        // route empties through SetDataObject which accepts them.
        for (int attempt = 1; attempt <= ClipboardRetries; attempt++)
        {
            try
            {
                if (text.Length == 0)
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.UnicodeText, string.Empty);
                    System.Windows.Clipboard.SetDataObject(data, copy: true);
                }
                else
                {
                    // copy:true so the data survives after our process — the target app reads it post-focus.
                    System.Windows.Clipboard.SetDataObject(text, copy: true);
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (attempt < ClipboardRetries)
                {
                    Thread.Sleep(ClipboardRetryDelayMs);
                }
            }
        }

        return false;
    }

    // ============ Synthetic Ctrl+V (SendInput) ============

    private bool TrySendCtrlV(out string? error)
    {
        error = null;
        try
        {
            // keydown Ctrl, keydown V, keyup V, keyup Ctrl.
            var inputs = new INPUT[4];

            inputs[0] = KeyboardInput(VK_CONTROL, keyUp: false);
            inputs[1] = KeyboardInput(VK_V, keyUp: false);
            inputs[2] = KeyboardInput(VK_V, keyUp: true);
            inputs[3] = KeyboardInput(VK_CONTROL, keyUp: true);

            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                int win32 = Marshal.GetLastWin32Error();
                error = $"SendInput injected {sent}/{inputs.Length} events (Win32 error {win32}). " +
                        "The target may be an elevated window VoxGen can't send input to.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static INPUT KeyboardInput(ushort virtualKey, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    // ---------- Win32 constants ----------

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    // ---------- Win32 structs ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    // Native INPUT's union (MOUSEINPUT | KEYBDINPUT | HARDWAREINPUT). We only inject keyboard
    // events, but the union must be sized to its LARGEST member (MOUSEINPUT) so each array element
    // has the correct native stride for SendInput. On 64-bit Windows MOUSEINPUT is 32 bytes
    // (LONG dx, LONG dy, DWORD mouseData, DWORD dwFlags, DWORD time = 20 bytes, then ULONG_PTR
    // dwExtraInfo 8-byte-aligned at offset 24 → 32). Explicit Size pads the union to that without
    // declaring unused fields (which would warn under TreatWarningsAsErrors). With the leading
    // DWORD `type` plus 8-byte alignment of the union, INPUT marshals to 40 bytes — matching native
    // on x64, which is what SendInput's cbSize (Marshal.SizeOf<INPUT>()) must equal.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
