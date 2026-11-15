using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>A preflighted set of file and state mutations for one Squad lifecycle operation.</summary>
public sealed class SquadDeploymentPlan
{
    /// <summary>
    ///     Each target's pre-#91 legacy Global prefix, fixed per target and taken from the
    ///     renderer's own prefix constant rather than derived from the target token, because two
    ///     targets' verified prefixes diverge from their token: Copilot renders beneath
    ///     <c>.github/</c>, not <c>.copilot/</c> (<c>CopilotRenderer</c>), and Antigravity
    ///     beneath <c>.agents/</c>, not <c>.antigravity/</c> (<c>AntigravityRenderer</c>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> LegacyGlobalPrefixes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["codex"] = ".codex/",
            ["cursor"] = ".cursor/",
            ["claude"] = ".claude/",
            ["copilot"] = ".github/",
            ["opencode"] = ".opencode/",
            ["kilo"] = ".kilo/",
            ["antigravity"] = ".agents/",
            ["warp"] = ".warp/",
            ["factory"] = ".factory/",
            ["pi"] = ".pi/",
            ["zcode"] = ".zcode/",
            ["devin"] = ".devin/",
            ["commandcode"] = ".commandcode/"
        };

    private readonly ISquadGlobalRootResolver? _globalRoots;
    private readonly bool _isSingleRootLayout;

    public SquadDeploymentPlan(
        string targetRoot,
        SquadPhysicalRootIdentity physicalRootIdentity,
        SquadDeploymentScope scope,
        SquadLock? squadLock,
        SquadReceipt receipt,
        IReadOnlyList<SquadFileMutation> fileMutations,
        IReadOnlyList<SquadFilePrecondition> filePreconditions,
        SquadStateMutation lockMutation,
        SquadStateMutation receiptMutation,
        ISquadGlobalRootResolver? globalRoots = null,
        bool isSingleRootLayout = false,
        IReadOnlyList<SquadOwnedBlockDrift>? blockDrifts = null,
        IReadOnlyList<SquadDegradedFile>? degradedFiles = null)
    {
        TargetRoot = targetRoot;
        PhysicalRootPath = physicalRootIdentity.PhysicalPath;
        PhysicalRootKey = physicalRootIdentity.Key;
        Scope = scope;
        Lock = squadLock;
        Receipt = receipt;
        FileMutations = fileMutations;
        PlannedFileChanges =
        [
            .. fileMutations.Select(mutation =>
                new SquadPlannedFileChange(mutation.RelativePath, mutation.Target, mutation.Kind))
        ];
        FilePreconditions = filePreconditions;
        LockMutation = lockMutation;
        ReceiptMutation = receiptMutation;
        _globalRoots = globalRoots;
        _isSingleRootLayout = isSingleRootLayout;
        BlockDrifts = blockDrifts ?? [];
        DegradedFiles = degradedFiles ?? [];
    }

    /// <summary>The absolute root into which harness-native files are deployed.</summary>
    public string TargetRoot { get; }

    public string PhysicalRootPath { get; }

    public string PhysicalRootKey { get; }

    /// <summary>The state scope for this deployment.</summary>
    public SquadDeploymentScope Scope { get; }

    /// <summary>The desired lock, or <see langword="null" /> for an uninstall.</summary>
    public SquadLock? Lock { get; }

    /// <summary>
    ///     The desired or retained ownership receipt. A transaction anchors it to its canonical root before
    ///     the first write, so after execution it names exactly what was written.
    /// </summary>
    public SquadReceipt Receipt { get; private set; }

    /// <summary>
    ///     The pending file mutations, in apply order. Internal payload: hosts report
    ///     <see cref="PlannedFileChanges" /> instead.
    /// </summary>
    public IReadOnlyList<SquadFileMutation> FileMutations { get; }

    /// <summary>
    ///     The pending file changes, in apply order, without payloads. Public so command hosts
    ///     can report what a plan will change — an uninstall plan's <see cref="Receipt" /> holds
    ///     only the files it retains, so the planned removals are readable from this list alone.
    /// </summary>
    public IReadOnlyList<SquadPlannedFileChange> PlannedFileChanges { get; }

    /// <summary>
    ///     Deployed files this plan could not read and therefore kept on trust rather than verifying, each
    ///     with the raw target token that produced it. Empty for a plan whose every file was read and checked.
    /// </summary>
    public IReadOnlyList<SquadDegradedFile> DegradedFiles { get; }

    public IReadOnlyList<SquadFilePrecondition> FilePreconditions { get; }

    /// <summary>
    /// The owned block entries whose current file content no longer matches the receipt at
    /// plan time. Update preserves these entries — rewriting only the blocks without drift —
    /// unless the caller passes <c>replaceManaged</c>, so the operator can see what was
    /// kept and why.
    /// </summary>
    public IReadOnlyList<SquadOwnedBlockDrift> BlockDrifts { get; }

    public SquadStateMutation LockMutation { get; }

    public SquadStateMutation ReceiptMutation { get; }

    /// <summary>
    ///     Replaces the receipt's target root with the canonical root it will be written against. Only the
    ///     transaction calls this, because only the store knows the home that a global receipt is anchored on.
    /// </summary>
    /// <param name="anchorRoot">The canonical absolute root to record.</param>
    public void AnchorReceipt(string anchorRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorRoot);
        Receipt = Receipt with { TargetRoot = anchorRoot };
    }

    /// <summary>
    ///     Records the home-owned files this run wrote, for a plan that has already passed every
    ///     preflight check.
    /// </summary>
    /// <param name="homeFiles">The records to persist, each digest read back from the path it names.</param>
    /// <remarks>
    ///     Home-owned files sit outside this plan's file mutations, so they are written by the caller
    ///     between planning and committing. Attaching here — rather than taking them as a planning
    ///     input — is what keeps a plan that fails preflight from describing a file the run never
    ///     delivered: a caller only has a record to attach once a write has landed, which can only
    ///     happen after the plan is validated.
    /// </remarks>
    public void AttachHomeFiles(IReadOnlyList<SquadHomeFile> homeFiles)
    {
        ArgumentNullException.ThrowIfNull(homeFiles);
        Receipt = Receipt with { HomeFiles = homeFiles };
    }

    /// <summary>
    ///     Resolves the absolute physical path where <c>file.RelativePath</c> will be written,
    ///     based on the deployment scope: for <see cref="SquadDeploymentScope.Project" /> that is
    ///     <c>PhysicalRootPath</c> (the project root) combined with the relative path unchanged;
    ///     for <see cref="SquadDeploymentScope.Global" /> it is the resolver's root for
    ///     <c>file.Target</c> combined with the relative path, unless this plan carries a legacy
    ///     single-root receipt, in which case it is <c>PhysicalRootPath</c> regardless.
    /// </summary>
    public string ResolvePhysicalPath(SquadOwnedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return ResolvePhysicalPath(file.Target, file.RelativePath);
    }

    /// <summary>
    ///     Resolves the absolute physical path for a bare target token and relative path, for
    ///     callers such as <c>SquadTransaction</c> whose in-flight mutations carry a target
    ///     token rather than a full <see cref="SquadOwnedFile" />.
    /// </summary>
    public string ResolvePhysicalPath(string target, string relativePath)
    {
        return SquadPathPolicy.ResolveFile(ResolvePhysicalRoot(target), relativePath);
    }

    /// <summary>Resolves the physical root directory for a bare target token.</summary>
    /// <remarks>
    ///     <c>globalRoots</c> is an opt-in, purely-additive parameter (R18/U8): every deployment
    ///     that predates it wrote every file under the single provided <c>PhysicalRootPath</c>
    ///     regardless of scope, because Global scope by itself only ever changed where lock,
    ///     receipt, and journal state lived (<c>SquadStateStore</c>), never where deployed
    ///     files landed. Falling back to <c>PhysicalRootPath</c> here when no resolver was supplied
    ///     keeps that legacy single-root behavior byte-for-byte unchanged. Once a resolver is
    ///     supplied, an unmapped target is a real bug: <see cref="SquadGlobalRoots" /> throws for it
    ///     directly, and this method never substitutes the project root for a resolver's answer —
    ///     except when this plan's receipt classifies as <see cref="SquadReceiptLayout.SingleRoot" />
    ///     (#91: a receipt written before rc.11 recorded every entry beneath the one deployment
    ///     root), in which case the recorded root is the correct physical answer regardless of what
    ///     a resolver would say.
    /// </remarks>
    public string ResolvePhysicalRoot(string target)
    {
        return ResolvePhysicalRoot(Scope, PhysicalRootPath, _globalRoots, target, _isSingleRootLayout);
    }

    private static string ResolvePhysicalRoot(
        SquadDeploymentScope scope,
        string physicalRootPath,
        ISquadGlobalRootResolver? globalRoots,
        string target,
        bool isSingleRootLayout)
    {
        if (globalRoots is null || (scope == SquadDeploymentScope.Global && isSingleRootLayout))
            return physicalRootPath;

        return scope switch
        {
            SquadDeploymentScope.Project => physicalRootPath,
            SquadDeploymentScope.Instance => physicalRootPath,
            SquadDeploymentScope.Global => globalRoots.ResolveGlobalRoot(
                SquadTargetCatalog.Parse([target]).Single()),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown deployment scope.")
        };
    }

    public static string ResolvePhysicalPath(
        SquadDeploymentScope scope,
        string physicalRootPath,
        ISquadGlobalRootResolver? globalRoots,
        string target,
        string relativePath,
        bool isSingleRootLayout)
    {
        return SquadPathPolicy.ResolveFile(
            ResolvePhysicalRoot(scope, physicalRootPath, globalRoots, target, isSingleRootLayout),
            relativePath);
    }

    /// <summary>
    ///     Resolves the absolute physical path where a receipt-owned file is deployed, using the
    ///     same scope and resolver rules as deploy time. Project scope resolves beneath
    ///     <paramref name="targetRoot" />; Global scope resolves beneath the file's target root from
    ///     <paramref name="globalRoots" />, unless <paramref name="receipt" /> classifies as a legacy
    ///     single-root receipt (<see cref="IsLegacySingleRootReceipt" />) — written before rc.11
    ///     (#91) — in which case it resolves beneath <paramref name="targetRoot" /> regardless of
    ///     what the resolver would answer, matching where that receipt's files actually live.
    ///     Consumers that verify deployed bytes — status reporting in particular — must use this
    ///     rather than joining <paramref name="targetRoot" /> with the relative path directly, which
    ///     only holds for Project scope.
    /// </summary>
    public static string ResolveOwnedFilePath(
        SquadReceipt receipt,
        string targetRoot,
        ISquadGlobalRootResolver? globalRoots,
        SquadOwnedFile file)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(file);

        return ResolvePhysicalPath(
            receipt.Scope,
            SquadPhysicalRootIdentity.Resolve(targetRoot).PhysicalPath,
            globalRoots,
            file.Target,
            file.RelativePath,
            IsLegacySingleRootReceipt(receipt));
    }

    /// <summary>
    ///     Classifies a Global receipt's owned files as legacy single-root or modern
    ///     per-target-roots, purely from their recorded paths — never touching disk (Q3b was
    ///     rejected precisely because a filesystem-dependent check could reclassify the same
    ///     receipt differently over time).
    /// </summary>
    /// <remarks>
    ///     Global receipts written before #91 (rc.9/rc.10) rendered exactly as Project scope did, so
    ///     every owned path still carried its harness's dot-prefixed project folder. Since #91 every
    ///     renderer strips that prefix under Global scope — guarded by
    ///     <c>RenderReceiptLayout_GlobalRender_ContainsNoPrefixedPaths</c> — and writes a bare path
    ///     instead, so whether a file starts with its own target's <see cref="LegacyGlobalPrefixes" />
    ///     entry is the fingerprint of a receipt #91 predates (A5): every entry prefixed classifies
    ///     as <see cref="SquadReceiptLayout.SingleRoot" />, no entry prefixed classifies as
    ///     <see cref="SquadReceiptLayout.PerTargetRoots" />, and a mix of the two is refused outright
    ///     rather than guessed at.
    /// </remarks>
    public static SquadReceiptLayout ClassifyGlobalLayout(IReadOnlyList<SquadOwnedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        bool anyPrefixed = false;
        bool anyBare = false;
        foreach (SquadOwnedFile file in files)
        {
            bool isPrefixed = LegacyGlobalPrefixes.TryGetValue(file.Target, out string? prefix) &&
                              file.RelativePath.StartsWith(prefix, StringComparison.Ordinal);
            if (isPrefixed)
                anyPrefixed = true;
            else
                anyBare = true;
        }

        if (anyPrefixed && anyBare)
            throw new InvalidDataException(
                "Squad receipt mixes legacy prefixed paths with modern bare paths across its " +
                "files, so its layout cannot be classified. Recover it by hand: verify or " +
                "remove each file, then delete the receipt and lock so a fresh install can " +
                "recreate them.");

        return anyPrefixed ? SquadReceiptLayout.SingleRoot : SquadReceiptLayout.PerTargetRoots;
    }

    /// <summary>
    ///     Whether <paramref name="receipt" /> is a Global receipt written before #91 (rc.9/rc.10),
    ///     whose owned files record each target's project-scope dot-prefixed path beneath the one
    ///     recorded deployment root rather than beneath that target's own global root.
    /// </summary>
    public static bool IsLegacySingleRootReceipt(SquadReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.Scope == SquadDeploymentScope.Global &&
               ClassifyGlobalLayout(receipt.Files) == SquadReceiptLayout.SingleRoot;
    }

    /// <summary>
    ///     The conflict a legacy single-root receipt raises before any download or file mutation,
    ///     naming the two commands (in order) that move a deployment to the current layout.
    /// </summary>
    public static SquadDeploymentConflictException LegacySingleRootLayoutConflict()
    {
        return new SquadDeploymentConflictException(
            "This Global Kyber-Squad receipt uses the legacy single-root layout written before " +
            "rc.11 (kyber-weave/kyber-weave#91) and cannot be updated in place. Run " +
            "'squad uninstall --global' to remove it, then 'squad install --global' to " +
            "reinstall with the current per-target layout.");
    }


    public static string IdentityFromRelativePath(string relativePath)
    {
        string fileName = Path.GetFileName(relativePath);

        // Bare agent.md inside a directory (e.g., .agents/agents/<name>/agent.md) resolves
        // to the parent directory name. This is Antigravity's native shape per the "Native Both"
        // pattern: agents render to .agents/agents/<name>/agent.md with identity <name>.
        if (fileName.Equals("agent.md", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(relativePath);
            return string.IsNullOrEmpty(parent) ? fileName : Path.GetFileName(parent);
        }

        if (fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(relativePath);
            return string.IsNullOrEmpty(parent) ? fileName : Path.GetFileName(parent);
        }

        if (fileName.EndsWith(".agent.md", StringComparison.Ordinal)) return fileName[..^".agent.md".Length];

        return Path.GetFileNameWithoutExtension(fileName);
    }

    public static string DeployedFileIdentity(string target, string relativePath)
    {
        return target + '\0' + relativePath;
    }
}

/// <summary>
///     One unmanaged file whose name matches a canonical Squad identity and whose
///     bytes do not match the current render. Doctor lists these; install refuses them.
/// </summary>
public sealed record SquadUnmanagedPathCollision(
    string RelativePath,
    string FullPath,
    string Target,
    string Identity);

/// <summary>
///     Whether a planned file change writes new bytes or removes a deployed file. Public so
///     command hosts can report what a plan will do without seeing the internal payload record.
/// </summary>
public enum SquadFileMutationKind
{
    Write,
    Delete
}

/// <summary>A deployed file a plan kept on trust, with the raw target token that produced it.</summary>
public sealed record SquadDegradedFile(string RelativePath, string Target);

/// <summary>
///     One planned file change without its payload, for command hosts that report what a plan
///     will do. An uninstall plan's receipt holds only the files it retains, so the removals it
///     plans are readable from these changes alone.
/// </summary>
public sealed record SquadPlannedFileChange(
    string RelativePath,
    string Target,
    SquadFileMutationKind Kind);

/// <summary>
///     A pending write or delete for one deployed file, identified by <c>(Target, RelativePath)</c>
///     so that Global scope's per-target physical roots (<see cref="SquadDeploymentPlan.ResolvePhysicalRoot(string)" />
///     can be resolved without cross-referencing the receipt by relative path alone.
/// </summary>
public sealed record SquadFileMutation(
    string RelativePath,
    string Target,
    SquadFileMutationKind Kind,
    ReadOnlyMemory<byte>? Content)
{
    public static SquadFileMutation Write(
        string relativePath,
        string target,
        ReadOnlyMemory<byte> content)
    {
        return new SquadFileMutation(relativePath, target, SquadFileMutationKind.Write, content);
    }

    public static SquadFileMutation Delete(string relativePath, string target)
    {
        return new SquadFileMutation(relativePath, target, SquadFileMutationKind.Delete, null);
    }
}

public enum SquadStateMutation
{
    Keep,
    Write,
    Delete
}

public enum SquadFilePreconditionKind
{
    Missing,
    ExactFile
}

public sealed record SquadFilePrecondition(
    string RelativePath,
    string Target,
    SquadFilePreconditionKind Kind,
    string? Sha256)
{
    public static SquadFilePrecondition Missing(string relativePath, string target)
    {
        return new SquadFilePrecondition(relativePath, target, SquadFilePreconditionKind.Missing, null);
    }

    public static SquadFilePrecondition Exact(string relativePath, string target, string sha256)
    {
        return new SquadFilePrecondition(relativePath, target, SquadFilePreconditionKind.ExactFile, sha256);
    }
}
