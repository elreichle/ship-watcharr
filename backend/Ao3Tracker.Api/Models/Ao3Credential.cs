namespace Ao3Tracker.Api.Models;

/// <summary>
/// Per-user AO3 login, stored encrypted at rest via Data Protection.
/// The raw password is never persisted in plaintext and never returned to the client.
/// Session state is kept separately from the credential so re-scraping doesn't
/// require re-authenticating with AO3 on every run.
/// </summary>
public class Ao3Credential
{
    public int Id { get; set; }

    public string UserId { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;

    public string Ao3Username { get; set; } = null!;

    /// <summary>Data-Protection-encrypted AO3 password.</summary>
    public string EncryptedPassword { get; set; } = null!;

    /// <summary>Data-Protection-encrypted AO3 session cookie, set after a successful login.</summary>
    public string? EncryptedSessionCookie { get; set; }

    public DateTimeOffset? SessionEstablishedAt { get; set; }
    public DateTimeOffset? SessionExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
