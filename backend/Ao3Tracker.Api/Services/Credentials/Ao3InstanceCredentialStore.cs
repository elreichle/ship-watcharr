using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Services.Credentials;

/// <summary>
/// Encrypts the instance's AO3 credential and session cookie at rest using ASP.NET Core Data
/// Protection.
///
/// The purpose string is deliberately the same one <see cref="Ao3CredentialStore"/> uses. A
/// protector is scoped by its purpose, so sharing it is what lets an existing per-user password be
/// carried over as ciphertext — copied, never decrypted, so no plaintext password is handled and
/// nobody has to type it again. Changing this string would orphan every credential saved before the
/// change.
/// </summary>
public class Ao3InstanceCredentialStore : IAo3InstanceCredentialStore
{
    /// <summary>Shared with <see cref="Ao3CredentialStore"/> on purpose. See the class remarks.</summary>
    internal const string Purpose = "Ao3Tracker.Ao3Credentials.v1";

    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;

    public Ao3InstanceCredentialStore(AppDbContext db, IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public Task<bool> HasCredentialAsync(CancellationToken ct = default) =>
        _db.Ao3InstanceCredentials.AnyAsync(ct);

    public async Task<string?> GetUsernameAsync(CancellationToken ct = default) =>
        (await FindAsync(track: false, ct))?.Ao3Username;

    public async Task SetCredentialAsync(string ao3Username, string ao3Password, CancellationToken ct = default)
    {
        var entity = await FindAsync(track: true, ct);
        var encryptedPassword = _protector.Protect(ao3Password);

        if (entity is null)
        {
            _db.Ao3InstanceCredentials.Add(new Ao3InstanceCredential
            {
                Ao3Username = ao3Username,
                EncryptedPassword = encryptedPassword,
            });
        }
        else
        {
            entity.Ao3Username = ao3Username;
            entity.EncryptedPassword = encryptedPassword;
            entity.UpdatedAt = DateTime.UtcNow;

            // The password changed, so a session established with the old one proves nothing.
            // Dropping it costs one login, not a re-entered credential.
            entity.EncryptedSessionCookie = null;
            entity.SessionEstablishedAt = null;
            entity.SessionExpiresAt = null;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveCredentialAsync(CancellationToken ct = default)
    {
        var entity = await FindAsync(track: true, ct);
        if (entity is null) return;

        _db.Ao3InstanceCredentials.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(string Ao3Username, string Ao3Password)?> GetDecryptedCredentialAsync(
        CancellationToken ct = default)
    {
        var entity = await FindAsync(track: false, ct);
        if (entity is null) return null;

        return (entity.Ao3Username, _protector.Unprotect(entity.EncryptedPassword));
    }

    public async Task<Ao3Session?> GetSessionAsync(CancellationToken ct = default)
    {
        var entity = await FindAsync(track: false, ct);
        if (entity?.EncryptedSessionCookie is null) return null;

        return new Ao3Session(
            _protector.Unprotect(entity.EncryptedSessionCookie),
            entity.SessionEstablishedAt ?? DateTime.UtcNow,
            entity.SessionExpiresAt);
    }

    public async Task SetSessionAsync(Ao3Session session, CancellationToken ct = default)
    {
        var entity = await FindAsync(track: true, ct)
            ?? throw new InvalidOperationException(
                "No instance AO3 credential is configured; cannot attach a session to it.");

        entity.EncryptedSessionCookie = _protector.Protect(session.SessionCookie);
        entity.SessionEstablishedAt = session.EstablishedAt;
        entity.SessionExpiresAt = session.ExpiresAt;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearSessionAsync(CancellationToken ct = default)
    {
        var entity = await FindAsync(track: true, ct);
        if (entity is null) return;

        entity.EncryptedSessionCookie = null;
        entity.SessionEstablishedAt = null;
        entity.SessionExpiresAt = null;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The singleton row, if it exists. Queried by key rather than <c>SingleOrDefaultAsync</c> over
    /// the table: the check constraint already guarantees at most one row, so a query that would
    /// throw on a second one is asserting something the database has settled.
    /// </summary>
    private Task<Ao3InstanceCredential?> FindAsync(bool track, CancellationToken ct)
    {
        var query = track ? _db.Ao3InstanceCredentials : _db.Ao3InstanceCredentials.AsNoTracking();
        return query.FirstOrDefaultAsync(c => c.Id == Ao3InstanceCredential.SingletonId, ct);
    }
}
