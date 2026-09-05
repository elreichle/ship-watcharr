using System.Security.Claims;
using Ao3Tracker.Api.Controllers;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Tests;

/// <summary>
/// Settings -> Account's password and username changes. Run against a real
/// <see cref="UserManager{TUser}"/> and a real database rather than a mock: password verification is
/// PBKDF2 done by the real hasher, and the duplicate-username rejection comes from Identity's own
/// user validator running against the real unique index, neither of which a mock would exercise.
/// </summary>
public class AccountCredentialsControllerTests : IDisposable
{
    // Kept open for the test's lifetime: an in-memory SQLite database exists only as long as a
    // connection to it does.
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _request;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RecordingAuthentication _auth = new();

    public AccountCredentialsControllerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();

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
            .AddSignInManager();

        // SignInManager needs a scheme provider; the recording fake standing in for the real
        // authentication service is what lets RefreshSignInAsync run without a cookie handler, data
        // protection, or a live host.
        services.AddAuthentication(IdentityConstants.ApplicationScheme);
        services.AddSingleton<IAuthenticationService>(_auth);

        // Supplies the real ProblemDetailsFactory that ControllerBase.ValidationProblem resolves.
        services.AddMvcCore();

        _provider = services.BuildServiceProvider();

        using (var setup = _provider.CreateScope())
            setup.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

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

    // ---- authentication guard -------------------------------------------------------------------

    [Fact]
    public async Task Rejects_an_unauthenticated_caller_changing_password()
    {
        var result = await Controller(null).ChangePassword(new ChangePasswordRequest("old", "new-password"));

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Rejects_a_caller_whose_account_no_longer_exists_changing_password()
    {
        var user = SeedUser("emma");
        var principal = Principal(user);
        DeleteUser(user);

        var result = await Controller(principal).ChangePassword(new ChangePasswordRequest("old", "new-password"));

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Rejects_an_unauthenticated_caller_changing_username()
    {
        var result = await Controller(null).ChangeUsername(new UpdateUsernameRequest("newname"));

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    [Fact]
    public async Task Rejects_a_caller_whose_account_no_longer_exists_changing_username()
    {
        var user = SeedUser("emma");
        var principal = Principal(user);
        DeleteUser(user);

        var result = await Controller(principal).ChangeUsername(new UpdateUsernameRequest("newname"));

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    // ---- password change --------------------------------------------------------------------------

    [Fact]
    public async Task Rejects_the_wrong_current_password()
    {
        var user = await SeedUserWithPasswordAsync("emma", "correct-horse");

        var problem = ProblemFrom(await Controller(Principal(user))
            .ChangePassword(new ChangePasswordRequest("wrong-password", "new-password")));

        Assert.NotEmpty(problem.Errors);
    }

    [Fact]
    public async Task Rejects_a_new_password_shorter_than_eight_characters()
    {
        var user = await SeedUserWithPasswordAsync("emma", "correct-horse");

        // [ApiController] would normally short-circuit on the [MinLength(8)] attribute before the
        // action runs; invoking the action directly here still exercises the same request DTO
        // validation via ChangePasswordAsync's own PasswordTooShort check, since a too-short password
        // never satisfies Identity's PasswordOptions either.
        var problem = ProblemFrom(await Controller(Principal(user))
            .ChangePassword(new ChangePasswordRequest("correct-horse", "short")));

        Assert.NotEmpty(problem.Errors);
    }

    [Fact]
    public async Task Changes_the_password_so_the_old_one_no_longer_verifies_and_the_new_one_does()
    {
        var user = await SeedUserWithPasswordAsync("emma", "correct-horse");

        var result = await Controller(Principal(user))
            .ChangePassword(new ChangePasswordRequest("correct-horse", "new-password"));

        Assert.IsType<NoContentResult>(result);

        var reloaded = await ReloadAsync(user.Id);
        Assert.False(await _userManager.CheckPasswordAsync(reloaded, "correct-horse"));
        Assert.True(await _userManager.CheckPasswordAsync(reloaded, "new-password"));
    }

    [Fact]
    public async Task Keeps_the_caller_signed_in_after_a_password_change()
    {
        var user = await SeedUserWithPasswordAsync("emma", "correct-horse");

        await Controller(Principal(user)).ChangePassword(new ChangePasswordRequest("correct-horse", "new-password"));

        Assert.Equal(1, _auth.SignInCount);
    }

    // ---- username change --------------------------------------------------------------------------

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    public async Task Rejects_a_username_shorter_than_three_characters(string input)
    {
        var user = SeedUser("emma");

        var problem = ProblemFrom(await Controller(Principal(user)).ChangeUsername(new UpdateUsernameRequest(input)));

        Assert.True(problem.Errors.ContainsKey(nameof(UpdateUsernameRequest.Username)));
    }

    [Fact]
    public async Task Rejects_a_username_longer_than_sixty_four_characters()
    {
        var user = SeedUser("emma");

        var problem = ProblemFrom(await Controller(Principal(user))
            .ChangeUsername(new UpdateUsernameRequest(new string('a', 65))));

        Assert.True(problem.Errors.ContainsKey(nameof(UpdateUsernameRequest.Username)));
    }

    [Fact]
    public async Task Rejects_a_duplicate_username()
    {
        SeedUser("alice");
        var bob = SeedUser("bob");

        var problem = ProblemFrom(await Controller(Principal(bob)).ChangeUsername(new UpdateUsernameRequest("alice")));

        Assert.NotEmpty(problem.Errors);
    }

    [Fact]
    public async Task Rejects_a_duplicate_username_in_a_different_case()
    {
        SeedUser("alice");
        var bob = SeedUser("bob");

        var problem = ProblemFrom(await Controller(Principal(bob)).ChangeUsername(new UpdateUsernameRequest("ALICE")));

        Assert.NotEmpty(problem.Errors);
    }

    [Fact]
    public async Task Renames_the_account_and_returns_the_updated_user()
    {
        var user = SeedUser("emma");

        var body = Body(await Controller(Principal(user)).ChangeUsername(new UpdateUsernameRequest("emma2")));

        Assert.Equal("emma2", body.Username);
        Assert.Equal("emma2", (await ReloadAsync(user.Id)).UserName);
    }

    [Fact]
    public async Task Trims_the_username_before_storing_it()
    {
        var user = SeedUser("emma");

        var body = Body(await Controller(Principal(user)).ChangeUsername(new UpdateUsernameRequest("  emma2  ")));

        Assert.Equal("emma2", body.Username);
    }

    [Fact]
    public async Task Keeps_the_caller_signed_in_after_a_rename()
    {
        var user = SeedUser("emma");

        await Controller(Principal(user)).ChangeUsername(new UpdateUsernameRequest("emma2"));

        Assert.Equal(1, _auth.SignInCount);
    }

    // ---- fixture ---------------------------------------------------------------------------------

    /// <summary>
    /// A user with no password hash, the way <c>LibraryTestHost.SeedUser</c> does it -- fine for the
    /// username tests, which never touch the password.
    /// </summary>
    private ApplicationUser SeedUser(string userName)
    {
        using var db = NewContext();

        var user = new ApplicationUser { UserName = userName, NormalizedUserName = userName.ToUpperInvariant() };
        db.Users.Add(user);
        db.SaveChanges();

        return user;
    }

    /// <summary>
    /// A user created through the real <see cref="UserManager{TUser}"/> so it carries an actual
    /// PBKDF2 hash -- <c>SeedUser</c> above leaves PasswordHash null, which ChangePasswordAsync would
    /// reject for a reason that has nothing to do with what these tests are checking.
    /// </summary>
    private async Task<ApplicationUser> SeedUserWithPasswordAsync(string userName, string password)
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser { UserName = userName };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);

        return user;
    }

    private void DeleteUser(ApplicationUser user)
    {
        using var db = NewContext();
        db.Users.Remove(db.Users.Single(u => u.Id == user.Id));
        db.SaveChanges();
    }

    private AppDbContext NewContext() => new SqliteAppDbContext(
        new DbContextOptionsBuilder<SqliteAppDbContext>().UseSqlite(_connection).Options);

    /// <summary>
    /// The principal an auth cookie would carry, built the way <c>LibraryTestHost.Build</c> does --
    /// NameIdentifier is the claim Identity keeps the user id under, and the one GetUserAsync reads.
    /// </summary>
    private static ClaimsPrincipal Principal(ApplicationUser user) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "Test"));

    private AccountCredentialsController Controller(ClaimsPrincipal? principal)
    {
        var http = new DefaultHttpContext { RequestServices = _request.ServiceProvider };
        if (principal is not null) http.User = principal;

        // SignInManager reaches the request through the accessor, not through ControllerContext.
        _request.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        return new AccountCredentialsController(
            _userManager,
            _request.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    /// <summary>Reads through a fresh context, so persistence assertions are real round-trips.</summary>
    private async Task<ApplicationUser> ReloadAsync(string userId)
    {
        await using var db = NewContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
    }

    private static CurrentUserDto Body(ActionResult<CurrentUserDto> result) =>
        Assert.IsType<CurrentUserDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static ValidationProblemDetails ProblemFrom(IActionResult result) =>
        Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result).Value);

    private static ValidationProblemDetails ProblemFrom(ActionResult<CurrentUserDto> result) =>
        Assert.IsType<ValidationProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value);

    /// <summary>
    /// Stands in for the real authentication service. Recording the sign-in is what lets the
    /// cookie-refresh assertions run without a cookie handler, data protection, or a host.
    /// </summary>
    private sealed class RecordingAuthentication : IAuthenticationService
    {
        public int SignInCount { get; private set; }

        // RefreshSignInAsync bails out early unless this reports an already-authenticated request,
        // so returning NoResult here would make the refresh look broken when it is not. In the app
        // the cookie middleware is what succeeds; here the principal on the context stands in.
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(context.User.Identity?.IsAuthenticated == true
                ? AuthenticateResult.Success(new AuthenticationTicket(
                    context.User, scheme ?? IdentityConstants.ApplicationScheme))
                : AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext c, string? s, AuthenticationProperties? p) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext c, string? s, AuthenticationProperties? p) => Task.CompletedTask;

        public Task SignInAsync(HttpContext c, string? s, ClaimsPrincipal principal, AuthenticationProperties? p)
        {
            SignInCount++;
            return Task.CompletedTask;
        }

        public Task SignOutAsync(HttpContext c, string? s, AuthenticationProperties? p) => Task.CompletedTask;
    }
}
