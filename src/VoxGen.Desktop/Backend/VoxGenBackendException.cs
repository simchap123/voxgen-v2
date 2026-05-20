using System;

namespace VoxGen.Desktop.Backend;

/// <summary>
/// Base type for every failure originating from the VoxGen managed backend. The overlay
/// and the calling code key off the concrete subtype to pick the right user-visible
/// message (PRD §13).
///
/// <list type="bullet">
///   <item><see cref="UnauthenticatedException"/> — 401, the caller refreshes + retries once.</item>
///   <item><see cref="TrialExpiredException"/> — 402, prompts upgrade.</item>
///   <item><see cref="QuotaExceededException"/> — 403, monthly/usage cap (PRD §16.3).</item>
///   <item><see cref="RateLimitedException"/> — 429, includes RetryAfter.</item>
///   <item><see cref="BackendUnavailableException"/> — 5xx or network failure.</item>
/// </list>
/// </summary>
public class VoxGenBackendException : Exception
{
    public VoxGenBackendException(string message) : base(message) { }
    public VoxGenBackendException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>HTTP 401. The session token is missing or expired — refresh and retry once.</summary>
public sealed class UnauthenticatedException : VoxGenBackendException
{
    public UnauthenticatedException(string message) : base(message) { }
}

/// <summary>HTTP 402. Trial ran out (day 31 or usage cap reached — PRD §16.2). Show upgrade path.</summary>
public sealed class TrialExpiredException : VoxGenBackendException
{
    public TrialExpiredException(string message) : base(message) { }
}

/// <summary>HTTP 403. Monthly fair-use cap hit (PRD §16.3). Distinct from rate limiting.</summary>
public sealed class QuotaExceededException : VoxGenBackendException
{
    public QuotaExceededException(string message) : base(message) { }
}

/// <summary>HTTP 429. Too many requests in a window. <see cref="RetryAfter"/> is populated when the response carried Retry-After.</summary>
public sealed class RateLimitedException : VoxGenBackendException
{
    public TimeSpan? RetryAfter { get; }
    public RateLimitedException(string message, TimeSpan? retryAfter) : base(message)
    {
        RetryAfter = retryAfter;
    }
}

/// <summary>HTTP 5xx, timeouts, DNS failures, connection refused. Surface "can't reach VoxGen" (PRD §13).</summary>
public sealed class BackendUnavailableException : VoxGenBackendException
{
    public BackendUnavailableException(string message) : base(message) { }
    public BackendUnavailableException(string message, Exception inner) : base(message, inner) { }
}
