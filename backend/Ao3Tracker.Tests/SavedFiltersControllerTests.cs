using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Tests;

/// <summary>
/// Saved filter sets: what you may store, who may read them back, and whether applying one to the
/// library actually narrows it the way its author meant.
///
/// The criteria tests run against real SQLite rather than a list in memory on purpose. Almost
/// every clause here is one EF has to translate — bitwise masks against a flags column, an
/// AND across several tag subqueries, an inclusive range — and a fake queryable would happily
/// evaluate all of them in C# while the database refused or, worse, answered differently.
/// </summary>
public class SavedFiltersControllerTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- storing a set -------------------------------------------------------------------------

    [Fact]
    public async Task Saves_a_set_and_lists_it_back()
    {
        var emma = _host.SeedUser();

        var created = await CreateAsync(emma, new("Long finished fics")
        {
            IsComplete = true,
            MinWordCount = 50_000,
            Sort = "kudos",
        });

        Assert.Equal("Long finished fics", created.Name);
        Assert.True(created.IsComplete);
        Assert.Equal(50_000, created.MinWordCount);
        Assert.Equal("kudos", created.Sort);

        var listed = Ok(await _host.SavedFilters(emma).GetFilters(default));
        Assert.Equal(["Long finished fics"], listed.Select(f => f.Name));
    }

    [Fact]
    public async Task Keeps_one_users_sets_out_of_anothers()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var hers = await CreateAsync(emma, new("Emma's view"));

        Assert.Empty(Ok(await _host.SavedFilters(sam).GetFilters(default)));

        // Not merely absent from the list — unreachable by id, which is the check that matters when
        // the id is a small integer anyone can guess.
        Assert.IsType<NotFoundResult>((await _host.SavedFilters(sam).GetFilter(hers.Id, default)).Result);
        Assert.IsType<NotFoundResult>(await _host.SavedFilters(sam).DeleteFilter(hers.Id, default));
    }

    [Fact]
    public async Task Replaces_a_set_rather_than_merging_into_it()
    {
        // The contract PUT has to keep: a criterion left out is a criterion cleared. Merging would
        // make "stop filtering by word count" inexpressible, since absent and cleared look alike.
        var emma = _host.SeedUser();
        var created = await CreateAsync(emma, new("Long fics") { MinWordCount = 50_000, IsComplete = true });

        var updated = Ok(await _host.SavedFilters(emma)
            .UpdateFilter(created.Id, new("Long fics") { MinWordCount = 100_000 }, default));

        Assert.Equal(100_000, updated.MinWordCount);
        Assert.Null(updated.IsComplete);
    }

    [Fact]
    public async Task Deletes_a_set_and_its_criteria()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1);
        var fluff = await SeedTagAsync(1, Ao3TagType.Freeform, "Fluff");

        var created = await CreateAsync(emma, new("Fluff only") { IncludeTagIds = [fluff] });

        Assert.IsType<NoContentResult>(await _host.SavedFilters(emma).DeleteFilter(created.Id, default));

        await using var db = _host.NewContext();
        Assert.Empty(await db.SavedWorkFilters.ToListAsync());

        // The criteria rows cascade. Left behind they would be orphans pointing at a missing set.
        Assert.Empty(await db.SavedWorkFilterTags.ToListAsync());
    }

    // ---- the one-default-per-user invariant ----------------------------------------------------

    [Fact]
    public async Task Moves_the_default_off_the_set_that_held_it()
    {
        // Enforced in the controller rather than by a filtered unique index, because HasFilter
        // takes provider-specific SQL — so this is the only thing holding the invariant up.
        var emma = _host.SeedUser();
        var first = await CreateAsync(emma, new("First") { IsDefault = true });
        var second = await CreateAsync(emma, new("Second") { IsDefault = true });

        var listed = Ok(await _host.SavedFilters(emma).GetFilters(default));

        Assert.Equal([second.Id], listed.Where(f => f.IsDefault).Select(f => f.Id));
        Assert.False(listed.Single(f => f.Id == first.Id).IsDefault);
    }

    [Fact]
    public async Task Leaves_another_users_default_alone()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var hers = await CreateAsync(emma, new("Emma's view") { IsDefault = true });

        await CreateAsync(sam, new("Sam's view") { IsDefault = true });

        Assert.True(Ok(await _host.SavedFilters(emma).GetFilter(hers.Id, default)).IsDefault);
    }

    [Fact]
    public async Task Toggles_the_default_without_touching_the_criteria()
    {
        var emma = _host.SeedUser();
        var created = await CreateAsync(emma, new("Long fics") { MinWordCount = 50_000 });

        var promoted = Ok(await _host.SavedFilters(emma).SetDefault(created.Id, new(true), default));
        Assert.True(promoted.IsDefault);
        Assert.Equal(50_000, promoted.MinWordCount);

        var demoted = Ok(await _host.SavedFilters(emma).SetDefault(created.Id, new(false), default));
        Assert.False(demoted.IsDefault);
        Assert.Equal(50_000, demoted.MinWordCount);
    }

    // ---- what a set may say --------------------------------------------------------------------

    [Fact]
    public async Task Refuses_two_sets_with_the_same_name()
    {
        // Compared case-insensitively here rather than left to the unique index: SQLite would match
        // these two and PostgreSQL would not, so the check has to be the controller's.
        var emma = _host.SeedUser();
        await CreateAsync(emma, new("Long fics"));

        Assert.True(Rejected(await _host.SavedFilters(emma).CreateFilter(new("long FICS"), default), "Name"));
    }

    [Fact]
    public async Task Lets_a_set_keep_its_own_name_when_edited()
    {
        var emma = _host.SeedUser();
        var created = await CreateAsync(emma, new("Long fics"));

        var updated = Ok(await _host.SavedFilters(emma)
            .UpdateFilter(created.Id, new("Long fics") { MinWordCount = 1000 }, default));

        Assert.Equal(1000, updated.MinWordCount);
    }

    [Fact]
    public async Task Refuses_a_sort_the_works_list_does_not_offer()
    {
        // Otherwise a typo'd sort is stored and the saved view silently opens in a different order.
        var emma = _host.SeedUser();

        Assert.True(Rejected(
            await _host.SavedFilters(emma).CreateFilter(new("By title") { Sort = "title" }, default),
            "Sort"));
    }

    [Fact]
    public async Task Refuses_a_range_whose_floor_is_above_its_ceiling()
    {
        var emma = _host.SeedUser();

        Assert.True(Rejected(
            await _host.SavedFilters(emma)
                .CreateFilter(new("Impossible") { MinWordCount = 100, MaxWordCount = 10 }, default),
            "MinWordCount"));
    }

    [Fact]
    public async Task Refuses_a_ship_you_do_not_follow()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var kirk = await SeedShipAsync("Kirk/Spock", sam);

        Assert.True(Rejected(
            await _host.SavedFilters(emma).CreateFilter(new("Theirs") { ShipId = kirk }, default),
            "ShipId"));
    }

    [Fact]
    public async Task Refuses_a_tag_that_does_not_exist()
    {
        // A bad id would otherwise reach the insert and come back as an unexplained 500 from the
        // foreign key.
        var emma = _host.SeedUser();

        Assert.True(Rejected(
            await _host.SavedFilters(emma).CreateFilter(new("Ghost tag") { IncludeTagIds = [999] }, default),
            "IncludeTagIds"));
    }

    [Fact]
    public async Task Refuses_a_tag_that_is_both_required_and_excluded()
    {
        // Contradictory, and unstorable besides: the criteria rows are keyed by (filter, tag), so
        // the second insert would violate the primary key.
        var emma = _host.SeedUser();
        var fluff = await SeedTagAsync(1, Ao3TagType.Freeform, "Fluff");

        Assert.True(Rejected(
            await _host.SavedFilters(emma)
                .CreateFilter(new("Both ways") { IncludeTagIds = [fluff], ExcludeTagIds = [fluff] }, default),
            "ExcludeTagIds"));
    }

    [Fact]
    public async Task Refuses_a_rating_AO3_does_not_have()
    {
        var emma = _host.SeedUser();

        Assert.True(Rejected(
            await _host.SavedFilters(emma).CreateFilter(new("Bad rating") { MinRating = "Filthy" }, default),
            "MinRating"));
    }

    [Fact]
    public async Task Round_trips_enum_criteria_as_names_not_numbers()
    {
        // The wire format is the enum's name. A flags column arriving as 17 would make the client
        // decode a bitmask, and a renumbered enum would silently change what a stored set means.
        var emma = _host.SeedUser();

        var created = await CreateAsync(emma, new("Femslash, no death")
        {
            MinRating = "TeenAndUpAudiences",
            MaxRating = "Mature",
            IncludeCategories = ["FF", "Multi"],
            ExcludeWarnings = ["MajorCharacterDeath"],
        });

        Assert.Equal("TeenAndUpAudiences", created.MinRating);
        Assert.Equal("Mature", created.MaxRating);
        Assert.Equal(["FF", "Multi"], created.IncludeCategories);
        Assert.Equal(["MajorCharacterDeath"], created.ExcludeWarnings);
    }

    // ---- applying a set to the library ---------------------------------------------------------

    [Fact]
    public async Task Narrows_the_works_list_to_the_set_it_is_given()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.WordCount = 1_000, 1);
        await SeedWorksAsync(lexa, w => w.WordCount = 80_000, 2);

        var filter = await CreateAsync(emma, new("Long fics") { MinWordCount = 50_000 });

        var page = Works(await _host.Works(emma).GetWorks(savedFilterId: filter.Id, ct: default));

        Assert.Equal([2], page.Items.Select(w => w.Id));
    }

    [Fact]
    public async Task Applies_the_default_set_to_an_unqualified_request()
    {
        // The whole point of marking one: opening Works without naming a set uses it.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.WordCount = 1_000, 1);
        await SeedWorksAsync(lexa, w => w.WordCount = 80_000, 2);

        await CreateAsync(emma, new("Long fics") { MinWordCount = 50_000, IsDefault = true });

        Assert.Equal([2], Works(await _host.Works(emma).GetWorks(ct: default)).Items.Select(w => w.Id));
    }

    [Fact]
    public async Task Shows_the_whole_library_when_the_default_is_declined()
    {
        // "Everything I follow" is otherwise inexpressible once a default exists.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.WordCount = 1_000, 1);
        await SeedWorksAsync(lexa, w => w.WordCount = 80_000, 2);

        await CreateAsync(emma, new("Long fics") { MinWordCount = 50_000, IsDefault = true });

        var page = Works(await _host.Works(emma).GetWorks(useDefaultFilter: false, ct: default));

        Assert.Equal([1, 2], page.Items.Select(w => w.Id).Order());
    }

    [Fact]
    public async Task Refuses_to_apply_someone_elses_set()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var hers = await CreateAsync(emma, new("Emma's view"));

        var result = await _host.Works(sam).GetWorks(savedFilterId: hers.Id, ct: default);

        // A 404 rather than an unfiltered library: silently ignoring an unknown set would answer a
        // request for one view with the contents of another.
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Applies_a_sets_own_sort_but_yields_to_an_explicit_one()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.Kudos = 10, 1);
        await SeedWorksAsync(lexa, w => w.Kudos = 90, 2);

        var filter = await CreateAsync(emma, new("By kudos") { Sort = "kudos", Ascending = true });

        var saved = Works(await _host.Works(emma).GetWorks(savedFilterId: filter.Id, ct: default));
        Assert.Equal([1, 2], saved.Items.Select(w => w.Id));

        // The works page's own dropdown has to keep working while a saved view is applied.
        var overridden = Works(await _host.Works(emma)
            .GetWorks(savedFilterId: filter.Id, sort: "kudos", ascending: false, ct: default));
        Assert.Equal([2, 1], overridden.Items.Select(w => w.Id));
    }

    [Fact]
    public async Task Keeps_a_set_naming_an_unfollowed_ship_inside_its_authors_library()
    {
        // The ship is checked when the set is saved and deliberately not when it is applied, so
        // this is what stops an unfollowed ship's works leaking back through a stale set.
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        var kirk = await SeedShipAsync("Kirk/Spock", emma);
        await SeedWorksAsync(kirk, 1);

        var filter = await CreateAsync(emma, new("Kirk/Spock") { ShipId = kirk });

        // Both users now follow it, so the row survives; emma stops.
        await _host.Ships(sam).WatchShip(new("Kirk/Spock"), default);
        await _host.Ships(emma).UnwatchShip(kirk, default);

        var page = Works(await _host.Works(emma).GetWorks(savedFilterId: filter.Id, ct: default));

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Lets_the_pages_own_ship_choice_outrank_the_sets()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        var kirk = await SeedShipAsync("Kirk/Spock", emma);
        await SeedWorksAsync(lexa, 1);
        await SeedWorksAsync(kirk, 2);

        var filter = await CreateAsync(emma, new("Lexa") { ShipId = lexa });

        var page = Works(await _host.Works(emma)
            .GetWorks(shipId: kirk, savedFilterId: filter.Id, ct: default));

        Assert.Equal([2], page.Items.Select(w => w.Id));
    }

    [Theory]
    [InlineData(true, new long[] { 1 })]
    [InlineData(false, new long[] { 2 })]
    public async Task Treats_completion_as_three_states(bool complete, long[] expected)
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.IsComplete = true, 1);
        await SeedWorksAsync(lexa, w => w.IsComplete = false, 2);

        var narrowed = await CreateAsync(emma, new("Narrowed") { IsComplete = complete });
        Assert.Equal(expected, await AppliedAsync(emma, narrowed));

        // Null is genuinely the third state, not a default that means "false".
        var both = await CreateAsync(emma, new("Both"));
        Assert.Equal([1, 2], (await AppliedAsync(emma, both)).Order());
    }

    [Fact]
    public async Task Filters_a_rating_band_inclusively_at_both_ends()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.Rating = Ao3Rating.GeneralAudiences, 1);
        await SeedWorksAsync(lexa, w => w.Rating = Ao3Rating.TeenAndUpAudiences, 2);
        await SeedWorksAsync(lexa, w => w.Rating = Ao3Rating.Mature, 3);
        await SeedWorksAsync(lexa, w => w.Rating = Ao3Rating.Explicit, 4);

        var band = await CreateAsync(emma, new("Teen to Mature")
        {
            MinRating = "TeenAndUpAudiences",
            MaxRating = "Mature",
        });

        Assert.Equal([2, 3], (await AppliedAsync(emma, band)).Order());
    }

    [Fact]
    public async Task Matches_a_work_carrying_any_of_the_included_categories()
    {
        // The mask is compared bitwise against the column. A work carrying one of several ticked
        // categories matches; the clause is an OR across the mask, not an AND.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.Categories = Ao3Category.FF, 1);
        await SeedWorksAsync(lexa, w => w.Categories = Ao3Category.MM | Ao3Category.Gen, 2);
        await SeedWorksAsync(lexa, w => w.Categories = Ao3Category.FM, 3);

        var filter = await CreateAsync(emma, new("Femslash or gen") { IncludeCategories = ["FF", "Gen"] });

        Assert.Equal([1, 2], (await AppliedAsync(emma, filter)).Order());
    }

    [Fact]
    public async Task Drops_a_work_carrying_any_excluded_warning()
    {
        // The filter people actually reach for, and the reason warnings are matched from the flags
        // column rather than the tag rows — the flags are set even when the tag went unrecognised.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.Warnings = Ao3Warning.NoArchiveWarningsApply, 1);
        await SeedWorksAsync(lexa, w => w.Warnings = Ao3Warning.MajorCharacterDeath | Ao3Warning.Underage, 2);
        await SeedWorksAsync(lexa, w => w.Warnings = Ao3Warning.GraphicDepictionsOfViolence, 3);

        var filter = await CreateAsync(emma, new("No death") { ExcludeWarnings = ["MajorCharacterDeath"] });

        Assert.Equal([1, 3], (await AppliedAsync(emma, filter)).Order());
    }

    [Fact]
    public async Task Requires_every_included_tag_rather_than_any_of_them()
    {
        // Ticking two boxes narrows. A single Any(t => included.Contains(t)) would widen instead,
        // which is the bug this test exists to catch.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1, 2, 3);

        var fluff = await SeedTagAsync(1, Ao3TagType.Freeform, "Fluff");
        var hurt = await SeedTagAsync(2, Ao3TagType.Freeform, "Hurt/Comfort");
        await TagWorkAsync(1, fluff);
        await TagWorkAsync(2, fluff, hurt);
        await TagWorkAsync(3, hurt);

        var filter = await CreateAsync(emma, new("Both") { IncludeTagIds = [fluff, hurt] });

        Assert.Equal([2], await AppliedAsync(emma, filter));
    }

    [Fact]
    public async Task Drops_a_work_carrying_any_excluded_tag()
    {
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1, 2);

        var angst = await SeedTagAsync(1, Ao3TagType.Freeform, "Angst");
        await TagWorkAsync(2, angst);

        var filter = await CreateAsync(emma, new("No angst") { ExcludeTagIds = [angst] });

        Assert.Equal([1], await AppliedAsync(emma, filter));
    }

    [Fact]
    public async Task Matches_a_work_by_any_of_the_included_authors()
    {
        // Authors are OR'ed where tags are AND'ed: a work has one byline per creator, so requiring
        // two at once would only ever match co-authored works.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, 1, 2, 3);

        var first = await SeedPseudAsync(1, "aurora");
        var second = await SeedPseudAsync(2, "bellamy");
        await ByAsync(1, first);
        await ByAsync(2, second);

        var filter = await CreateAsync(emma, new("Either") { IncludeAuthorIds = [first, second] });

        Assert.Equal([1, 2], (await AppliedAsync(emma, filter)).Order());
    }

    [Fact]
    public async Task Counts_what_a_set_currently_matches()
    {
        // The number that tells someone whether the set they just built does what they meant.
        var emma = _host.SeedUser();
        var lexa = await SeedShipAsync("Clarke Griffin/Lexa", emma);
        await SeedWorksAsync(lexa, w => w.WordCount = 1_000, 1);
        await SeedWorksAsync(lexa, w => w.WordCount = 80_000, 2, 3);

        var filter = await CreateAsync(emma, new("Long fics") { MinWordCount = 50_000 });

        Assert.Equal(2, filter.MatchingWorkCount);
    }

    [Fact]
    public async Task Counts_only_works_the_owner_can_see()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        await SeedWorksAsync(await SeedShipAsync("Kirk/Spock", sam), 1);

        var filter = await CreateAsync(emma, new("Everything"));

        Assert.Equal(0, filter.MatchingWorkCount);
    }

    // ---- the vocabulary and the pickers --------------------------------------------------------

    [Fact]
    public async Task Serves_AO3s_own_wording_for_every_value_a_filter_takes()
    {
        var emma = _host.SeedUser();

        var vocabulary = Ok(await _host.Lookups(emma).GetVocabulary(default));

        Assert.Contains(vocabulary.Ratings, r => r is { Value: "TeenAndUpAudiences", Label: "Teen And Up Audiences" });
        Assert.Contains(vocabulary.Categories, c => c is { Value: "FF", Label: "F/F" });
        Assert.Contains(vocabulary.Warnings, w => w.Value == "MajorCharacterDeath");
        Assert.Equal(
            ["updated", "kudos", "hits", "bookmarks", "comments", "words"],
            vocabulary.Sorts.Select(s => s.Value));

        // Unknown means "the scraper never read one", so offering it as a band bound would ask the
        // user to filter on our own shortfall.
        Assert.DoesNotContain(vocabulary.Ratings, r => r.Value == "Unknown");
    }

    [Fact]
    public async Task Suggests_only_tags_that_appear_in_your_own_library()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);
        await SeedWorksAsync(await SeedShipAsync("Kirk/Spock", sam), 2);

        var mine = await SeedTagAsync(1, Ao3TagType.Freeform, "Fluff");
        var theirs = await SeedTagAsync(2, Ao3TagType.Freeform, "Angst");
        await TagWorkAsync(1, mine);
        await TagWorkAsync(2, theirs);

        var suggestions = Ok(await _host.Lookups(emma).SearchTags(ct: default));

        Assert.Equal(["Fluff"], suggestions.Select(t => t.Name));
    }

    [Fact]
    public async Task Searches_tags_case_insensitively_on_either_provider()
    {
        // Through NameNormalized, never Name: SQLite's LIKE ignores case and PostgreSQL's does not,
        // so searching the display column would answer differently per provider.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);

        var fluff = await SeedTagAsync(1, Ao3TagType.Freeform, "Fluff and Angst");
        await TagWorkAsync(1, fluff);

        Assert.Equal(["Fluff and Angst"], Ok(await _host.Lookups(emma).SearchTags("fluff", ct: default))
            .Select(t => t.Name));
    }

    [Fact]
    public async Task Narrows_tag_suggestions_to_one_AO3_tag_type()
    {
        // "Fluff" the freeform and "Fluff" the character are different criteria.
        var emma = _host.SeedUser();
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);

        var fandom = await SeedTagAsync(1, Ao3TagType.Fandom, "The 100");
        var freeform = await SeedTagAsync(2, Ao3TagType.Freeform, "Fluff");
        await TagWorkAsync(1, fandom, freeform);

        var suggestions = Ok(await _host.Lookups(emma).SearchTags(type: "Fandom", ct: default));

        Assert.Equal(["The 100"], suggestions.Select(t => t.Name));
    }

    [Fact]
    public async Task Rejects_a_tag_type_AO3_does_not_have()
    {
        var emma = _host.SeedUser();

        var result = await _host.Lookups(emma).SearchTags(type: "Nonsense", ct: default);

        var problem = Assert.IsType<ValidationProblemDetails>(
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        Assert.True(problem.Errors.ContainsKey("type"));
    }

    [Fact]
    public async Task Suggests_only_authors_who_wrote_something_in_your_library()
    {
        var emma = _host.SeedUser("emma");
        var sam = _host.SeedUser("sam");
        await SeedWorksAsync(await SeedShipAsync("Clarke Griffin/Lexa", emma), 1);
        await SeedWorksAsync(await SeedShipAsync("Kirk/Spock", sam), 2);

        await ByAsync(1, await SeedPseudAsync(1, "aurora"));
        await ByAsync(2, await SeedPseudAsync(2, "bellamy"));

        Assert.Equal(["aurora"], Ok(await _host.Lookups(emma).SearchAuthors(ct: default))
            .Select(a => a.DisplayName));
    }

    // ---- fixture -------------------------------------------------------------------------------

    /// <summary>
    /// A request with every criterion defaulted to "unconstrained", so each test writes only the
    /// one thing it is about. Mirrors <c>SaveFilterRequest</c>; converted on the way in.
    /// </summary>
    private sealed class Filter(string name)
    {
        public string Name { get; } = name;
        public bool IsDefault { get; init; }
        public int? ShipId { get; init; }
        public bool? IsComplete { get; init; }
        public int? MinWordCount { get; init; }
        public int? MaxWordCount { get; init; }
        public string? MinRating { get; init; }
        public string? MaxRating { get; init; }
        public IReadOnlyList<string>? IncludeCategories { get; init; }
        public IReadOnlyList<string>? ExcludeWarnings { get; init; }
        public string Sort { get; init; } = "updated";
        public bool Ascending { get; init; }
        public IReadOnlyList<int>? IncludeTagIds { get; init; }
        public IReadOnlyList<int>? ExcludeTagIds { get; init; }
        public IReadOnlyList<int>? IncludeAuthorIds { get; init; }

        public SaveFilterRequest ToRequest() => new(
            Name,
            IsDefault,
            ShipId,
            IsComplete,
            MinWordCount,
            MaxWordCount,
            MinRating: MinRating,
            MaxRating: MaxRating,
            IncludeCategories: IncludeCategories,
            ExcludeWarnings: ExcludeWarnings,
            Sort: Sort,
            Ascending: Ascending,
            IncludeTagIds: IncludeTagIds,
            ExcludeTagIds: ExcludeTagIds,
            IncludeAuthorIds: IncludeAuthorIds);
    }

    private async Task<SavedFilterDto> CreateAsync(ApplicationUser user, Filter filter)
    {
        var result = await _host.SavedFilters(user).CreateFilter(filter.ToRequest(), default);
        return Assert.IsType<SavedFilterDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
    }

    /// <summary>The ids a set matches, read back through the works list that actually applies it.</summary>
    private async Task<List<long>> AppliedAsync(ApplicationUser user, SavedFilterDto filter)
    {
        var page = Works(await _host.Works(user).GetWorks(savedFilterId: filter.Id, ct: default));
        return [.. page.Items.Select(w => w.Id)];
    }

    /// <summary>Creates a ship through the real endpoint, so its schedule is wired up too.</summary>
    private async Task<int> SeedShipAsync(string tagName, ApplicationUser watcher)
    {
        var result = await _host.Ships(watcher).WatchShip(new(tagName), default);
        return Assert.IsType<WatchedShipDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value).ShipId;
    }

    private Task SeedWorksAsync(int shipId, params long[] ids) => SeedWorksAsync(shipId, _ => { }, ids);

    private async Task SeedWorksAsync(int shipId, Action<Work> customize, params long[] ids)
    {
        await using var db = _host.NewContext();

        foreach (var id in ids)
        {
            var work = new Work { Id = id, Title = $"Work {id}" };
            customize(work);
            db.Works.Add(work);
            db.ShipWorks.Add(new ShipWork { ShipId = shipId, WorkId = id });
        }

        await db.SaveChangesAsync();
    }

    private async Task<int> SeedTagAsync(int id, Ao3TagType type, string name)
    {
        await using var db = _host.NewContext();
        db.Tags.Add(new Tag { Id = id, Type = type, Name = name, NameNormalized = name.ToUpperInvariant() });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task TagWorkAsync(long workId, params int[] tagIds)
    {
        await using var db = _host.NewContext();
        foreach (var tagId in tagIds) db.WorkTags.Add(new WorkTag { WorkId = workId, TagId = tagId });
        await db.SaveChangesAsync();
    }

    private async Task<int> SeedPseudAsync(int id, string name)
    {
        await using var db = _host.NewContext();
        db.Ao3Pseuds.Add(new Ao3Pseud
        {
            Id = id,
            Username = name,
            PseudName = name,
            UsernameNormalized = name.ToUpperInvariant(),
            PseudNameNormalized = name.ToUpperInvariant(),
            DisplayName = name,
            DisplayNameNormalized = name.ToUpperInvariant(),
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task ByAsync(long workId, int pseudId)
    {
        await using var db = _host.NewContext();
        db.WorkAuthors.Add(new WorkAuthor { WorkId = workId, PseudId = pseudId, Position = 0 });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Assignable rather than exact: the list endpoints declare IReadOnlyList and hand back the
    /// List behind it, which an exact type check would reject for no reason a caller would care about.
    /// </summary>
    private static T Ok<T>(ActionResult<T> result) =>
        Assert.IsAssignableFrom<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    /// <summary>True when the request came back a 400 naming <paramref name="field"/>.</summary>
    private static bool Rejected<T>(ActionResult<T> result, string field)
    {
        var problem = Assert.IsType<ValidationProblemDetails>(
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
        return problem.Errors.ContainsKey(field);
    }

    private static PagedResult<WorkListItemDto> Works(ActionResult<PagedResult<WorkListItemDto>> result) =>
        Assert.IsType<PagedResult<WorkListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
