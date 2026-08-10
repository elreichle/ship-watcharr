using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Services.Credentials;

/// <summary>
/// Encrypts AO3 credentials/session cookies at rest using ASP.NET Core Data Protection.
/// A dedicated purpose string scopes the protector so these keys can't decrypt unrelated
/// Data Protection payloads (e.g. auth cookies) and vice versa.
/// </summary>
public class Ao3CredentialStore : IAo3CredentialStore
{
    // Shared with the instance-level store rather than repeated, so the two cannot drift apart
    // while a per-user credential is still being carried over into the instance one as ciphertext.
    private const string Purpose = Ao3InstanceCredentialStore.Purpose;

    private readonly AppDbContext _db;
    private readonly IDataProtector _protector;

    public Ao3CredentialStore(AppDbContext db, IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _protector = dataProtectionProvider.CreateProtector(Purpose);
    }

    public async Task<bool> HasCredentialAsync(string userId, CancellationToken ct = default) =>
        await _db.Ao3Credentials.AnyAsync(c => c.UserId == userId, ct);

    public async Task SetCredentialAsync(string userId, string ao3Username, string ao3Password, CancellationToken ct = default)
    {
        var entity = await _db.Ao3Credentials.SingleOrDefaultAsync(c => c.UserId == userId, ct);
        var encryptedPassword = _protector.Protect(ao3Password);

        if (entity is null)
        {
            entity = new Ao3Credential
            {
                UserId = userId,
                Ao3Username = ao3Username,
                EncryptedPassword = encryptedPassword,
            };
            _db.Ao3Credentials.Add(entity);
        }
        else
        {
            entity.Ao3Username = ao3Username;
            entity.EncryptedPassword = encryptedPassword;
            entity.UpdatedAt = DateTime.UtcNow;
            // Credentials changed: any existing session is no longer trustworthy.
            entity.EncryptedSessionCookie = null;
            entity.SessionEstablishedAt = null;
            entity.SessionExpiresAt = null;
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task RemoveCredentialAsync(string userId, CancellationToken ct = default)
    {
        var entity = await _db.Ao3Credentials.SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (entity is null) return;

        _db.Ao3Credentials.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<(string Ao3Username, string Ao3Password)?> GetDecryptedCredentialAsync(string userId, CancellationToken ct = default)
    {
        var entity = await _db.Ao3Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (entity is null) return null;

        return (entity.Ao3Username, _protector.Unprotect(entity.EncryptedPassword));
    }

    public async Task<Ao3Session?> GetSessionAsync(string userId, CancellationToken ct = default)
    {
        var entity = await _db.Ao3Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (entity?.EncryptedSessionCookie is null) return null;

        return new Ao3Session(
            _protector.Unprotect(entity.EncryptedSessionCookie),
            entity.SessionEstablishedAt ?? DateTime.UtcNow,
            entity.SessionExpiresAt);
    }

    public async Task SetSessionAsync(string userId, Ao3Session session, CancellationToken ct = default)
    {
        var entity = await _db.Ao3Credentials.SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (entity is null)
            throw new InvalidOperationException($"No AO3 credential exists for user {userId}; cannot attach a session.");

        entity.EncryptedSessionCookie = _protector.Protect(session.SessionCookie);
        entity.SessionEstablishedAt = session.EstablishedAt;
        entity.SessionExpiresAt = session.ExpiresAt;
        entity.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    public async Task ClearSessionAsync(string userId, CancellationToken ct = default)
    {
        var entity = await _db.Ao3Credentials.SingleOrDefaultAsync(c => c.UserId == userId, ct);
        if (entity is null) return;

        entity.EncryptedSessionCookie = null;
        entity.SessionEstablishedAt = null;
        entity.SessionExpiresAt = null;
        await _db.SaveChangesAsync(ct);
    }
}
