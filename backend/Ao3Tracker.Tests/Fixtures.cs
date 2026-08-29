namespace Ao3Tracker.Tests;

/// <summary>
/// Captured AO3 pages, read off disk beside the test assembly.
///
/// Files rather than inline constants: these are whole pages someone saved from the archive, and
/// the point of them is that nothing was tidied up on the way in. A parser that passes against a
/// hand-written snippet has only been tested against what its author remembered.
/// </summary>
internal static class Fixtures
{
    /// <summary>
    /// <c>https://archiveofourown.org/users/login</c>, logged out. Carries two login forms — the
    /// header dropdown and the real one — which is the trap <see cref="Api.Services.Scraping.Ao3LoginPage"/>
    /// is written around.
    /// </summary>
    public const string LoginPage = "ao3-login-page.html";

    /// <summary>A work's own page, captured while logged in — so it carries the greeting header.</summary>
    public const string WorkPage = "ao3-work-page.html";

    /// <summary>A works index with no results, also captured while logged in.</summary>
    public const string EmptyListing = "ao3-empty-listing.html";

    /// <summary>
    /// One page of <c>/tags/Clarke Griffin*s*Lexa/works</c>, captured **logged out** on
    /// 2026-08-29. Its pair is <see cref="AuthenticatedListing"/>: the same URL, the same sort and
    /// the same moment, captured with a session. They are only useful together — see
    /// <c>Ao3RestrictedWorkVisibilityTests</c>, which is the whole reason both exist.
    /// </summary>
    public const string AnonymousListing = "ao3-anonymous-listing.html";

    /// <summary>The logged-in half of the pair above. Identity and CSRF token redacted.</summary>
    public const string AuthenticatedListing = "ao3-authenticated-listing.html";

    public static string Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Fixture '{name}' is not beside the test assembly.", path);

        return File.ReadAllText(path);
    }
}
