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
    // Left as a placeholder ON PURPOSE: with no backend URL, the desktop runs in no-login
    // local-Whisper mode (download → run → talk, no account). The Vercel backend
    // (https://voxgenflow.vercel.app, serving /api/v2/transcribe + /api/v2/license) is ready to
    // turn on later by restoring this URL — once Supabase auth is set up for end users.
    public const string VoxGenBackendBaseUrl = "REPLACE_AT_BUILD";

    /// <summary>
    /// Supabase project URL (PRD §6.4). Used for auth REST calls
    /// (<c>{url}/auth/v1/token</c>, <c>{url}/auth/v1/signup</c>, etc.).
    /// </summary>
    public const string SupabaseUrl = "https://xsdngjfnsszulezxvsjd.supabase.co";

    /// <summary>
    /// Supabase publishable (anon) key — required as the <c>apikey</c> header for every
    /// Supabase REST call. Safe to ship in the client by Supabase's design (RLS protects data).
    /// </summary>
    public const string SupabaseAnonKey = "sb_publishable_4vg9iXKj_H0Hzsxd1bGjQA_l1-Y1H2X";
}
