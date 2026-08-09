namespace Ao3Tracker.Api.Services.Storage;

/// <summary>
/// Resolved filesystem locations for everything the app persists locally: the SQLite
/// database file (when that provider is active), the Data Protection key ring, and the
/// admin-editable settings.json overlay. All three live under one data directory so a
/// single mounted volume (Docker) or folder (bare-metal) is enough to make the instance
/// durable across restarts.
/// </summary>
public record StoragePaths(string DataDirectory, string SqliteDbPath, string SettingsFilePath, string KeysDirectory)
{
    public static StoragePaths Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredDir = configuration["Storage:DataDirectory"] ?? "data";
        var dataDirectory = Path.IsPathRooted(configuredDir)
            ? configuredDir
            : Path.Combine(environment.ContentRootPath, configuredDir);

        Directory.CreateDirectory(dataDirectory);

        return new StoragePaths(
            DataDirectory: dataDirectory,
            SqliteDbPath: Path.Combine(dataDirectory, "ao3tracker.db"),
            SettingsFilePath: Path.Combine(dataDirectory, "settings.json"),
            KeysDirectory: Path.Combine(dataDirectory, "keys"));
    }
}
