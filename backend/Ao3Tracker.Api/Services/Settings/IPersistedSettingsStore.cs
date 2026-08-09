namespace Ao3Tracker.Api.Services.Settings;

/// <summary>
/// Writes admin-configured overrides (currently just database provider selection) to
/// settings.json in the data directory, where it's picked up as a configuration layer on
/// the next startup — see StoragePaths and Program.cs. Applying a change always requires a
/// restart; this store only ever writes the file, it never touches the running app's config.
/// </summary>
public interface IPersistedSettingsStore
{
    Task SaveDatabaseSettingsAsync(string provider, string? postgresConnectionString, CancellationToken ct = default);
}
