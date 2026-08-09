using System.Security.Claims;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Controllers;

/// <summary>
/// Named, reusable sets of library criteria — AO3's filter sidebar, saved. PER-USER: a set is
/// private to the account that made it, and every lookup below is scoped by the caller's id rather
/// than by the row's own visibility.
///
/// Sets are validated hard on the way in and trusted on the way out. A criterion that cannot be
/// satisfied — a tag id that doesn't exist, a minimum above its maximum, a sort the works list
/// doesn't offer — is rejected here, so that applying a saved set is a query and never a second
/// round of checks. The one exception is <see cref="SavedWorkFilter.ShipId"/>, which is checked
/// against the caller's subscriptions when saved and deliberately not when applied; see the remarks
/// on that property.
/// </summary>
[ApiController]
[Authorize]
[Route("api/saved-filters")]
public class SavedFiltersController : ControllerBase
{
    /// <summary>
    /// Ceiling on tag and author criteria per set, per include/exclude list. Each included tag
    /// becomes its own <c>EXISTS</c> clause, so an unbounded list is an unbounded query — and no
    /// real saved view narrows by fifty tags at once.
    /// </summary>
    private const int MaxCriteriaPerList = 50;

    private readonly AppDbContext _db;

    public SavedFiltersController(AppDbContext db)
    {
        _db = db;
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException("Authenticated request missing user id claim.");

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SavedFilterDto>>> GetFilters(CancellationToken ct)
    {
        var userId = CurrentUserId;

        var filters = await LoadAsync(userId, id: null, ct);

        var dtos = new List<SavedFilterDto>(filters.Count);
        foreach (var filter in filters) dtos.Add(await DescribeAsync(userId, filter, ct));

        return Ok(dtos);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SavedFilterDto>> GetFilter(int id, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var filter = (await LoadAsync(userId, id, ct)).SingleOrDefault();
        if (filter is null) return NotFound();

        return Ok(await DescribeAsync(userId, filter, ct));
    }

    [HttpPost]
    public async Task<ActionResult<SavedFilterDto>> CreateFilter(SaveFilterRequest request, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var criteria = await ValidateAsync(userId, request, existingId: null, ct);
        if (criteria is null) return ValidationProblem(ModelState);

        var filter = new SavedWorkFilter { UserId = userId };
        criteria.ApplyTo(filter);

        _db.SavedWorkFilters.Add(filter);
        await SaveWithDefaultInvariantAsync(userId, filter, ct);

        var saved = (await LoadAsync(userId, filter.Id, ct)).Single();
        return CreatedAtAction(nameof(GetFilter), new { id = filter.Id }, await DescribeAsync(userId, saved, ct));
    }

    /// <summary>
    /// Replaces a set outright. Not a patch: every criterion is nullable and null means
    /// "unconstrained", so a merge could not express "stop filtering by word count" — the omitted
    /// field and the cleared one arrive identical.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<SavedFilterDto>> UpdateFilter(
        int id,
        SaveFilterRequest request,
        CancellationToken ct)
    {
        var userId = CurrentUserId;

        var filter = (await LoadAsync(userId, id, ct)).SingleOrDefault();
        if (filter is null) return NotFound();

        var criteria = await ValidateAsync(userId, request, existingId: id, ct);
        if (criteria is null) return ValidationProblem(ModelState);

        criteria.ApplyTo(filter);
        filter.UpdatedAt = DateTime.UtcNow;

        await SaveWithDefaultInvariantAsync(userId, filter, ct);

        var saved = (await LoadAsync(userId, id, ct)).Single();
        return Ok(await DescribeAsync(userId, saved, ct));
    }

    /// <summary>
    /// Marks a set as the one the works list opens with, or gives that up. Separate from
    /// <see cref="UpdateFilter"/> so the list page can toggle it without re-posting — and
    /// re-posting a set the user has not opened is how an editor with a stale copy silently
    /// reverts someone's criteria.
    /// </summary>
    [HttpPut("{id:int}/default")]
    public async Task<ActionResult<SavedFilterDto>> SetDefault(
        int id,
        SetDefaultFilterRequest request,
        CancellationToken ct)
    {
        var userId = CurrentUserId;

        var filter = (await LoadAsync(userId, id, ct)).SingleOrDefault();
        if (filter is null) return NotFound();

        filter.IsDefault = request.IsDefault;
        filter.UpdatedAt = DateTime.UtcNow;

        await SaveWithDefaultInvariantAsync(userId, filter, ct);

        return Ok(await DescribeAsync(userId, filter, ct));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteFilter(int id, CancellationToken ct)
    {
        var userId = CurrentUserId;

        var filter = await _db.SavedWorkFilters
            .FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId, ct);
        if (filter is null) return NotFound();

        // Tag and author criteria cascade; nothing else points at the set.
        _db.SavedWorkFilters.Remove(filter);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ---- loading and describing ---------------------------------------------------------------

    /// <summary>
    /// The caller's sets, or one of them, with the criteria collections loaded — <c>ApplyFilter</c>
    /// reads them directly, and a set with unloaded navigations would apply as if it had no tag or
    /// author criteria at all rather than failing.
    /// </summary>
    private async Task<List<SavedWorkFilter>> LoadAsync(string userId, int? id, CancellationToken ct) =>
        await _db.SavedWorkFilters
            .Where(f => f.UserId == userId)
            .Where(f => id == null || f.Id == id)
            .Include(f => f.Ship)
            .Include(f => f.Tags).ThenInclude(t => t.Tag)
            .Include(f => f.Authors).ThenInclude(a => a.Pseud)

            // Default first, then by name: the set that opens by default is the one a list of
            // saved views is really about.
            .OrderByDescending(f => f.IsDefault)
            .ThenBy(f => f.Name)
            .ToListAsync(ct);

    private async Task<SavedFilterDto> DescribeAsync(
        string userId,
        SavedWorkFilter filter,
        CancellationToken ct)
    {
        var matching = await WorkQueries
            .ApplyFilter(WorkQueries.Library(_db, userId, filter.ShipId), filter)
            .CountAsync(ct);

        IReadOnlyList<SavedFilterTagDto> Tags(bool exclude) =>
        [
            .. filter.Tags
                .Where(t => t.Exclude == exclude)
                .OrderBy(t => t.Tag.Name)
                .Select(t => new SavedFilterTagDto(t.TagId, t.Tag.Name, t.Tag.Type.ToString())),
        ];

        IReadOnlyList<SavedFilterAuthorDto> Authors(bool exclude) =>
        [
            .. filter.Authors
                .Where(a => a.Exclude == exclude)
                .OrderBy(a => a.Pseud.DisplayName)
                .Select(a => new SavedFilterAuthorDto(a.PseudId, a.Pseud.DisplayName, a.Pseud.Username)),
        ];

        return new SavedFilterDto(
            filter.Id,
            filter.Name,
            filter.IsDefault,
            filter.ShipId,
            filter.Ship?.CanonicalTagName,
            filter.IsComplete,
            filter.MinWordCount,
            filter.MaxWordCount,
            filter.MinChapterCount,
            filter.MaxChapterCount,
            filter.MinKudos,
            filter.MaxKudos,
            filter.MinHits,
            filter.MaxHits,
            filter.MinComments,
            filter.MaxComments,
            filter.MinBookmarks,
            filter.MaxBookmarks,
            filter.MinRating?.ToString(),
            filter.MaxRating?.ToString(),
            FlagNames(filter.IncludeCategories, Ao3Labels.Categories),
            FlagNames(filter.ExcludeCategories, Ao3Labels.Categories),
            FlagNames(filter.IncludeWarnings, Ao3Labels.Warnings),
            FlagNames(filter.ExcludeWarnings, Ao3Labels.Warnings),
            filter.LanguageCode,
            filter.UpdatedAfter,
            filter.UpdatedBefore,
            filter.Sort,
            filter.Ascending,
            Tags(exclude: false),
            Tags(exclude: true),
            Authors(exclude: false),
            Authors(exclude: true),
            matching,
            filter.CreatedAt,
            filter.UpdatedAt);
    }

    /// <summary>
    /// Expands a flags value into the enum names that are set, in the vocabulary's own order.
    /// Driven off <see cref="Ao3Labels"/> rather than <c>Enum.GetValues</c> so the wire format
    /// lists exactly the values the editor was offered — no more, no fewer.
    /// </summary>
    private static IReadOnlyList<string> FlagNames<TEnum>(
        TEnum? value,
        IReadOnlyList<(string Value, string Label)> vocabulary)
        where TEnum : struct, Enum
    {
        if (value is not TEnum set) return [];

        var bits = Convert.ToInt64(set);
        return
        [
            .. vocabulary
                .Where(option =>
                    Enum.TryParse<TEnum>(option.Value, out var flag) && (bits & Convert.ToInt64(flag)) != 0)
                .Select(option => option.Value),
        ];
    }

    // ---- the one-default-per-user invariant ---------------------------------------------------

    /// <summary>
    /// Commits <paramref name="filter"/>, clearing any other set that claimed the default in the
    /// same transaction.
    ///
    /// Code rather than a filtered unique index because <c>HasFilter</c> takes provider-specific
    /// SQL and one model serves both SQLite and PostgreSQL — see the remarks on
    /// <see cref="SavedWorkFilter.IsDefault"/>. The transaction is what makes it hold: two requests
    /// each marking a different set as default would otherwise both read "no other default" and
    /// both write one.
    /// </summary>
    private async Task SaveWithDefaultInvariantAsync(
        string userId,
        SavedWorkFilter filter,
        CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        if (filter.IsDefault)
        {
            // Saved first so the new set has an id to exclude by — on a create it has none until
            // the insert lands, and every other set would look like "some other row" including,
            // after the insert, itself.
            await _db.SaveChangesAsync(ct);

            var previous = await _db.SavedWorkFilters
                .Where(f => f.UserId == userId && f.IsDefault && f.Id != filter.Id)
                .ToListAsync(ct);

            foreach (var other in previous) other.IsDefault = false;
        }

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    // ---- validation ---------------------------------------------------------------------------

    /// <summary>
    /// Everything a request has to prove before it becomes a row. Null when it failed, with the
    /// reasons already on <c>ModelState</c>.
    /// </summary>
    private async Task<ValidatedCriteria?> ValidateAsync(
        string userId,
        SaveFilterRequest request,
        int? existingId,
        CancellationToken ct)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            ModelState.AddModelError(nameof(request.Name), "Give this filter a name.");
        }
        else
        {
            // Compared in memory, case-insensitively, because the unique index behind this differs
            // between the providers: SQLite would match "Long fics" against "long fics" and
            // PostgreSQL would not. A user has few sets, so the read costs nothing.
            var taken = await _db.SavedWorkFilters
                .Where(f => f.UserId == userId && f.Id != existingId)
                .Select(f => f.Name)
                .ToListAsync(ct);

            // Reported under the existing set's own spelling rather than the one just typed. They
            // differ only in case, which is exactly the collision the message has to explain — and
            // echoing the typed spelling back makes it read as though nothing clashed.
            if (taken.FirstOrDefault(each => string.Equals(each, name, StringComparison.OrdinalIgnoreCase))
                is string clash)
            {
                ModelState.AddModelError(
                    nameof(request.Name),
                    $"You already have a filter called “{clash}”.");
            }
        }

        if (!WorkQueries.IsOfferedSort(request.Sort))
        {
            ModelState.AddModelError(
                nameof(request.Sort),
                $"'{request.Sort}' is not a sort the works list offers.");
        }

        if (request.ShipId is int shipId
            && !await _db.WatchedShips.AnyAsync(w => w.UserId == userId && w.ShipId == shipId, ct))
        {
            // Checked here and not when the set is applied. Saving a filter against a ship you do
            // not follow is a mistake worth reporting; still holding one after you unfollow the
            // ship is not, and the works query already stops it reading outside your library.
            ModelState.AddModelError(nameof(request.ShipId), "You aren't following that ship.");
        }

        RequireOrdered(nameof(request.MinWordCount), request.MinWordCount, request.MaxWordCount, "word count");
        RequireOrdered(nameof(request.MinChapterCount), request.MinChapterCount, request.MaxChapterCount, "chapter count");
        RequireOrdered(nameof(request.MinKudos), request.MinKudos, request.MaxKudos, "kudos");
        RequireOrdered(nameof(request.MinHits), request.MinHits, request.MaxHits, "hits");
        RequireOrdered(nameof(request.MinComments), request.MinComments, request.MaxComments, "comments");
        RequireOrdered(nameof(request.MinBookmarks), request.MinBookmarks, request.MaxBookmarks, "bookmarks");

        RequireNonNegative(nameof(request.MinWordCount), request.MinWordCount);
        RequireNonNegative(nameof(request.MaxWordCount), request.MaxWordCount);
        RequireNonNegative(nameof(request.MinChapterCount), request.MinChapterCount);
        RequireNonNegative(nameof(request.MaxChapterCount), request.MaxChapterCount);
        RequireNonNegative(nameof(request.MinKudos), request.MinKudos);
        RequireNonNegative(nameof(request.MaxKudos), request.MaxKudos);
        RequireNonNegative(nameof(request.MinHits), request.MinHits);
        RequireNonNegative(nameof(request.MaxHits), request.MaxHits);
        RequireNonNegative(nameof(request.MinComments), request.MinComments);
        RequireNonNegative(nameof(request.MaxComments), request.MaxComments);
        RequireNonNegative(nameof(request.MinBookmarks), request.MinBookmarks);
        RequireNonNegative(nameof(request.MaxBookmarks), request.MaxBookmarks);

        var minRating = ParseValue<Ao3Rating>(request.MinRating, nameof(request.MinRating));
        var maxRating = ParseValue<Ao3Rating>(request.MaxRating, nameof(request.MaxRating));
        if (minRating is Ao3Rating low && maxRating is Ao3Rating high && low > high)
        {
            ModelState.AddModelError(
                nameof(request.MinRating),
                "The lowest rating has to be at or below the highest.");
        }

        var includeCategories = ParseFlags<Ao3Category>(request.IncludeCategories, nameof(request.IncludeCategories));
        var excludeCategories = ParseFlags<Ao3Category>(request.ExcludeCategories, nameof(request.ExcludeCategories));
        var includeWarnings = ParseFlags<Ao3Warning>(request.IncludeWarnings, nameof(request.IncludeWarnings));
        var excludeWarnings = ParseFlags<Ao3Warning>(request.ExcludeWarnings, nameof(request.ExcludeWarnings));

        if (request.UpdatedAfter is DateTime after
            && request.UpdatedBefore is DateTime before
            && after > before)
        {
            ModelState.AddModelError(
                nameof(request.UpdatedAfter),
                "The earliest update date has to be on or before the latest.");
        }

        var includeTagIds = Distinct(request.IncludeTagIds);
        var excludeTagIds = Distinct(request.ExcludeTagIds);
        var includeAuthorIds = Distinct(request.IncludeAuthorIds);
        var excludeAuthorIds = Distinct(request.ExcludeAuthorIds);

        RequireWithinLimit(nameof(request.IncludeTagIds), includeTagIds.Count, "tags");
        RequireWithinLimit(nameof(request.ExcludeTagIds), excludeTagIds.Count, "tags");
        RequireWithinLimit(nameof(request.IncludeAuthorIds), includeAuthorIds.Count, "authors");
        RequireWithinLimit(nameof(request.ExcludeAuthorIds), excludeAuthorIds.Count, "authors");

        // A tag on both lists is not merely contradictory, it is unstorable: the criteria rows are
        // keyed by (filter, tag), so the second one would fail the insert with a primary key
        // violation and surface as a 500.
        if (includeTagIds.Intersect(excludeTagIds).Any())
        {
            ModelState.AddModelError(
                nameof(request.ExcludeTagIds),
                "A tag can be required or excluded, not both.");
        }

        if (includeAuthorIds.Intersect(excludeAuthorIds).Any())
        {
            ModelState.AddModelError(
                nameof(request.ExcludeAuthorIds),
                "An author can be required or excluded, not both.");
        }

        await RequireExistingAsync(
            nameof(request.IncludeTagIds),
            [.. includeTagIds, .. excludeTagIds],
            ids => _db.Tags.Where(t => ids.Contains(t.Id)).Select(t => t.Id),
            "tag",
            ct);

        await RequireExistingAsync(
            nameof(request.IncludeAuthorIds),
            [.. includeAuthorIds, .. excludeAuthorIds],
            ids => _db.Ao3Pseuds.Where(p => ids.Contains(p.Id)).Select(p => p.Id),
            "author",
            ct);

        if (!ModelState.IsValid) return null;

        return new ValidatedCriteria(
            name,
            request,
            minRating,
            maxRating,
            includeCategories,
            excludeCategories,
            includeWarnings,
            excludeWarnings,
            includeTagIds,
            excludeTagIds,
            includeAuthorIds,
            excludeAuthorIds);
    }

    private static List<int> Distinct(IReadOnlyList<int>? ids) => ids is null ? [] : [.. ids.Distinct()];

    private void RequireOrdered(string field, int? min, int? max, string what)
    {
        if (min is int low && max is int high && low > high)
            ModelState.AddModelError(field, $"The smallest {what} has to be at or below the largest.");
    }

    private void RequireNonNegative(string field, int? value)
    {
        if (value is int number && number < 0)
            ModelState.AddModelError(field, "That can't be negative.");
    }

    private void RequireWithinLimit(string field, int count, string what)
    {
        if (count > MaxCriteriaPerList)
            ModelState.AddModelError(field, $"A filter can name at most {MaxCriteriaPerList} {what} per list.");
    }

    /// <summary>Reports every id that isn't a real row, rather than only the first.</summary>
    private async Task RequireExistingAsync(
        string field,
        List<int> ids,
        Func<List<int>, IQueryable<int>> lookup,
        string what,
        CancellationToken ct)
    {
        if (ids.Count == 0) return;

        var found = await lookup(ids).ToListAsync(ct);
        var missing = ids.Except(found).ToList();

        if (missing.Count > 0)
            ModelState.AddModelError(field, $"No such {what}: {string.Join(", ", missing)}.");
    }

    /// <summary>
    /// One enum name to its value. Rejects anything that isn't a defined member, which also rules
    /// out the raw numbers <c>Enum.TryParse</c> would otherwise accept.
    /// </summary>
    private TEnum? ParseValue<TEnum>(string? name, string field) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (Enum.TryParse<TEnum>(name, ignoreCase: true, out var value) && Enum.IsDefined(value))
            return value;

        ModelState.AddModelError(field, $"'{name}' isn't one of the values this filter accepts.");
        return null;
    }

    /// <summary>Combines a list of enum names into one flags value. Null for an empty list.</summary>
    private TEnum? ParseFlags<TEnum>(IReadOnlyList<string>? names, string field) where TEnum : struct, Enum
    {
        if (names is null || names.Count == 0) return null;

        long bits = 0;
        foreach (var name in names)
        {
            if (ParseValue<TEnum>(name, field) is TEnum flag) bits |= Convert.ToInt64(flag);
        }

        return bits == 0 ? null : (TEnum)Enum.ToObject(typeof(TEnum), bits);
    }

    /// <summary>
    /// A request that has passed every check, ready to be written onto an entity. Exists so that
    /// nothing between validation and persistence has to re-derive a parsed enum or a trimmed name.
    /// </summary>
    private sealed record ValidatedCriteria(
        string Name,
        SaveFilterRequest Request,
        Ao3Rating? MinRating,
        Ao3Rating? MaxRating,
        Ao3Category? IncludeCategories,
        Ao3Category? ExcludeCategories,
        Ao3Warning? IncludeWarnings,
        Ao3Warning? ExcludeWarnings,
        List<int> IncludeTagIds,
        List<int> ExcludeTagIds,
        List<int> IncludeAuthorIds,
        List<int> ExcludeAuthorIds)
    {
        public void ApplyTo(SavedWorkFilter filter)
        {
            filter.Name = Name;
            filter.IsDefault = Request.IsDefault;
            filter.ShipId = Request.ShipId;
            filter.IsComplete = Request.IsComplete;
            filter.MinWordCount = Request.MinWordCount;
            filter.MaxWordCount = Request.MaxWordCount;
            filter.MinChapterCount = Request.MinChapterCount;
            filter.MaxChapterCount = Request.MaxChapterCount;
            filter.MinKudos = Request.MinKudos;
            filter.MaxKudos = Request.MaxKudos;
            filter.MinHits = Request.MinHits;
            filter.MaxHits = Request.MaxHits;
            filter.MinComments = Request.MinComments;
            filter.MaxComments = Request.MaxComments;
            filter.MinBookmarks = Request.MinBookmarks;
            filter.MaxBookmarks = Request.MaxBookmarks;
            filter.MinRating = MinRating;
            filter.MaxRating = MaxRating;
            filter.IncludeCategories = IncludeCategories;
            filter.ExcludeCategories = ExcludeCategories;
            filter.IncludeWarnings = IncludeWarnings;
            filter.ExcludeWarnings = ExcludeWarnings;
            filter.LanguageCode = string.IsNullOrWhiteSpace(Request.LanguageCode)
                ? null
                : Request.LanguageCode.Trim();
            filter.UpdatedAfter = Request.UpdatedAfter;
            filter.UpdatedBefore = Request.UpdatedBefore;
            filter.Sort = Request.Sort;
            filter.Ascending = Request.Ascending;

            // Rebuilt wholesale, matching PUT's replace-don't-merge contract. Clearing the tracked
            // collection deletes the rows that are gone, because both criteria tables cascade from
            // the set and EF treats a removed child of an owned-style collection as a delete.
            filter.Tags.Clear();
            foreach (var tagId in IncludeTagIds) filter.Tags.Add(new SavedWorkFilterTag { TagId = tagId });
            foreach (var tagId in ExcludeTagIds)
                filter.Tags.Add(new SavedWorkFilterTag { TagId = tagId, Exclude = true });

            filter.Authors.Clear();
            foreach (var pseudId in IncludeAuthorIds)
                filter.Authors.Add(new SavedWorkFilterAuthor { PseudId = pseudId });
            foreach (var pseudId in ExcludeAuthorIds)
                filter.Authors.Add(new SavedWorkFilterAuthor { PseudId = pseudId, Exclude = true });
        }
    }
}
