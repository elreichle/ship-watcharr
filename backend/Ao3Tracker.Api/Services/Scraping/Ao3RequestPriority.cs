namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// How urgently a request needs the instance's one outbound channel, for <see cref="Ao3RateGate"/>
/// to order waiters by. Lower is sooner.
/// </summary>
/// <remarks>
/// Priority changes the order requests go out in, never how many go out or how far apart: the
/// gate's spacing is the same whoever is at the front. What it buys is that a reader who clicked
/// Download is not queued behind a backfill page nobody is waiting for.
/// </remarks>
public enum Ao3RequestPriority
{
    /// <summary>Somebody is waiting on this — a download they asked for, a ship they just followed.</summary>
    Interactive = 0,

    /// <summary>A scheduled pass: the ship walks. The default when nothing says otherwise.</summary>
    Scheduled = 1,

    /// <summary>Filling in the library behind the scenes: work detail pages.</summary>
    Background = 2,
}

/// <summary>
/// The priority in force for the requests made inside a scope, carried ambiently so that the
/// scrapers and fetchers between a worker and the transport do not each need a parameter for
/// something none of them decides.
/// </summary>
/// <example>
/// <code>
/// using (Ao3AmbientPriority.Enter(Ao3RequestPriority.Interactive))
///     await fetcher.FetchAsync(id, ct);
/// </code>
/// </example>
public static class Ao3AmbientPriority
{
    private static readonly AsyncLocal<Ao3RequestPriority?> Ambient = new();

    /// <summary>The priority in force, or <see cref="Ao3RequestPriority.Scheduled"/> outside any scope.</summary>
    public static Ao3RequestPriority Current => Ambient.Value ?? Ao3RequestPriority.Scheduled;

    /// <summary>Puts <paramref name="priority"/> in force until the returned scope is disposed.</summary>
    public static IDisposable Enter(Ao3RequestPriority priority)
    {
        var previous = Ambient.Value;
        Ambient.Value = priority;
        return new Scope(previous);
    }

    private sealed class Scope(Ao3RequestPriority? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
