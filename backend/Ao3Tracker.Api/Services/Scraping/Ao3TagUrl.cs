namespace Ao3Tracker.Api.Services.Scraping;

/// <summary>
/// Turns a tag's display name into the path segment AO3 addresses it by, i.e. the
/// <c>{segment}</c> in <c>/tags/{segment}/works</c>.
///
/// AO3 cannot percent-encode these characters and be understood by its own router, so it
/// substitutes them for asterisk-delimited digraphs *before* the segment is percent-encoded.
/// That is why this is a two-step transform rather than a call to
/// <see cref="Uri.EscapeDataString(string)"/>: encoding alone produces a URL AO3 404s on.
///
/// Once a ship's <c>Ao3TagId</c> has been harvested, <c>/tags/{id}/works</c> is the better
/// address — it survives the tag being renamed, which this one does not. This exists to make
/// the first request, before any id is known.
/// </summary>
public static class Ao3TagUrl
{
    /// <summary>
    /// AO3's substitutions, in the order applied. None of the replacements introduces a
    /// character that is itself a key, so applying them one after another is safe — a later
    /// pass can never re-escape an earlier pass's output.
    /// </summary>
    private static readonly (char Character, string Escape)[] Substitutions =
    [
        ('/', "*s*"),
        ('&', "*a*"),
        ('.', "*d*"),
        ('?', "*q*"),
        ('#', "*h*"),
    ];

    public static string ToUrlSegment(string tagName)
    {
        ArgumentNullException.ThrowIfNull(tagName);

        var substituted = tagName;
        foreach (var (character, escape) in Substitutions)
            substituted = substituted.Replace(character.ToString(), escape, StringComparison.Ordinal);

        // Encode whole-string rather than per character: a name containing an emoji or any other
        // astral-plane character is a surrogate pair, and encoding half of one produces mojibake.
        //
        // .NET escapes '*' as %2A (RFC 3986 reserves it), which would destroy the digraphs the
        // step above just wrote, so they are put back. Nothing else can have produced a '*': AO3
        // does not permit one in a tag name, which is precisely what makes it usable as a
        // delimiter.
        return Uri.EscapeDataString(substituted).Replace("%2A", "*", StringComparison.Ordinal);
    }

    /// <summary>
    /// The inverse: recovers a tag's display name from a path segment. Used to read the canonical
    /// tag out of the URL AO3 redirects a synonym to, which is more reliable than parsing it out of
    /// the page — the heading's markup is AO3's to change, while the URL is its public interface.
    /// </summary>
    public static string FromUrlSegment(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        // Decoded first, so a name containing a percent-encoded asterisk cannot be mistaken for a
        // digraph delimiter — the substitutions were applied before encoding, so they come off after.
        var decoded = Uri.UnescapeDataString(segment);

        foreach (var (character, escape) in Substitutions)
            decoded = decoded.Replace(escape, character.ToString(), StringComparison.Ordinal);

        return decoded;
    }

    /// <summary>
    /// Pulls the tag segment out of a works-index URL, or null if the URL is not one.
    ///
    /// Null is the important case: a redirect to a login page or an error page means the response
    /// says nothing about what the tag is called, and treating it as an answer would rename a
    /// user's ship to something like "login".
    /// </summary>
    public static string? TryGetTagSegment(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;

        // Matches /tags/{segment}/works, and nothing else. Segments come back with their trailing
        // slashes, hence the trim.
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        if (!parts[0].Equals("tags", StringComparison.OrdinalIgnoreCase)) return null;
        if (!parts[2].Equals("works", StringComparison.OrdinalIgnoreCase)) return null;

        return parts[1];
    }
}
