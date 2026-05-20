using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Clipboard;

/// <summary>
/// Types Unicode text into the focused window via Win32 <c>SendInput</c> with
/// <c>KEYEVENTF_UNICODE</c> (no virtual-key mapping, so it works regardless of keyboard layout).
/// Used by live dictation to append words as they're spoken. Never throws.
/// </summary>
public sealed class KeystrokeTyper : IKeystrokeTyper
{
    private readonly ILogger _logger;

    public KeystrokeTyper(ILogger logger) => _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            var inputs = BuildUnicodeInputs(text);
            if (inputs.Length == 0) return;
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                _logger.Warning("SendInput typed fewer events than expected", new()
                {
                    ["sent"] = sent,
                    ["expected"] = inputs.Length,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Keystroke typing failed", new() { ["error"] = ex.Message });
        }
    }

    private static INPUT[] BuildUnicodeInputs(string text)
    {
        var list = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            list.Add(MakeUnicode(c, keyUp: false));
            list.Add(MakeUnicode(c, keyUp: true));
        }
        return list.ToArray();
    }

    private static INPUT MakeUnicode(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0u),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public HARDWAREINPUT hi;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
