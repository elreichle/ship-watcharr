using Ao3Tracker.Api.Commands;
using Ao3Tracker.Api.Data;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ao3Tracker.Tests;

/// <summary>
/// The server-side <c>reset-password</c> command. Run against a real <see cref="UserManager{TUser}"/>
/// and database, because what matters is that the real hasher accepts the printed password and the
/// real store saved the cleared lockout.
/// </summary>
public class ResetPasswordCommandTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly UserManager<ApplicationUser> _users;

    public ResetPasswordCommandTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
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
            .AddEntityFrameworkStores<AppDbContext>();

        _provider = services.BuildServiceProvider();
        using (var setup = _provider.CreateScope())
            setup.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();

        _scope = _provider.CreateScope();
        _users = _scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Prints_a_password_that_signs_in_and_retires_the_old_one()
    {
        await CreateUserAsync("emma", "forgotten-password");
        var output = new StringWriter();

        var exitCode = await ResetPasswordCommand.RunAsync(["emma"], _users, output);

        Assert.Equal(0, exitCode);
        var printed = PrintedPassword(output);
        var user = await Fresh("emma");
        Assert.True(await _users.CheckPasswordAsync(user, printed));
        Assert.False(await _users.CheckPasswordAsync(user, "forgotten-password"));
    }

    [Fact]
    public async Task Clears_the_lockout_and_the_failed_attempts_and_changes_the_stamp()
    {
        var user = await CreateUserAsync("emma", "forgotten-password");
        await _users.AccessFailedAsync(user);
        await _users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(5));
        var stampBefore = user.SecurityStamp;

        await ResetPasswordCommand.RunAsync(["emma"], _users, new StringWriter());

        user = await Fresh("emma");
        Assert.Null(user.LockoutEnd);
        Assert.Equal(0, user.AccessFailedCount);
        Assert.NotEqual(stampBefore, user.SecurityStamp);
    }

    [Fact]
    public async Task Finds_the_account_whatever_the_case_of_the_name()
    {
        await CreateUserAsync("Emma", "forgotten-password");

        Assert.Equal(0, await ResetPasswordCommand.RunAsync(["  emma "], _users, new StringWriter()));
    }

    [Fact]
    public async Task An_unknown_name_changes_nothing_and_lists_the_accounts()
    {
        await CreateUserAsync("emma", "forgotten-password");
        await CreateUserAsync("alice", "another-password");
        var output = new StringWriter();

        var exitCode = await ResetPasswordCommand.RunAsync(["emmma"], _users, output);

        Assert.Equal(1, exitCode);
        Assert.Contains("No account is named 'emmma'", output.ToString());
        Assert.Contains("alice, emma", output.ToString());
        Assert.True(await _users.CheckPasswordAsync(await Fresh("emma"), "forgotten-password"));
    }

    [Fact]
    public async Task No_username_prints_usage_and_the_accounts()
    {
        await CreateUserAsync("emma", "forgotten-password");
        var output = new StringWriter();

        var exitCode = await ResetPasswordCommand.RunAsync([], _users, output);

        Assert.Equal(2, exitCode);
        Assert.Contains("Usage: reset-password <username>", output.ToString());
        Assert.Contains("emma", output.ToString());
    }

    [Fact]
    public void Generated_passwords_are_four_groups_of_four_without_lookalike_characters()
    {
        var password = ResetPasswordCommand.GeneratePassword();

        Assert.Matches("^[a-z2-9]{4}-[a-z2-9]{4}-[a-z2-9]{4}-[a-z2-9]{4}$", password);
        Assert.DoesNotMatch("[01ilo]", password);
        Assert.NotEqual(password, ResetPasswordCommand.GeneratePassword());
    }

    private async Task<ApplicationUser> CreateUserAsync(string userName, string password)
    {
        var user = new ApplicationUser { UserName = userName };
        Assert.True((await _users.CreateAsync(user, password)).Succeeded);
        return user;
    }

    // A second scope, so the assertions read what was saved and not what the command's tracked
    // entity happens to hold in memory.
    private async Task<ApplicationUser> Fresh(string userName)
    {
        using var scope = _provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await users.FindByNameAsync(userName))!;
    }

    private static string PrintedPassword(StringWriter output) =>
        output.ToString().Split('\n').Select(line => line.Trim())
            .Single(line => System.Text.RegularExpressions.Regex.IsMatch(line, "^[a-z2-9]{4}(-[a-z2-9]{4}){3}$"));
}
