using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>A preflighted set of file and state mutations for one Squad lifecycle operation.</summary>
public sealed class SquadDeploymentPlan
{
    private SquadDeploymentPlan(
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
        IReadOnlyList<SquadOwnedBlockDrift>? blockDrifts = null)
    {
        TargetRoot = targetRoot;
        PhysicalRootPath = physicalRootIdentity.PhysicalPath;
        PhysicalRootKey = physicalRootIdentity.Key;
        Scope = scope;
        Lock = squadLock;
        Receipt = receipt;
        FileMutations = fileMutations;
        PlannedFileChanges = [.. fileMutations.Select(mutation =>
            new SquadPlannedFileChange(mutation.RelativePath, mutation.Target, mutation.Kind))];
        FilePreconditions = filePreconditions;
        LockMutation = lockMutation;
        ReceiptMutation = receiptMutation;
        _globalRoots = globalRoots;
        _isSingleRootLayout = isSingleRootLayout;
        BlockDrifts = blockDrifts ?? [];
    }

    /// <summary>The absolute root into which harness-native files are deployed.</summary>
    public string TargetRoot { get; }

    internal string PhysicalRootPath { get; }

    internal string PhysicalRootKey { get; }

    /// <summary>The state scope for this deployment.</summary>
    public SquadDeploymentScope Scope { get; }

    /// <summary>The desired lock, or <see langword="null"/> for an uninstall.</summary>
    public SquadLock? Lock { get; }

    /// <summary>The desired or retained ownership receipt.</summary>
    public SquadReceipt Receipt { get; }

    /// <summary>
    /// The pending file mutations, in apply order. Internal payload: hosts report
    /// <see cref="PlannedFileChanges"/> instead.
    /// </summary>
    internal IReadOnlyList<SquadFileMutation> FileMutations { get; }

    /// <summary>
    /// The pending file changes, in apply order, without payloads. Public so command hosts
    /// can report what a plan will change — an uninstall plan's <see cref="Receipt"/> holds
    /// only the files it retains, so the planned removals are readable from this list alone.
    /// </summary>
    public IReadOnlyList<SquadPlannedFileChange> PlannedFileChanges { get; }

    internal IReadOnlyList<SquadFilePrecondition> FilePreconditions { get; }

    /// <summary>
    /// The owned block entries whose current file content no longer matches the receipt at
    /// plan time. Update preserves these entries — rewriting only the blocks without drift —
    /// unless the caller passes <c>replaceManaged</c>, so the operator can see what was
    /// kept and why.
    /// </summary>
    public IReadOnlyList<SquadOwnedBlockDrift> BlockDrifts { get; }

    internal SquadStateMutation LockMutation { get; }

    internal SquadStateMutation ReceiptMutation { get; }

    private readonly ISquadGlobalRootResolver? _globalRoots;
    private readonly bool _isSingleRootLayout;

    /// <summary>
    /// Resolves the absolute physical path where <c>file.RelativePath</c> will be written,
    /// based on the deployment scope: for <see cref="SquadDeploymentScope.Project"/> that is
    /// <c>PhysicalRootPath</c> (the project root) combined with the relative path unchanged;
    /// for <see cref="SquadDeploymentScope.Global"/> it is the resolver's root for
    /// <c>file.Target</c> combined with the relative path, unless this plan carries a legacy
    /// single-root receipt, in which case it is <c>PhysicalRootPath</c> regardless.
    /// </summary>
    internal string ResolvePhysicalPath(SquadOwnedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return ResolvePhysicalPath(file.Target, file.RelativePath);
    }

    /// <summary>
    /// Resolves the absolute physical path for a bare target token and relative path, for
    /// callers such as <see cref="SquadTransaction"/> whose in-flight mutations carry a target
    /// token rather than a full <see cref="SquadOwnedFile"/>.
    /// </summary>
    internal string ResolvePhysicalPath(string target, string relativePath) =>
        SquadPathPolicy.ResolveFile(ResolvePhysicalRoot(target), relativePath);

    /// <summary>Resolves the physical root directory for a bare target token.</summary>
    /// <remarks>
    /// <c>globalRoots</c> is an opt-in, purely-additive parameter (R18/U8): every deployment
    /// that predates it wrote every file under the single provided <c>PhysicalRootPath</c>
    /// regardless of scope, because Global scope by itself only ever changed where lock,
    /// receipt, and journal state lived (<see cref="SquadStateStore"/>), never where deployed
    /// files landed. Falling back to <c>PhysicalRootPath</c> here when no resolver was supplied
    /// keeps that legacy single-root behavior byte-for-byte unchanged. Once a resolver is
    /// supplied, an unmapped target is a real bug: <see cref="SquadGlobalRoots"/> throws for it
    /// directly, and this method never substitutes the project root for a resolver's answer —
    /// except when this plan's receipt classifies as <see cref="SquadReceiptLayout.SingleRoot"/>
    /// (#91: a receipt written before rc.11 recorded every entry beneath the one deployment
    /// root), in which case the recorded root is the correct physical answer regardless of what
    /// a resolver would say.
    /// </remarks>
    internal string ResolvePhysicalRoot(string target) =>
        ResolvePhysicalRoot(Scope, PhysicalRootPath, _globalRoots, target, _isSingleRootLayout);

    private static string ResolvePhysicalRoot(
        SquadDeploymentScope scope,
        string physicalRootPath,
        ISquadGlobalRootResolver? globalRoots,
        string target,
        bool isSingleRootLayout)
    {
        if (globalRoots is null || (scope == SquadDeploymentScope.Global && isSingleRootLayout))
        {
            return physicalRootPath;
        }

        return scope switch
        {
            SquadDeploymentScope.Project => physicalRootPath,
            SquadDeploymentScope.Global => globalRoots.ResolveGlobalRoot(
                SquadTargetCatalog.Parse([target]).Single()),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown deployment scope.")
        };
    }

    private static string ResolvePhysicalPath(
        SquadDeploymentScope scope,
        string physicalRootPath,
        ISquadGlobalRootResolver? globalRoots,
        string target,
        string relativePath,
        bool isSingleRootLayout) =>
        SquadPathPolicy.ResolveFile(
            ResolvePhysicalRoot(scope, physicalRootPath, globalRoots, target, isSingleRootLayout),
            relativePath);

    /// <summary>
    /// Resolves the absolute physical path where a receipt-owned file is deployed, using the
    /// same scope and resolver rules as deploy time. Project scope resolves beneath
    /// <paramref name="targetRoot"/>; Global scope resolves beneath the file's target root from
    /// <paramref name="globalRoots"/>, unless <paramref name="receipt"/> classifies as a legacy
    /// single-root receipt (<see cref="IsLegacySingleRootReceipt"/>) — written before rc.11
    /// (#91) — in which case it resolves beneath <paramref name="targetRoot"/> regardless of
    /// what the resolver would answer, matching where that receipt's files actually live.
    /// Consumers that verify deployed bytes — status reporting in particular — must use this
    /// rather than joining <paramref name="targetRoot"/> with the relative path directly, which
    /// only holds for Project scope.
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
    /// Each target's pre-#91 legacy Global prefix, fixed per target and taken from the
    /// renderer's own prefix constant rather than derived from the target token, because two
    /// targets' verified prefixes diverge from their token: Copilot renders beneath
    /// <c>.github/</c>, not <c>.copilot/</c> (<see cref="CopilotRenderer"/>), and Antigravity
    /// beneath <c>.agents/</c>, not <c>.antigravity/</c> (<see cref="AntigravityRenderer"/>).
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
            ["devin"] = ".devin/"
        };

    /// <summary>
    /// Classifies a Global receipt's owned files as legacy single-root or modern
    /// per-target-roots, purely from their recorded paths — never touching disk (Q3b was
    /// rejected precisely because a filesystem-dependent check could reclassify the same
    /// receipt differently over time).
    /// </summary>
    /// <remarks>
    /// Global receipts written before #91 (rc.9/rc.10) rendered exactly as Project scope did, so
    /// every owned path still carried its harness's dot-prefixed project folder. Since #91 every
    /// renderer strips that prefix under Global scope — guarded by
    /// <c>RenderReceiptLayout_GlobalRender_ContainsNoPrefixedPaths</c> — and writes a bare path
    /// instead, so whether a file starts with its own target's <see cref="LegacyGlobalPrefixes"/>
    /// entry is the fingerprint of a receipt #91 predates (A5): every entry prefixed classifies
    /// as <see cref="SquadReceiptLayout.SingleRoot"/>, no entry prefixed classifies as
    /// <see cref="SquadReceiptLayout.PerTargetRoots"/>, and a mix of the two is refused outright
    /// rather than guessed at.
    /// </remarks>
    internal static SquadReceiptLayout ClassifyGlobalLayout(IReadOnlyList<SquadOwnedFile> files)
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
        {
            throw new InvalidDataException(
                "Squad receipt mixes legacy prefixed paths with modern bare paths across its " +
                "files, so its layout cannot be classified. Recover it by hand: verify or " +
                "remove each file, then delete the receipt and lock so a fresh install can " +
                "recreate them.");
        }

        return anyPrefixed ? SquadReceiptLayout.SingleRoot : SquadReceiptLayout.PerTargetRoots;
    }

    /// <summary>
    /// Whether <paramref name="receipt"/> is a Global receipt written before #91 (rc.9/rc.10),
    /// whose owned files record each target's project-scope dot-prefixed path beneath the one
    /// recorded deployment root rather than beneath that target's own global root.
    /// </summary>
    public static bool IsLegacySingleRootReceipt(SquadReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return receipt.Scope == SquadDeploymentScope.Global &&
            ClassifyGlobalLayout(receipt.Files) == SquadReceiptLayout.SingleRoot;
    }

    /// <summary>
    /// The conflict a legacy single-root receipt raises before any download or file mutation,
    /// naming the two commands (in order) that move a deployment to the current layout.
    /// </summary>
    internal static SquadDeploymentConflictException LegacySingleRootLayoutConflict() =>
        new(
            "This Global Kyber-Squad receipt uses the legacy single-root layout written before " +
            "rc.11 (kyber-weave/kyber-weave#91) and cannot be updated in place. Run " +
            "'squad uninstall --global' to remove it, then 'squad install --global' to " +
            "reinstall with the current per-target layout.");

    /// <summary>Preflights a new installation without changing the deployment tree.</summary>
    public static SquadDeploymentPlan CreateInstall(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadLock squadLock,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        IReadOnlyList<SquadDegradation> degradations,
        bool adopt,
        TimeProvider timeProvider,
        ISquadGlobalRootResolver? globalRoots = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
        IReadOnlyList<SquadRenderedBlock>? blocks = null)
    {
        ValidateCommon(targetRoot, squadLock, renderedFiles, degradations, timeProvider);
        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(targetRoot);
        string root = identity.PhysicalPath;
        IReadOnlyList<NormalizedDeploymentFile> normalizedFiles = NormalizeRenderedFiles(
            root,
            scope,
            globalRoots,
            renderedFiles);
        List<SquadFileMutation> mutations = new List<SquadFileMutation>();
        List<SquadFilePrecondition> preconditions = new List<SquadFilePrecondition>();
        List<SquadOwnedFile> ownedFiles = new List<SquadOwnedFile>();

        foreach (NormalizedDeploymentFile rendered in normalizedFiles)
        {
            if (IsOwnedBySiblingReceipt(siblingGlobalReceipts, rendered.File.Target, rendered.File.RelativePath))
                throw SiblingGlobalOwnership(rendered.File.RelativePath, rendered.File.Target);

            bool existsAsFile = File.Exists(rendered.FullPath);
            if (existsAsFile)
            {
                string currentDigest = Digest(File.ReadAllBytes(rendered.FullPath));
                string renderedDigest = Digest(rendered.File.Content.Span);
                if (!adopt || !string.Equals(currentDigest, renderedDigest, StringComparison.Ordinal))
                    throw UnmanagedCollision(rendered.File.RelativePath);

                preconditions.Add(SquadFilePrecondition.Exact(
                    rendered.File.RelativePath,
                    rendered.File.Target,
                    currentDigest));
                ownedFiles.Add(new SquadOwnedFile(
                    rendered.File.RelativePath,
                    renderedDigest,
                    rendered.File.Target,
                    true));
                continue;
            }

            if (Directory.Exists(rendered.FullPath))
                throw UnmanagedCollision(rendered.File.RelativePath);

            preconditions.Add(SquadFilePrecondition.Missing(
                rendered.File.RelativePath,
                rendered.File.Target));
            mutations.Add(SquadFileMutation.Write(
                rendered.File.RelativePath,
                rendered.File.Target,
                rendered.File.Content));
            ownedFiles.Add(new SquadOwnedFile(
                rendered.File.RelativePath,
                Digest(rendered.File.Content.Span),
                rendered.File.Target,
                false));
        }

        List<SquadOwnedBlock> ownedBlocks = PlanBlockInstalls(
            root,
            scope,
            globalRoots,
            normalizedFiles,
            blocks,
            mutations,
            preconditions);

        SquadReceipt receipt = NewReceipt(scope, timeProvider, degradations, ownedFiles, ownedBlocks);
        return new SquadDeploymentPlan(
            Path.GetFullPath(targetRoot),
            identity,
            scope,
            squadLock,
            receipt,
            mutations,
            preconditions,
            SquadStateMutation.Write,
            SquadStateMutation.Write,
            globalRoots,
            isSingleRootLayout: false);
    }

    /// <summary>Preflights an update while preserving locally edited receipt-owned files by default.</summary>
    public static SquadDeploymentPlan CreateUpdate(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadLock squadLock,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        SquadReceipt previousReceipt,
        IReadOnlyList<SquadDegradation> degradations,
        bool replaceManaged,
        TimeProvider timeProvider,
        ISquadGlobalRootResolver? globalRoots = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
        IReadOnlyList<SquadRenderedBlock>? blocks = null)
    {
        ValidateCommon(targetRoot, squadLock, renderedFiles, degradations, timeProvider);
        ArgumentNullException.ThrowIfNull(previousReceipt);
        EnsureReceiptScope(previousReceipt, scope);

        // The double-prefix bug #91 describes can only occur once a per-target resolver is in
        // play: with none, legacy single-root and modern per-target-roots resolution already
        // agree (both fall back to the plan's own physical root), so there is nothing to
        // refuse against.
        if (globalRoots is not null && IsLegacySingleRootReceipt(previousReceipt))
        {
            throw LegacySingleRootLayoutConflict();
        }

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(targetRoot);
        string root = identity.PhysicalPath;
        IReadOnlyList<NormalizedDeploymentFile> normalizedFiles = NormalizeRenderedFiles(
            root,
            scope,
            globalRoots,
            renderedFiles);
        Dictionary<string, SquadOwnedFile> previousByPath = ReceiptFilesByPath(root, previousReceipt);
        List<SquadFileMutation> mutations = new List<SquadFileMutation>();
        List<SquadFilePrecondition> preconditions = new List<SquadFilePrecondition>();
        List<SquadOwnedFile> nextOwnedFiles = new List<SquadOwnedFile>();
        HashSet<string> renderedIdentities = new HashSet<string>(StringComparer.Ordinal);

        foreach (NormalizedDeploymentFile rendered in normalizedFiles)
        {
            string relativePath = rendered.File.RelativePath;
            string fileIdentity = DeployedFileIdentity(rendered.File.Target, relativePath);
            renderedIdentities.Add(fileIdentity);
            string renderedDigest = Digest(rendered.File.Content.Span);
            if (!previousByPath.TryGetValue(fileIdentity, out SquadOwnedFile? previous))
            {
                if (IsOwnedBySiblingReceipt(siblingGlobalReceipts, rendered.File.Target, relativePath))
                    throw SiblingGlobalOwnership(relativePath, rendered.File.Target);

                if (File.Exists(rendered.FullPath) || Directory.Exists(rendered.FullPath))
                    throw UnmanagedCollision(relativePath);

                preconditions.Add(SquadFilePrecondition.Missing(relativePath, rendered.File.Target));
                mutations.Add(SquadFileMutation.Write(relativePath, rendered.File.Target, rendered.File.Content));
                nextOwnedFiles.Add(new SquadOwnedFile(
                    relativePath,
                    renderedDigest,
                    rendered.File.Target,
                    false));
                continue;
            }

            if (Directory.Exists(rendered.FullPath))
            {
                throw new SquadDeploymentConflictException(
                    $"Receipt-owned path '{relativePath}' is now a directory. " +
                    "Move it aside before updating Squad.");
            }

            if (!File.Exists(rendered.FullPath))
            {
                preconditions.Add(SquadFilePrecondition.Missing(relativePath, rendered.File.Target));
                mutations.Add(SquadFileMutation.Write(relativePath, rendered.File.Target, rendered.File.Content));
                nextOwnedFiles.Add(new SquadOwnedFile(
                    relativePath,
                    renderedDigest,
                    rendered.File.Target,
                    false));
                continue;
            }

            string currentDigest = Digest(File.ReadAllBytes(rendered.FullPath));
            bool isLocallyEdited = !string.Equals(
                currentDigest,
                previous.Sha256,
                StringComparison.Ordinal);
            if (isLocallyEdited && !replaceManaged)
            {
                nextOwnedFiles.Add(previous);
                continue;
            }

            preconditions.Add(SquadFilePrecondition.Exact(relativePath, rendered.File.Target, currentDigest));
            bool fileWillBeWritten = !string.Equals(
                currentDigest,
                renderedDigest,
                StringComparison.Ordinal);
            if (fileWillBeWritten)
                mutations.Add(SquadFileMutation.Write(relativePath, rendered.File.Target, rendered.File.Content));

            nextOwnedFiles.Add(new SquadOwnedFile(
                relativePath,
                renderedDigest,
                rendered.File.Target,
                previous.Adopted && !fileWillBeWritten));
        }

        List<SquadOwnedBlockDrift> blockDrifts = [];
        (List<SquadOwnedBlock> nextBlocks, HashSet<string> renderedBlockIdentities) = PlanBlockUpdates(
            root,
            scope,
            globalRoots,
            normalizedFiles,
            previousReceipt,
            blocks,
            replaceManaged,
            mutations,
            preconditions,
            blockDrifts);

        foreach (SquadOwnedFile previous in previousReceipt.Files)
        {
            if (renderedIdentities.Contains(DeployedFileIdentity(previous.Target, previous.RelativePath)))
                continue;

            if (IsOwnedBySiblingReceipt(siblingGlobalReceipts, previous.Target, previous.RelativePath))
                continue;

            // The legacy-receipt refusal above already guarantees previousReceipt is not
            // single-root by the time this loop runs, so every remaining entry resolves through
            // the modern per-target path.
            string fullPath = ResolvePhysicalPath(
                scope, root, globalRoots, previous.Target, previous.RelativePath, isSingleRootLayout: false);

            if (Directory.Exists(fullPath))
            {
                nextOwnedFiles.Add(previous);
                continue;
            }

            if (!File.Exists(fullPath))
                continue;

            string currentDigest = Digest(File.ReadAllBytes(fullPath));
            if (string.Equals(currentDigest, previous.Sha256, StringComparison.Ordinal))
            {
                preconditions.Add(SquadFilePrecondition.Exact(
                    previous.RelativePath,
                    previous.Target,
                    currentDigest));
                mutations.Add(SquadFileMutation.Delete(previous.RelativePath, previous.Target));
            }
            else
                nextOwnedFiles.Add(previous);
        }

        RetireMissingBlocks(
            root,
            scope,
            globalRoots,
            previousReceipt,
            renderedBlockIdentities,
            nextBlocks,
            mutations,
            preconditions);

        // Classified from what this update actually produces, not merely inherited from
        // previousReceipt (already confirmed non-single-root above whenever a resolver is
        // supplied) — so an update run with no resolver, where retained and freshly rendered
        // entries can legitimately differ in shape, never stamps a layout that disagrees with
        // the files it is about to write (#91: a mismatched label persisted here is exactly
        // what a later run that does supply a resolver would wrongly trust).
        SquadReceiptLayout nextLayout = SquadReceiptLayout.PerTargetRoots;
        if (scope == SquadDeploymentScope.Global)
        {
            try
            {
                nextLayout = ClassifyGlobalLayout(nextOwnedFiles);
            }
            catch (InvalidDataException)
            {
                throw LegacySingleRootLayoutConflict();
            }
        }

        SquadReceipt receipt = NewReceipt(scope, timeProvider, degradations, nextOwnedFiles, nextBlocks) with
        {
            Layout = nextLayout
        };
        return new SquadDeploymentPlan(
            Path.GetFullPath(targetRoot),
            identity,
            scope,
            squadLock,
            receipt,
            mutations,
            preconditions,
            SquadStateMutation.Write,
            SquadStateMutation.Write,
            globalRoots,
            isSingleRootLayout: nextLayout == SquadReceiptLayout.SingleRoot,
            blockDrifts: blockDrifts);
    }

    /// <summary>Preflights an ownership-aware uninstall without changing the deployment tree.</summary>
    public static SquadDeploymentPlan CreateUninstall(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadReceipt receipt,
        ISquadGlobalRootResolver? globalRoots = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentNullException.ThrowIfNull(receipt);
        EnsureReceiptScope(receipt, scope);

        // Resolved once, from the receipt being uninstalled, and used both to walk its files
        // below and to carry on the plan instance so SquadTransaction resolves the same way at
        // execute time (#91): a legacy single-root receipt's files live beneath the recorded
        // root regardless of what a per-target resolver would answer.
        bool isSingleRootLayout = IsLegacySingleRootReceipt(receipt);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(targetRoot);
        string root = identity.PhysicalPath;
        _ = ReceiptFilesByPath(root, receipt);
        RequireProjectScopeForBlocks(scope, receipt.Blocks.Count > 0, "uninstall");
        List<SquadFileMutation> mutations = new List<SquadFileMutation>();
        List<SquadFilePrecondition> preconditions = new List<SquadFilePrecondition>();
        List<SquadOwnedFile> retained = new List<SquadOwnedFile>();
        foreach (SquadOwnedFile owned in receipt.Files)
        {
            if (IsOwnedBySiblingReceipt(siblingGlobalReceipts, owned.Target, owned.RelativePath))
                continue;

            string fullPath = ResolvePhysicalPath(
                scope, root, globalRoots, owned.Target, owned.RelativePath, isSingleRootLayout);

            if (Directory.Exists(fullPath))
            {
                retained.Add(owned);
                continue;
            }

            if (!File.Exists(fullPath))
                continue;

            string currentDigest = Digest(File.ReadAllBytes(fullPath));
            if (string.Equals(currentDigest, owned.Sha256, StringComparison.Ordinal))
            {
                preconditions.Add(SquadFilePrecondition.Exact(
                    owned.RelativePath,
                    owned.Target,
                    currentDigest));
                mutations.Add(SquadFileMutation.Delete(owned.RelativePath, owned.Target));
            }
            else
                retained.Add(owned);
        }

        List<SquadOwnedBlock> retainedBlocks = PlanBlockRemovals(
            root,
            scope,
            globalRoots,
            receipt,
            mutations,
            preconditions);

        // The layout carries forward onto the retained receipt so a later re-serialize (when
        // any file was locally edited and survives the uninstall) still writes the correct
        // explicit `layout` — reclassified from what actually remains, not merely copied.
        SquadReceiptLayout retainedLayout = scope == SquadDeploymentScope.Global
            ? ClassifyGlobalLayout(retained)
            : receipt.Layout;
        // The schema follows the same rule as the writer: a project receipt that still owns
        // a block stays on v3, while one whose blocks are all gone drops back to v1 so it
        // serializes byte-identical to every block-less receipt. Global receipts never own
        // blocks, so their schema carries over untouched.
        string retainedSchema = scope == SquadDeploymentScope.Global
            ? receipt.Schema
            : retainedBlocks.Count > 0 ? SquadStateStore.ReceiptSchemaV3 : SquadStateStore.ReceiptSchemaV1;
        SquadReceipt retainedReceipt = receipt with
        {
            Files = retained,
            Blocks = retainedBlocks,
            Layout = retainedLayout,
            Schema = retainedSchema
        };
        bool hasRetainedFiles = retained.Count > 0 || retainedBlocks.Count > 0;
        return new SquadDeploymentPlan(
            Path.GetFullPath(targetRoot),
            identity,
            scope,
            null,
            retainedReceipt,
            mutations,
            preconditions,
            hasRetainedFiles ? SquadStateMutation.Keep : SquadStateMutation.Delete,
            hasRetainedFiles ? SquadStateMutation.Write : SquadStateMutation.Delete,
            globalRoots,
            isSingleRootLayout);
    }

    private static void ValidateCommon(
        string targetRoot,
        SquadLock squadLock,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        IReadOnlyList<SquadDegradation> degradations,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentNullException.ThrowIfNull(squadLock);
        ArgumentNullException.ThrowIfNull(renderedFiles);
        ArgumentNullException.ThrowIfNull(degradations);
        ArgumentNullException.ThrowIfNull(timeProvider);
    }

    /// <summary>
    /// Lists every rendered path that already exists at its resolved physical location
    /// with bytes that do not match the render. <c>squad doctor --global</c> surfaces
    /// the whole set as warnings; <see cref="CreateInstall"/> still throws on the first
    /// via the existing unmanaged-collision rule.
    /// </summary>
    public static IReadOnlyList<SquadUnmanagedPathCollision> CollectUnmanagedCollisions(
        string targetRoot,
        SquadDeploymentScope scope,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        ISquadGlobalRootResolver? globalRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentNullException.ThrowIfNull(renderedFiles);

        string root = SquadPhysicalRootIdentity.Resolve(targetRoot).PhysicalPath;
        IReadOnlyList<NormalizedDeploymentFile> normalizedFiles = NormalizeRenderedFiles(
            root,
            scope,
            globalRoots,
            renderedFiles);
        List<SquadUnmanagedPathCollision> collisions = [];
        foreach (NormalizedDeploymentFile rendered in normalizedFiles)
        {
            if (Directory.Exists(rendered.FullPath))
            {
                collisions.Add(ToCollision(rendered));
                continue;
            }

            if (!File.Exists(rendered.FullPath))
            {
                continue;
            }

            string currentDigest = Digest(File.ReadAllBytes(rendered.FullPath));
            string renderedDigest = Digest(rendered.File.Content.Span);
            if (!string.Equals(currentDigest, renderedDigest, StringComparison.Ordinal))
            {
                collisions.Add(ToCollision(rendered));
            }
        }

        return collisions;
    }

    private static SquadUnmanagedPathCollision ToCollision(NormalizedDeploymentFile rendered) =>
        new(
            rendered.File.RelativePath,
            rendered.FullPath,
            rendered.File.Target,
            IdentityFromRelativePath(rendered.File.RelativePath));

    internal static string IdentityFromRelativePath(string relativePath)
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

        if (fileName.EndsWith(".agent.md", StringComparison.Ordinal))
        {
            return fileName[..^".agent.md".Length];
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static IReadOnlyList<NormalizedDeploymentFile> NormalizeRenderedFiles(
        string root,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? globalRoots,
        IReadOnlyList<SquadDeploymentFile> renderedFiles)
    {
        List<NormalizedDeploymentFile> normalized = new List<NormalizedDeploymentFile>(renderedFiles.Count);
        Dictionary<string, string> seenIdentities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> seenFullPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (SquadDeploymentFile rendered in renderedFiles)
        {
            ArgumentNullException.ThrowIfNull(rendered);
            string relativePath = SquadPathPolicy.NormalizeRelativePath(rendered.RelativePath);
            string portableIdentity = SquadPathPolicy.GetPortableIdentity(relativePath);
            if (!string.Equals(portableIdentity, relativePath, StringComparison.Ordinal))
            {
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad output path '{rendered.RelativePath}' is not a portable " +
                    "canonical path because a segment ends in a dot or space.");
            }

            string fileIdentity = DeployedFileIdentity(rendered.Target, portableIdentity);
            if (seenIdentities.TryGetValue(fileIdentity, out _))
            {
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad output path '{rendered.RelativePath}' has a portable " +
                    "alias collision. Fix the upstream render before deploying it.");
            }

            seenIdentities.Add(fileIdentity, relativePath);
            SquadDeploymentFile normalizedFile = rendered with { RelativePath = relativePath };
            // A freshly rendered file is never legacy: every renderer has stripped its Global
            // prefix since #91, so this always resolves through the modern per-target path.
            string fullPath = ResolvePhysicalPath(
                scope, root, globalRoots, rendered.Target, relativePath, isSingleRootLayout: false);
            if (seenFullPaths.TryGetValue(fullPath, out string? existingPath))
            {
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad output path '{rendered.RelativePath}' resolves to the same " +
                    $"physical file as '{existingPath}'. Fix the upstream render before deploying it.");
            }

            seenFullPaths.Add(fullPath, relativePath);
            normalized.Add(new NormalizedDeploymentFile(normalizedFile, fullPath));
        }

        return normalized;
    }

    private static Dictionary<string, SquadOwnedFile> ReceiptFilesByPath(
        string root,
        SquadReceipt receipt)
    {
        Dictionary<string, SquadOwnedFile> files = new Dictionary<string, SquadOwnedFile>(StringComparer.Ordinal);
        HashSet<string> portableIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SquadOwnedFile owned in receipt.Files)
        {
            ArgumentNullException.ThrowIfNull(owned);
            string normalizedPath = SquadPathPolicy.NormalizeRelativePath(owned.RelativePath);
            _ = SquadPathPolicy.ResolveFile(root, normalizedPath);
            if (!string.Equals(normalizedPath, owned.RelativePath, StringComparison.Ordinal))
            {
                throw new SquadPathContainmentException(
                    $"Receipt path '{owned.RelativePath}' is not a normalized portable path.");
            }

            string portableIdentity = SquadPathPolicy.GetPortableIdentity(normalizedPath);
            if (!string.Equals(portableIdentity, normalizedPath, StringComparison.Ordinal))
            {
                throw new SquadDeploymentConflictException(
                    $"Squad receipt path '{owned.RelativePath}' is not a portable canonical path.");
            }

            string deployedIdentity = DeployedFileIdentity(owned.Target, portableIdentity);
            if (!portableIdentities.Add(deployedIdentity))
            {
                throw new SquadDeploymentConflictException(
                    $"Squad receipt path '{owned.RelativePath}' has a portable alias collision.");
            }

            if (!files.TryAdd(DeployedFileIdentity(owned.Target, normalizedPath), owned))
            {
                throw new SquadDeploymentConflictException(
                    $"Squad receipt contains duplicate path '{normalizedPath}' for target '{owned.Target}'.");
            }
        }

        return files;
    }

    private static SquadReceipt NewReceipt(
        SquadDeploymentScope scope,
        TimeProvider timeProvider,
        IReadOnlyList<SquadDegradation> degradations,
        IReadOnlyList<SquadOwnedFile> ownedFiles,
        IReadOnlyList<SquadOwnedBlock>? ownedBlocks = null)
    {
        IReadOnlyList<SquadOwnedBlock> blocks = ownedBlocks ?? [];
        // The in-memory schema follows the writer: a project receipt that owns a block reads
        // v3, everything else keeps today's value (v1 — Global upgrades to v2 at write time).
        string schema = blocks.Count > 0 && scope == SquadDeploymentScope.Project
            ? SquadStateStore.ReceiptSchemaV3
            : SquadStateStore.ReceiptSchemaV1;
        return new(
            schema,
            scope,
            ".",
            timeProvider.GetUtcNow(),
            degradations.ToArray(),
            ownedFiles.ToArray())
        {
            Blocks = blocks.ToArray()
        };
    }

    private static void EnsureReceiptScope(
        SquadReceipt receipt,
        SquadDeploymentScope requestedScope)
    {
        if (receipt.Scope != requestedScope)
        {
            throw new SquadDeploymentConflictException(
                $"The Squad receipt has scope '{receipt.Scope}' but the operation requested " +
                $"'{requestedScope}'. Use the receipt's original scope.");
        }
    }

    /// <summary>
    /// Blocks are a project-scope ownership kind: the v3 receipt that records them exists for
    /// project scope only. A block arriving with any other scope is a renderer contract
    /// violation, refused loudly rather than silently dropped.
    /// </summary>
    private static void RequireProjectScopeForBlocks(
        SquadDeploymentScope scope,
        bool hasBlocks,
        string operation)
    {
        if (scope != SquadDeploymentScope.Project && hasBlocks)
        {
            throw new SquadDeploymentConflictException(
                $"Squad hook blocks cannot be {operation} under '{scope}' scope: " +
                $"'{SquadStateStore.ReceiptSchemaV3}' receipts are written for project scope only.");
        }
    }

    /// <summary>
    /// Plans the install-time splice for every rendered block: the spliced file becomes a
    /// <see cref="SquadFileMutation.Write"/> with an <see cref="SquadFilePrecondition.Exact"/>
    /// precondition on the current digest (or <c>Missing</c> when the file is absent), and the
    /// receipt claims only the spliced entries — never the whole file.
    /// </summary>
    /// <remarks>
    /// An existing user file at a block path is content to merge with, never an
    /// <c>UnmanagedCollision</c>: blocks own entries, not files, so there is nothing to
    /// collide with. <see cref="SquadTransaction"/>'s claim-and-publish protocol is
    /// unchanged because a block rides it as an ordinary write with a precondition.
    /// </remarks>
    private static List<SquadOwnedBlock> PlanBlockInstalls(
        string root,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? globalRoots,
        IReadOnlyList<NormalizedDeploymentFile> normalizedFiles,
        IReadOnlyList<SquadRenderedBlock>? blocks,
        List<SquadFileMutation> mutations,
        List<SquadFilePrecondition> preconditions)
    {
        if (blocks is null || blocks.Count == 0)
            return [];
        RequireProjectScopeForBlocks(scope, hasBlocks: true, operation: "installed");

        HashSet<string> seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (NormalizedDeploymentFile normalized in normalizedFiles)
            seenIdentities.Add(DeployedFileIdentity(normalized.File.Target, normalized.File.RelativePath));

        List<SquadOwnedBlock> ownedBlocks = new List<SquadOwnedBlock>(blocks.Count);
        foreach (SquadRenderedBlock block in blocks)
        {
            ArgumentNullException.ThrowIfNull(block);
            string relativePath = NormalizeBlockPath(block);
            string identity = DeployedFileIdentity(block.Target, relativePath);
            if (!seenIdentities.Add(identity))
            {
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad block '{block.RelativePath}' has a portable " +
                    "alias collision. Fix the upstream render before deploying it.");
            }

            string fullPath = ResolvePhysicalPath(
                scope, root, globalRoots, block.Target, relativePath, isSingleRootLayout: false);
            (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = SplitBlockEntries(block);

            bool existsAsFile = File.Exists(fullPath);
            string spliced;
            IReadOnlyList<SquadHookOwnedEntry> owned;
            if (existsAsFile)
            {
                byte[] currentBytes = File.ReadAllBytes(fullPath);
                string currentText = DecodeBlockText(relativePath, currentBytes);
                (spliced, owned) = SpliceBlockContent(block.Format, relativePath, currentText, pre, post);
                preconditions.Add(SquadFilePrecondition.Exact(
                    relativePath,
                    block.Target,
                    Digest(currentBytes)));
                if (!string.Equals(spliced, currentText, StringComparison.Ordinal))
                    mutations.Add(SquadFileMutation.Write(relativePath, block.Target, Encoding.UTF8.GetBytes(spliced)));
            }
            else
            {
                if (Directory.Exists(fullPath))
                    throw UnmanagedCollision(relativePath);

                (spliced, owned) = SpliceBlockContent(block.Format, relativePath, null, pre, post);
                preconditions.Add(SquadFilePrecondition.Missing(relativePath, block.Target));
                mutations.Add(SquadFileMutation.Write(relativePath, block.Target, Encoding.UTF8.GetBytes(spliced)));
            }

            ownedBlocks.Add(new SquadOwnedBlock(
                relativePath,
                block.Target,
                !existsAsFile,
                [.. owned.Select(entry => new SquadOwnedBlockEntry(entry.Location, entry.Digest))]));
        }

        return ownedBlocks;
    }

    /// <summary>
    /// Plans the update-time rewrite for every rendered block. A block whose owned entries
    /// drifted is left untouched and reported on <see cref="BlockDrifts"/> unless
    /// <paramref name="replaceManaged"/> is set, in which case the drift is overwritten like
    /// any other managed byte.
    /// </summary>
    private static (List<SquadOwnedBlock> Next, HashSet<string> Rendered) PlanBlockUpdates(
        string root,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? globalRoots,
        IReadOnlyList<NormalizedDeploymentFile> normalizedFiles,
        SquadReceipt previousReceipt,
        IReadOnlyList<SquadRenderedBlock>? blocks,
        bool replaceManaged,
        List<SquadFileMutation> mutations,
        List<SquadFilePrecondition> preconditions,
        List<SquadOwnedBlockDrift> blockDrifts)
    {
        if ((blocks is null || blocks.Count == 0) && previousReceipt.Blocks.Count == 0)
            return ([], []);
        RequireProjectScopeForBlocks(scope, blocks is not null && blocks.Count > 0, "updated");
        RequireProjectScopeForBlocks(scope, previousReceipt.Blocks.Count > 0, "updated");

        HashSet<string> seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        foreach (NormalizedDeploymentFile normalized in normalizedFiles)
            seenIdentities.Add(DeployedFileIdentity(normalized.File.Target, normalized.File.RelativePath));

        Dictionary<string, SquadOwnedBlock> previousByIdentity = new(StringComparer.Ordinal);
        foreach (SquadOwnedBlock previous in previousReceipt.Blocks)
        {
            ArgumentNullException.ThrowIfNull(previous);
            previousByIdentity[DeployedFileIdentity(previous.Target, previous.RelativePath)] = previous;
        }

        List<SquadOwnedBlock> nextBlocks = [];
        HashSet<string> renderedIdentities = new(StringComparer.Ordinal);
        foreach (SquadRenderedBlock block in blocks ?? [])
        {
            ArgumentNullException.ThrowIfNull(block);
            string relativePath = NormalizeBlockPath(block);
            string identity = DeployedFileIdentity(block.Target, relativePath);
            if (!seenIdentities.Add(identity))
            {
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad block '{block.RelativePath}' has a portable " +
                    "alias collision. Fix the upstream render before deploying it.");
            }

            renderedIdentities.Add(identity);
            string fullPath = ResolvePhysicalPath(
                scope, root, globalRoots, block.Target, relativePath, isSingleRootLayout: false);
            (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post) = SplitBlockEntries(block);

            if (!previousByIdentity.TryGetValue(identity, out SquadOwnedBlock? previous))
            {
                if (Directory.Exists(fullPath))
                    throw UnmanagedCollision(relativePath);

                if (File.Exists(fullPath))
                {
                    byte[] currentBytes = File.ReadAllBytes(fullPath);
                    string currentText = DecodeBlockText(relativePath, currentBytes);
                    (string spliced, IReadOnlyList<SquadHookOwnedEntry> owned) = SpliceBlockContent(
                        block.Format, relativePath, currentText, pre, post);
                    preconditions.Add(SquadFilePrecondition.Exact(
                        relativePath,
                        block.Target,
                        Digest(currentBytes)));
                    if (!string.Equals(spliced, currentText, StringComparison.Ordinal))
                        mutations.Add(SquadFileMutation.Write(relativePath, block.Target, Encoding.UTF8.GetBytes(spliced)));
                    nextBlocks.Add(new SquadOwnedBlock(
                        relativePath,
                        block.Target,
                        false,
                        [.. owned.Select(entry => new SquadOwnedBlockEntry(entry.Location, entry.Digest))]));
                }
                else
                {
                    (string spliced, IReadOnlyList<SquadHookOwnedEntry> owned) = SpliceBlockContent(
                        block.Format, relativePath, null, pre, post);
                    preconditions.Add(SquadFilePrecondition.Missing(relativePath, block.Target));
                    mutations.Add(SquadFileMutation.Write(relativePath, block.Target, Encoding.UTF8.GetBytes(spliced)));
                    nextBlocks.Add(new SquadOwnedBlock(
                        relativePath,
                        block.Target,
                        true,
                        [.. owned.Select(entry => new SquadOwnedBlockEntry(entry.Location, entry.Digest))]));
                }

                continue;
            }

            if (Directory.Exists(fullPath))
            {
                throw new SquadDeploymentConflictException(
                    $"Receipt-owned block path '{relativePath}' is now a directory. " +
                    "Move it aside before updating Squad.");
            }

            if (!File.Exists(fullPath))
            {
                (string spliced, IReadOnlyList<SquadHookOwnedEntry> owned) = SpliceBlockContent(
                    block.Format, relativePath, null, pre, post);
                preconditions.Add(SquadFilePrecondition.Missing(relativePath, block.Target));
                mutations.Add(SquadFileMutation.Write(relativePath, block.Target, Encoding.UTF8.GetBytes(spliced)));
                nextBlocks.Add(new SquadOwnedBlock(
                    relativePath,
                    block.Target,
                    true,
                    [.. owned.Select(entry => new SquadOwnedBlockEntry(entry.Location, entry.Digest))]));
                continue;
            }

            byte[] current = File.ReadAllBytes(fullPath);
            string currentJson = DecodeBlockText(relativePath, current, allowBlank: false);
            IReadOnlyList<SquadHookDrift> drifts = FindBlockDrift(block, relativePath, currentJson, previous);
            if (drifts.Count > 0 && !replaceManaged)
            {
                foreach (SquadHookDrift drift in drifts)
                    blockDrifts.Add(new SquadOwnedBlockDrift(relativePath, block.Target, drift.Location, drift.Reason));
                nextBlocks.Add(previous);
                continue;
            }

            (string rewritten, IReadOnlyList<SquadHookOwnedEntry> rewrittenOwned) = SpliceBlockContent(
                block.Format, relativePath, currentJson, pre, post);
            preconditions.Add(SquadFilePrecondition.Exact(
                relativePath,
                block.Target,
                Digest(current)));
            if (!string.Equals(rewritten, currentJson, StringComparison.Ordinal))
                mutations.Add(SquadFileMutation.Write(relativePath, block.Target, Encoding.UTF8.GetBytes(rewritten)));
            nextBlocks.Add(new SquadOwnedBlock(
                relativePath,
                block.Target,
                previous.CreatedFile,
                [.. rewrittenOwned.Select(entry => new SquadOwnedBlockEntry(entry.Location, entry.Digest))]));
        }

        return (nextBlocks, renderedIdentities);
    }

    /// <summary>
    /// Retires previously owned blocks the new render no longer carries: only the owned
    /// entries are removed, the user's file otherwise untouched.
    /// </summary>
    private static void RetireMissingBlocks(
        string root,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? globalRoots,
        SquadReceipt previousReceipt,
        HashSet<string> renderedIdentities,
        List<SquadOwnedBlock> nextBlocks,
        List<SquadFileMutation> mutations,
        List<SquadFilePrecondition> preconditions)
    {
        foreach (SquadOwnedBlock previous in previousReceipt.Blocks)
        {
            if (renderedIdentities.Contains(DeployedFileIdentity(previous.Target, previous.RelativePath)))
                continue;

            if (RemoveOwnedBlockEntries(root, scope, globalRoots, previous, mutations, preconditions))
                nextBlocks.Add(previous);
        }
    }

    /// <summary>
    /// Plans the removal of one receipt-owned block's entries from its shared file.
    /// Returns <see langword="true"/> when the block survives in the receipt — the path is
    /// now a directory or the file no longer parses, so hand repair owns it.
    /// </summary>
    /// <remarks>
    /// The file is deleted only when Squad created it and no hook remains: anything else
    /// is the user's file with Squad's entries excised.
    /// </remarks>
    private static List<SquadOwnedBlock> PlanBlockRemovals(
        string root,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? globalRoots,
        SquadReceipt receipt,
        List<SquadFileMutation> mutations,
        List<SquadFilePrecondition> preconditions)
    {
        List<SquadOwnedBlock> retained = [];
        foreach (SquadOwnedBlock owned in receipt.Blocks)
        {
            ArgumentNullException.ThrowIfNull(owned);
            if (RemoveOwnedBlockEntries(root, scope, globalRoots, owned, mutations, preconditions))
                retained.Add(owned);
        }

        return retained;
    }

    private static bool RemoveOwnedBlockEntries(
        string root,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? globalRoots,
        SquadOwnedBlock owned,
        List<SquadFileMutation> mutations,
        List<SquadFilePrecondition> preconditions)
    {
        string fullPath = ResolvePhysicalPath(
            scope, root, globalRoots, owned.Target, owned.RelativePath, isSingleRootLayout: false);

        if (Directory.Exists(fullPath))
            return true;

        if (!File.Exists(fullPath))
            return false;

        byte[] currentBytes = File.ReadAllBytes(fullPath);
        string currentText;
        string trimmed;
        try
        {
            currentText = DecodeBlockText(owned.RelativePath, currentBytes);
            if (string.IsNullOrWhiteSpace(currentText))
                return false;

            trimmed = RemoveBlockContent(FormatForTarget(owned), owned.RelativePath, currentText);
        }
        catch (SquadDeploymentConflictException)
        {
            return true;
        }

        if (string.Equals(trimmed, NormalizeBlockJson(owned.RelativePath, currentText), StringComparison.Ordinal))
            return false;

        preconditions.Add(SquadFilePrecondition.Exact(
            owned.RelativePath,
            owned.Target,
            Digest(currentBytes)));
        if (SquadHookJsonBlock.IsEmpty(FormatForTarget(owned), trimmed) && owned.CreatedFile)
            mutations.Add(SquadFileMutation.Delete(owned.RelativePath, owned.Target));
        else
            mutations.Add(SquadFileMutation.Write(owned.RelativePath, owned.Target, Encoding.UTF8.GetBytes(trimmed)));

        return false;
    }

    /// <summary>
    /// Resolves the hook-file shape for a receipt-owned block from its recorded relative
    /// path, so removal never depends on a render that may no longer exist.
    /// </summary>
    private static SquadHookBlockFormat FormatForTarget(SquadOwnedBlock owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        foreach (SquadHookBlockFormat format in Enum.GetValues<SquadHookBlockFormat>())
        {
            if (string.Equals(
                    SquadHookJsonBlock.RelativePath(format),
                    owned.RelativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        throw new SquadDeploymentConflictException(
            $"Squad receipt block '{owned.RelativePath}' is not a known shared hook file. " +
            "Recover it by hand: verify or remove each entry, then delete the receipt and lock " +
            "so a fresh install can recreate them.");
    }

    private static string NormalizeBlockPath(SquadRenderedBlock block)
    {
        string relativePath = SquadPathPolicy.NormalizeRelativePath(block.RelativePath);
        string portableIdentity = SquadPathPolicy.GetPortableIdentity(relativePath);
        if (!string.Equals(portableIdentity, relativePath, StringComparison.Ordinal))
        {
            throw new SquadDeploymentConflictException(
                $"Rendered Squad block path '{block.RelativePath}' is not a portable " +
                "canonical path because a segment ends in a dot or space.");
        }

        return relativePath;
    }

    /// <summary>
    /// Splits a rendered block's entries into the pre and post hook containers by their
    /// logical container name. Anything else is a renderer bug, refused before it can
    /// silently land in the wrong container.
    /// </summary>
    private static (IReadOnlyList<JsonNode> Pre, IReadOnlyList<JsonNode> Post) SplitBlockEntries(
        SquadRenderedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block.Entries);
        List<JsonNode> pre = [];
        List<JsonNode> post = [];
        foreach (SquadRenderedBlockEntry entry in block.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.Entry is null)
            {
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad block '{block.RelativePath}' carries an empty hook entry. " +
                    "Fix the upstream render before deploying it.");
            }

            if (string.Equals(entry.Container, "PreToolUse", StringComparison.OrdinalIgnoreCase))
                pre.Add(entry.Entry);
            else if (string.Equals(entry.Container, "PostToolUse", StringComparison.OrdinalIgnoreCase))
                post.Add(entry.Entry);
            else
            {
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad block '{block.RelativePath}' names unknown hook container " +
                    $"'{entry.Container ?? "null"}'. Fix the upstream render before deploying it.");
            }
        }

        return (pre, post);
    }

    private static IReadOnlyList<SquadHookDrift> FindBlockDrift(
        SquadRenderedBlock block,
        string relativePath,
        string currentJson,
        SquadOwnedBlock previous)
    {
        Dictionary<string, string> expected = new(StringComparer.Ordinal);
        foreach (SquadOwnedBlockEntry entry in previous.Entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            expected[entry.Container] = entry.Sha256;
        }

        try
        {
            return SquadHookJsonBlock.FindDrift(block.Format, currentJson, expected);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException)
        {
            throw BlockConflict(
                relativePath,
                "does not parse as a hook file. Move it aside before updating Squad.",
                exception);
        }
    }

    private static SquadDeploymentConflictException BlockConflict(
        string relativePath,
        string detail,
        Exception? inner = null) =>
        inner is null
            ? new($"Squad hook block '{relativePath}' {detail}")
            : new($"Squad hook block '{relativePath}' {detail}", inner);

    private static string DecodeBlockText(string relativePath, byte[] bytes, bool allowBlank = true)
    {
        string text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];
        if (string.IsNullOrWhiteSpace(text))
        {
            // A blank hook file is a minimal document, not an error: a user can create
            // .codex/hooks.json empty and still expect `squad install` to work. A file a
            // receipt already owns is different: Squad's entries cannot vanish silently.
            if (allowBlank)
                return string.Empty;

            throw BlockConflict(
                relativePath,
                "is empty although a receipt owns entries in it. Move it aside before deploying Squad.");
        }

        return text;
    }

    /// <summary>
    /// Splices the managed entries into the current hook-file content, replacing Squad's
    /// previous entries while leaving the user's entries in place. Planning must stay
    /// side-effect free for dry runs, so this calls the content-level splice that
    /// <see cref="SquadHookJsonBlock"/> owns: there is exactly one implementation, and a
    /// dry run, an install and the file-level splice cannot drift apart. A missing or blank
    /// file starts from the format's minimal document.
    /// </summary>
    private static (string Content, IReadOnlyList<SquadHookOwnedEntry> Owned) SpliceBlockContent(
        SquadHookBlockFormat format,
        string relativePath,
        string? existingJson,
        IReadOnlyList<JsonNode> preToolUse,
        IReadOnlyList<JsonNode> postToolUse)
    {
        try
        {
            return SquadHookJsonBlock.SpliceContent(format, existingJson, preToolUse, postToolUse);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw UnusableBlockFile(relativePath, exception);
        }
    }

    /// <summary>Removes Squad's entries from hook-file content, restoring the user's content.</summary>
    private static string RemoveBlockContent(
        SquadHookBlockFormat format,
        string relativePath,
        string existingJson)
    {
        try
        {
            return SquadHookJsonBlock.RemoveContent(format, existingJson);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw UnusableBlockFile(relativePath, exception);
        }
    }

    /// <summary>Serializes the parsed content back, so removal can tell "nothing ours left" apart from a rewrite.</summary>
    private static string NormalizeBlockJson(string relativePath, string existingJson)
    {
        try
        {
            return SquadHookJsonBlock.NormalizeContent(existingJson);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw UnusableBlockFile(relativePath, exception);
        }
    }

    private static SquadDeploymentConflictException UnusableBlockFile(string relativePath, Exception inner) =>
        BlockConflict(
            relativePath,
            $"is not a usable hook file ({inner.Message}) Move it aside before deploying Squad.",
            inner);

    internal static string DeployedFileIdentity(string target, string relativePath) =>
        target + '\0' + relativePath;

    private static bool IsOwnedBySiblingReceipt(
        IReadOnlyList<SquadReceipt>? siblingReceipts,
        string target,
        string relativePath)
    {
        if (siblingReceipts is null || siblingReceipts.Count == 0)
            return false;

        foreach (SquadReceipt sibling in siblingReceipts)
        {
            foreach (SquadOwnedFile owned in sibling.Files)
            {
                if (string.Equals(owned.Target, target, StringComparison.Ordinal) &&
                    string.Equals(owned.RelativePath, relativePath, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static SquadDeploymentConflictException SiblingGlobalOwnership(
        string relativePath,
        string target) =>
        new(
            $"Global path '{relativePath}' for target '{target}' is already owned by another " +
            "Squad deployment. Uninstall or update that deployment before installing from this project.");

    private static string Digest(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static SquadDeploymentConflictException UnmanagedCollision(string relativePath) =>
        new(
            $"Unmanaged path '{relativePath}' collides with generated Squad output. " +
            "Move the file aside, or use --adopt during install only when its bytes match exactly.");

    private sealed record NormalizedDeploymentFile(
        SquadDeploymentFile File,
        string FullPath);
}

/// <summary>
/// One unmanaged file whose name matches a canonical Squad identity and whose
/// bytes do not match the current render. Doctor lists these; install refuses them.
/// </summary>
public sealed record SquadUnmanagedPathCollision(
    string RelativePath,
    string FullPath,
    string Target,
    string Identity);

/// <summary>
/// Whether a planned file change writes new bytes or removes a deployed file. Public so
/// command hosts can report what a plan will do without seeing the internal payload record.
/// </summary>
public enum SquadFileMutationKind
{
    Write,
    Delete
}

/// <summary>
/// One planned file change without its payload, for command hosts that report what a plan
/// will do. An uninstall plan's receipt holds only the files it retains, so the removals it
/// plans are readable from these changes alone.
/// </summary>
public sealed record SquadPlannedFileChange(
    string RelativePath,
    string Target,
    SquadFileMutationKind Kind);

/// <summary>
/// A pending write or delete for one deployed file, identified by <c>(Target, RelativePath)</c>
/// so that Global scope's per-target physical roots (<see cref="SquadDeploymentPlan.ResolvePhysicalRoot(string)"/>
/// can be resolved without cross-referencing the receipt by relative path alone.
/// </summary>
internal sealed record SquadFileMutation(
    string RelativePath,
    string Target,
    SquadFileMutationKind Kind,
    byte[]? Content)
{
    public static SquadFileMutation Write(
        string relativePath,
        string target,
        ReadOnlyMemory<byte> content) =>
        new(relativePath, target, SquadFileMutationKind.Write, content.ToArray());

    public static SquadFileMutation Delete(string relativePath, string target) =>
        new(relativePath, target, SquadFileMutationKind.Delete, null);
}

internal enum SquadStateMutation
{
    Keep,
    Write,
    Delete
}

internal enum SquadFilePreconditionKind
{
    Missing,
    ExactFile
}

internal sealed record SquadFilePrecondition(
    string RelativePath,
    string Target,
    SquadFilePreconditionKind Kind,
    string? Sha256)
{
    public static SquadFilePrecondition Missing(string relativePath, string target) =>
        new(relativePath, target, SquadFilePreconditionKind.Missing, null);

    public static SquadFilePrecondition Exact(string relativePath, string target, string sha256) =>
        new(relativePath, target, SquadFilePreconditionKind.ExactFile, sha256);
}
