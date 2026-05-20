using System.Collections.Generic;
using VoxGen.Desktop.Logging;

namespace VoxGen.Desktop.Tests;

/// <summary>No-op logger so tests don't write to %APPDATA%.</summary>
internal sealed class TestLogger : ILogger
{
    public void Log(LogLevel level, string message, IReadOnlyDictionary<string, object?>? context = null)
    {
        // intentionally empty
    }
}
