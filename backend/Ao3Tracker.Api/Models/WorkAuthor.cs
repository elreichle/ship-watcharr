namespace Ao3Tracker.Api.Models;

/// <summary>
/// Join between <see cref="Work"/> and <see cref="Ao3Pseud"/>. Composite PK (WorkId, PseudId).
///
/// Only creators marked <c>rel="author"</c> in the blurb become rows here. That filter is
/// mandatory rather than incidental: AO3 renders gift recipients as links inside the very same
/// heading element, so selecting all anchors would record recipients as co-authors.
/// </summary>
public class WorkAuthor
{
    public long WorkId { get; set; }
    public Work Work { get; set; } = null!;

    public int PseudId { get; set; }
    public Ao3Pseud Pseud { get; set; } = null!;

    /// <summary>Byline order, 0-based. Works with several creators are common.</summary>
    public int Position { get; set; }
}
