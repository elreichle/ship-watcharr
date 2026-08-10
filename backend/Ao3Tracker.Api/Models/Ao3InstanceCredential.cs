namespace Ao3Tracker.Api.Models;

/// <summary>
/// The one AO3 account this deployment scrapes as, stored encrypted at rest via Data Protection.
///
/// Instance-level rather than per-user because scraped data is shared: one <c>Ship</c> is scraped
/// once for everyone following it, so "whose login does this scrape use" has no per-user answer.
/// See <see cref="Ao3Credential"/>, the per-user row this replaces.
///
/// The credential is the durable source of truth and the session is only a cache of it. A password
/// saved here stays saved — it does not expire, and losing the session cookie is not losing the
/// login. That split is why <see cref="EncryptedSessionCookie"/> can be cleared at any time without
/// anyone having to type a password again.
/// </summary>
public class Ao3InstanceCredential
{
    /// <summary>
    /// Always <see cref="SingletonId"/>. A check constraint enforces it, so "one account per
    /// deployment" is a fact about the table rather than a rule every caller has to remember.
    /// </summary>
    public int Id { get; set; } = SingletonId;

    /// <summary>The only key this table ever holds. See <see cref="Id"/>.</summary>
    public const int SingletonId = 1;

    public string Ao3Username { get; set; } = null!;

    /// <summary>Data-Protection-encrypted AO3 password. Never returned to a client.</summary>
    public string EncryptedPassword { get; set; } = null!;

    /// <summary>
    /// Data-Protection-encrypted AO3 session cookie, set after a successful login. Disposable: it
    /// exists so a scrape run does not have to re-authenticate with AO3 every time, which would put
    /// avoidable load on the archive.
    /// </summary>
    public string? EncryptedSessionCookie { get; set; }

    // DateTime (UTC), not DateTimeOffset: the SQLite provider can only translate equality
    // on DateTimeOffset columns, not ordering/range comparisons — see README.
    public DateTime? SessionEstablishedAt { get; set; }
    public DateTime? SessionExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
