using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Resolves where a deployment plan's files live, and classifies the layout of a receipt's files.</summary>
public static class SquadDeploymentPlanRules
{
    /// <summary>
    ///     Replaces the receipt's target root with the canonical root it will be written against. Only the
    ///     transaction calls this, because only the store knows the home that a global receipt is anchored on.
    /// </summary>
    /// <param name="anchorRoot">The canonical absolute root to record.</param>
    /// <param name="plan">The deployment plan.</param>
    public static void AnchorReceipt(this SquadDeploymentPlan plan, string anchorRoot)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorRoot);
        plan.Receipt = plan.Receipt with { TargetRoot = anchorRoot };
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
    /// <param name="plan">The deployment plan.</param>
    public static void AttachHomeFiles(this SquadDeploymentPlan plan, IReadOnlyList<SquadHomeFile> homeFiles)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(homeFiles);
        plan.Receipt = plan.Receipt with { HomeFiles = homeFiles };
    }

    /// <summary>
    ///     Resolves the absolute physical path where <c>file.RelativePath</c> will be written,
    ///     based on the deployment scope: for <see cref="SquadDeploymentScope.Project" /> that is
    ///     <c>PhysicalRootPath</c> (the project root) combined with the relative path unchanged;
    ///     for <see cref="SquadDeploymentScope.Global" /> it is the resolver's root for
    ///     <c>file.Target</c> combined with the relative path, unless this plan carries a legacy
    ///     single-root receipt, in which case it is <c>PhysicalRootPath</c> regardless.
    /// </summary>
    /// <param name="file">The owned file to resolve.</param>
    /// <param name="semantics">The filesystem's containment rules.</param>
    /// <returns>The absolute path the file resolves to.</returns>
    /// <param name="plan">The deployment plan.</param>
    public static string ResolvePhysicalPath(this SquadDeploymentPlan plan, SquadOwnedFile file, IFileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(file);

        return plan.ResolvePhysicalPath(file.Target, file.RelativePath, semantics);
    }

    /// <summary>
    ///     Resolves the absolute physical path for a bare target token and relative path, for
    ///     callers such as <c>SquadTransaction</c> whose in-flight mutations carry a target
    ///     token rather than a full <see cref="SquadOwnedFile" />.
    /// </summary>
    /// <param name="target">The target token the file belongs to.</param>
    /// <param name="relativePath">The portable relative path from the source tree.</param>
    /// <param name="semantics">The filesystem's containment rules.</param>
    /// <returns>The absolute path the file resolves to.</returns>
    /// <param name="plan">The deployment plan.</param>
    public static string ResolvePhysicalPath(this SquadDeploymentPlan plan, string target, string relativePath, IFileSystemPathSemantics semantics)
    {
        return SquadPathPolicy.ResolveFile(plan.ResolvePhysicalRoot(target), relativePath, semantics);
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
    /// <param name="plan">The deployment plan.</param>
    /// <param name="target">The bare target token.</param>
    public static string ResolvePhysicalRoot(this SquadDeploymentPlan plan, string target)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ResolvePhysicalRoot(plan.Scope, plan.PhysicalRootPath, plan.GlobalRoots, target, plan.IsSingleRootLayout);
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

    /// <param name="scope">Whether the run is project, instance or global scope.</param>
    /// <param name="physicalRootPath">The deployment's physical root.</param>
    /// <param name="globalRoots">The per-target global root resolver, when the deployment has one.</param>
    /// <param name="target">The target token the file belongs to.</param>
    /// <param name="relativePath">The portable relative path from the source tree.</param>
    /// <param name="isSingleRootLayout">Whether the receipt records the pre-#91 single-root layout.</param>
    /// <param name="semantics">The filesystem's containment rules.</param>
    /// <returns>The absolute path the file resolves to.</returns>
    public static string ResolvePhysicalPath(
        SquadDeploymentScope scope,
        string physicalRootPath,
        ISquadGlobalRootResolver? globalRoots,
        string target,
        string relativePath,
        bool isSingleRootLayout,
        IFileSystemPathSemantics semantics)
    {
        return SquadPathPolicy.ResolveFile(
            ResolvePhysicalRoot(scope, physicalRootPath, globalRoots, target, isSingleRootLayout),
            relativePath,
            semantics);
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
    /// <param name="receipt">The receipt whose layout decides the root.</param>
    /// <param name="targetRoot">The root the command is anchored at.</param>
    /// <param name="globalRoots">The per-target global root resolver, when the deployment has one.</param>
    /// <param name="file">The owned file to resolve.</param>
    /// <param name="semantics">The filesystem's containment and canonical spelling rules.</param>
    /// <returns>The absolute path the file resolves to.</returns>
    public static string ResolveOwnedFilePath(
        SquadReceipt receipt,
        string targetRoot,
        ISquadGlobalRootResolver? globalRoots,
        SquadOwnedFile file,
        IFileSystemPathSemantics semantics)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(semantics);

        return ResolvePhysicalPath(
            receipt.Scope,
            SquadPhysicalRootIdentities.Resolve(targetRoot, semantics).PhysicalPath,
            globalRoots,
            file.Target,
            file.RelativePath,
            IsLegacySingleRootReceipt(receipt),
            semantics);
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
    ///     instead, so whether a file starts with its own target's <see cref="HarnessRegistry.LegacyGlobalPrefixes" />
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
            bool isPrefixed = HarnessRegistry.LegacyGlobalPrefixes.TryGetValue(file.Target, out string? prefix) &&
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
