using System;

namespace VoxGen.Desktop.Clipboard;

public enum PasteOutcome
{
    /// <summary>Text was placed on the clipboard and successfully pasted into the target window.</summary>
    Pasted,

    /// <summary>Paste failed; the final text was left on the clipboard so the user can paste manually (PRD §8.8).</summary>
    LeftOnClipboard,
}

public sealed record PasteResult(PasteOutcome Outcome, string? Error = null);

/// <summary>
/// Paste pipeline (PRD §8.8). Implementations set the clipboard, restore focus to the window
/// the hotkey captured, and synthesize Ctrl+V. The transcript is <b>never lost</b>: on any
/// failure the text stays on the clipboard and the result is <see cref="PasteOutcome.LeftOnClipboard"/>.
///
/// Must be called on an STA / UI thread (WPF clipboard requirement) — the controller marshals here.
/// </summary>
public interface IClipboardPaste
{
    PasteResult PasteInto(IntPtr targetWindow, string text);
}
