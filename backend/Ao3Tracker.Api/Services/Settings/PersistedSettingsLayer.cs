using Microsoft.Extensions.Configuration;

namespace Ao3Tracker.Api.Services.Settings;

/// <summary>
/// Layers the admin-editable settings.json over the deployment's own configuration at startup, so a
/// choice saved through the admin UI (currently the database provider) is the source of truth across
/// restarts until it is changed there again, or an operator deletes the file.
///
/// The operator contact is deliberately kept out of that layering. <see cref="PersistedSettingsStore"/>
/// reads the admin's saved contact from the file on demand, and the resolver treats the configured
/// value as what clearing that override reverts to. Letting the file shadow the configured value
/// would make the two indistinguishable after a restart: the settings page would report the override
/// as its own fallback, and clearing it would leave the old contact in the User-Agent until the next
/// boot, because the file is not reloaded on change.
/// </summary>
public static class PersistedSettingsLayer
{
    public const string OperatorContactKey = "Ao3HttpClient:OperatorContact";

    public static void Add(ConfigurationManager configuration, string settingsFilePath)
    {
        // Read before the file can shadow it. Null when the deployment set nothing, and a null in the
        // in-memory source still shadows the file's value, which is the point.
        var deploymentContact = configuration[OperatorContactKey];

        configuration.AddJsonFile(settingsFilePath, optional: true, reloadOnChange: false);
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [OperatorContactKey] = deploymentContact,
        });
    }
}
