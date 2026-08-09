using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// The values a filter can be built out of: AO3's controlled vocabulary, and the tags and authors
/// this reader's library actually contains.
///
/// Tag and author searches are scoped to the caller's watched ships rather than the whole table.
/// That is a usefulness decision before a privacy one — a picker offering every tag on the instance
/// would mostly suggest criteria that match none of your works — but it also keeps one user's
/// library from being enumerated through another's autocomplete.
/// </summary>
[ApiController]
[Authorize]
[Route("api/lookups")]
public class LookupsController : ControllerBase
{
    /// <summary>
    /// How many suggestions one search returns. A picker shows a shortlist; anything longer is
    /// answered by typing more, not by scrolling.
    /// </summary>
    private const int MaxResults = 20;

    private readonly AppDbContext _db;

    public LookupsController(AppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    /// <summary>
    /// Ratings, categories, warnings and sorts, each as a value the filter API takes paired with
    /// AO3's own wording for it. Served rather than hard-coded in the client so there is one copy
    /// of this vocabulary — see the remarks on <see cref="Ao3Labels"/>.
    /// </summary>
    [HttpGet("vocabulary")]
    public async Task<ActionResult<FilterVocabularyDto>> GetVocabulary(CancellationToken ct)
    {
        var userId = CurrentUserId;

        // Only languages that appear in this reader's library. Offering AO3's full language list
        // would fill the picker with choices that match nothing here.
        var languages = await WorkQueries.Library(_db, userId, shipId: null)
            .Where(w => w.LanguageCode != null && w.LanguageName != null)
            .Select(w => new { Code = w.LanguageCode!, Name = w.LanguageName! })
            .Distinct()
            .OrderBy(l => l.Name)
            .Take(MaxResults * 5)
            .ToListAsync(ct);

        return Ok(new FilterVocabularyDto(
            [.. Ao3Labels.Ratings.Select(r => new VocabularyOptionDto(r.Value, r.Label))],
            [.. Ao3Labels.Categories.Select(c => new VocabularyOptionDto(c.Value, c.Label))],
            [.. Ao3Labels.Warnings.Select(w => new VocabularyOptionDto(w.Value, w.Label))],
            [.. WorkQueries.Sorts.Select(s => new VocabularyOptionDto(s, DescribeSort(s)))],
            [.. languages.Select(l => new VocabularyOptionDto(l.Code, l.Name))]));
    }

    private static string DescribeSort(string sort) => sort switch
    {
        "updated" => "Last updated",
        "kudos" => "Kudos",
        "hits" => "Hits",
        "bookmarks" => "Bookmarks",
        "comments" => "Comments",
        "words" => "Word count",
        _ => sort,
    };

    /// <param name="q">Matched as a substring of the tag name, case-insensitively.</param>
    /// <param name="type">"Fandom", "Relationship", "Character", "Freeform" or "Warning". Omit for
    /// all of them.</param>
    [HttpGet("tags")]
    public async Task<ActionResult<IReadOnlyList<SavedFilterTagDto>>> SearchTags(
        [FromQuery] string? q = null,
        [FromQuery] string? type = null,
        CancellationToken ct = default)
    {
        var userId = CurrentUserId;

        Ao3TagType? tagType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<Ao3TagType>(type, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                ModelState.AddModelError(nameof(type), $"'{type}' isn't a tag type AO3 has.");
                return ValidationProblem(ModelState);
            }

            tagType = parsed;
        }

        // Compared as ids rather than as entities: an id subquery becomes a plain IN (...), which
        // both providers translate, while Contains() over an entity queryable does not reliably.
        var libraryWorkIds = WorkQueries.Library(_db, userId, shipId: null).Select(w => w.Id);

        var tags = _db.Tags.Where(t => t.Works.Any(wt => libraryWorkIds.Contains(wt.WorkId)));
        if (tagType is Ao3TagType only) tags = tags.Where(t => t.Type == only);

        // Searched through NameNormalized, never Name: SQLite's LIKE is case-insensitive and
        // PostgreSQL's is not, so comparing the display column would quietly return different
        // results per provider. See the remarks on Tag.NameNormalized.
        if (Normalize(q) is string search) tags = tags.Where(t => t.NameNormalized.Contains(search));

        var rows = await tags
            .OrderBy(t => t.NameNormalized)
            .Take(MaxResults)
            .Select(t => new { t.Id, t.Name, t.Type })
            .ToListAsync(ct);

        return Ok(rows.Select(t => new SavedFilterTagDto(t.Id, t.Name, t.Type.ToString())).ToList());
    }

    /// <param name="q">Matched as a substring of the byline, case-insensitively.</param>
    [HttpGet("authors")]
    public async Task<ActionResult<IReadOnlyList<SavedFilterAuthorDto>>> SearchAuthors(
        [FromQuery] string? q = null,
        CancellationToken ct = default)
    {
        var userId = CurrentUserId;

        var libraryWorkIds = WorkQueries.Library(_db, userId, shipId: null).Select(w => w.Id);

        var pseuds = _db.Ao3Pseuds.Where(p => p.Works.Any(wa => libraryWorkIds.Contains(wa.WorkId)));

        // DisplayNameNormalized for the same provider-portability reason as tags above.
        if (Normalize(q) is string search)
            pseuds = pseuds.Where(p => p.DisplayNameNormalized.Contains(search));

        var rows = await pseuds
            .OrderBy(p => p.DisplayNameNormalized)
            .Take(MaxResults)
            .Select(p => new SavedFilterAuthorDto(p.Id, p.DisplayName, p.Username))
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>The uppercased form the normalized columns hold, or null for a blank search.</summary>
    private static string? Normalize(string? q) =>
        string.IsNullOrWhiteSpace(q) ? null : q.Trim().ToUpperInvariant();
}
