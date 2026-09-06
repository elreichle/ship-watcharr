namespace Ao3Tracker.Api.Services.Html;

/// <summary>
/// Turns a work's stored summary into markup that is safe to render.
///
/// <c>Work.SummaryHtml</c> is HTML an anonymous stranger typed into AO3 and this app stored exactly
/// as it was published — the scraper deliberately keeps it verbatim rather than deciding at write
/// time what a future reader may see. So the decision is made here, once, on the way out: an
/// endpoint hands a client sanitized markup, and no client has to remember to sanitize it itself.
/// </summary>
/// <remarks>
/// The rule is an allowlist of element <i>names</i> and <b>no attributes at all</b>. Every attribute
/// is dropped even from elements that are kept, and elements that are not on the list are unwrapped
/// to their contents — an anchor becomes its own words. That costs a summary its links, which is a
/// real loss and the price of a rule with no URL parsing in it: nothing here has to be right about
/// which schemes are safe, which encodings of <c>javascript:</c> are equivalent, or which attribute
/// AO3's markup will start carrying next. The walk itself is <see cref="HtmlAllowlist"/>; this is
/// the policy it applies.
/// </remarks>
public static class WorkSummaryHtml
{
    /// <summary>
    /// What a summary is allowed to be made of: the formatting a prose summary actually uses.
    /// Deliberately short — an element earns a place here by appearing in real summaries, not by
    /// being harmless.
    /// </summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "p", "br", "hr",
        "em", "strong", "i", "b", "u", "s", "del", "ins", "small", "sub", "sup",
        "blockquote", "q", "cite",
        "ul", "ol", "li", "dl", "dt", "dd",
        "code", "pre",
    };

    private sealed class Policy : HtmlAllowlistPolicy
    {
        public static readonly Policy Instance = new();

        public override IReadOnlySet<string> Allowed => WorkSummaryHtml.Allowed;
    }

    /// <summary>
    /// The summary as markup a browser may be given, or null where there is nothing to show —
    /// including a summary that was only markup, which is a work with no summary rather than a work
    /// whose summary is empty.
    /// </summary>
    public static string? Sanitize(string? html) => HtmlAllowlist.Sanitize(html, Policy.Instance);
}
