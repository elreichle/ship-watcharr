using System.Text.RegularExpressions;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Settings;
using Ao3Tracker.Api.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// Lets an admin switch the instance between SQLite (the zero-config default) and a
/// PostgreSQL database they provide. Changing providers only ever writes settings.json —
/// see IPersistedSettingsStore — and then restarts the process so Program.cs re-reads
/// config and reconnects. There is deliberately no live data migration between providers;
/// switching starts the newly-selected database empty (see README).
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin/database")]
public class AdminDatabaseController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly IConfiguration _configuration;
    private readonly StoragePaths _storagePaths;
    private readonly IPersistedSettingsStore _settingsStore;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<AdminDatabaseController> _logger;

    public AdminDatabaseController(
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        StoragePaths storagePaths,
        IPersistedSettingsStore settingsStore,
        IHostApplicationLifetime appLifetime,
        ILogger<AdminDatabaseController> logger)
    {
        _userManager = userManager;
        _configuration = configuration;
        _storagePaths = storagePaths;
        _settingsStore = settingsStore;
        _appLifetime = appLifetime;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<DatabaseStatusDto>> GetStatus()
    {
        if (!await IsCurrentUserAdminAsync()) return Forbid();

        var provider = _configuration["Database:Provider"] ?? "Sqlite";
        var isPostgres = string.Equals(provider, "Postgres", StringComparison.OrdinalIgnoreCase);
        var postgresConnectionString = _configuration["Database:PostgresConnectionString"];

        return Ok(new DatabaseStatusDto(
            Provider: isPostgres ? "Postgres" : "Sqlite",
            SqliteDbPath: _storagePaths.SqliteDbPath,
            PostgresConfigured: !string.IsNullOrWhiteSpace(postgresConnectionString),
            PostgresConnectionSummary: isPostgres ? MaskPassword(postgresConnectionString) : null));
    }

    [HttpPut]
    public async Task<IActionResult> UpdateSettings(UpdateDatabaseSettingsRequest request, CancellationToken ct)
    {
        if (!await IsCurrentUserAdminAsync()) return Forbid();

        var switchingToPostgres = string.Equals(request.Provider, "Postgres", StringComparison.OrdinalIgnoreCase);
        var switchingToSqlite = string.Equals(request.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase);

        if (!switchingToPostgres && !switchingToSqlite)
            return BadRequest(new { message = "Provider must be 'Sqlite' or 'Postgres'." });

        if (switchingToPostgres)
        {
            if (string.IsNullOrWhiteSpace(request.PostgresConnectionString))
                return BadRequest(new { message = "A PostgreSQL connection string is required to switch providers." });

            // Fail fast on a bad connection string now, rather than after the restart —
            // at that point settings.json is the highest-precedence config source, so a
            // typo here would otherwise leave the instance unable to start at all.
            try
            {
                await using var connection = new NpgsqlConnection(request.PostgresConnectionString);
                await connection.OpenAsync(ct);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = $"Could not connect to that PostgreSQL database: {ex.Message}" });
            }
        }

        await _settingsStore.SaveDatabaseSettingsAsync(
            request.Provider,
            switchingToPostgres ? request.PostgresConnectionString : null,
            ct);

        _logger.LogWarning(
            "Database provider changed to {Provider} by {UserId}; restarting to apply.",
            request.Provider, _userManager.GetUserId(User));

        // Give the response time to flush before the process exits. Under
        // docker-compose's `restart: unless-stopped`, exiting brings the container right
        // back up with the new settings.json in effect.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), CancellationToken.None);
            _appLifetime.StopApplication();
        }, CancellationToken.None);

        return Accepted(new { message = "Settings saved. The application is restarting to apply the new database configuration." });
    }

    private async Task<bool> IsCurrentUserAdminAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        return user?.IsAdmin == true;
    }

    private static string? MaskPassword(string? connectionString) =>
        string.IsNullOrWhiteSpace(connectionString)
            ? null
            : Regex.Replace(connectionString, "(Password=)[^;]*", "$1***", RegexOptions.IgnoreCase);
}
