namespace VoxGen.Desktop.Clipboard;

/// <summary>
/// Types text into the currently-focused window via synthetic Unicode keystrokes — used by live
/// dictation to append words as the user speaks (PRD §20 streaming, opt-in). Append-only; never
/// touches the clipboard. Must be called while the user's target window has focus.
/// </summary>
public interface IKeystrokeTyper
{
    void TypeText(string text);
}
