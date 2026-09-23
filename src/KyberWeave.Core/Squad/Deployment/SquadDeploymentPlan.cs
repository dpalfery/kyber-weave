using System.Security.Cryptography;

namespace KyberWeave.Core.Squad.Deployment;

/// <summary>A preflighted set of file and state mutations for one Squad lifecycle operation.</summary>
public sealed class SquadDeploymentPlan
{
    private const string ReceiptSchema = "kyber-squad.receipt/v1";

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
        ISquadGlobalRootResolver? globalRoots = null)
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

    internal SquadStateMutation LockMutation { get; }

    internal SquadStateMutation ReceiptMutation { get; }

    private readonly ISquadGlobalRootResolver? _globalRoots;

    /// <summary>
    /// Resolves the absolute physical path where <c>file.RelativePath</c> will be written,
    /// based on the deployment scope: for <see cref="SquadDeploymentScope.Project"/> that is
    /// <c>PhysicalRootPath</c> (the project root) combined with the relative path unchanged;
    /// for <see cref="SquadDeploymentScope.Global"/> it is the resolver's root for
    /// <c>file.Target</c> combined with the relative path.
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

    /// <summary>
    /// Resolves the physical root directory for a file based on its target and this plan's scope.
    /// Used for path verification and containment checks in both Project and Global deployments.
    /// </summary>
    internal string ResolvePhysicalRoot(SquadOwnedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return ResolvePhysicalRoot(file.Target);
    }

    /// <summary>Resolves the physical root directory for a bare target token.</summary>
    /// <remarks>
    /// <c>globalRoots</c> is an opt-in, purely-additive parameter (R18/U8): every deployment
    /// that predates it wrote every file under the single provided <c>PhysicalRootPath</c>
    /// regardless of scope, because Global scope by itself only ever changed where lock,
    /// receipt, and journal state lived (<see cref="SquadStateStore"/>), never where deployed
    /// files landed. Falling back to <c>PhysicalRootPath</c> here when no resolver was supplied
    /// keeps that legacy single-root behavior byte-for-byte unchanged. Once a resolver is
    /// supplied, an unmapped target is a real bug: <see cref="SquadGlobalRoots"/> throws for it
    /// directly, and this method never substitutes the project root for a resolver's answer.
    /// </remarks>
    internal string ResolvePhysicalRoot(string target) =>
        ResolvePhysicalRoot(Scope, PhysicalRootPath, _globalRoots, target);

    private static string ResolvePhysicalRoot(
        SquadDeploymentScope scope,
        string physicalRootPath,
        ISquadGlobalRootResolver? globalRoots,
        string target)
    {
        if (globalRoots is null)
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
        string relativePath) =>
        SquadPathPolicy.ResolveFile(
            ResolvePhysicalRoot(scope, physicalRootPath, globalRoots, target),
            relativePath);

    /// <summary>
    /// Resolves the absolute physical path where a receipt-owned file is deployed, using the
    /// same scope and resolver rules as deploy time. Project scope resolves beneath
    /// <paramref name="targetRoot"/>; Global scope resolves beneath the file's target root
    /// from <paramref name="globalRoots"/> (falling back to the single root when no resolver
    /// is supplied, matching legacy single-root deployments). Consumers that verify deployed
    /// bytes — status reporting in particular — must use this rather than joining
    /// <paramref name="targetRoot"/> with the relative path directly, which only holds for
    /// Project scope.
    /// </summary>
    public static string ResolveOwnedFilePath(
        SquadDeploymentScope scope,
        string targetRoot,
        ISquadGlobalRootResolver? globalRoots,
        SquadOwnedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return ResolvePhysicalPath(
            scope,
            SquadPhysicalRootIdentity.Resolve(targetRoot).PhysicalPath,
            globalRoots,
            file.Target,
            file.RelativePath);
    }

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
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null)
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

        SquadReceipt receipt = NewReceipt(scope, timeProvider, degradations, ownedFiles);
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
            globalRoots);
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
        IReadOnlyList<SquadReceipt>? siblingGlobalReceipts = null)
    {
        ValidateCommon(targetRoot, squadLock, renderedFiles, degradations, timeProvider);
        ArgumentNullException.ThrowIfNull(previousReceipt);
        EnsureReceiptScope(previousReceipt, scope);

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

        foreach (SquadOwnedFile previous in previousReceipt.Files)
        {
            if (renderedIdentities.Contains(DeployedFileIdentity(previous.Target, previous.RelativePath)))
                continue;

            if (IsOwnedBySiblingReceipt(siblingGlobalReceipts, previous.Target, previous.RelativePath))
                continue;

            string fullPath = ResolvePhysicalPath(scope, root, globalRoots, previous.Target, previous.RelativePath);

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

        SquadReceipt receipt = NewReceipt(scope, timeProvider, degradations, nextOwnedFiles);
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
            globalRoots);
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

        SquadPhysicalRootIdentity identity = SquadPhysicalRootIdentity.Resolve(targetRoot);
        string root = identity.PhysicalPath;
        _ = ReceiptFilesByPath(root, receipt);
        List<SquadFileMutation> mutations = new List<SquadFileMutation>();
        List<SquadFilePrecondition> preconditions = new List<SquadFilePrecondition>();
        List<SquadOwnedFile> retained = new List<SquadOwnedFile>();
        foreach (SquadOwnedFile owned in receipt.Files)
        {
            if (IsOwnedBySiblingReceipt(siblingGlobalReceipts, owned.Target, owned.RelativePath))
                continue;

            string fullPath = ResolvePhysicalPath(scope, root, globalRoots, owned.Target, owned.RelativePath);

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

        SquadReceipt retainedReceipt = receipt with { Files = retained };
        bool hasRetainedFiles = retained.Count > 0;
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
            globalRoots);
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
            string fullPath = ResolvePhysicalPath(scope, root, globalRoots, rendered.Target, relativePath);
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
        IReadOnlyList<SquadOwnedFile> ownedFiles) =>
        new(
            ReceiptSchema,
            scope,
            ".",
            timeProvider.GetUtcNow(),
            degradations.ToArray(),
            ownedFiles.ToArray());

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
