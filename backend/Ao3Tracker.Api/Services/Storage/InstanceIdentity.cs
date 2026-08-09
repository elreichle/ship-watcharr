using System.Security.Cryptography;

namespace Ao3Tracker.Api.Services.Storage;

/// <summary>
/// A short, stable, random identifier for this deployment, generated on first run and kept in
/// the data directory alongside everything else the instance persists.
///
/// It exists so two installations are distinguishable in AO3's logs without either revealing
/// anything about who runs them — an admin can say "instance a3f9c2 is fetching too fast"
/// precisely, instead of inferring it from an IP address. Random rather than derived from
/// hostname, machine id, or MAC address: those would leak details of the operator's environment,
/// which is exactly what this is meant to avoid.
/// </summary>
public sealed class InstanceIdentity
{
    private const string FileName = "instance-id";

    public string Id { get; }

    private InstanceIdentity(string id) => Id = id;

    public static InstanceIdentity LoadOrCreate(StoragePaths paths)
    {
        var path = Path.Combine(paths.DataDirectory, FileName);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (IsWellFormed(existing)) return new InstanceIdentity(existing);
        }

        // 3 bytes -> 6 lowercase hex chars. Enough to tell a handful of instances apart in a log
        // line; deliberately too short to be useful for tracking anyone across contexts.
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();
        File.WriteAllText(path, id);
        return new InstanceIdentity(id);
    }

    private static bool IsWellFormed(string value) =>
        value.Length == 6 && value.All(char.IsAsciiHexDigitLower);
}
