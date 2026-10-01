using JetBrains.Annotations;

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
    SquadApmIdentity Apm) : IEquatable<SquadLock>
{
    public bool Equals(SquadLock? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        return Schema == other.Schema &&
               SquadVersion == other.SquadVersion &&
               CliVersion == other.CliVersion &&
               McpVersion == other.McpVersion &&
               Bundle == other.Bundle &&
               Targets.SequenceEqual(other.Targets, StringComparer.Ordinal) &&
               Exclusions.SequenceEqual(other.Exclusions, StringComparer.Ordinal) &&
               Translation == other.Translation &&
               BundleDigest == other.BundleDigest &&
               AssetDigest == other.AssetDigest &&
               Apm.Equals(other.Apm);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Schema, StringComparer.Ordinal);
        hash.Add(SquadVersion, StringComparer.Ordinal);
        hash.Add(CliVersion, StringComparer.Ordinal);
        hash.Add(McpVersion, StringComparer.Ordinal);
        hash.Add(Bundle, StringComparer.Ordinal);
        foreach (string target in Targets)
            hash.Add(target, StringComparer.Ordinal);
        foreach (string exclusion in Exclusions)
            hash.Add(exclusion, StringComparer.Ordinal);
        hash.Add(Translation, StringComparer.Ordinal);
        hash.Add(BundleDigest, StringComparer.Ordinal);
        hash.Add(AssetDigest, StringComparer.Ordinal);
        hash.Add(Apm);
        return hash.ToHashCode();
    }
}

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

/// <summary>How a Global receipt's owned files map onto physical roots.</summary>
public enum SquadReceiptLayout
{
    /// <summary>Every owned file lives beneath the one recorded deployment root (pre-#91).</summary>
    SingleRoot,

    /// <summary>Each owned file lives beneath its own target's global root (since #91).</summary>
    PerTargetRoots
}

/// <summary>The ownership boundary for one Squad deployment.</summary>
public sealed record SquadReceipt(
    string Schema,
    SquadDeploymentScope Scope,
    string TargetRoot,
    DateTimeOffset InstalledAtUtc,
    IReadOnlyList<SquadDegradation> Degradations,
    IReadOnlyList<SquadOwnedFile> Files) : IEquatable<SquadReceipt>
{
    /// <summary>
    /// How this receipt's owned files map onto physical roots. Meaningful only for
    /// <see cref="SquadDeploymentScope.Global"/>; a project receipt never reads or writes it.
    /// Additive so every existing positional construction of <see cref="SquadReceipt"/> keeps
    /// compiling: it defaults to <see cref="SquadReceiptLayout.PerTargetRoots"/>, the layout
    /// every deployment has used since #91, and a legacy receipt read from v1 JSON gets this
    /// overwritten by classification in <see cref="SquadStateStore.DeserializeReceipt"/>.
    /// </summary>
    public SquadReceiptLayout Layout { get; init; } = SquadReceiptLayout.PerTargetRoots;

    public bool Equals(SquadReceipt? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        return Schema == other.Schema &&
               Scope == other.Scope &&
               TargetRoot == other.TargetRoot &&
               InstalledAtUtc == other.InstalledAtUtc &&
               Degradations.SequenceEqual(other.Degradations) &&
               Files.SequenceEqual(other.Files) &&
               Layout == other.Layout;
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Schema, StringComparer.Ordinal);
        hash.Add(Scope);
        hash.Add(TargetRoot, StringComparer.Ordinal);
        hash.Add(InstalledAtUtc);
        foreach (SquadDegradation degradation in Degradations)
            hash.Add(degradation);
        foreach (SquadOwnedFile file in Files)
            hash.Add(file);
        hash.Add(Layout);
        return hash.ToHashCode();
    }
}

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

    public ReadOnlyMemory<byte> Content { get; }

    public string Target { get; }
}

/// <summary>Supplies the per-user location used by global Squad state.</summary>
public interface ISquadUserPaths
{
    string ApplicationDataDirectory { get; }
}

internal sealed class DefaultSquadUserPaths : ISquadUserPaths
{
    public static DefaultSquadUserPaths Instance { get; } = new();

    private DefaultSquadUserPaths()
    {
    }

    private string? _applicationDataDirectoryOverride;

    public void SetApplicationDataDirectory(string applicationDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        _applicationDataDirectoryOverride = Path.GetFullPath(applicationDataDirectory);
    }

    public string ApplicationDataDirectory =>
        string.IsNullOrWhiteSpace(_applicationDataDirectoryOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : _applicationDataDirectoryOverride;
}

/// <summary>Raised when a deployment would overwrite a path outside its receipt authority.</summary>
public sealed class SquadDeploymentConflictException : InvalidOperationException
{
    [UsedImplicitly]
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
    [UsedImplicitly]
    public SquadPathContainmentException()
    {
    }

    public SquadPathContainmentException(string message)
        : base(message)
    {
    }

    [UsedImplicitly]
    public SquadPathContainmentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
