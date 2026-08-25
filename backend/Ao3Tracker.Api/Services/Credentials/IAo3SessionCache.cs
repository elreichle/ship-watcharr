using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Api.Services.Credentials;

/// <summary>
/// The cached AO3 session, seen from the one angle the HTTP client needs: read it, and throw it
/// away when AO3 stops honouring it.
///
/// Deliberately narrower than <see cref="IAo3InstanceCredentialStore"/>. The client that attaches
/// cookies to outbound requests has no business being able to read the deployment's password, and
/// this is what makes that a fact about the type rather than a habit.
/// </summary>
public interface IAo3SessionCache
{
    /// <summary>The cached session, or null when there is none stored or the stored one has expired.</summary>
    Task<Ao3Session?> GetUsableAsync(CancellationToken ct = default);

    /// <summary>
    /// Forgets the cached cookie. Costs exactly one login: the password is the durable source of
    /// truth and stays where it is.
    /// </summary>
    Task DiscardAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="IAo3SessionCache"/> over the credential store, in a scope of its own.
///
/// The scope is the point. This is reached from inside the shared HTTP client, which runs in
/// whatever scope the current scrape job holds — and discarding a dead session is a write.
/// Performing it through that scope's <c>AppDbContext</c> would call <c>SaveChangesAsync</c> in the
/// middle of a walk, flushing whatever the ingestor happened to have tracked at that moment: a
/// half-ingested page committed by a cookie check. So both the read and the write get a fresh scope,
/// which at one request per five-to-eight seconds costs nothing worth measuring.
///
/// Expiry is applied here rather than in the store so that "usable" has one definition.
/// </summary>
public sealed class Ao3SessionCache : IAo3SessionCache
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;

    public Ao3SessionCache(IServiceScopeFactory scopes, TimeProvider? timeProvider = null)
    {
        _scopes = scopes;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<Ao3Session?> GetUsableAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var session = await Store(scope).GetSessionAsync(ct);

        return session is not null && session.IsUsableAt(_time.GetUtcNow().UtcDateTime) ? session : null;
    }

    public async Task DiscardAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        await Store(scope).ClearSessionAsync(ct);
    }

    private static IAo3InstanceCredentialStore Store(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IAo3InstanceCredentialStore>();
}
