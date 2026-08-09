using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ao3Tracker.Tests;

/// <summary>
/// Precedence for "who does AO3 contact about this instance". Runs against a real SQLite database
/// rather than a mock, because the fallback is an actual query over Identity's user table.
/// </summary>
public class OperatorContactResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteAppDbContext _db;

    public OperatorContactResolverTests()
    {
        // Kept open for the lifetime of the test: an in-memory SQLite database exists only as long
        // as a connection to it does.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<SqliteAppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new SqliteAppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private OperatorContactResolver Resolver(string? savedContact = null, string? configuredContact = null) =>
        new(new StubSettingsStore(savedContact),
            Options.Create(new Ao3HttpClientOptions { OperatorContact = configuredContact ?? "" }),
            _db);

    private async Task AddUserAsync(string id, string email, bool isAdmin)
    {
        _db.Users.Add(new ApplicationUser
        {
            Id = id,
            Email = email,
            UserName = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            IsAdmin = isAdmin,
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Defaults_to_the_admin_account_address()
    {
        // The "works out of the box" path: registration is email-based, so the address someone
        // signs up with is both their username and a real mailbox.
        await AddUserAsync("u1", "emma@example.com", isAdmin: true);

        var result = await Resolver().ResolveAsync();

        Assert.Equal("emma@example.com", result.Contact);
        Assert.Equal(OperatorContactSource.AdminAccount, result.Source);
        Assert.True(result.IsConfigured);
    }

    [Fact]
    public async Task Ignores_non_admin_accounts()
    {
        await AddUserAsync("u1", "guest@example.com", isAdmin: false);

        var result = await Resolver().ResolveAsync();

        Assert.Null(result.Contact);
        Assert.Equal(OperatorContactSource.None, result.Source);
    }

    [Fact]
    public async Task Picks_the_oldest_admin_when_there_are_several()
    {
        // Must be stable: otherwise the instance's identity would change depending on who
        // registered most recently.
        await AddUserAsync("a-first", "first@example.com", isAdmin: true);
        await AddUserAsync("b-second", "second@example.com", isAdmin: true);

        var result = await Resolver().ResolveAsync();

        Assert.Equal("first@example.com", result.Contact);
    }

    [Fact]
    public async Task Configuration_beats_the_admin_account()
    {
        await AddUserAsync("u1", "emma@example.com", isAdmin: true);

        var result = await Resolver(configuredContact: "ops@example.com").ResolveAsync();

        Assert.Equal("ops@example.com", result.Contact);
        Assert.Equal(OperatorContactSource.Configuration, result.Source);
    }

    [Fact]
    public async Task Admin_setting_beats_everything()
    {
        await AddUserAsync("u1", "emma@example.com", isAdmin: true);

        var result = await Resolver(savedContact: "chosen@example.com", configuredContact: "ops@example.com")
            .ResolveAsync();

        Assert.Equal("chosen@example.com", result.Contact);
        Assert.Equal(OperatorContactSource.AdminSetting, result.Source);
    }

    [Fact]
    public async Task Resolve_default_ignores_the_admin_setting()
    {
        // What the settings UI shows as "clearing this reverts to X".
        await AddUserAsync("u1", "emma@example.com", isAdmin: true);

        var resolver = Resolver(savedContact: "chosen@example.com");

        Assert.Equal("chosen@example.com", (await resolver.ResolveAsync()).Contact);
        Assert.Equal("emma@example.com", (await resolver.ResolveDefaultAsync()).Contact);
    }

    [Fact]
    public async Task Reports_none_on_a_brand_new_instance()
    {
        // Fresh install, nobody registered yet. Scraping must be off, not defaulted to something.
        var result = await Resolver().ResolveAsync();

        Assert.False(result.IsConfigured);
        Assert.Equal(OperatorContactSource.None, result.Source);
    }

    [Fact]
    public async Task Blank_saved_value_falls_through_rather_than_disabling_scraping()
    {
        await AddUserAsync("u1", "emma@example.com", isAdmin: true);

        var result = await Resolver(savedContact: "   ").ResolveAsync();

        Assert.Equal("emma@example.com", result.Contact);
        Assert.Equal(OperatorContactSource.AdminAccount, result.Source);
    }

    private sealed class StubSettingsStore(string? contact) : IPersistedSettingsStore
    {
        private string? _contact = contact;

        public Task SaveDatabaseSettingsAsync(string provider, string? cs, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> ReadOperatorContactAsync(CancellationToken ct = default) =>
            Task.FromResult(string.IsNullOrWhiteSpace(_contact) ? null : _contact);

        public Task SaveOperatorContactAsync(string? value, CancellationToken ct = default)
        {
            _contact = value;
            return Task.CompletedTask;
        }
    }
}
