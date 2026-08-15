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
        SquadStateMutation receiptMutation)
    {
        TargetRoot = targetRoot;
        PhysicalRootPath = physicalRootIdentity.PhysicalPath;
        PhysicalRootKey = physicalRootIdentity.Key;
        Scope = scope;
        Lock = squadLock;
        Receipt = receipt;
        FileMutations = fileMutations;
        FilePreconditions = filePreconditions;
        LockMutation = lockMutation;
        ReceiptMutation = receiptMutation;
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

    internal IReadOnlyList<SquadFileMutation> FileMutations { get; }

    internal IReadOnlyList<SquadFilePrecondition> FilePreconditions { get; }

    internal SquadStateMutation LockMutation { get; }

    internal SquadStateMutation ReceiptMutation { get; }

    /// <summary>Preflights a new installation without changing the deployment tree.</summary>
    public static SquadDeploymentPlan CreateInstall(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadLock squadLock,
        IReadOnlyList<SquadDeploymentFile> renderedFiles,
        IReadOnlyList<SquadDegradation> degradations,
        bool adopt,
        TimeProvider timeProvider)
    {
        ValidateCommon(targetRoot, squadLock, renderedFiles, degradations, timeProvider);
        var identity = SquadPhysicalRootIdentity.Resolve(targetRoot);
        var root = identity.PhysicalPath;
        var normalizedFiles = NormalizeRenderedFiles(root, renderedFiles);
        var mutations = new List<SquadFileMutation>();
        var preconditions = new List<SquadFilePrecondition>();
        var ownedFiles = new List<SquadOwnedFile>();

        foreach (var rendered in normalizedFiles)
        {
            var existsAsFile = File.Exists(rendered.FullPath);
            if (existsAsFile)
            {
                var currentDigest = Digest(File.ReadAllBytes(rendered.FullPath));
                var renderedDigest = Digest(rendered.File.Content.Span);
                if (!adopt || !string.Equals(currentDigest, renderedDigest, StringComparison.Ordinal))
                    throw UnmanagedCollision(rendered.File.RelativePath);

                preconditions.Add(SquadFilePrecondition.Exact(
                    rendered.File.RelativePath,
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

            preconditions.Add(SquadFilePrecondition.Missing(rendered.File.RelativePath));
            mutations.Add(SquadFileMutation.Write(
                rendered.File.RelativePath,
                rendered.File.Content));
            ownedFiles.Add(new SquadOwnedFile(
                rendered.File.RelativePath,
                Digest(rendered.File.Content.Span),
                rendered.File.Target,
                false));
        }

        var receipt = NewReceipt(scope, timeProvider, degradations, ownedFiles);
        return new SquadDeploymentPlan(
            Path.GetFullPath(targetRoot),
            identity,
            scope,
            squadLock,
            receipt,
            mutations,
            preconditions,
            SquadStateMutation.Write,
            SquadStateMutation.Write);
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
        TimeProvider timeProvider)
    {
        ValidateCommon(targetRoot, squadLock, renderedFiles, degradations, timeProvider);
        ArgumentNullException.ThrowIfNull(previousReceipt);
        EnsureReceiptScope(previousReceipt, scope);

        var identity = SquadPhysicalRootIdentity.Resolve(targetRoot);
        var root = identity.PhysicalPath;
        var normalizedFiles = NormalizeRenderedFiles(root, renderedFiles);
        var previousByPath = ReceiptFilesByPath(root, previousReceipt);
        var mutations = new List<SquadFileMutation>();
        var preconditions = new List<SquadFilePrecondition>();
        var nextOwnedFiles = new List<SquadOwnedFile>();
        var renderedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rendered in normalizedFiles)
        {
            var relativePath = rendered.File.RelativePath;
            renderedPaths.Add(relativePath);
            var renderedDigest = Digest(rendered.File.Content.Span);
            if (!previousByPath.TryGetValue(relativePath, out var previous))
            {
                if (File.Exists(rendered.FullPath) || Directory.Exists(rendered.FullPath))
                    throw UnmanagedCollision(relativePath);

                preconditions.Add(SquadFilePrecondition.Missing(relativePath));
                mutations.Add(SquadFileMutation.Write(relativePath, rendered.File.Content));
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
                preconditions.Add(SquadFilePrecondition.Missing(relativePath));
                mutations.Add(SquadFileMutation.Write(relativePath, rendered.File.Content));
                nextOwnedFiles.Add(new SquadOwnedFile(
                    relativePath,
                    renderedDigest,
                    rendered.File.Target,
                    false));
                continue;
            }

            var currentDigest = Digest(File.ReadAllBytes(rendered.FullPath));
            var isLocallyEdited = !string.Equals(
                currentDigest,
                previous.Sha256,
                StringComparison.Ordinal);
            if (isLocallyEdited && !replaceManaged)
            {
                nextOwnedFiles.Add(previous);
                continue;
            }

            preconditions.Add(SquadFilePrecondition.Exact(relativePath, currentDigest));
            var fileWillBeWritten = !string.Equals(
                currentDigest,
                renderedDigest,
                StringComparison.Ordinal);
            if (fileWillBeWritten)
                mutations.Add(SquadFileMutation.Write(relativePath, rendered.File.Content));

            nextOwnedFiles.Add(new SquadOwnedFile(
                relativePath,
                renderedDigest,
                rendered.File.Target,
                previous.Adopted && !fileWillBeWritten));
        }

        foreach (var previous in previousReceipt.Files)
        {
            if (renderedPaths.Contains(previous.RelativePath))
                continue;

            var fullPath = SquadPathPolicy.ResolveFile(root, previous.RelativePath);
            if (Directory.Exists(fullPath))
            {
                nextOwnedFiles.Add(previous);
                continue;
            }

            if (!File.Exists(fullPath))
                continue;

            var currentDigest = Digest(File.ReadAllBytes(fullPath));
            if (string.Equals(currentDigest, previous.Sha256, StringComparison.Ordinal))
            {
                preconditions.Add(SquadFilePrecondition.Exact(
                    previous.RelativePath,
                    currentDigest));
                mutations.Add(SquadFileMutation.Delete(previous.RelativePath));
            }
            else
                nextOwnedFiles.Add(previous);
        }

        var receipt = NewReceipt(scope, timeProvider, degradations, nextOwnedFiles);
        return new SquadDeploymentPlan(
            Path.GetFullPath(targetRoot),
            identity,
            scope,
            squadLock,
            receipt,
            mutations,
            preconditions,
            SquadStateMutation.Write,
            SquadStateMutation.Write);
    }

    /// <summary>Preflights an ownership-aware uninstall without changing the deployment tree.</summary>
    public static SquadDeploymentPlan CreateUninstall(
        string targetRoot,
        SquadDeploymentScope scope,
        SquadReceipt receipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        ArgumentNullException.ThrowIfNull(receipt);
        EnsureReceiptScope(receipt, scope);

        var identity = SquadPhysicalRootIdentity.Resolve(targetRoot);
        var root = identity.PhysicalPath;
        _ = ReceiptFilesByPath(root, receipt);
        var mutations = new List<SquadFileMutation>();
        var preconditions = new List<SquadFilePrecondition>();
        var retained = new List<SquadOwnedFile>();
        foreach (var owned in receipt.Files)
        {
            var fullPath = SquadPathPolicy.ResolveFile(root, owned.RelativePath);
            if (Directory.Exists(fullPath))
            {
                retained.Add(owned);
                continue;
            }

            if (!File.Exists(fullPath))
                continue;

            var currentDigest = Digest(File.ReadAllBytes(fullPath));
            if (string.Equals(currentDigest, owned.Sha256, StringComparison.Ordinal))
            {
                preconditions.Add(SquadFilePrecondition.Exact(
                    owned.RelativePath,
                    currentDigest));
                mutations.Add(SquadFileMutation.Delete(owned.RelativePath));
            }
            else
                retained.Add(owned);
        }

        var retainedReceipt = receipt with { Files = retained };
        var hasRetainedFiles = retained.Count > 0;
        return new SquadDeploymentPlan(
            Path.GetFullPath(targetRoot),
            identity,
            scope,
            null,
            retainedReceipt,
            mutations,
            preconditions,
            hasRetainedFiles ? SquadStateMutation.Keep : SquadStateMutation.Delete,
            hasRetainedFiles ? SquadStateMutation.Write : SquadStateMutation.Delete);
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

    private static IReadOnlyList<NormalizedDeploymentFile> NormalizeRenderedFiles(
        string root,
        IReadOnlyList<SquadDeploymentFile> renderedFiles)
    {
        var normalized = new List<NormalizedDeploymentFile>(renderedFiles.Count);
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rendered in renderedFiles)
        {
            ArgumentNullException.ThrowIfNull(rendered);
            var relativePath = SquadPathPolicy.NormalizeRelativePath(rendered.RelativePath);
            var portableIdentity = SquadPathPolicy.GetPortableIdentity(relativePath);
            if (!string.Equals(portableIdentity, relativePath, StringComparison.Ordinal))
            {
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad output path '{rendered.RelativePath}' is not a portable " +
                    "canonical path because a segment ends in a dot or space.");
            }

            if (seen.TryGetValue(portableIdentity, out _))
            {
                throw new SquadDeploymentConflictException(
                    $"Rendered Squad output path '{rendered.RelativePath}' has a portable " +
                    "alias collision. Fix the upstream render before deploying it.");
            }

            seen.Add(portableIdentity, relativePath);
            var normalizedFile = rendered with { RelativePath = relativePath };
            normalized.Add(new NormalizedDeploymentFile(
                normalizedFile,
                SquadPathPolicy.ResolveFile(root, relativePath)));
        }

        return normalized;
    }

    private static Dictionary<string, SquadOwnedFile> ReceiptFilesByPath(
        string root,
        SquadReceipt receipt)
    {
        var files = new Dictionary<string, SquadOwnedFile>(StringComparer.Ordinal);
        var portablePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var owned in receipt.Files)
        {
            ArgumentNullException.ThrowIfNull(owned);
            var normalizedPath = SquadPathPolicy.NormalizeRelativePath(owned.RelativePath);
            _ = SquadPathPolicy.ResolveFile(root, normalizedPath);
            if (!string.Equals(normalizedPath, owned.RelativePath, StringComparison.Ordinal))
            {
                throw new SquadPathContainmentException(
                    $"Receipt path '{owned.RelativePath}' is not a normalized portable path.");
            }

            var portableIdentity = SquadPathPolicy.GetPortableIdentity(normalizedPath);
            if (!string.Equals(portableIdentity, normalizedPath, StringComparison.Ordinal))
            {
                throw new SquadDeploymentConflictException(
                    $"Squad receipt path '{owned.RelativePath}' is not a portable canonical path.");
            }

            if (!portablePaths.Add(portableIdentity))
            {
                throw new SquadDeploymentConflictException(
                    $"Squad receipt path '{owned.RelativePath}' has a portable alias collision.");
            }

            if (!files.TryAdd(normalizedPath, owned))
            {
                throw new SquadDeploymentConflictException(
                    $"Squad receipt contains duplicate path '{normalizedPath}'.");
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

internal enum SquadFileMutationKind
{
    Write,
    Delete
}

internal sealed record SquadFileMutation(
    string RelativePath,
    SquadFileMutationKind Kind,
    byte[]? Content)
{
    public static SquadFileMutation Write(
        string relativePath,
        ReadOnlyMemory<byte> content) =>
        new(relativePath, SquadFileMutationKind.Write, content.ToArray());

    public static SquadFileMutation Delete(string relativePath) =>
        new(relativePath, SquadFileMutationKind.Delete, null);
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
    SquadFilePreconditionKind Kind,
    string? Sha256)
{
    public static SquadFilePrecondition Missing(string relativePath) =>
        new(relativePath, SquadFilePreconditionKind.Missing, null);

    public static SquadFilePrecondition Exact(string relativePath, string sha256) =>
        new(relativePath, SquadFilePreconditionKind.ExactFile, sha256);
}
