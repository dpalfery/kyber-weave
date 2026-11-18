namespace KyberWeave.Contracts.Repositories;

/// <summary>The identifiers a command needs: a GUID, a random token and a random file name.</summary>
/// <remarks>
/// Code above the data layer asks this instead of calling <see cref="Guid.NewGuid" />, <see cref="Random" /> or
/// <see cref="System.IO.Path.GetRandomFileName" />, so a test that observes the value supplies a predictable one.
/// Every generated value is unique and no two calls return the same value.
/// </remarks>
public interface IIdGenerator
{
    /// <summary>Creates a new GUID.</summary>
    /// <returns>A GUID that no other call returned.</returns>
    Guid NewGuid();

    /// <summary>Creates a random token for a file name, a folder name or a marker.</summary>
    /// <returns>A non-empty string of letters, digits, hyphens and underscores, safe in a file name.</returns>
    string NewRandomToken();

    /// <summary>Creates a random file name, with no directory part.</summary>
    /// <returns>A bare file name that no other call returned.</returns>
    string NewRandomFileName();
}
