using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

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
/// AO3's markup will start carrying next.
///
/// A handful of elements are dropped whole rather than unwrapped, because their contents are not
/// prose: unwrapping a <c>&lt;script&gt;</c> would leave its source behind as text.
///
/// The walk is iterative. Summaries are untrusted input, so their nesting depth is untrusted input
/// too, and a recursive walk over markup nested a few thousand deep would take the request down
/// with it.
/// </remarks>
public static class WorkSummaryHtml
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// What a summary is allowed to be made of: the formatting a prose summary actually uses.
    /// Deliberately short — an element earns a place here by appearing in real summaries, not by
    /// being harmless.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "p", "br", "hr",
        "em", "strong", "i", "b", "u", "s", "del", "ins", "small", "sub", "sup",
        "blockquote", "q", "cite",
        "ul", "ol", "li", "dl", "dt", "dd",
        "code", "pre",
    };

    /// <summary>
    /// Elements dropped with everything inside them. These hold script, style, raw text or embedded
    /// content rather than prose, so unwrapping one would spill its innards into the summary as
    /// words.
    /// </summary>
    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "noframes", "template", "title", "head",
        "iframe", "frame", "frameset", "object", "embed", "applet", "canvas", "svg", "math",
        "audio", "video", "source", "track", "param", "picture",
        "form", "input", "button", "select", "option", "optgroup", "textarea",
        "base", "link", "meta", "xmp", "plaintext",
    };

    /// <summary>Allowed elements with no closing tag and no contents.</summary>
    private static readonly HashSet<string> Void = new(StringComparer.Ordinal) { "br", "hr" };

    /// <summary>
    /// How deep the output may nest. Past it elements are unwrapped rather than emitted, so markup
    /// nested a thousand deep — which no summary is by accident — cannot be handed on as a thousand
    /// nested elements for something downstream to choke on. The words are kept either way.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// The summary as markup a browser may be given, or null where there is nothing to show —
    /// including a summary that was only markup, which is a work with no summary rather than a work
    /// whose summary is empty.
    /// </summary>
    public static string? Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var body = Parser.ParseDocument(html).Body;
        if (body is null) return null;

        var output = new StringBuilder();
        var hasWords = false;

        // Pending work, deepest-first. Holds either a node still to be walked or a literal closing
        // tag to emit once its children are done, which is what makes the walk iterative.
        var pending = new Stack<Step>();
        PushChildren(pending, body, depth: 0);

        while (pending.Count > 0)
        {
            var (node, close, depth) = pending.Pop();

            if (close is not null)
            {
                output.Append(close);
                continue;
            }

            switch (node)
            {
                case IText text:
                    output.Append(WebUtility.HtmlEncode(text.Data));
                    hasWords |= !string.IsNullOrWhiteSpace(text.Data);
                    break;

                case IElement element:
                    var name = element.LocalName;

                    if (Dropped.Contains(name)) break;

                    if (!Allowed.Contains(name) || depth >= MaxDepth)
                    {
                        // Unwrapped: the element goes, its words stay. Unwrapping does not nest, so
                        // the depth its children are walked at is this one.
                        PushChildren(pending, element, depth);
                        break;
                    }

                    output.Append('<').Append(name).Append('>');

                    if (Void.Contains(name)) break;

                    // Pushed before the children so it is popped after them.
                    pending.Push(new Step(null, $"</{name}>", depth));
                    PushChildren(pending, element, depth + 1);
                    break;

                // Comments, processing instructions and doctypes carry nothing a reader wants.
            }
        }

        return hasWords ? output.ToString() : null;
    }

    /// <summary>Reversed, so the stack pops them back into document order.</summary>
    private static void PushChildren(Stack<Step> pending, INode parent, int depth)
    {
        for (var i = parent.ChildNodes.Length - 1; i >= 0; i--)
        {
            pending.Push(new Step(parent.ChildNodes[i], null, depth));
        }
    }

    /// <summary>One item of pending work: a node to walk, or a closing tag to emit.</summary>
    private readonly record struct Step(INode? Node, string? Close, int Depth);
}
