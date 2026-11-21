using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Rendering;
using static KyberWeave.Core.Squad.Deployment.SquadDeploymentPlanRules;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>Preflights install, update and uninstall operations against the deployment tree.</summary>
public sealed class SquadDeploymentPlanner : ISquadDeploymentPlanner
{
    private readonly IFileSystem _files;
    private readonly IFileSystemPathSemantics _semantics;
    private readonly IPathResolver _paths;

    /// <summary>Creates a planner over the file-system, path and environment seams.</summary>
    /// <param name="files">Every tree read the preflight performs.</param>
    /// <param name="semantics">The canonical spelling and containment rules the plan is resolved with.</param>
    /// <param name="paths">Turns a caller's root spelling into a full path.</param>
    public SquadDeploymentPlanner(
        IFileSystem files,
        IFileSystemPathSemantics semantics,
        IPathResolver paths)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentNullException.ThrowIfNull(paths);
        _files = files;
        _semantics = semantics;
        _paths = paths;
    }

    /// <inheritdoc />
    public SquadDeploymentPlan CreateInstall(
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
        // The preflight reads the tree and writes nothing, so its repeated walks over the same
        // parent directories share one listing for the length of this call.
        using IDisposable listingScope = SquadFileSystemPathSemantics.BeginListingScope();
        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentities.Resolve(targetRoot, _semantics);
        string root = identity.PhysicalPath;
        IReadOnlyList<NormalizedDeploymentFile> normalizedFiles = NormalizeRenderedFiles(
            root,
            scope,
            globalRoots,
            renderedFiles);
        List<SquadFileMutation> mutations = new();
        List<SquadFilePrecondition> preconditions = new();
        List<SquadOwnedFile> ownedFiles = new();

        foreach (NormalizedDeploymentFile rendered in normalizedFiles)
        {
            if (IsOwnedBySiblingReceipt(siblingGlobalReceipts, rendered.File.Target, rendered.File.RelativePath))
                throw SiblingGlobalOwnership(rendered.File.RelativePath, rendered.File.Target);

            bool existsAsFile = _files.FileExists(rendered.FullPath);
            if (existsAsFile)
            {
                byte[] diskBytes = _files.ReadAllBytes(rendered.FullPath);
                ReadOnlySpan<byte> diskSpan = diskBytes;
                if (diskSpan.Length >= 3 && diskSpan[0] == 0xEF && diskSpan[1] == 0xBB && diskSpan[2] == 0xBF)
                {
                    diskSpan = diskSpan[3..];
                }

                string currentDigest = Digest(diskSpan);
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

            if (_files.DirectoryExists(rendered.FullPath))
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

        SquadReceipt receipt = NewReceipt(scope, root, timeProvider, degradations, ownedFiles, ownedBlocks);
        return new SquadDeploymentPlan(
            _paths.GetFullPath(targetRoot, root),
            identity,
            scope,
            squadLock,
            receipt,
            mutations,
            preconditions,
            SquadStateMutation.Write,
            SquadStateMutation.Write,
            globalRoots);
    }

    /// <summary>Preflights an update while preserving locally edited receipt-owned files by default.</summary>
    public SquadDeploymentPlan CreateUpdate(
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
        // The preflight reads the tree and writes nothing, so its repeated walks over the same
        // parent directories share one listing for the length of this call.
        using IDisposable listingScope = SquadFileSystemPathSemantics.BeginListingScope();
        ArgumentNullException.ThrowIfNull(previousReceipt);
        EnsureReceiptScope(previousReceipt, scope);

        // The double-prefix bug #91 describes can only occur once a per-target resolver is in
        // play: with none, legacy single-root and modern per-target-roots resolution already
        // agree (both fall back to the plan's own physical root), so there is nothing to
        // refuse against.
        if (globalRoots is not null && IsLegacySingleRootReceipt(previousReceipt))
            throw LegacySingleRootLayoutConflict();

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentities.Resolve(targetRoot, _semantics);
        string root = identity.PhysicalPath;
        IReadOnlyList<NormalizedDeploymentFile> normalizedFiles = NormalizeRenderedFiles(
            root,
            scope,
            globalRoots,
            renderedFiles);
        Dictionary<string, SquadOwnedFile> previousByPath = ReceiptFilesByPath(root, previousReceipt);
        List<SquadFileMutation> mutations = new();
        List<SquadFilePrecondition> preconditions = new();
        List<SquadOwnedFile> nextOwnedFiles = new();
        List<SquadDegradedFile> degradedFiles = new();
        HashSet<string> renderedIdentities = new(StringComparer.Ordinal);

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

                if (_files.FileExists(rendered.FullPath) || _files.DirectoryExists(rendered.FullPath))
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

            if (_files.DirectoryExists(rendered.FullPath))
                throw new SquadDeploymentConflictException(
                    $"Receipt-owned path '{relativePath}' is now a directory. " +
                    "Move it aside before updating Squad.");

            if (!_files.FileExists(rendered.FullPath))
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

            byte[] currentBytes;
            try
            {
                currentBytes = _files.ReadAllBytes(rendered.FullPath);
            }
            catch (Exception exception) when (SquadResponseGuidanceDeployer.IsDegradableFailure(exception))
            {
                // Bytes that cannot be read cannot be proven unedited, so the record is kept and the file is
                // left alone; the caller reports it by name.
                nextOwnedFiles.Add(previous);
                AddDegradedFile(degradedFiles, relativePath, rendered.File.Target);
                continue;
            }

            string currentDigest = Digest(currentBytes);
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
                scope, root, globalRoots, previous.Target, previous.RelativePath, false, _semantics);

            if (_files.DirectoryExists(fullPath))
            {
                nextOwnedFiles.Add(previous);
                continue;
            }

            if (!_files.FileExists(fullPath))
                continue;

            // An adopted entry is a file the host already had whose bytes equalled the release when Squad
            // recorded them: the receipt says Squad never wrote them. Requirement 13.6 removes only what
            // the Squad wrote, so a path a later release stops rendering stays on disk under its recorded
            // hash, and the record is kept so a host that edits it later is still reported.
            if (previous.Adopted)
            {
                nextOwnedFiles.Add(previous);
                continue;
            }

            byte[] previousBytes;
            try
            {
                previousBytes = _files.ReadAllBytes(fullPath);
            }
            catch (Exception exception) when (SquadResponseGuidanceDeployer.IsDegradableFailure(exception))
            {
                // Unreadable means unprovable: keep the record rather than delete bytes we cannot verify.
                nextOwnedFiles.Add(previous);
                AddDegradedFile(degradedFiles, previous.RelativePath, previous.Target);
                continue;
            }

            string currentDigest = Digest(previousBytes);
            if (string.Equals(currentDigest, previous.Sha256, StringComparison.Ordinal))
            {
                preconditions.Add(SquadFilePrecondition.Exact(
                    previous.RelativePath,
                    previous.Target,
                    currentDigest));
                mutations.Add(SquadFileMutation.Delete(previous.RelativePath, previous.Target));
            }
            else
            {
                nextOwnedFiles.Add(previous);
            }
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
            try
            {
                nextLayout = ClassifyGlobalLayout(nextOwnedFiles);
            }
            catch (InvalidDataException)
            {
                throw LegacySingleRootLayoutConflict();
            }

        SquadReceipt receipt = NewReceipt(scope, root, timeProvider, degradations, nextOwnedFiles, nextBlocks) with
        {
            // Carried forward, never decided here: home-owned files are written outside this plan, so
            // attaching a record during planning would let a plan that later fails preflight describe
            // a file no run delivered. The caller attaches what it actually wrote, and only after this
            // plan has passed every check.
            Layout = nextLayout,
            HomeFiles = previousReceipt.HomeFiles
        };
        return new SquadDeploymentPlan(
            _paths.GetFullPath(targetRoot, root),
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
            blockDrifts: blockDrifts,
            degradedFiles: degradedFiles);
    }

    /// <summary>Preflights an ownership-aware uninstall without changing the deployment tree.</summary>
    /// <remarks>
    ///     A non-null <paramref name="onlyTarget" /> removes just that harness: every other harness's files,
    ///     blocks and degradations stay in the retained receipt, which is deleted only when nothing is left.
    /// </remarks>
    public SquadDeploymentPlan CreateUninstall(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadReceipt receipt,
        ISquadGlobalRootResolver? globalRoots = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null,
        SquadTarget? onlyTarget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentNullException.ThrowIfNull(receipt);
        EnsureReceiptScope(receipt, scope);

        // The preflight reads the tree and writes nothing, so its repeated walks over the same
        // parent directories share one listing for the length of this call.
        using IDisposable listingScope = SquadFileSystemPathSemantics.BeginListingScope();


        // Resolved once, from the receipt being uninstalled, and used both to walk its files
        // below and to carry on the plan instance so SquadTransaction resolves the same way at
        // execute time (#91): a legacy single-root receipt's files live beneath the recorded
        // root regardless of what a per-target resolver would answer.
        bool isSingleRootLayout = IsLegacySingleRootReceipt(receipt);

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentities.Resolve(targetRoot, _semantics);
        string root = identity.PhysicalPath;
        _ = ReceiptFilesByPath(root, receipt);
        RequireProjectScopeForBlocks(scope, receipt.Blocks.Count > 0, "uninstall");
        List<SquadFileMutation> mutations = new();
        List<SquadFilePrecondition> preconditions = new();
        List<SquadOwnedFile> retained = new();
        string? onlyTargetToken = onlyTarget is { } only ? SquadTargetCatalog.GetToken(only) : null;
        foreach (SquadOwnedFile owned in receipt.Files)
        {
            if (IsOwnedBySiblingReceipt(siblingGlobalReceipts, owned.Target, owned.RelativePath))
                continue;

            if (onlyTargetToken is not null && !string.Equals(owned.Target, onlyTargetToken, StringComparison.Ordinal))
            {
                retained.Add(owned);
                continue;
            }

            string fullPath = ResolvePhysicalPath(
                scope, root, globalRoots, owned.Target, owned.RelativePath, isSingleRootLayout, _semantics);

            if (_files.DirectoryExists(fullPath))
            {
                retained.Add(owned);
                continue;
            }

            if (!_files.FileExists(fullPath))
                continue;

            string currentDigest = Digest(_files.ReadAllBytes(fullPath));
            if (string.Equals(currentDigest, owned.Sha256, StringComparison.Ordinal))
            {
                preconditions.Add(SquadFilePrecondition.Exact(
                    owned.RelativePath,
                    owned.Target,
                    currentDigest));
                mutations.Add(SquadFileMutation.Delete(owned.RelativePath, owned.Target));
            }
            else
            {
                retained.Add(owned);
            }
        }

        List<SquadOwnedBlock> retainedBlocks = PlanBlockRemovals(
            root,
            scope,
            globalRoots,
            receipt,
            mutations,
            preconditions,
            onlyTargetToken);

        // The layout carries forward onto the retained receipt so a later re-serialize (when
        // any file was locally edited and survives the uninstall) still writes the correct
        // explicit `layout` — reclassified from what actually remains, not merely copied.
        SquadReceiptLayout retainedLayout = scope == SquadDeploymentScope.Global
            ? ClassifyGlobalLayout(retained)
            : receipt.Layout;
        // The schema follows the same rule as the writer: a receipt that still owns
        // a block stays on v3, while one whose blocks are all gone drops back to the
        // block-less schema for its scope (v1 at project scope, v2 at global scope)
        // so it serializes byte-identical to every block-less receipt. A global receipt
        // that keeps at least one block stays on v3.
        string retainedSchema = retainedBlocks.Count > 0
            ? SquadStateSchemas.ReceiptSchemaV3
            : scope == SquadDeploymentScope.Global
                ? SquadStateSchemas.ReceiptSchemaV2
                : SquadStateSchemas.ReceiptSchemaV1;
        SquadReceipt retainedReceipt = receipt with
        {
            Files = retained,
            Blocks = retainedBlocks,
            Layout = retainedLayout,
            Schema = retainedSchema,
            Degradations = onlyTargetToken is null
                  || retained.Any(f => string.Equals(f.Target, onlyTargetToken, StringComparison.Ordinal))
                  || retainedBlocks.Any(b => string.Equals(b.Target, onlyTargetToken, StringComparison.Ordinal))
                ? receipt.Degradations
                : [.. receipt.Degradations.Where(d => !string.Equals(d.Target, onlyTargetToken, StringComparison.Ordinal))]
        };
        bool hasRetainedFiles = retained.Count > 0 || retainedBlocks.Count > 0;
        return new SquadDeploymentPlan(
            _paths.GetFullPath(targetRoot, root),
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
    ///     Lists every rendered path that already exists at its resolved physical location
    ///     with bytes that do not match the render. Files recorded as managed in the
    ///     deployment's receipt (or sibling global receipts) are excluded from collision
    ///     reporting. <c>squad doctor --global</c> surfaces the whole set as warnings;
    ///     <see cref="CreateInstall" /> still throws on the first via the existing
    ///     unmanaged-collision rule.
    /// </summary>
    public IReadOnlyList<SquadUnmanagedPathCollision> CollectUnmanagedCollisions(
        string targetRoot,
        SquadDeploymentScope scope,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        ISquadGlobalRootResolver? globalRoots,
        SquadReceipt? receipt = null,
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentNullException.ThrowIfNull(renderedFiles);

        string root = SquadPhysicalRootIdentities.Resolve(targetRoot, _semantics).PhysicalPath;
        IReadOnlyList<NormalizedDeploymentFile> normalizedFiles = NormalizeRenderedFiles(
            root,
            scope,
            globalRoots,
            renderedFiles);

        HashSet<string> managedIdentities = new(StringComparer.Ordinal);
        if (receipt is not null)
            foreach (SquadOwnedFile file in receipt.Files)
                managedIdentities.Add(DeployedFileIdentity(file.Target,
                    SquadPathPolicy.NormalizeRelativePath(file.RelativePath)));

        if (siblingGlobalReceipts is not null)
            foreach (SquadReceipt sibling in siblingGlobalReceipts)
                foreach (SquadOwnedFile file in sibling.Files)
                    managedIdentities.Add(DeployedFileIdentity(file.Target,
                        SquadPathPolicy.NormalizeRelativePath(file.RelativePath)));

        List<SquadUnmanagedPathCollision> collisions = [];
        foreach (NormalizedDeploymentFile rendered in normalizedFiles)
        {
            if (managedIdentities.Contains(DeployedFileIdentity(rendered.File.Target, rendered.File.RelativePath)))
                continue;

            if (_files.DirectoryExists(rendered.FullPath))
            {
                collisions.Add(ToCollision(rendered));
                continue;
            }

            if (!_files.FileExists(rendered.FullPath)) continue;

            byte[] diskBytes = _files.ReadAllBytes(rendered.FullPath);
            ReadOnlySpan<byte> diskSpan = diskBytes;
            if (diskSpan.Length >= 3 && diskSpan[0] == 0xEF && diskSpan[1] == 0xBB && diskSpan[2] == 0xBF)
            {
                diskSpan = diskSpan[3..];
            }

            string currentDigest = Digest(diskSpan);
            string renderedDigest = Digest(rendered.File.Content.Span);
            if (!string.Equals(currentDigest, renderedDigest, StringComparison.Ordinal))
                collisions.Add(ToCollision(rendered));
        }

        return collisions;
    }

    private static SquadUnmanagedPathCollision ToCollision(NormalizedDeploymentFile rendered)
    {
        return new SquadUnmanagedPathCollision(
            rendered.File.RelativePath,
            rendered.FullPath,
            rendered.File.Target,
            IdentityFromRelativePath(rendered.File.RelativePath));
    }

    private IReadOnlyList<NormalizedDeploymentFile> NormalizeRenderedFiles(
        string root,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? globalRoots,
        IReadOnlyList<SquadDeploymentFile> renderedFiles)
    {
        List<NormalizedDeploymentFile> normalized = new(renderedFiles.Count);
        Dictionary<string, string> seenIdentities = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> seenFullPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (SquadDeploymentFile rendered in renderedFiles)
        {
            ArgumentNullException.ThrowIfNull(rendered);
            string relativePath = SquadPathPolicy.NormalizeRelativePath(rendered.RelativePath);
            string portableIdentity = SquadPathPolicy.GetPortableIdentity(relativePath);
            if (!string.Equals(portableIdentity, relativePath, StringComparison.Ordinal))
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad output path '{rendered.RelativePath}' is not a portable " +
                    "canonical path because a segment ends in a dot or space.");

            string fileIdentity = DeployedFileIdentity(rendered.Target, portableIdentity);
            if (seenIdentities.TryGetValue(fileIdentity, out _))
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad output path '{rendered.RelativePath}' has a portable " +
                    "alias collision. Fix the upstream render before deploying it.");

            seenIdentities.Add(fileIdentity, relativePath);
            SquadDeploymentFile normalizedFile = rendered with { RelativePath = relativePath };
            // A freshly rendered file is never legacy: every renderer has stripped its Global
            // prefix since #91, so this always resolves through the modern per-target path.
            string fullPath = ResolvePhysicalPath(
                scope, root, globalRoots, rendered.Target, relativePath, false, _semantics);
            if (seenFullPaths.TryGetValue(fullPath, out string? existingPath))
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad output path '{rendered.RelativePath}' resolves to the same " +
                    $"physical file as '{existingPath}'. Fix the upstream render before deploying it.");

            seenFullPaths.Add(fullPath, relativePath);
            normalized.Add(new NormalizedDeploymentFile(normalizedFile, fullPath));
        }

        return normalized;
    }

    private Dictionary<string, SquadOwnedFile> ReceiptFilesByPath(
        string root,
        SquadReceipt receipt)
    {
        Dictionary<string, SquadOwnedFile> files = new(StringComparer.Ordinal);
        HashSet<string> portableIdentities = new(StringComparer.OrdinalIgnoreCase);
        foreach (SquadOwnedFile owned in receipt.Files)
        {
            ArgumentNullException.ThrowIfNull(owned);
            string normalizedPath = SquadPathPolicy.NormalizeRelativePath(owned.RelativePath);
            _ = SquadPathPolicy.ResolveFile(root, normalizedPath, _semantics);
            if (!string.Equals(normalizedPath, owned.RelativePath, StringComparison.Ordinal))
                throw new SquadPathContainmentException(
                    $"Receipt path '{owned.RelativePath}' is not a normalized portable path.");

            string portableIdentity = SquadPathPolicy.GetPortableIdentity(normalizedPath);
            if (!string.Equals(portableIdentity, normalizedPath, StringComparison.Ordinal))
                throw new SquadDeploymentConflictException(
                    $"Squad receipt path '{owned.RelativePath}' is not a portable canonical path.");

            string deployedIdentity = DeployedFileIdentity(owned.Target, portableIdentity);
            if (!portableIdentities.Add(deployedIdentity))
                throw new SquadDeploymentConflictException(
                    $"Squad receipt path '{owned.RelativePath}' has a portable alias collision.");

            if (!files.TryAdd(DeployedFileIdentity(owned.Target, normalizedPath), owned))
                throw new SquadDeploymentConflictException(
                    $"Squad receipt contains duplicate path '{normalizedPath}' for target '{owned.Target}'.");
        }

        return files;
    }

    /// <summary>
    ///     Builds the receipt a plan will write. A project receipt records the canonical physical root it
    ///     was planned against. A global receipt cannot, because the home anchor is known only to the
    ///     store, so it carries <see cref="SquadStateSchemas.UnanchoredTargetRoot" /> until the transaction
    ///     anchors it at write time.
    /// </summary>
    private static SquadReceipt NewReceipt(
        SquadDeploymentScope scope,
        string physicalRoot,
        TimeProvider timeProvider,
        IReadOnlyList<SquadDegradation> degradations,
        IReadOnlyList<SquadOwnedFile> ownedFiles,
        IReadOnlyList<SquadOwnedBlock>? ownedBlocks = null)
    {
        IReadOnlyList<SquadOwnedBlock> blocks = ownedBlocks ?? [];
        // The in-memory schema follows the writer: a receipt that owns a block reads v3,
        // regardless of whether the scope is project or global; everything else keeps the
        // pre-block schema (v1, with Global upgrading to v2 at write time).
        string schema = blocks.Count > 0 && scope != SquadDeploymentScope.Instance
            ? SquadStateSchemas.ReceiptSchemaV3
            : SquadStateSchemas.ReceiptSchemaV1;
        return new SquadReceipt(
            schema,
            scope,
            scope == SquadDeploymentScope.Global ? SquadStateSchemas.UnanchoredTargetRoot : physicalRoot,
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
            throw new SquadDeploymentConflictException(
                $"The Squad receipt has scope '{receipt.Scope}' but the operation requested " +
                $"'{requestedScope}'. Use the receipt's original scope.");
    }

    /// <summary>
    /// Blocks are a shared-file ownership kind rather than a project-only one: they are valid for
    /// both project and global scope, while instance scope is still rejected. This keeps the
    /// project contract unchanged while allowing a global install to own hook blocks and home-owned
    /// files under the home-level state directory.
    /// </summary>
    private static void RequireProjectScopeForBlocks(
        SquadDeploymentScope scope,
        bool hasBlocks,
        string operation)
    {
        if (scope == SquadDeploymentScope.Instance && hasBlocks)
        {
            throw new SquadDeploymentConflictException(
                $"Squad hook blocks cannot be {operation} under '{scope}' scope: " +
                $"'{SquadStateSchemas.ReceiptSchemaV3}' receipts are written for project and global scope only.");
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
    private List<SquadOwnedBlock> PlanBlockInstalls(
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
                scope, root, globalRoots, block.Target, relativePath, isSingleRootLayout: false, _semantics);
            (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post, IReadOnlyList<JsonNode> subagentStop,
                IReadOnlyList<JsonNode> sessionStart, IReadOnlyList<JsonNode> sessionEnd) = SplitBlockEntries(block);

            bool existsAsFile = _files.FileExists(fullPath);
            string spliced;
            IReadOnlyList<SquadHookOwnedEntry> owned;
            if (existsAsFile)
            {
                byte[] currentBytes = _files.ReadAllBytes(fullPath);
                string currentText = DecodeBlockText(relativePath, currentBytes);
                (spliced, owned) = SpliceBlockContent(
                        block.Format, relativePath, currentText, pre, post, subagentStop, sessionStart, sessionEnd);
                preconditions.Add(SquadFilePrecondition.Exact(
                    relativePath,
                    block.Target,
                    Digest(currentBytes)));
                if (!string.Equals(spliced, currentText, StringComparison.Ordinal))
                    mutations.Add(SquadFileMutation.Write(relativePath, block.Target, Encoding.UTF8.GetBytes(spliced)));
            }
            else
            {
                if (_files.DirectoryExists(fullPath))
                    throw UnmanagedCollision(relativePath);

                (spliced, owned) = SpliceBlockContent(
                        block.Format, relativePath, null, pre, post, subagentStop, sessionStart, sessionEnd);
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
    /// drifted is left untouched and reported on <see cref="SquadDeploymentPlan.BlockDrifts"/> unless
    /// <paramref name="replaceManaged"/> is set, in which case the drift is overwritten like
    /// any other managed byte.
    /// </summary>
    private (List<SquadOwnedBlock> Next, HashSet<string> Rendered) PlanBlockUpdates(
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
                scope, root, globalRoots, block.Target, relativePath, isSingleRootLayout: false, _semantics);
            (IReadOnlyList<JsonNode> pre, IReadOnlyList<JsonNode> post, IReadOnlyList<JsonNode> subagentStop,
                IReadOnlyList<JsonNode> sessionStart, IReadOnlyList<JsonNode> sessionEnd) = SplitBlockEntries(block);

            if (!previousByIdentity.TryGetValue(identity, out SquadOwnedBlock? previous))
            {
                if (_files.DirectoryExists(fullPath))
                    throw UnmanagedCollision(relativePath);

                if (_files.FileExists(fullPath))
                {
                    byte[] currentBytes = _files.ReadAllBytes(fullPath);
                    string currentText = DecodeBlockText(relativePath, currentBytes);
                    (string spliced, IReadOnlyList<SquadHookOwnedEntry> owned) = SpliceBlockContent(
                        block.Format, relativePath, currentText, pre, post, subagentStop, sessionStart, sessionEnd);
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
                        block.Format, relativePath, null, pre, post, subagentStop, sessionStart, sessionEnd);
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

            if (_files.DirectoryExists(fullPath))
            {
                throw new SquadDeploymentConflictException(
                    $"Receipt-owned block path '{relativePath}' is now a directory. " +
                    "Move it aside before updating Squad.");
            }

            if (!_files.FileExists(fullPath))
            {
                (string spliced, IReadOnlyList<SquadHookOwnedEntry> owned) = SpliceBlockContent(
                    block.Format, relativePath, null, pre, post, subagentStop, sessionStart, sessionEnd);
                preconditions.Add(SquadFilePrecondition.Missing(relativePath, block.Target));
                mutations.Add(SquadFileMutation.Write(relativePath, block.Target, Encoding.UTF8.GetBytes(spliced)));
                nextBlocks.Add(new SquadOwnedBlock(
                    relativePath,
                    block.Target,
                    true,
                    [.. owned.Select(entry => new SquadOwnedBlockEntry(entry.Location, entry.Digest))]));
                continue;
            }

            byte[] current = _files.ReadAllBytes(fullPath);
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
                block.Format, relativePath, currentJson, pre, post, subagentStop, sessionStart, sessionEnd);
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
    private void RetireMissingBlocks(
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
    private List<SquadOwnedBlock> PlanBlockRemovals(
        string root,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? globalRoots,
        SquadReceipt receipt,
        List<SquadFileMutation> mutations,
        List<SquadFilePrecondition> preconditions,
        string? onlyTargetToken = null)
    {
        List<SquadOwnedBlock> retained = [];
        foreach (SquadOwnedBlock owned in receipt.Blocks)
        {
            ArgumentNullException.ThrowIfNull(owned);
            if (onlyTargetToken is not null && !string.Equals(owned.Target, onlyTargetToken, StringComparison.Ordinal))
            {
                retained.Add(owned);
                continue;
            }

            if (RemoveOwnedBlockEntries(root, scope, globalRoots, owned, mutations, preconditions))
                retained.Add(owned);
        }

        return retained;
    }

    private bool RemoveOwnedBlockEntries(
        string root,
        SquadDeploymentScope scope,
        ISquadGlobalRootResolver? globalRoots,
        SquadOwnedBlock owned,
        List<SquadFileMutation> mutations,
        List<SquadFilePrecondition> preconditions)
    {
        string fullPath = ResolvePhysicalPath(
            scope, root, globalRoots, owned.Target, owned.RelativePath, isSingleRootLayout: false, _semantics);

        if (_files.DirectoryExists(fullPath))
            return true;

        if (!_files.FileExists(fullPath))
            return false;

        byte[] currentBytes = _files.ReadAllBytes(fullPath);
        string currentText;
        string trimmed;
        try
        {
            // Update already refuses to deploy against a blanked receipt-owned file. Uninstall
            // made the opposite call and reported the block removed, so it dropped out of
            // the receipt and the file was left blank with nothing recorded to repair it
            // from. Take the same decision here, by the same code: allow the blank only
            // when the receipt owns no entries in the file, which is the case where a
            // blank file genuinely has nothing to lose.
            currentText = DecodeBlockText(
                owned.RelativePath,
                currentBytes,
                allowBlank: owned.Entries.Count == 0);
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
    /// Resolves the hook-file shape for a receipt-owned block, reading the receipt the way
    /// status and doctor do: the recorded target decides the shape first, and the per-format
    /// portable path is only the fallback for a target that owns no shared hook file.
    /// </summary>
    private static SquadHookBlockFormat FormatForTarget(SquadOwnedBlock owned)
    {
        ArgumentNullException.ThrowIfNull(owned);
        if (SquadHookJsonBlock.TryResolveFormat(owned, out SquadHookBlockFormat format))
            return format;

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
    /// Refuses a container only the Factory hook file holds, so a renderer bug cannot write a key the harness never
    /// reads.
    /// </summary>
    private static void RequireFactoryFor(SquadRenderedBlock block, string container)
    {
        if (block.Format != SquadHookBlockFormat.Factory)
        {
            throw new SquadDeploymentConflictException(
                $"Rendered Squad block '{block.RelativePath}' names {container}, which only " +
                "the Factory hook file holds. Fix the upstream render before deploying it.");
        }
    }

    /// <summary>
    /// Splits a rendered block's entries into the pre, post, SubagentStop and session-event hook containers by their
    /// logical container name. Anything else is a renderer bug, refused before it can silently land in the wrong
    /// container.
    /// </summary>
    private static (
        IReadOnlyList<JsonNode> Pre,
        IReadOnlyList<JsonNode> Post,
        IReadOnlyList<JsonNode> SubagentStop,
        IReadOnlyList<JsonNode> SessionStart,
        IReadOnlyList<JsonNode> SessionEnd) SplitBlockEntries(SquadRenderedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block.Entries);
        List<JsonNode> pre = [];
        List<JsonNode> post = [];
        List<JsonNode> subagentStop = [];
        List<JsonNode> sessionStart = [];
        List<JsonNode> sessionEnd = [];
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
            {
                pre.Add(entry.Entry);
                continue;
            }

            if (string.Equals(entry.Container, "PostToolUse", StringComparison.OrdinalIgnoreCase))
            {
                post.Add(entry.Entry);
                continue;
            }

            if (string.Equals(entry.Container, "SubagentStop", StringComparison.OrdinalIgnoreCase))
            {
                // Only Factory's hook file has this container; a renderer that names it for any
                // other shape would otherwise write a key that harness never reads.
                if (block.Format != SquadHookBlockFormat.Factory)
                {
                    throw new SquadDeploymentConflictException(
                        $"Rendered Squad block '{block.RelativePath}' names SubagentStop, which only " +
                        "the Factory hook file holds. Fix the upstream render before deploying it.");
                }

                subagentStop.Add(entry.Entry);
                continue;
            }

            if (string.Equals(entry.Container, "SessionStart", StringComparison.OrdinalIgnoreCase))
            {
                // Only Factory's hook file holds the session events its worker guard joins.
                RequireFactoryFor(block, "SessionStart");
                sessionStart.Add(entry.Entry);
                continue;
            }

            if (string.Equals(entry.Container, "SessionEnd", StringComparison.OrdinalIgnoreCase))
            {
                RequireFactoryFor(block, "SessionEnd");
                sessionEnd.Add(entry.Entry);
                continue;
            }

            if (string.Equals(entry.Container, "kyber-arbiter", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Entry is not JsonObject group)
                {
                    throw new SquadDeploymentConflictException(
                        $"Rendered Antigravity block '{block.RelativePath}' names the managed " +
                        "group container but carries a non-object payload. Fix the upstream render before deploying it.");
                }

                foreach ((string container, List<JsonNode> destination) in new[]
                         {
                             ("PreToolUse", pre),
                             ("PostToolUse", post),
                         })
                {
                    JsonNode? payload = group[container];
                    if (payload is JsonArray array)
                    {
                        foreach (JsonNode? node in array)
                        {
                            if (node is not null)
                            {
                                destination.Add(node);
                            }
                        }

                        continue;
                    }

                    if (payload is not null)
                    {
                        destination.Add(payload);
                    }
                }

                continue;
            }

            throw new SquadDeploymentConflictException(
                $"Rendered Squad block '{block.RelativePath}' names unknown hook container " +
                $"'{entry.Container ?? "null"}'. Fix the upstream render before deploying it.");
        }

        return (pre, post, subagentStop, sessionStart, sessionEnd);
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
        IReadOnlyList<JsonNode> postToolUse,
        IReadOnlyList<JsonNode> subagentStop,
        IReadOnlyList<JsonNode> sessionStart,
        IReadOnlyList<JsonNode> sessionEnd)
    {
        try
        {
            return SquadHookJsonBlock.SpliceContent(
                format, existingJson, preToolUse, postToolUse, subagentStop, sessionStart, sessionEnd);
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

    /// <summary>
    /// Records an unreadable deployed file with the raw target token from its receipt or render.
    /// The token is kept verbatim — never resolved to <see cref="SquadTarget"/> — so an unknown
    /// token is still reported by name instead of dropped as clean.
    /// </summary>
    private static void AddDegradedFile(List<SquadDegradedFile> degradedFiles, string relativePath, string target)
    {
        degradedFiles.Add(new SquadDegradedFile(relativePath, target));
    }

    private static bool IsOwnedBySiblingReceipt(
        IReadOnlyList<SquadReceipt>? siblingReceipts,
        string target,
        string relativePath)
    {
        if (siblingReceipts is null || siblingReceipts.Count == 0)
            return false;

        foreach (SquadReceipt sibling in siblingReceipts)
            foreach (SquadOwnedFile owned in sibling.Files)
                if (string.Equals(owned.Target, target, StringComparison.Ordinal) &&
                    string.Equals(owned.RelativePath, relativePath, StringComparison.Ordinal))
                    return true;

        return false;
    }

    private static SquadDeploymentConflictException SiblingGlobalOwnership(
        string relativePath,
        string target)
    {
        return new SquadDeploymentConflictException(
            $"Global path '{relativePath}' for target '{target}' is already owned by another " +
            "Squad deployment. Uninstall or update that deployment before installing from this project.");
    }

    private static string Digest(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private static SquadDeploymentConflictException UnmanagedCollision(string relativePath)
    {
        return new SquadDeploymentConflictException(
            $"Unmanaged path '{relativePath}' collides with generated Squad output. " +
            "Move the file aside, or use --adopt during install only when its bytes match exactly.");
    }

    private sealed record NormalizedDeploymentFile(
        SquadDeploymentFile File,
        string FullPath);
}
