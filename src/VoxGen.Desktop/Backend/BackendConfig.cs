namespace VoxGen.Desktop.Backend;

/// <summary>
/// Build/deploy-time configuration for the services VoxGen talks to.
///
/// These values are NOT committed real — the constants below are placeholders that get
/// substituted at build/packaging time (e.g. via an env-var-driven MSBuild target that
/// rewrites this file, or via embedded resources). Keep them as <see cref="string"/>
/// constants so the rest of the code can reference them without a config-loading dance.
///
/// PRD §6.2 — Supabase is accessed via REST (HttpClient), never via the Supabase SDK.
/// PRD §9.1 — the VoxGen backend is the only thing that holds transcription keys; the
/// desktop app holds none.
/// </summary>
public static class BackendConfig
{
    /// <summary>
    /// Base URL for the VoxGen-managed backend (PRD §9). Endpoints live under this,
    /// e.g. <c>{base}/v1/transcribe</c>, <c>{base}/v1/license</c>.
    /// </summary>
    public const string VoxGenBackendBaseUrl = "REPLACE_AT_BUILD";

    /// <summary>
    /// Supabase project URL (PRD §6.4). Used for auth REST calls
    /// (<c>{url}/auth/v1/token</c>, <c>{url}/auth/v1/signup</c>, etc.).
    /// </summary>
    public const string SupabaseUrl = "REPLACE_AT_BUILD";

    /// <summary>
    /// Supabase anon (publishable) key — required as the <c>apikey</c> header for every
    /// Supabase REST call. Not a secret in the sense an API key is; safe to ship in the
    /// client per Supabase's design. Still substituted at build time so dev and prod
    /// values don't get mixed up.
    /// </summary>
    public const string SupabaseAnonKey = "REPLACE_AT_BUILD";
}
