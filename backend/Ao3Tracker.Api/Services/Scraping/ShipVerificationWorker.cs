using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Polls for followed tags AO3 has not confirmed yet and checks them.
///
/// Separate from <see cref="ScrapeWorker"/> despite the similar shape, because the two answer to
/// different things: a scrape runs on a ship's own schedule, while a verification runs once,
/// promptly, and then never again. Sharing a tick would tie a newly followed tag's check to a
/// scrape interval measured in hours.
///
/// Both still share the one rate-limit gate, so this cannot outpace anything — a verification
/// request queues behind whatever scraping is already in flight.
/// </summary>
public class ShipVerificationWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Ships checked per tick. Each costs a real, rate-limited request, so this bounds how much of
    /// the shared gate one batch of new follows can occupy before scrapes get a turn.
    /// </summary>
    private const int BatchSize = 10;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ShipVerificationWorker> _logger;

    /// <summary>Last logged scraping-enabled state; null until the first check. See ExecuteAsync.</summary>
    private bool? _scrapingEnabled;

    public ShipVerificationWorker(IServiceScopeFactory scopeFactory, ILogger<ShipVerificationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await VerifyDueShipsAsync(stoppingToken);
            }
            catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, stoppingToken))
            {
                // Must absorb request timeouts as well as ordinary faults: this is a
                // BackgroundService, so anything escaping here stops the host. See ScrapeCancellation.
                _logger.LogError(ex, "Unhandled error while verifying ships");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Internal so a test can run one tick without a host or a timer.</summary>
    internal async Task VerifyDueShipsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();

        // Checked before touching the database, and re-checked every tick rather than once at
        // startup: a fresh install boots with no operator contact, and one only appears when
        // somebody saves an email or a contact in the UI. Without the gate every pending ship would
        // burn an attempt and a backoff step on a failure that was never about the tag.
        var userAgents = scope.ServiceProvider.GetRequiredService<Ao3UserAgentProvider>();
        var (ok, _, error) = await userAgents.TryGetUserAgentAsync(ct);

        if (!ok)
        {
            // Logged on transition only. This runs every minute, and an instance with no contact is
            // a steady state, not an event worth repeating 1,440 times a day.
            if (_scrapingEnabled != false)
            {
                _logger.LogWarning("Ship verification is paused.\n\n{Error}", error);
                _scrapingEnabled = false;
            }
            return;
        }

        _scrapingEnabled = true;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        var due = await db.Ships
            .Where(s => s.VerificationState == ShipVerificationState.Pending)
            .Where(s => s.NextVerificationAttemptAt == null || s.NextVerificationAttemptAt <= now)
            .OrderBy(s => s.NextVerificationAttemptAt ?? DateTime.MinValue)
            .ThenBy(s => s.Id)
            .Select(s => s.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        // Ids rather than entities, and a scope per ship: a merge deletes one of the rows involved,
        // which would leave any other tracked instance of it stale for the rest of the batch.
        foreach (var shipId in due)
        {
            ct.ThrowIfCancellationRequested();

            using var perShip = _scopeFactory.CreateScope();
            var verifier = perShip.ServiceProvider.GetRequiredService<IShipVerifier>();

            try
            {
                await verifier.VerifyAsync(shipId, ct);
            }
            catch (Exception ex) when (!ScrapeCancellation.IsShutdown(ex, ct))
            {
                // The verifier records its own inconclusive outcomes; reaching here means it threw
                // outside that path, so this ship is skipped and picked up on a later tick.
                _logger.LogError(ex, "Verification of ship {ShipId} failed unexpectedly", shipId);
            }
        }
    }
}
