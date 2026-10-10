using System.Security.Cryptography;
using Ao3Tracker.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ao3Tracker.Api.Commands;

/// <summary>
/// <c>dotnet Ao3Tracker.Api.dll reset-password &lt;username&gt;</c>: the way back in for someone who
/// has forgotten their password. There is no email reset (email is optional, and an instance need
/// not be able to send mail), and when the forgotten account is the only admin there is nobody in
/// the app who could reset it for them — so this runs on the server, where reaching the shell is
/// what proves you own the instance.
/// </summary>
/// <remarks>
/// The new password is generated rather than accepted as an argument, so it never sits in a shell
/// history or a process list. It also clears any lockout and the failed-attempt count, since a
/// forgotten password is usually what caused the lockout, and it changes the security stamp, which
/// signs the account out everywhere else. Whoever runs this should change the password under
/// Settings once they are back in.
/// </remarks>
public static class ResetPasswordCommand
{
    public const string Name = "reset-password";

    // No 0/o, 1/l/i: someone types this from a terminal into a login form.
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    /// <returns>The process exit code: 0 reset, 1 no such account, 2 no username given.</returns>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, UserManager<ApplicationUser> users, TextWriter output)
    {
        if (args.Count != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            output.WriteLine($"Usage: {Name} <username>");
            await WriteAccountsAsync(users, output);
            return 2;
        }

        var user = await users.FindByNameAsync(args[0].Trim());
        if (user is null)
        {
            output.WriteLine($"No account is named '{args[0].Trim()}'.");
            await WriteAccountsAsync(users, output);
            return 1;
        }

        var password = GeneratePassword();
        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;

        // Saves the fields above along with the new stamp, in one write.
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            output.WriteLine($"Could not reset the password: {string.Join(" ", result.Errors.Select(e => e.Description))}");
            return 1;
        }

        output.WriteLine($"Password for '{user.UserName}' reset. Any lockout is cleared.");
        output.WriteLine();
        output.WriteLine($"    {password}");
        output.WriteLine();
        output.WriteLine("Sign in with it, then change it under Settings.");
        return 0;
    }

    /// <summary>Four groups of four, about 79 bits: easy to read off a screen, impossible to guess.</summary>
    internal static string GeneratePassword() =>
        string.Join('-', Enumerable.Range(0, 4).Select(_ => RandomNumberGenerator.GetString(Alphabet, 4)));

    // Someone who has forgotten their password may have forgotten their username too.
    private static async Task WriteAccountsAsync(UserManager<ApplicationUser> users, TextWriter output)
    {
        var names = await users.Users.OrderBy(u => u.UserName).Select(u => u.UserName).ToListAsync();
        output.WriteLine(names.Count == 0
            ? "This instance has no accounts yet."
            : $"Accounts on this instance: {string.Join(", ", names)}");
    }
}
