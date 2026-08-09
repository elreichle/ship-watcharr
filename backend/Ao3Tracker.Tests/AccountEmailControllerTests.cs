using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using Ao3Tracker.Api.Controllers;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Scraping;
using Ao3Tracker.Api.Services.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Tests;

/// <summary>
/// The optional account email at Settings → Account. Two things make this worth running against a
/// real <see cref="UserManager{TUser}"/> and a real database rather than a mock: the endpoint has to
/// treat blank as "clear it" instead of "invalid" (an [EmailAddress] annotation on the request DTO
/// would silently break that, which is why the DTO carries a comment forbidding one), and the
/// address it writes is what AO3 ends up seeing, through the admin-account fallback in
/// <see cref="OperatorContactResolver"/>.
/// </summary>
public class AccountEmailControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _request;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RecordingAuthentication _auth = new();

    public AccountEmailControllerTests()
    {
        // Kept open for the lifetime of the test: an in-memory SQLite database exists only as long
        // as a connection to it does.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();

        // Mirrors Program.cs: the abstract AppDbContext is the service type, the concrete
        // SqliteAppDbContext is the implementation, and Identity's stores bind to the abstract one.
        // Registering it any other way here would be exercising a composition the app never uses.
        services.AddDbContext<AppDbContext, SqliteAppDbContext>(o => o.UseSqlite(_connection));

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.User.RequireUniqueEmail = false;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddUserValidator<RejectsOneSentinelAddress>();

        // SignInManager needs a scheme provider; the recording fake standing in for the real
        // authentication service is what lets the cookie-refresh assertion work without a cookie
        // handler, data protection, or a live host. Registered last, so it wins.
        services.AddAuthentication(IdentityConstants.ApplicationScheme);
        services.AddSingleton<IAuthenticationService>(_auth);

        // Supplies the real ProblemDetailsFactory that ControllerBase.ValidationProblem resolves,
        // so the 400 bodies asserted below are the ones the API actually returns.
        services.AddMvcCore();

        // The real resolver, for the tests that follow an address all the way to "this is what AO3
        // sees" rather than stubbing that answer.
        services.Configure<Ao3HttpClientOptions>(o => o.OperatorContact = "");
        services.AddSingleton<IPersistedSettingsStore, NoSavedSettings>();
        services.AddScoped<IOperatorContactResolver, OperatorContactResolver>();

        _provider = services.BuildServiceProvider();

        using (var setup = _provider.CreateScope())
            setup.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

        // One scope stands in for "the request under test". Seeding uses its own scope so this one
        // starts with a cold change tracker, the way a real request does.
        _request = _provider.CreateScope();
        _userManager = _request.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    }

    public void Dispose()
    {
        _request.Dispose();
        _provider.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- reading -------------------------------------------------------------------------------

    [Fact]
    public async Task Returns_the_address_on_file()
    {
        var user = await SeedUserAsync("emma", "emma@example.com");

        var body = Body(await Controller(await SignedInAsync(user)).Get(default));

        Assert.Equal("emma@example.com", body.Email);
    }

    [Fact]
    public async Task Returns_null_for_an_account_that_never_gave_one()
    {
        // The common case now that registration no longer asks: a 200 with null, not a 404.
        var user = await SeedUserAsync("emma");

        var body = Body(await Controller(await SignedInAsync(user)).Get(default));

        Assert.Null(body.Email);
        Assert.False(body.IsUsedAsOperatorContact);
    }

    [Fact]
    public async Task Rejects_an_unauthenticated_caller()
    {
        // The null-user guard is the last line of defence if [Authorize] is ever dropped.
        var result = await Controller(principal: null).Get(default);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task Rejects_a_caller_whose_account_no_longer_exists()
    {
        // A cookie outliving its user row must 401, not throw on the null user.
        var user = await SeedUserAsync("emma");
        var principal = await SignedInAsync(user);
        await DeleteUserAsync(user);

        Assert.IsType<UnauthorizedResult>((await Controller(principal).Get(default)).Result);
    }

    [Fact]
    public async Task Requires_authentication_at_the_routing_layer()
    {
        // Direct instantiation cannot run the filter, and losing [Authorize] would make every
        // user's address anonymously readable — so assert the attribute itself is still there.
        Assert.NotNull(typeof(AccountEmailController).GetCustomAttribute<AuthorizeAttribute>());
    }

    // ---- clearing: the regression this file exists for -----------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task Blank_input_clears_the_address_rather_than_failing_validation(string? input)
    {
        // Blank means "clear it", not "invalid". Moving the format check above the blank-collapse
        // in Update() breaks these. The other half of that regression -- an [EmailAddress] back on
        // the request DTO -- is caught by Blank_input_survives_model_validation below, not here.
        var user = await SeedUserAsync("emma", "emma@example.com");

        var body = Body(await Controller(await SignedInAsync(user)).Update(new(input), default));

        Assert.Null(body.Email);
        Assert.Null((await ReloadAsync(user.Id)).Email);
    }

    [Fact]
    public void Nothing_on_the_request_dto_rejects_a_blank_address()
    {
        // The tests around this one invoke the action directly, which skips the automatic model
        // validation [ApiController] performs -- so none of them can see an [EmailAddress]
        // reappearing on UpdateAccountEmailRequest, and this is the guard its <remarks> asks for.
        // Attributes are collected from the constructor parameter as well as the property because
        // that is where an attribute on a positional record actually lands, and MVC reads both.
        var dto = typeof(UpdateAccountEmailRequest);
        var attributes = dto
            .GetProperty(nameof(UpdateAccountEmailRequest.Email))!
            .GetCustomAttributes<ValidationAttribute>()
            .Concat(dto.GetConstructors().Single().GetParameters().Single()
                .GetCustomAttributes<ValidationAttribute>());

        foreach (var attribute in attributes)
            foreach (var blank in new string?[] { null, "", "   ", "\t\n" })
                Assert.True(attribute.IsValid(blank),
                    $"{attribute.GetType().Name} rejects \"{blank}\", which would turn " +
                    "\"clear my email\" into a 400 before the action runs.");
    }

    [Fact]
    public async Task Clearing_also_clears_the_normalized_lookup_column()
    {
        // A stale NormalizedEmail would keep a "cleared" address findable by FindByEmailAsync —
        // invisible to every other assertion here, and only caught because a real UserManager runs
        // UpdateNormalizedEmailAsync.
        var user = await SeedUserAsync("emma", "emma@example.com");

        await Controller(await SignedInAsync(user)).Update(new(null), default);

        Assert.Null((await ReloadAsync(user.Id)).NormalizedEmail);
    }

    [Fact]
    public async Task Trims_surrounding_whitespace_before_storing()
    {
        // Untrimmed values break DescribeAsync's equality check and would put whitespace into the
        // User-Agent comment.
        var user = await SeedUserAsync("emma");

        var body = Body(await Controller(await SignedInAsync(user))
            .Update(new("  emma@example.com \n"), default));

        Assert.Equal("emma@example.com", body.Email);
        Assert.Equal("emma@example.com", (await ReloadAsync(user.Id)).Email);
    }

    [Fact]
    public async Task Stores_the_uppercase_lookup_form()
    {
        // Any refactor that writes user.Email straight through the DbContext instead of going via
        // SetEmailAsync regresses this silently.
        var user = await SeedUserAsync("emma");

        await Controller(await SignedInAsync(user)).Update(new("Emma@Example.com"), default);

        Assert.Equal("EMMA@EXAMPLE.COM", (await ReloadAsync(user.Id)).NormalizedEmail);
    }

    // ---- format rejection ----------------------------------------------------------------------

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("@example.com")]
    [InlineData("emma@")]
    [InlineData("a@@b.com")]
    [InlineData("   not-an-email   ")]
    public async Task Rejects_a_malformed_address(string input)
    {
        // The padded case proves trimming happens before validation, so a leading space cannot
        // smuggle a bad value past the check.
        var user = await SeedUserAsync("emma");

        var problem = Problem(await Controller(await SignedInAsync(user)).Update(new(input), default));

        Assert.True(problem.Errors.ContainsKey("Email"));
    }

    [Fact]
    public async Task Leaves_the_stored_address_untouched_when_the_new_one_is_rejected()
    {
        // A rejected update must not have partially written.
        var user = await SeedUserAsync("emma", "emma@example.com");

        Problem(await Controller(await SignedInAsync(user)).Update(new("not-an-email"), default));

        Assert.Equal("emma@example.com", (await ReloadAsync(user.Id)).Email);
    }

    [Fact]
    public async Task Surfaces_store_failures_as_validation_errors()
    {
        // Pins that IdentityResult.Errors reach the caller instead of being swallowed into a false
        // 200. Note the key is Identity's error code, not the field name.
        var user = await SeedUserAsync("emma");

        var problem = Problem(await Controller(await SignedInAsync(user))
            .Update(new(RejectsOneSentinelAddress.Address), default));

        Assert.True(problem.Errors.ContainsKey("EmailRejectedByStore"));
    }

    [Fact]
    public async Task Rejects_an_update_from_a_caller_whose_account_no_longer_exists()
    {
        var user = await SeedUserAsync("emma");
        var principal = await SignedInAsync(user);
        await DeleteUserAsync(user);

        var result = await Controller(principal).Update(new("emma@example.com"), default);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    // ---- staying signed in ---------------------------------------------------------------------

    [Fact]
    public async Task Saving_an_address_keeps_the_caller_signed_in()
    {
        // SetEmailAsync rotates the security stamp, which SecurityStampValidator then treats as
        // grounds to reject the auth cookie. Without the RefreshSignInAsync in Update(), saving
        // your own email silently logs you out within the validation interval.
        var user = await SeedUserAsync("emma");
        var before = (await ReloadAsync(user.Id)).SecurityStamp;

        await Controller(await SignedInAsync(user)).Update(new("emma@example.com"), default);

        Assert.NotEqual(before, (await ReloadAsync(user.Id)).SecurityStamp);
        Assert.Equal(1, _auth.SignInCount);
        Assert.Equal(user.Id, _userManager.GetUserId(_auth.SignedIn!));
    }

    [Fact]
    public async Task Does_not_reissue_the_cookie_when_the_update_was_rejected()
    {
        // Nothing changed, so nothing needs re-signing.
        var user = await SeedUserAsync("emma");

        Problem(await Controller(await SignedInAsync(user)).Update(new("not-an-email"), default));

        Assert.Equal(0, _auth.SignInCount);
    }

    // ---- the operator-contact flag, against a stubbed resolver ---------------------------------

    [Fact]
    public async Task Flags_the_address_when_it_is_the_admin_account_fallback()
    {
        var user = await SeedUserAsync("emma", "emma@example.com", isAdmin: true);

        var body = Body(await Controller(
            await SignedInAsync(user), "emma@example.com", OperatorContactSource.AdminAccount).Get(default));

        Assert.True(body.IsUsedAsOperatorContact);
    }

    [Fact]
    public async Task Matches_the_operator_contact_case_insensitively()
    {
        // Dropping OrdinalIgnoreCase would tell an admin their address is unused while AO3 is
        // receiving it.
        var user = await SeedUserAsync("emma", "emma@example.com", isAdmin: true);

        var body = Body(await Controller(
            await SignedInAsync(user), "Emma@Example.COM", OperatorContactSource.AdminAccount).Get(default));

        Assert.True(body.IsUsedAsOperatorContact);
    }

    [Fact]
    public async Task Does_not_flag_a_different_address()
    {
        var user = await SeedUserAsync("emma", "emma@example.com", isAdmin: true);

        var body = Body(await Controller(
            await SignedInAsync(user), "someone@example.com", OperatorContactSource.AdminAccount).Get(default));

        Assert.False(body.IsUsedAsOperatorContact);
    }

    [Theory]
    [InlineData(OperatorContactSource.None)]
    [InlineData(OperatorContactSource.AdminSetting)]
    [InlineData(OperatorContactSource.Configuration)]
    public async Task Only_the_admin_account_fallback_counts(OperatorContactSource source)
    {
        // Current behaviour, pinned deliberately: even when the resolved contact is character-for-
        // character this user's address, the flag stays false unless it got there by the fallback.
        var user = await SeedUserAsync("emma", "emma@example.com", isAdmin: true);

        var body = Body(await Controller(await SignedInAsync(user), "emma@example.com", source).Get(default));

        Assert.False(body.IsUsedAsOperatorContact);
    }

    [Fact]
    public async Task Does_not_flag_an_account_with_no_email()
    {
        // Guards the user.Email.Trim() in DescribeAsync against a null.
        var user = await SeedUserAsync("emma", isAdmin: true);

        var body = Body(await Controller(
            await SignedInAsync(user), "other@example.com", OperatorContactSource.AdminAccount).Get(default));

        Assert.False(body.IsUsedAsOperatorContact);
    }

    // ---- end to end against the real resolver --------------------------------------------------

    [Fact]
    public async Task Reflects_the_flag_in_the_same_response_that_sets_the_address()
    {
        // The feature's whole promise: save an address, and be told straight away that it is now
        // what AO3 sees. Catches the resolver being consulted before the save, or against a stale
        // context.
        var user = await SeedUserAsync("emma", isAdmin: true);

        var body = Body(await ControllerWithRealResolver(await SignedInAsync(user))
            .Update(new("emma@example.com"), default));

        Assert.Equal("emma@example.com", body.Email);
        Assert.True(body.IsUsedAsOperatorContact);
    }

    [Fact]
    public async Task Stops_flagging_once_the_address_is_cleared()
    {
        // Also documents the sharp edge: clearing an "optional" field is what disables scraping
        // instance-wide when nothing else supplies a contact.
        var user = await SeedUserAsync("emma", "emma@example.com", isAdmin: true);
        var controller = ControllerWithRealResolver(await SignedInAsync(user));

        Assert.False(Body(await controller.Update(new("  "), default)).IsUsedAsOperatorContact);

        var resolved = await _request.ServiceProvider
            .GetRequiredService<IOperatorContactResolver>().ResolveAsync();
        Assert.Equal(OperatorContactSource.None, resolved.Source);
    }

    [Fact]
    public async Task Does_not_flag_a_non_admin_account()
    {
        // Only admins are the fallback, so an ordinary user's address is never the contact.
        var user = await SeedUserAsync("guest", isAdmin: false);

        var body = Body(await ControllerWithRealResolver(await SignedInAsync(user))
            .Update(new("guest@example.com"), default));

        Assert.Equal("guest@example.com", body.Email);
        Assert.False(body.IsUsedAsOperatorContact);
    }

    // ---- fixture -------------------------------------------------------------------------------

    private async Task<ApplicationUser> SeedUserAsync(
        string userName, string? email = null, bool isAdmin = false)
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser { UserName = userName, IsAdmin = isAdmin };
        Assert.True((await users.CreateAsync(user, "correct-horse")).Succeeded);

        if (email is not null)
            Assert.True((await users.SetEmailAsync(user, email)).Succeeded);

        return user;
    }

    private async Task DeleteUserAsync(ApplicationUser user)
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        Assert.True((await users.DeleteAsync((await users.FindByIdAsync(user.Id))!)).Succeeded);
    }

    /// <summary>
    /// The principal the auth cookie would carry, built by the same factory SignInManager uses — so
    /// the test never has to know which claim type Identity keeps the user id under.
    /// </summary>
    private Task<ClaimsPrincipal> SignedInAsync(ApplicationUser user) =>
        _request.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>()
            .CreateAsync(user);

    private AccountEmailController Controller(ClaimsPrincipal? principal, IOperatorContactResolver contacts)
    {
        var http = new DefaultHttpContext { RequestServices = _request.ServiceProvider };
        if (principal is not null) http.User = principal;

        // SignInManager reaches the request through the accessor, not through ControllerContext.
        _request.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        return new AccountEmailController(
            _userManager,
            _request.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>(),
            contacts)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private AccountEmailController Controller(
        ClaimsPrincipal? principal,
        string? contact = null,
        OperatorContactSource source = OperatorContactSource.None) =>
        Controller(principal, new StubContacts(new OperatorContactResolution(contact, source)));

    /// <summary>The controller wired to the real resolver, reading the database it just wrote to.</summary>
    private AccountEmailController ControllerWithRealResolver(ClaimsPrincipal principal) =>
        Controller(principal, _request.ServiceProvider.GetRequiredService<IOperatorContactResolver>());

    /// <summary>
    /// Reads through a fresh context, so persistence assertions are real round-trips rather than
    /// the change tracker echoing back what the controller just set in memory.
    /// </summary>
    private async Task<ApplicationUser> ReloadAsync(string userId)
    {
        await using var db = new SqliteAppDbContext(
            new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite(_connection).Options);

        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private static AccountEmailDto Body(ActionResult<AccountEmailDto> result) =>
        Assert.IsType<AccountEmailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static ValidationProblemDetails Problem(ActionResult<AccountEmailDto> result) =>
        Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);

    private sealed class StubContacts(OperatorContactResolution resolution) : IOperatorContactResolver
    {
        public Task<OperatorContactResolution> ResolveAsync(CancellationToken ct = default) =>
            Task.FromResult(resolution);

        public Task<OperatorContactResolution> ResolveDefaultAsync(CancellationToken ct = default) =>
            Task.FromResult(resolution);
    }

    private sealed class NoSavedSettings : IPersistedSettingsStore
    {
        public Task SaveDatabaseSettingsAsync(string provider, string? cs, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> ReadOperatorContactAsync(CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task SaveOperatorContactAsync(string? value, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Stands in for the real authentication service. Recording the sign-in is what lets the
    /// cookie-refresh test assert its point without a cookie handler, data protection, or a host.
    /// </summary>
    private sealed class RecordingAuthentication : IAuthenticationService
    {
        public ClaimsPrincipal? SignedIn { get; private set; }

        public int SignInCount { get; private set; }

        // RefreshSignInAsync bails out early unless this reports an already-authenticated request,
        // so returning NoResult here would make the refresh look broken when it is not. In the app
        // the cookie middleware is what succeeds; here the principal on the context stands in.
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(context.User.Identity?.Name is null
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(
                    context.User, scheme ?? IdentityConstants.ApplicationScheme)));

        public Task ChallengeAsync(HttpContext c, string? s, AuthenticationProperties? p) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext c, string? s, AuthenticationProperties? p) => Task.CompletedTask;

        public Task SignInAsync(HttpContext c, string? s, ClaimsPrincipal principal, AuthenticationProperties? p)
        {
            SignedIn = principal;
            SignInCount++;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext c, string? s, AuthenticationProperties? p) => Task.CompletedTask;
    }

    /// <summary>
    /// With RequireUniqueEmail off there is no realistic input that makes SetEmailAsync fail, so
    /// this injects one at the layer Identity actually reports failures from. The rule is not the
    /// point — the point is that whatever IdentityResult comes back reaches the caller.
    /// </summary>
    private sealed class RejectsOneSentinelAddress : IUserValidator<ApplicationUser>
    {
        public const string Address = "rejected@example.com";

        public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user) =>
            Task.FromResult(user.Email == Address
                ? IdentityResult.Failed(new IdentityError
                {
                    Code = "EmailRejectedByStore",
                    Description = "That address cannot be used on this instance.",
                })
                : IdentityResult.Success);
    }
}
