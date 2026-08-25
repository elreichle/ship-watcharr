using Microsoft.Net.Http.Headers;

namespace Ao3Tracker.Api.Services.Scraping;

/// <param name="ExpiresAt">
/// When the server said this cookie stops being valid, or null for a session cookie it gave no end
/// date for.
/// </param>
public sealed record Ao3Cookie(string Value, DateTime? ExpiresAt);

/// <summary>
/// Turns the <c>Set-Cookie</c> headers of a response into the <c>Cookie</c> header of the next one.
///
/// Hand-rolled rather than delegated to a <see cref="System.Net.CookieContainer"/> because the jar
/// this deployment uses is a database row shared by every process reading its data, not a per-handler
/// cache that dies with the socket. A container would be a second, divergent copy of the session.
///
/// Nothing here knows AO3's cookie names. The session is "whatever the login response set", which is
/// the only description that survives the archive renaming anything.
///
/// The jar carries each cookie's expiry alongside its value rather than deriving the session's
/// lifetime from the raw headers, because those are not the same set: a header can announce a
/// *deletion*, and a deletion's date is in the past by construction. Reading the lifetime off the
/// headers would date the whole session to whenever the oldest thing AO3 retired was.
/// </summary>
public static class Ao3Cookies
{
    /// <summary>
    /// Applies a response's <c>Set-Cookie</c> headers on top of the cookies already held, in order.
    /// A cookie set to an empty value or to an expiry that has already passed is a deletion and is
    /// removed rather than carried forward — that is how a server retires one.
    /// </summary>
    public static IReadOnlyDictionary<string, Ao3Cookie> Apply(
        IReadOnlyDictionary<string, Ao3Cookie> held,
        IEnumerable<string> setCookieHeaders,
        DateTime utcNow)
    {
        var jar = new Dictionary<string, Ao3Cookie>(held, StringComparer.Ordinal);

        foreach (var header in setCookieHeaders)
        {
            if (!SetCookieHeaderValue.TryParse(header, out var cookie)) continue;

            var name = cookie.Name.ToString();
            if (name.Length == 0) continue;

            var expiry = ExpiryOf(cookie, utcNow);

            if (cookie.Value.Length == 0 || expiry <= utcNow) jar.Remove(name);
            else jar[name] = new Ao3Cookie(cookie.Value.ToString(), expiry);
        }

        return jar;
    }

    /// <summary>An empty jar, to apply the first response's cookies onto.</summary>
    public static IReadOnlyDictionary<string, Ao3Cookie> Empty { get; } =
        new Dictionary<string, Ao3Cookie>(StringComparer.Ordinal);

    /// <summary>The <c>Cookie</c> header for a set of held cookies, or null when there are none.</summary>
    public static string? ToHeader(IReadOnlyDictionary<string, Ao3Cookie> jar) =>
        jar.Count == 0 ? null : string.Join("; ", jar.Select(pair => $"{pair.Key}={pair.Value.Value}"));

    /// <summary>
    /// The soonest any cookie the jar is actually holding stops being valid, or null when every one
    /// of them is a session cookie with no stated end.
    ///
    /// The earliest rather than the latest: the session is only whole while all of its parts are, so
    /// the first expiry is when it stops being the thing that was stored. Null is the honest answer
    /// for a session cookie, and means "usable until a page proves otherwise" — never "usable
    /// forever".
    ///
    /// Taken over the jar rather than over the headers that built it, which is not a detail: a
    /// header announcing a deletion carries a date already gone by, and counting it would date the
    /// session to the past and force a fresh login on every single poll.
    /// </summary>
    public static DateTime? EarliestExpiry(IReadOnlyDictionary<string, Ao3Cookie> jar)
    {
        DateTime? earliest = null;

        foreach (var expiry in jar.Values.Select(cookie => cookie.ExpiresAt))
        {
            if (expiry is null) continue;
            if (earliest is null || expiry < earliest) earliest = expiry;
        }

        return earliest;
    }

    /// <summary>
    /// When a cookie stops being valid. <c>Max-Age</c> wins over <c>Expires</c> where both are
    /// present, as the cookie spec requires — the two disagree whenever a client's clock is off,
    /// and <c>Max-Age</c> is the one that does not depend on it.
    /// </summary>
    private static DateTime? ExpiryOf(SetCookieHeaderValue cookie, DateTime utcNow) =>
        cookie.MaxAge is { } maxAge ? utcNow + maxAge
        : cookie.Expires is { } expires ? expires.UtcDateTime
        : null;
}
