using System.Globalization;
using System.Security.Cryptography;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Host;

/// <summary>Generates identifiers from the operating system's own sources.</summary>
public sealed class SystemIdGenerator : IIdGenerator
{
    /// <inheritdoc />
    public Guid NewGuid() => Guid.NewGuid();

    /// <inheritdoc />
    public string NewRandomToken() => RandomNumberGenerator.GetHexString(32, lowercase: true);

    /// <inheritdoc />
    public string NewRandomFileName() => Path.GetRandomFileName();

    /// <inheritdoc />
    public string NewSortableId(DateTimeOffset at) =>
        $"{at.ToUniversalTime().UtcTicks.ToString("x16", CultureInfo.InvariantCulture)}-"
        + RandomNumberGenerator.GetHexString(8, lowercase: true);
}
