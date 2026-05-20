using System;

namespace VoxGen.Desktop.Core;

/// <summary>
/// Marshals work onto the UI thread. Lets the <c>DictationController</c> remain WPF-free and
/// testable: production uses a WPF-Dispatcher-backed implementation, tests use a synchronous
/// pass-through that runs the action inline.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queue <paramref name="action"/> to run on the UI thread (fire-and-forget).</summary>
    void Post(Action action);
}
