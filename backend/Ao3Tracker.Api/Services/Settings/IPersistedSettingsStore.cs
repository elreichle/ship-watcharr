namespace Ao3Tracker.Api.Services.Settings;

/// <summary>
/// Reads and writes admin-configured overrides in settings.json in the data directory — see
/// StoragePaths and Program.cs.
///
/// Two different kinds of setting live here, and they behave differently on purpose:
///
/// - Database provider selection is read as a *configuration layer at startup*, so changing it
///   requires a restart. It has to work that way: the setting decides which database to open, so
///   it cannot itself be stored in one.
/// - Scraping settings are read *on demand* via <see cref="ReadOperatorContactAsync"/>, so they
///   take effect on the next request with no restart. Nothing about them is needed to boot.
/// </summary>
public interface IPersistedSettingsStore
{
    Task SaveDatabaseSettingsAsync(string provider, string? postgresConnectionString, CancellationToken ct = default);

    /// <summary>The admin-configured operator contact, or null if none has been saved.</summary>
    Task<string?> ReadOperatorContactAsync(CancellationToken ct = default);

    /// <summary>Saves the operator contact. Pass null or blank to clear it and fall back to the default.</summary>
    Task SaveOperatorContactAsync(string? operatorContact, CancellationToken ct = default);
}
