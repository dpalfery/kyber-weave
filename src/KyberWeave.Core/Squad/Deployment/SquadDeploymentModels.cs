using JetBrains.Annotations;
using KyberWeave.Core.Squad.Model;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Where Kyber-Squad deployment state is stored.</summary>
public enum SquadDeploymentScope
{
    Project,
    Global,
    Instance
}

/// <summary>The upstream APM build pinned by a Squad release.</summary>
public sealed record SquadApmIdentity(
    string Version,
    string TagCommit,
    string AssetSha256)
{
    /// <summary>The value recorded in every field when no upstream build is pinned.</summary>
    /// <remarks>
    ///     Canonical <c>toolchain.yml</c> declares <c>validated-release: null</c> since rendering
    ///     stopped shelling out to an external toolchain, so this is the value a real install
    ///     writes today — not an edge case. It is a sentinel rather than a null identity because
    ///     the lock schema carries the three fields unconditionally, and dropping them would be a
    ///     schema bump for a field that is already vestigial.
    /// </remarks>
    public const string Unverified = "unverified";

    /// <summary>The identity written when a release pins no upstream build.</summary>
    public static SquadApmIdentity None { get; } = new(Unverified, Unverified, Unverified);
}

/// <summary>An applied preset and its digest recorded in a deployment lock.</summary>
public sealed record SquadAppliedPreset(string Name, string Digest);

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
    SquadApmIdentity Apm,
    IReadOnlyDictionary<SquadTarget, SquadAppliedPreset>? ModelPresets = null);

/// <summary>Parameters for a Kyber-Squad install lifecycle operation.</summary>
public sealed record SquadInstallRequest(
    string TargetRoot,
    SquadDeploymentScope Scope,
    IReadOnlyList<SquadTarget> Targets,
    IReadOnlyList<string>? Exclusions = null,
    string? Version = null,
    string Bundle = "full",
    string Translation = "best-effort",
    bool Adopt = false,
    bool DryRun = false,
    string Repository = "dpalfery/kyber-weave",
    IReadOnlyDictionary<SquadTarget, string>? ModelPresets = null);

/// <summary>Parameters for a Kyber-Squad update lifecycle operation.</summary>
public sealed record SquadUpdateRequest(
    string TargetRoot,
    SquadDeploymentScope Scope,
    IReadOnlyList<SquadTarget>? Targets = null,
    IReadOnlyList<string>? Exclusions = null,
    string? Version = null,
    string Bundle = "full",
    string Translation = "best-effort",
    bool ReplaceManaged = false,
    bool DryRun = false,
    string Repository = "dpalfery/kyber-weave",
    IReadOnlyDictionary<SquadTarget, string>? ModelPresets = null);

/// <summary>Parameters for a Kyber-Squad uninstall lifecycle operation.</summary>
public sealed record SquadUninstallRequest(
    string TargetRoot,
    SquadDeploymentScope Scope,
    bool DryRun = false);

/// <summary>The result of executing a Kyber-Squad lifecycle operation.</summary>
/// <remarks>
///     <see cref="Warnings" /> carries what a committed transaction could not finish, such as a registry
///     write that failed after the deployment itself committed. The deployment is never rolled back for
///     them, so a caller that prints only success has hidden the problem.
/// </remarks>
public sealed record SquadLifecycleResult(
    bool Success,
    SquadDeploymentPlan? Plan = null,
    SquadReceipt? Receipt = null,
    SquadLock? Lock = null,
    IReadOnlyList<SquadDegradation>? Degradations = null,
    IReadOnlyList<string>? Errors = null,
    bool DryRun = false,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<SquadModelDiffItem>? ModelChanges = null);

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
    IReadOnlyList<SquadOwnedFile> Files)
{
    /// <summary>
    ///     How this receipt's owned files map onto physical roots. Meaningful only for
    ///     <see cref="SquadDeploymentScope.Global" />; a project receipt never reads or writes it.
    ///     Additive so every existing positional construction of <see cref="SquadReceipt" /> keeps
    ///     compiling: it defaults to <see cref="SquadReceiptLayout.PerTargetRoots" />, the layout
    ///     every deployment has used since #91, and a legacy receipt read from v1 JSON gets this
    ///     overwritten by classification in <see cref="SquadStateStore.DeserializeReceipt" />.
    /// </summary>
    public SquadReceiptLayout Layout { get; init; } = SquadReceiptLayout.PerTargetRoots;
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

/// <summary>Supplies the per-user locations used by Squad state.</summary>
public interface ISquadUserPaths
{
    /// <summary>The per-user application data directory that holds legacy global Squad state.</summary>
    string ApplicationDataDirectory { get; }

    /// <summary>
    ///     The user's home directory. Home-level Squad state lives beneath it at
    ///     <c>.kyber-weave/squad</c>, so it does not depend on the platform application data folder.
    /// </summary>
    string HomeDirectory { get; }
}

/// <summary>One project that has registered itself in the home-level Squad registry.</summary>
/// <remarks>
///     <see cref="Stale" /> records that the registered root no longer carries a deployment, or no
///     longer exists. A stale entry is reported rather than silently dropped, so an operator can
///     see what was registered before deciding to unregister it.
/// </remarks>
public sealed record SquadProjectRegistryEntry(
    string RootKey,
    string Path,
    DateTimeOffset LastUpdatedUtc,
    IReadOnlyList<string> Targets,
    bool Stale,
    string? StaleReason);

/// <summary>A legacy Squad state root that the migration did not move, and why.</summary>
/// <param name="Root">The legacy root as found on disk.</param>
/// <param name="Reason">Why it was left in place.</param>
/// <param name="Guidance">What the operator should do next.</param>
public sealed record SquadUnmigratedLegacyRoot(string Root, string Reason, string Guidance);

/// <summary>The outcome of moving legacy Squad state into the home-level layout.</summary>
/// <remarks>
///     Immutable so a caller can render it, log it, or assert on it without the migration still
///     being able to change it afterwards.
/// </remarks>
public sealed record SquadMigrationReport(
    IReadOnlyList<string> MigratedItems,
    IReadOnlyList<SquadUnmigratedLegacyRoot> UnmigratedLegacyRoots,
    IReadOnlyList<string> ImportedProjects,
    IReadOnlyList<string> Warnings);

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
