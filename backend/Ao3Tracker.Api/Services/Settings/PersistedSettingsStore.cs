using System.Text.Json;
using System.Text.Json.Nodes;
using Ao3Tracker.Api.Services.Storage;

namespace Ao3Tracker.Api.Services.Settings;

public class PersistedSettingsStore : IPersistedSettingsStore
{
    private static readonly SemaphoreSlim WriteLock = new(1, 1);
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly StoragePaths _paths;

    public PersistedSettingsStore(StoragePaths paths)
    {
        _paths = paths;
    }

    public async Task SaveDatabaseSettingsAsync(string provider, string? postgresConnectionString, CancellationToken ct = default)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            var root = File.Exists(_paths.SettingsFilePath)
                ? JsonNode.Parse(await File.ReadAllTextAsync(_paths.SettingsFilePath, ct)) as JsonObject ?? new JsonObject()
                : new JsonObject();

            root["Database"] = new JsonObject
            {
                ["Provider"] = provider,
                ["PostgresConnectionString"] = postgresConnectionString,
            };

            await File.WriteAllTextAsync(_paths.SettingsFilePath, root.ToJsonString(WriteOptions), ct);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
