namespace Caisson.Ingestion.Security;

/// <summary>
/// A resolved GitHub credential (story #172, Task #205). Wraps the secret token so it can never be logged or
/// serialized in plaintext: <see cref="ToString"/> is masked and the type carries no public getter that
/// exposes the raw value except <see cref="Reveal"/>, which callers use only at the moment they set the HTTP
/// <c>Authorization</c> header. PAT-first for v1; a future GitHub App provider mints an installation token and
/// returns the same wrapper, so no call site changes (story Q3).
/// </summary>
public sealed class GitHubCredential
{
    private readonly string _token;

    /// <summary>Creates a credential wrapper around a non-empty bearer token.</summary>
    public GitHubCredential(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        _token = token;
    }

    /// <summary>Returns the raw token. Call ONLY when setting the Authorization header — never log the result.</summary>
    public string Reveal() => _token;

    /// <summary>Always returns a fixed mask so an accidental log/interpolation never leaks the token.</summary>
    public override string ToString() => "GitHubCredential(***redacted***)";
}
