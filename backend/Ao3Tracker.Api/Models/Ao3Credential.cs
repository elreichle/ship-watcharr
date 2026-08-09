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

    // DateTime (UTC), not DateTimeOffset: the SQLite provider can only translate equality
    // on DateTimeOffset columns, not ordering/range comparisons — see README.
    public DateTime? SessionEstablishedAt { get; set; }
    public DateTime? SessionExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
