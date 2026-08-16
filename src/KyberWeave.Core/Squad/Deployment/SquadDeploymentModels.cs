namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Where Kyber-Squad deployment state is stored.</summary>
public enum SquadDeploymentScope
{
    Project,
    Global
}

/// <summary>The upstream APM build pinned by a Squad release.</summary>
public sealed record SquadApmIdentity(
    string Version,
    string TagCommit,
    string AssetSha256)
{
    /// <summary>The value recorded in every field when no upstream build is pinned.</summary>
    /// <remarks>
    /// Canonical <c>toolchain.yml</c> declares <c>validated-release: null</c> since rendering
    /// stopped shelling out to an external toolchain, so this is the value a real install
    /// writes today — not an edge case. It is a sentinel rather than a null identity because
    /// the lock schema carries the three fields unconditionally, and dropping them would be a
    /// schema bump for a field that is already vestigial.
    /// </remarks>
    public const string Unverified = "unverified";

    /// <summary>The identity written when a release pins no upstream build.</summary>
    public static SquadApmIdentity None { get; } = new(Unverified, Unverified, Unverified);
}

/// <summary>The reproducible inputs used for a Squad deployment.</summary>
public sealed record SquadLock(
    string Schema,
    string SquadVersion,
    string CliVersion,
    string McpVersion,
    string Bundle,
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> Exclusions,
    string Translation,
    string BundleDigest,
    string AssetDigest,
    SquadApmIdentity Apm);

/// <summary>A documented loss of native harness behavior in a rendered deployment.</summary>
public sealed record SquadDegradation(
    string Target,
    string Subject,
    string Code);

/// <summary>A deployed path and the exact bytes over which Squad has authority.</summary>
public sealed record SquadOwnedFile(
    string RelativePath,
    string Sha256,
    string Target,
    bool Adopted);

/// <summary>The ownership boundary for one Squad deployment.</summary>
public sealed record SquadReceipt(
    string Schema,
    SquadDeploymentScope Scope,
    string TargetRoot,
    DateTimeOffset InstalledAtUtc,
    IReadOnlyList<SquadDegradation> Degradations,
    IReadOnlyList<SquadOwnedFile> Files);

/// <summary>A harness-native file produced by the upstream renderer.</summary>
public sealed record SquadDeploymentFile
{
    public SquadDeploymentFile(string relativePath, byte[] content, string target)
    {
        ArgumentNullException.ThrowIfNull(content);
        RelativePath = relativePath;
        Content = content;
        Target = target;
    }

    public string RelativePath { get; init; }

    public ReadOnlyMemory<byte> Content { get; init; }

    public string Target { get; init; }
}

/// <summary>Supplies the per-user location used by global Squad state.</summary>
public interface ISquadUserPaths
{
    string ApplicationDataDirectory { get; }
}

/// <summary>Raised when a deployment would overwrite a path outside its receipt authority.</summary>
public sealed class SquadDeploymentConflictException : InvalidOperationException
{
    public SquadDeploymentConflictException()
    {
    }

    public SquadDeploymentConflictException(string message)
        : base(message)
    {
    }

    public SquadDeploymentConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when a deployment path escapes its declared target root.</summary>
public sealed class SquadPathContainmentException : InvalidOperationException
{
    public SquadPathContainmentException()
    {
    }

    public SquadPathContainmentException(string message)
        : base(message)
    {
    }

    public SquadPathContainmentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
