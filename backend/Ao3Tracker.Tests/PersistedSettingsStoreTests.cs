using System.Text.Json.Nodes;
using Ao3Tracker.Api.Services.Settings;
using Ao3Tracker.Api.Services.Storage;

namespace Ao3Tracker.Tests;

public class PersistedSettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("shipwatcharr-settings").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private PersistedSettingsStore Store() => new(new StoragePaths(
        DataDirectory: _dir,
        SqliteDbPath: Path.Combine(_dir, "db.sqlite"),
        SettingsFilePath: SettingsPath,
        KeysDirectory: Path.Combine(_dir, "keys")));

    [Fact]
    public async Task Returns_null_when_nothing_has_been_saved()
    {
        Assert.Null(await Store().ReadOperatorContactAsync());
    }

    [Fact]
    public async Task Round_trips_a_contact()
    {
        var store = Store();
        await store.SaveOperatorContactAsync("emma@example.com");

        Assert.Equal("emma@example.com", await store.ReadOperatorContactAsync());
    }

    [Fact]
    public async Task Trims_on_save()
    {
        var store = Store();
        await store.SaveOperatorContactAsync("  emma@example.com  ");

        Assert.Equal("emma@example.com", await store.ReadOperatorContactAsync());
    }

    [Fact]
    public async Task Clearing_removes_the_override()
    {
        var store = Store();
        await store.SaveOperatorContactAsync("emma@example.com");
        await store.SaveOperatorContactAsync(null);

        Assert.Null(await store.ReadOperatorContactAsync());
    }

    [Fact]
    public async Task Saving_a_contact_preserves_database_settings()
    {
        // Both settings share one file. Writing either must not clobber the other — and losing the
        // database section would leave the instance unable to find its own data on next boot.
        var store = Store();
        await store.SaveDatabaseSettingsAsync("Postgres", "Host=db;Database=x");
        await store.SaveOperatorContactAsync("emma@example.com");

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();

        Assert.Equal("Postgres", root["Database"]!["Provider"]!.GetValue<string>());
        Assert.Equal("emma@example.com", root["Ao3HttpClient"]!["OperatorContact"]!.GetValue<string>());
    }

    [Fact]
    public async Task Saving_database_settings_preserves_the_contact()
    {
        var store = Store();
        await store.SaveOperatorContactAsync("emma@example.com");
        await store.SaveDatabaseSettingsAsync("Sqlite", null);

        Assert.Equal("emma@example.com", await store.ReadOperatorContactAsync());
    }

    [Fact]
    public async Task Preserves_unknown_keys_an_operator_hand_edited_in()
    {
        await File.WriteAllTextAsync(SettingsPath, """
            { "Ao3HttpClient": { "MaxRequestsPerRun": 250 }, "Custom": "keep me" }
            """);

        var store = Store();
        await store.SaveOperatorContactAsync("emma@example.com");

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();

        Assert.Equal(250, root["Ao3HttpClient"]!["MaxRequestsPerRun"]!.GetValue<int>());
        Assert.Equal("keep me", root["Custom"]!.GetValue<string>());
    }

    [Fact]
    public async Task Clearing_leaves_no_empty_section_behind()
    {
        var store = Store();
        await store.SaveOperatorContactAsync("emma@example.com");
        await store.SaveOperatorContactAsync(null);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();

        Assert.False(root.ContainsKey("Ao3HttpClient"));
    }

    [Fact]
    public async Task Clearing_keeps_other_keys_in_the_section()
    {
        await File.WriteAllTextAsync(SettingsPath, """
            { "Ao3HttpClient": { "OperatorContact": "a@b.com", "MaxRequestsPerRun": 250 } }
            """);

        var store = Store();
        await store.SaveOperatorContactAsync(null);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath))!.AsObject();

        Assert.Equal(250, root["Ao3HttpClient"]!["MaxRequestsPerRun"]!.GetValue<int>());
        Assert.Null(await store.ReadOperatorContactAsync());
    }

    [Fact]
    public async Task Malformed_settings_file_does_not_take_the_scraper_down()
    {
        await File.WriteAllTextAsync(SettingsPath, "{ this is not json");

        Assert.Null(await Store().ReadOperatorContactAsync());
    }

    [Fact]
    public async Task Concurrent_saves_do_not_corrupt_the_file()
    {
        var store = Store();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            i % 2 == 0
                ? store.SaveOperatorContactAsync($"user{i}@example.com")
                : store.SaveDatabaseSettingsAsync("Sqlite", null)));

        var root = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath));
        Assert.NotNull(root);
    }
}
