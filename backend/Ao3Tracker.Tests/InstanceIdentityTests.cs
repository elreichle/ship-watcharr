using Ao3Tracker.Api.Services.Storage;

namespace Ao3Tracker.Tests;

public class InstanceIdentityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("shipwatcharr-tests").FullName;

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private StoragePaths Paths() => new(
        DataDirectory: _dir,
        SqliteDbPath: Path.Combine(_dir, "db.sqlite"),
        SettingsFilePath: Path.Combine(_dir, "settings.json"),
        KeysDirectory: Path.Combine(_dir, "keys"));

    [Fact]
    public void Generates_a_six_character_hex_id()
    {
        var identity = InstanceIdentity.LoadOrCreate(Paths());

        Assert.Equal(6, identity.Id.Length);
        Assert.All(identity.Id, c => Assert.True(char.IsAsciiHexDigitLower(c), $"'{c}' is not lowercase hex"));
    }

    [Fact]
    public void Is_stable_across_restarts()
    {
        // The id appears in the User-Agent. If it changed on every boot, an AO3 admin tracking a
        // misbehaving instance would see a new identity each time and could not correlate anything.
        var first = InstanceIdentity.LoadOrCreate(Paths()).Id;
        var second = InstanceIdentity.LoadOrCreate(Paths()).Id;

        Assert.Equal(first, second);
    }

    [Fact]
    public void Persists_to_the_data_directory()
    {
        var identity = InstanceIdentity.LoadOrCreate(Paths());

        var path = Path.Combine(_dir, "instance-id");
        Assert.True(File.Exists(path));
        Assert.Equal(identity.Id, File.ReadAllText(path).Trim());
    }

    [Fact]
    public void Tolerates_surrounding_whitespace()
    {
        File.WriteAllText(Path.Combine(_dir, "instance-id"), "  a1b2c3\n");

        Assert.Equal("a1b2c3", InstanceIdentity.LoadOrCreate(Paths()).Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("ABCDEF")]     // uppercase is not the format we write
    [InlineData("a1b2")]       // too short
    [InlineData("a1b2c3d4")]   // too long
    public void Replaces_a_malformed_id(string contents)
    {
        File.WriteAllText(Path.Combine(_dir, "instance-id"), contents);

        var identity = InstanceIdentity.LoadOrCreate(Paths());

        Assert.Equal(6, identity.Id.Length);
        Assert.NotEqual(contents, identity.Id);
    }

    [Fact]
    public void Different_instances_get_different_ids()
    {
        var otherDir = Directory.CreateTempSubdirectory("shipwatcharr-tests").FullName;
        try
        {
            var a = InstanceIdentity.LoadOrCreate(Paths()).Id;
            var b = InstanceIdentity.LoadOrCreate(new StoragePaths(
                otherDir,
                Path.Combine(otherDir, "db.sqlite"),
                Path.Combine(otherDir, "settings.json"),
                Path.Combine(otherDir, "keys"))).Id;

            Assert.NotEqual(a, b);
        }
        finally
        {
            Directory.Delete(otherDir, recursive: true);
        }
    }
}
