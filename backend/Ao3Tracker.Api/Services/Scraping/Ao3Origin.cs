namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Whether an address read off a fetched page addresses the archive this deployment is configured
/// for, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The one rule shared by every place a URL that came out of AO3's own markup or out of AO3's own
/// redirects decides where something goes: the login POST that carries the deployment's password
/// (<see cref="Ao3SessionEstablisher"/>), and the download links fetched with its session cookie
/// attached (<see cref="Ao3DownloadLinks"/>). Measured against
/// <see cref="Ao3HttpClientOptions.BaseUrl"/> in both, because that is the one address in those
/// flows no page can influence.
/// </para>
/// <para>
/// The whole origin — scheme, host and port together — rather than the host alone. What travels to
/// these addresses is a password or a session cookie, and one that kept the name and dropped to
/// <c>http</c> would put it on the wire in the clear; a host comparison says yes to that.
/// Ordinal-ignore-case because that is how an origin is compared: AO3 is one archive and this is
/// not the place to learn about anybody's subdomains.
/// </para>
/// </remarks>
public static class Ao3Origin
{
    /// <summary>
    /// Whether <paramref name="candidate"/> is an http(s) address on <paramref name="configured"/>'s
    /// origin. False whenever either is missing or is not a web address: nothing checkable is
    /// nothing this app may send to.
    /// </summary>
    public static bool IsTheConfiguredArchive(string? candidate, string? configured) =>
        Uri.TryCreate(candidate, UriKind.Absolute, out var target)
        && Uri.TryCreate(configured, UriKind.Absolute, out var archive)
        && IsTheConfiguredArchive(target, archive);

    /// <inheritdoc cref="IsTheConfiguredArchive(string?, string?)"/>
    public static bool IsTheConfiguredArchive(Uri candidate, Uri configured) =>
        IsWeb(candidate)
        && IsWeb(configured)
        && string.Equals(
            candidate.GetLeftPart(UriPartial.Authority),
            configured.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a URL addresses the archive's dedicated download host: exactly <c>download.</c>
    /// prefixed to the configured authority, same scheme. AO3 serves every download by 302ing
    /// there (verified against the live archive, 2026-09-05), so the redirect walk may continue
    /// onto it — carrying the instance's identity, never its session, whose reach is decided by
    /// <see cref="IsTheConfiguredArchive(Uri, Uri)"/> alone and does not widen with this.
    /// </summary>
    public static bool IsTheArchivesDownloadHost(Uri candidate, Uri configured) =>
        IsWeb(candidate)
        && IsWeb(configured)
        && string.Equals(candidate.Scheme, configured.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            candidate.Authority,
            "download." + configured.Authority,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a URL addresses the web at all.
    /// </summary>
    /// <remarks>
    /// Not ceremony. <c>Uri.TryCreate("/downloads/1/x.epub", UriKind.Absolute, …)</c> <em>succeeds</em>
    /// on Linux, yielding <c>file:///downloads/1/x.epub</c> — so a bare path read off a page would be
    /// treated as absolute and then resolved against local disk. The login establisher was found
    /// doing exactly that.
    /// </remarks>
    public static bool IsWeb(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
}
