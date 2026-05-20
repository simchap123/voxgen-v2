using System.Collections.Generic;

namespace VoxGen.Desktop.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Local structured logger. PRD §10 rule 10 — every settings read/write failure is logged.
/// Implementations must never throw; logging must not crash the app.
/// </summary>
public interface ILogger
{
    void Log(LogLevel level, string message, IReadOnlyDictionary<string, object?>? context = null);
}

public static class LoggerExtensions
{
    public static void Debug(this ILogger l, string m, Dictionary<string, object?>? ctx = null) =>
        l.Log(LogLevel.Debug, m, ctx);

    public static void Info(this ILogger l, string m, Dictionary<string, object?>? ctx = null) =>
        l.Log(LogLevel.Info, m, ctx);

    public static void Warning(this ILogger l, string m, Dictionary<string, object?>? ctx = null) =>
        l.Log(LogLevel.Warning, m, ctx);

    public static void Error(this ILogger l, string m, Dictionary<string, object?>? ctx = null) =>
        l.Log(LogLevel.Error, m, ctx);
}
