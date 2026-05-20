namespace VoxGen.Desktop.Overlay;

public enum OverlayState
{
    /// <summary>Not visible.</summary>
    Hidden,

    /// <summary>Recording in progress.</summary>
    Recording,

    /// <summary>Audio captured; awaiting transcription.</summary>
    Transcribing,
}

/// <summary>
/// The on-screen recording indicator (PRD §8.10). This slice ships a minimal three-state pill;
/// the v1-faithful live-waveform overlay is a later polish pass. Kept behind an interface so the
/// <c>DictationController</c> stays WPF-free and unit-testable.
///
/// All members must be invoked on the UI thread — the controller marshals via IUiDispatcher.
/// </summary>
public interface IRecordingOverlay
{
    void SetState(OverlayState state);

    /// <summary>Show a brief error pill (e.g. no microphone) that auto-hides. Returns to Hidden afterward.</summary>
    void ShowError(string message);
}
