using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Credentials;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Tests;

/// <summary>
/// The one AO3 account this deployment scrapes as.
///
/// Over a real SQLite database and real Data Protection keys on disk, because the two properties
/// worth pinning are exactly the ones a mock would agree with while the real thing failed: that a
/// saved password survives the process that saved it, and that the table cannot hold a second
/// account.
/// </summary>
public class Ao3InstanceCredentialStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _keysDirectory;

    public Ao3InstanceCredentialStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _keysDirectory = Directory.CreateTempSubdirectory("shipwatcharr-credential-keys-").FullName;

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();

        try
        {
            Directory.Delete(_keysDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    private AppDbContext NewContext() => new SqliteAppDbContext(
        new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite(_connection).Options);

    /// <summary>
    /// A store with its own context, over keys persisted to disk the way Program.cs configures them.
    /// Building a second one stands in for a restart: new context, new protector, same key ring.
    /// </summary>
    private Ao3InstanceCredentialStore Store()
    {
        var protection = new ServiceCollection()
            .AddDataProtection()
            .SetApplicationName("Ao3Tracker")
            .PersistKeysToFileSystem(new DirectoryInfo(_keysDirectory))
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();

        return new Ao3InstanceCredentialStore(NewContext(), protection);
    }

    [Fact]
    public async Task Reports_no_credential_on_a_fresh_install()
    {
        var store = Store();

        Assert.False(await store.HasCredentialAsync());
        Assert.Null(await store.GetUsernameAsync());
        Assert.Null(await store.GetDecryptedCredentialAsync());
    }

    [Fact]
    public async Task Round_trips_a_credential()
    {
        var store = Store();
        await store.SetCredentialAsync("scraper_account", "hunter2");

        Assert.True(await store.HasCredentialAsync());
        Assert.Equal(("scraper_account", "hunter2"), await store.GetDecryptedCredentialAsync());
    }

    [Fact]
    public async Task Never_stores_the_password_in_plaintext()
    {
        await Store().SetCredentialAsync("scraper_account", "hunter2");

        await using var db = NewContext();
        var stored = await db.Ao3InstanceCredentials.SingleAsync();

        Assert.DoesNotContain("hunter2", stored.EncryptedPassword);
    }

    [Fact]
    public async Task A_saved_credential_survives_a_restart()
    {
        // The property Emma asked for: entered once, it stays. A second store over the same key ring
        // and database is what a restarted process sees — if the keys were ephemeral, Unprotect here
        // would throw rather than return the password.
        await Store().SetCredentialAsync("scraper_account", "hunter2");

        Assert.Equal(("scraper_account", "hunter2"), await Store().GetDecryptedCredentialAsync());
    }

    [Fact]
    public async Task Updating_replaces_the_credential_rather_than_adding_one()
    {
        var store = Store();
        await store.SetCredentialAsync("first_account", "hunter2");
        await store.SetCredentialAsync("second_account", "correct-horse");

        Assert.Equal(("second_account", "correct-horse"), await store.GetDecryptedCredentialAsync());

        await using var db = NewContext();
        Assert.Equal(1, await db.Ao3InstanceCredentials.CountAsync());
    }

    [Fact]
    public async Task Changing_the_password_discards_the_cached_session()
    {
        // A session established with the old password proves nothing about the new one. Dropping it
        // costs one login; keeping it would have the scraper present a cookie it cannot renew.
        var store = Store();
        await store.SetCredentialAsync("scraper_account", "hunter2");
        await store.SetSessionAsync(new Ao3Session("cookie-value", DateTime.UtcNow, null));

        await store.SetCredentialAsync("scraper_account", "correct-horse");

        Assert.Null(await store.GetSessionAsync());
    }

    [Fact]
    public async Task Losing_the_session_is_not_losing_the_login()
    {
        // The distinction the whole design rests on: the credential is the durable source of truth
        // and the session is only a cache of it, so clearing one must not disturb the other.
        var store = Store();
        await store.SetCredentialAsync("scraper_account", "hunter2");
        await store.SetSessionAsync(new Ao3Session("cookie-value", DateTime.UtcNow, null));

        await store.ClearSessionAsync();

        Assert.Null(await store.GetSessionAsync());
        Assert.True(await store.HasCredentialAsync());
        Assert.Equal(("scraper_account", "hunter2"), await store.GetDecryptedCredentialAsync());
    }

    [Fact]
    public async Task Round_trips_a_session()
    {
        var store = Store();
        await store.SetCredentialAsync("scraper_account", "hunter2");

        var expires = new DateTime(2027, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        await store.SetSessionAsync(new Ao3Session("cookie-value", DateTime.UtcNow, expires));

        var session = await store.GetSessionAsync();
        Assert.NotNull(session);
        Assert.Equal("cookie-value", session.SessionCookie);
        Assert.Equal(expires, session.ExpiresAt);
    }

    [Fact]
    public async Task Refuses_to_attach_a_session_with_no_credential_to_attach_it_to()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Store().SetSessionAsync(new Ao3Session("cookie-value", DateTime.UtcNow, null)));
    }

    [Fact]
    public async Task Removing_the_credential_leaves_nothing_behind()
    {
        var store = Store();
        await store.SetCredentialAsync("scraper_account", "hunter2");

        await store.RemoveCredentialAsync();

        Assert.False(await store.HasCredentialAsync());

        await using var db = NewContext();
        Assert.Empty(db.Ao3InstanceCredentials);
    }

    [Fact]
    public async Task Removing_a_credential_that_is_not_there_is_not_an_error()
    {
        await Store().RemoveCredentialAsync();

        Assert.False(await Store().HasCredentialAsync());
    }

    [Fact]
    public async Task Reads_a_password_encrypted_under_the_purpose_the_retired_per_user_store_used()
    {
        // The purpose string is written out here rather than read off the store, because what this
        // pins is that it never changes. The InstanceAo3Credential migration copied per-user
        // ciphertext across without decrypting it, so a credential saved before that migration is
        // still readable today only while this exact string is in use. The per-user store itself is
        // gone; the ciphertext it wrote is not.
        var protection = new ServiceCollection()
            .AddDataProtection()
            .SetApplicationName("Ao3Tracker")
            .PersistKeysToFileSystem(new DirectoryInfo(_keysDirectory))
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();

        var carriedOver = protection.CreateProtector("Ao3Tracker.Ao3Credentials.v1").Protect("hunter2");

        await using (var db = NewContext())
        {
            db.Ao3InstanceCredentials.Add(new Ao3InstanceCredential
            {
                Ao3Username = "scraper_account",
                EncryptedPassword = carriedOver,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(("scraper_account", "hunter2"), await Store().GetDecryptedCredentialAsync());
    }

    [Fact]
    public async Task The_database_refuses_a_second_account()
    {
        // "One account per deployment" is enforced by the check constraint rather than by every
        // caller remembering to reuse id 1. Without it a second row would simply be a second login
        // for a scrape that can only use one.
        await Store().SetCredentialAsync("scraper_account", "hunter2");

        await using var db = NewContext();
        db.Ao3InstanceCredentials.Add(new Ao3InstanceCredential
        {
            Id = Ao3InstanceCredential.SingletonId + 1,
            Ao3Username = "second_account",
            EncryptedPassword = "irrelevant",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
