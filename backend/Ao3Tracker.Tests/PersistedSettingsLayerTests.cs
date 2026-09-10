using Ao3Tracker.Api.Services.Settings;
using Microsoft.Extensions.Configuration;

namespace Ao3Tracker.Tests;

/// <summary>
/// settings.json is layered over the deployment's own configuration at startup so an admin's
/// choices survive restarts. The operator contact is the exception: the resolver reads the admin's
/// value from the file on demand and treats configuration as what clearing it reverts to, so the
/// file must not be allowed to shadow that fallback.
/// </summary>
public class PersistedSettingsLayerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("settings-layer-").FullName;
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ConfigurationManager Layered(string? deploymentContact, string fileJson)
    {
        File.WriteAllText(SettingsPath, fileJson);
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Ao3HttpClient:OperatorContact"] = deploymentContact,
        });
        PersistedSettingsLayer.Add(config, SettingsPath);
        return config;
    }

    [Fact]
    public void Saved_database_settings_still_win_over_the_deployment()
    {
        var config = Layered("env@example.invalid",
            """{ "Database": { "Provider": "Postgres" } }""");

        Assert.Equal("Postgres", config["Database:Provider"]);
    }

    [Fact]
    public void Saved_operator_contact_does_not_shadow_the_deployments_own()
    {
        var config = Layered("env@example.invalid",
            """{ "Ao3HttpClient": { "OperatorContact": "admin@example.invalid" } }""");

        Assert.Equal("env@example.invalid", config["Ao3HttpClient:OperatorContact"]);
    }

    [Fact]
    public void Saved_operator_contact_leaves_an_unset_deployment_contact_unset()
    {
        var config = Layered(null,
            """{ "Ao3HttpClient": { "OperatorContact": "admin@example.invalid" } }""");

        Assert.True(string.IsNullOrEmpty(config["Ao3HttpClient:OperatorContact"]));
    }

    [Fact]
    public void A_missing_file_is_not_an_error()
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["Ao3HttpClient:OperatorContact"] = "env@example.invalid" });

        PersistedSettingsLayer.Add(config, Path.Combine(_dir, "absent.json"));

        Assert.Equal("env@example.invalid", config["Ao3HttpClient:OperatorContact"]);
    }
}
