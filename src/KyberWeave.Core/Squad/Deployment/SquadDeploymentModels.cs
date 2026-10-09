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

/// <summary>One managed hook entry owned inside a shared file: its JSON pointer and canonical digest.</summary>
/// <remarks>
/// The member is named <c>Container</c> rather than <c>Pointer</c> because the latter trips
/// CA1720 (<c>System.Reflection.Pointer</c>); the value is still an RFC 6901 JSON pointer
/// addressing the entry inside its file (for example <c>/hooks/preToolUse/0</c>).
/// </remarks>
public sealed record SquadOwnedBlockEntry(string Container, string Sha256);

/// <summary>The ownership boundary for one block spliced into a shared hook file.</summary>
/// <remarks>
/// Squad owns only <see cref="Entries"/> inside the file, never the file itself:
/// <see cref="CreatedFile"/> records whether Squad created the file so uninstall deletes
/// it only when nothing else remains.
/// </remarks>
public sealed record SquadOwnedBlock(
    string RelativePath,
    string Target,
    bool CreatedFile,
    IReadOnlyList<SquadOwnedBlockEntry> Entries);

/// <summary>One owned block entry whose current file content no longer matches its record.</summary>
public sealed record SquadOwnedBlockDrift(
    string RelativePath,
    string Target,
    string Location,
    string Reason);

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
    IReadOnlyList<SquadOwnedFile> Files)
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

    /// <summary>
    /// The hook-file blocks owned inside shared files. Additive so every existing
    /// positional construction of <see cref="SquadReceipt"/> keeps compiling: it defaults
    /// to empty, and only a receipt that actually owns a block carries any — those
    /// serialize on <c>kyber-squad.receipt/v3</c> while every receipt without blocks
    /// stays byte-identical v1 or v2, as ADR 0024 requires.
    /// </summary>
    public IReadOnlyList<SquadOwnedBlock> Blocks { get; init; } = [];
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
