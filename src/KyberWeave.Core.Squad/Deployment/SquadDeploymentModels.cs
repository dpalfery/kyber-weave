using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;

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
    IReadOnlyDictionary<SquadTarget, string>? ModelPresets = null,
    SquadArbiterWiring? Arbiter = null);

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
    IReadOnlyDictionary<SquadTarget, string>? ModelPresets = null,
    SquadArbiterWiring? Arbiter = null);

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

/// <summary>A deployed path and the digest of the default Squad shipped there, for a file in the home.</summary>
/// <remarks>
///     <see cref="SquadOwnedFile" /> cannot describe these files: its target token selects a harness's
///     global root, and a home-level file such as the response guidance belongs to no harness — every
///     one of them reads it through the same path. <see cref="RelativePath" /> is therefore taken from
///     the home state root, <c>&lt;home&gt;/.kyber-weave</c>, rather than from a target root.
///     <para>
///         <see cref="Sha256" /> is the digest of the default this run ships, not a record of what the
///         operator left on disk. That is what makes an edit detectable: the deployed file no longer
///         matching the recorded digest is exactly the "the user changed this" question a refresh has
///         to answer before it writes.
///     </para>
/// </remarks>
public sealed record SquadHomeFile(string RelativePath, string Sha256);

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

    /// <summary>
    /// The hook-file blocks owned inside shared files. Additive so every existing
    /// positional construction of <see cref="SquadReceipt"/> keeps compiling: it defaults
    /// to empty, and only a receipt that actually owns a block carries any — those
    /// serialize on <c>kyber-squad.receipt/v3</c> while every receipt without blocks
    /// stays byte-identical v1 or v2, as ADR 0024 requires.
    /// </summary>
    public IReadOnlyList<SquadOwnedBlock> Blocks { get; init; } = [];

    /// <summary>
    ///     The files this receipt owns in the Kyber-Weave home state root,
    ///     <c>&lt;home&gt;/.kyber-weave</c>, rather than beneath a harness root.
    /// </summary>
    /// <remarks>
    ///     Additive and optional on the persisted schema: every global receipt written before the
    ///     user-home response guidance existed carries no <c>homeFiles</c> field, and reads with this
    ///     empty. A project or instance receipt never carries one.
    /// </remarks>
    public IReadOnlyList<SquadHomeFile> HomeFiles { get; init; } = [];
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
