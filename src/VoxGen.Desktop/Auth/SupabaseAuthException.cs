using System;
using System.Net;

namespace VoxGen.Desktop.Auth;

/// <summary>
/// Thrown by <see cref="SupabaseAuth"/> when Supabase returns a non-2xx response.
/// Carries the parsed error message and status code so the UI can show the user
/// something specific (e.g. "Invalid login credentials" vs "Email already registered").
/// </summary>
public sealed class SupabaseAuthException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }

    public SupabaseAuthException(HttpStatusCode statusCode, string message, string? errorCode = null)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public SupabaseAuthException(HttpStatusCode statusCode, string message, Exception inner)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
