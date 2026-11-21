using Ao3Tracker.Api.Services.Settings;

namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// An admin's switch for stopping every request to AO3 while keeping the site itself up: the
/// library, ratings, notes and downloaded works all stay readable, and the links to AO3 open in
/// the reader's own browser. Made for an archive that is struggling, when even a backed-off
/// instance is more than it wants.
/// </summary>
/// <remarks>
/// Saved to settings.json as well as set on <see cref="Ao3RateGate"/>, so a restart or a redeploy
/// comes back paused. <see cref="RestoreAsync"/> runs at startup, before any worker.
/// </remarks>
public sealed class Ao3RequestPause
{
    private readonly Ao3RateGate _gate;
    private readonly IPersistedSettingsStore _settings;
    private readonly TimeProvider _time;

    public Ao3RequestPause(Ao3RateGate gate, IPersistedSettingsStore settings, TimeProvider time)
    {
        _gate = gate;
        _settings = settings;
        _time = time;
    }

    /// <summary>
    /// Pauses requests, or keeps an existing pause and its date. Saved before the gate is set, so
    /// a pause that could not be saved is not one that silently ends at the next restart.
    /// </summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        var since = _gate.PausedSince ?? _time.GetUtcNow();

        await _settings.SaveAo3PausedSinceAsync(since, ct);
        _gate.Pause(since);
    }

    public async Task ResumeAsync(CancellationToken ct = default)
    {
        await _settings.SaveAo3PausedSinceAsync(null, ct);
        _gate.Resume();
    }

    /// <summary>Puts a saved pause back on the gate. For startup.</summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        if (await _settings.ReadAo3PausedSinceAsync(ct) is { } since) _gate.Pause(since);
    }
}
