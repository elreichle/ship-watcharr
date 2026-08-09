namespace Ao3Tracker.Api.Services.Credentials;

public record Ao3Session(string SessionCookie, DateTime EstablishedAt, DateTime? ExpiresAt);

/// <summary>
/// Reads/writes a user's AO3 credential and session state. Implementations are responsible
/// for encryption at rest; callers never see ciphertext and the raw password is never
/// exposed again once stored.
/// </summary>
public interface IAo3CredentialStore
{
    Task<bool> HasCredentialAsync(string userId, CancellationToken ct = default);

    /// <summary>Stores/overwrites the AO3 username + password for a user. Encrypts the password before persisting.</summary>
    Task SetCredentialAsync(string userId, string ao3Username, string ao3Password, CancellationToken ct = default);

    Task RemoveCredentialAsync(string userId, CancellationToken ct = default);

    /// <summary>Returns the decrypted password for use by a scraper. Never expose this value outside the backend.</summary>
    Task<(string Ao3Username, string Ao3Password)?> GetDecryptedCredentialAsync(string userId, CancellationToken ct = default);

    Task<Ao3Session?> GetSessionAsync(string userId, CancellationToken ct = default);

    Task SetSessionAsync(string userId, Ao3Session session, CancellationToken ct = default);

    Task ClearSessionAsync(string userId, CancellationToken ct = default);
}
