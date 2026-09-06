using System.Net;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Ao3Tracker.Api.Services.Html;

/// <summary>
/// What one sanitizer keeps. A policy names the elements that survive and, per element, the
/// attributes it has checked and wants emitted; everything it does not name is unwrapped to its
/// words. <see cref="HtmlAllowlist.Sanitize"/> is the walk that applies it.
/// </summary>
/// <remarks>
/// Two policies exist — <see cref="WorkSummaryHtml"/>, which keeps no attribute at all, and
/// <see cref="WorkChapterHtml"/>, which keeps the three a chapter cannot read without. They share
/// the walk rather than each carrying a copy of it because the walk is where the safety lives: the
/// drop list, the depth cap and the iterative traversal are decisions that must not drift apart
/// between the two places author-written markup reaches a reader's browser.
/// </remarks>
public abstract class HtmlAllowlistPolicy
{
    /// <summary>Element names emitted as themselves. Everything else is unwrapped or dropped.</summary>
    public abstract IReadOnlySet<string> Allowed { get; }

    /// <summary>
    /// Elements dropped with everything inside them. The default is what neither policy has any use
    /// for: script, style, raw text and embedded content, whose innards are not prose.
    /// </summary>
    public virtual IReadOnlySet<string> Dropped => HtmlAllowlist.DefaultDropped;

    /// <summary>
    /// Whether this particular allowed element is emitted. Called only for elements whose name is
    /// in <see cref="Allowed"/>; answering false unwraps the element as if it were not. The hook
    /// for an element whose worth depends on an attribute — an image with no usable source is
    /// nothing at all.
    /// </summary>
    public virtual bool Keep(IElement element) => true;

    /// <summary>
    /// Writes the attributes this policy keeps for an emitted element, each through
    /// <see cref="HtmlAllowlist.AppendAttribute"/>. The default writes none: an attribute is where
    /// a URL or an event handler hides, so keeping one is a decision a policy makes by name.
    /// </summary>
    public virtual void AppendAttributes(IElement element, StringBuilder output)
    {
    }

    /// <summary>
    /// Whether an emitted element counts as content in its own right, so that a document made
    /// only of such elements is not reported as empty. Text always counts; this is for the element
    /// that carries something a reader wants without words in it.
    /// </summary>
    public virtual bool IsContent(IElement element) => false;
}

/// <summary>
/// The allowlist walk behind every sanitizer in this directory.
/// </summary>
/// <remarks>
/// The rule is an allowlist of element <i>names</i> and, per policy, an allowlist of checked
/// attributes. Elements not on the list are unwrapped to their contents — an anchor a policy does
/// not keep becomes its own words — and a handful are dropped whole, because their contents are
/// not prose: unwrapping a <c>&lt;script&gt;</c> would leave its source behind as text.
///
/// The walk is iterative. The input is untrusted, so its nesting depth is untrusted too, and a
/// recursive walk over markup nested a few thousand deep would take the request down with it.
/// </remarks>
public static class HtmlAllowlist
{
    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// Elements dropped with everything inside them, unless a policy says otherwise. These hold
    /// script, style, raw text or embedded content rather than prose, so unwrapping one would spill
    /// its innards into the output as words.
    /// </summary>
    public static readonly IReadOnlySet<string> DefaultDropped = new HashSet<string>(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "noframes", "template", "title", "head",
        "iframe", "frame", "frameset", "object", "embed", "applet", "canvas", "svg", "math",
        "audio", "video", "source", "track", "param", "picture",
        "form", "input", "button", "select", "option", "optgroup", "textarea",
        "base", "link", "meta", "xmp", "plaintext",
    };

    /// <summary>Elements with no closing tag and no contents, should a policy allow them.</summary>
    private static readonly HashSet<string> Void = new(StringComparer.Ordinal) { "br", "hr", "img" };

    /// <summary>
    /// How deep the output may nest. Past it elements are unwrapped rather than emitted, so markup
    /// nested a thousand deep — which nothing is by accident — cannot be handed on as a thousand
    /// nested elements for something downstream to choke on. The words are kept either way.
    /// </summary>
    public const int MaxDepth = 64;

    /// <summary>
    /// The markup a browser may be given, or null where there is nothing to show — including input
    /// that was only markup, which is a document with nothing in it rather than an empty one.
    /// </summary>
    public static string? Sanitize(string? html, HtmlAllowlistPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var body = Parser.ParseDocument(html).Body;
        if (body is null) return null;

        var output = new StringBuilder();
        var hasContent = false;

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
                    hasContent |= !string.IsNullOrWhiteSpace(text.Data);
                    break;

                case IElement element:
                    var name = element.LocalName;

                    if (policy.Dropped.Contains(name)) break;

                    if (!policy.Allowed.Contains(name) || depth >= MaxDepth || !policy.Keep(element))
                    {
                        // Unwrapped: the element goes, its words stay. Unwrapping does not nest, so
                        // the depth its children are walked at is this one.
                        PushChildren(pending, element, depth);
                        break;
                    }

                    output.Append('<').Append(name);
                    policy.AppendAttributes(element, output);
                    output.Append('>');
                    hasContent |= policy.IsContent(element);

                    if (Void.Contains(name)) break;

                    // Pushed before the children so it is popped after them.
                    pending.Push(new Step(null, $"</{name}>", depth));
                    PushChildren(pending, element, depth + 1);
                    break;

                // Comments, processing instructions and doctypes carry nothing a reader wants.
            }
        }

        return hasContent ? output.ToString() : null;
    }

    /// <summary>
    /// One attribute, encoded for the position it is going into. The only way a policy writes one,
    /// so that no value reaches the output unencoded whatever it was checked for.
    /// </summary>
    public static void AppendAttribute(StringBuilder output, string name, string value) =>
        output.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');

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
