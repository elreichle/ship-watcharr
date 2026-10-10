using Ao3Tracker.Api.Controllers;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Tests;

/// <summary>
/// What a failed sign-in says. Identity's own lockout (five failures, five minutes) runs for real
/// here, because the case that matters is the one where the account is locked and the right
/// password is still turned away. That has to read differently from a wrong password.
/// </summary>
public class AuthControllerLoginTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _request;
    private readonly UserManager<ApplicationUser> _userManager;

    public AuthControllerLoginTests()
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
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager();
        services.AddAuthentication(IdentityConstants.ApplicationScheme);

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

    [Fact]
    public async Task A_wrong_password_is_called_wrong()
    {
        await CreateUserAsync();

        Assert.Equal("Invalid username or password.", await LoginMessageAsync("emma", "wrong-password"));
    }

    [Fact]
    public async Task An_unknown_username_is_called_wrong_too()
    {
        Assert.Equal("Invalid username or password.", await LoginMessageAsync("nobody", "wrong-password"));
    }

    [Fact]
    public async Task The_failure_that_locks_the_account_says_so()
    {
        await CreateUserAsync();
        for (var i = 0; i < 4; i++)
            Assert.Equal("Invalid username or password.", await LoginMessageAsync("emma", "wrong-password"));

        var message = await LoginMessageAsync("emma", "wrong-password");

        Assert.Contains("locked for 5 more minutes", message);
    }

    [Fact]
    public async Task The_right_password_during_a_lockout_is_told_about_the_lockout()
    {
        var user = await CreateUserAsync();
        await _userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddSeconds(30));

        var message = await LoginMessageAsync("emma", "correct-horse");

        Assert.Contains("locked for 1 more minute.", message);
        Assert.Contains("even the right password", message);
    }

    private async Task<ApplicationUser> CreateUserAsync()
    {
        var user = new ApplicationUser { UserName = "emma" };
        Assert.True((await _userManager.CreateAsync(user, "correct-horse")).Succeeded);
        return user;
    }

    private async Task<string> LoginMessageAsync(string username, string password)
    {
        var http = new DefaultHttpContext { RequestServices = _request.ServiceProvider };
        _request.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        var controller = new AuthController(
            _userManager,
            _request.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>())
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.Login(new LoginRequest(username, password));
        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(result.Result);
        return (string)unauthorized.Value!.GetType().GetProperty("message")!.GetValue(unauthorized.Value)!;
    }
}
