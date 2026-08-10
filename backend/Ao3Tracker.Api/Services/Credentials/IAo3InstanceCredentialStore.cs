namespace Ao3Tracker.Api.Services.Credentials;

/// <summary>
/// Reads/writes the one AO3 account this deployment scrapes as, and its cached session.
/// Implementations own encryption at rest; callers never see ciphertext, and the raw password is
/// never exposed again once stored.
///
/// The same shape as <see cref="IAo3CredentialStore"/> minus the user id — which is the whole
/// point. See <see cref="Models.Ao3InstanceCredential"/> for why the login is instance-level.
/// </summary>
public interface IAo3InstanceCredentialStore
{
    /// <summary>Whether an account is configured at all. This is what gates scraping.</summary>
    Task<bool> HasCredentialAsync(CancellationToken ct = default);

    /// <summary>The stored AO3 username, or null when none is configured. Safe to show a client.</summary>
    Task<string?> GetUsernameAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores or replaces the AO3 username and password, encrypting the password before it is
    /// persisted. Replacing the password discards any cached session, which was established with
    /// the old one.
    /// </summary>
    Task SetCredentialAsync(string ao3Username, string ao3Password, CancellationToken ct = default);

    Task RemoveCredentialAsync(CancellationToken ct = default);

    /// <summary>Decrypted, for the scraper to log in with. Never let this leave the backend.</summary>
    Task<(string Ao3Username, string Ao3Password)?> GetDecryptedCredentialAsync(CancellationToken ct = default);

    Task<Ao3Session?> GetSessionAsync(CancellationToken ct = default);

    Task SetSessionAsync(Ao3Session session, CancellationToken ct = default);

    Task ClearSessionAsync(CancellationToken ct = default);
}
