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
        await UpdateAsync(root =>
        {
            root["Database"] = new JsonObject
            {
                ["Provider"] = provider,
                ["PostgresConnectionString"] = postgresConnectionString,
            };
        }, ct);
    }

    public async Task<string?> ReadOperatorContactAsync(CancellationToken ct = default)
    {
        var root = await ReadAsync(ct);
        var value = root?["Ao3HttpClient"]?["OperatorContact"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public async Task SaveOperatorContactAsync(string? operatorContact, CancellationToken ct = default)
    {
        await UpdateAsync(root =>
        {
            // Merge into any existing Ao3HttpClient object rather than replacing it, so saving a
            // contact can never silently drop other keys an operator has hand-edited into the file.
            var section = root["Ao3HttpClient"] as JsonObject ?? new JsonObject();

            if (string.IsNullOrWhiteSpace(operatorContact))
                section.Remove("OperatorContact");
            else
                section["OperatorContact"] = operatorContact.Trim();

            // Don't leave an empty section behind. settings.json is layered over appsettings.json
            // at startup as the highest-precedence source (this key excepted, see
            // PersistedSettingsLayer), so anything left here is something a future reader has to
            // reason about — keep the file to what is actually overridden.
            if (section.Count == 0)
                root.Remove("Ao3HttpClient");
            else
                root["Ao3HttpClient"] = section;
        }, ct);
    }

    private async Task<JsonObject?> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_paths.SettingsFilePath)) return null;

        try
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(_paths.SettingsFilePath, ct)) as JsonObject;
        }
        catch (JsonException)
        {
            // A hand-edited settings.json with a syntax error must not take the scraper down; the
            // caller falls back to its default. Startup config binding surfaces the error properly.
            return null;
        }
    }

    private async Task UpdateAsync(Action<JsonObject> mutate, CancellationToken ct)
    {
        await WriteLock.WaitAsync(ct);
        try
        {
            var root = await ReadAsync(ct) ?? new JsonObject();
            mutate(root);
            await File.WriteAllTextAsync(_paths.SettingsFilePath, root.ToJsonString(WriteOptions), ct);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
