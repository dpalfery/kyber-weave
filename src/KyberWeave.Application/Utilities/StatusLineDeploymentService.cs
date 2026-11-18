using System.Security.Cryptography;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Domain.StatusLine;

namespace KyberWeave.Application.Utilities;

/// <summary>Plans and applies the status-line deployment over <see cref="IStatusLineFiles"/>.</summary>
/// <remarks>
/// Every mutation is confined to the harness staging root and cross-checked against that harness's settings paths and
/// auto-load directories, because Kyber Utilities never writes a harness settings file (A4, C2) and never activates a
/// status line on the user's behalf (A3, C1). Building a plan is the dry run: it reads the receipt and the target
/// paths and writes nothing, not even a directory.
/// </remarks>
public sealed class StatusLineDeploymentService(
    IStatusLineFiles files,
    IFileSystemPathSemantics pathSemantics) : IStatusLineDeploymentService
{
    /// <summary>Preflights a deployment without changing the filesystem.</summary>
    /// <param name="target">The harness to deploy the artifacts for.</param>
    /// <param name="roots">The resolved per-user roots the deployment is confined to.</param>
    /// <param name="files">
    /// The complete set of artifacts to own for this harness. A file already recorded in the receipt
    /// but absent from this set stays owned and is left untouched: a deploy never silently disowns a
    /// file it wrote earlier, because that would put it beyond <see cref="CreateRemove"/>'s reach.
    /// </param>
    /// <exception cref="StatusLineDeploymentConflictException">
    /// A planned path is occupied by an unmanaged file or directory, is a harness settings path, is
    /// inside an auto-load directory, or appears twice in <paramref name="files"/>.
    /// </exception>
    /// <remarks>
    /// An artifact whose relative path is not portable — rooted, containing <c>..</c>, or using a
    /// name Windows would alias — is a defect in the artifact set rather than an operator condition,
    /// so <c>SquadPathPolicy</c>'s own path exception propagates unchanged instead of being
    /// relabelled as a conflict the operator is expected to resolve.
    /// </remarks>
    public StatusLineDeploymentPlan CreateDeploy(
        StatusLineTarget target,
        StatusLineTargetRoots roots,
        IReadOnlyList<StatusLineDeploymentFile> files)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(files);

        string stagingRoot = roots.ResolveStagingRoot(target);
        EnsureOutsideReservedLocations(target, roots, stagingRoot, "The staging root");
        if (files.Count == 0)
        {
            throw new ArgumentException(
                "A status-line deployment must contain at least one file. An empty set would have to " +
                "mean either 'own nothing' or 'disown everything', and neither is a deploy.",
                nameof(files));
        }

        StatusLineReceiptFormat format = new(roots, target, pathSemantics);
        StatusLineReceipt? existing = Read(format);
        Dictionary<string, StatusLineOwnedFile> ownedByPath = OwnedByPath(existing);

        List<StatusLinePendingChange> changes = [];
        List<StatusLineOwnedFile> owned = [];
        HashSet<string> plannedPaths = new(StringComparer.Ordinal);
        foreach (StatusLineDeploymentFile file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            string relativePath = SquadPathPolicy.NormalizeRelativePath(file.RelativePath);
            if (!plannedPaths.Add(relativePath))
            {
                throw new StatusLineDeploymentConflictException(
                    $"The status-line deployment for '{target}' lists '{relativePath}' more than once.");
            }

            string physicalPath = SquadPathPolicy.ResolveFile(stagingRoot, relativePath, pathSemantics);
            EnsureNoSymlinks(roots.ResolveHarnessRoot(target), physicalPath);
            EnsureOutsideReservedLocations(target, roots, physicalPath, $"'{relativePath}'");
            string digest = Digest(file.Content.Span);
            RefuseUnmanagedOccupant(ownedByPath, relativePath, physicalPath);
            changes.Add(new StatusLinePendingChange(
                relativePath,
                physicalPath,
                StatusLineFileChangeKind.Write,
                file.Content));
            owned.Add(new StatusLineOwnedFile(relativePath, digest, target));
        }

        foreach (StatusLineOwnedFile previous in existing?.Files ?? [])
        {
            ArgumentNullException.ThrowIfNull(previous);
            if (!plannedPaths.Contains(previous.RelativePath))
                owned.Add(previous);
        }

        return new StatusLineDeploymentPlan(
            target,
            stagingRoot,
            format.ReceiptPath,
            new StatusLineReceipt(owned),
            changes);
    }

    /// <summary>Preflights an ownership-aware removal without changing the filesystem.</summary>
    /// <remarks>
    /// The receipt decides everything. A file is deleted only when the receipt owns it <em>and</em>
    /// its bytes are still the ones the deploy wrote; a file the user edited, and every file the
    /// receipt does not name, is left exactly as it is. An edited file stays in the receipt, so
    /// <c>status</c> can still report the drift and a later removal can still see it.
    /// </remarks>
    /// <exception cref="StatusLineDeploymentConflictException">
    /// The receipt records a path that does not resolve beneath the staging root — which is what a
    /// hand-edited receipt looks like, and what would otherwise let a removal delete an arbitrary
    /// file.
    /// </exception>
    public StatusLineDeploymentPlan CreateRemove(StatusLineTarget target, StatusLineTargetRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        string stagingRoot = roots.ResolveStagingRoot(target);
        StatusLineReceiptFormat format = new(roots, target, pathSemantics);
        StatusLineReceipt? existing = Read(format);
        List<StatusLinePendingChange> changes = [];
        List<StatusLineOwnedFile> retained = [];
        foreach (StatusLineOwnedFile owned in existing?.Files ?? [])
        {
            ArgumentNullException.ThrowIfNull(owned);
            string physicalPath = ResolveOwnedPhysicalPath(target, roots, stagingRoot, owned.RelativePath);
            if (!files.FileExists(physicalPath) && !files.DirectoryExists(physicalPath))
                continue;

            if (files.FileExists(physicalPath) && IsUnmodified(physicalPath, owned.Sha256))
            {
                changes.Add(new StatusLinePendingChange(
                    owned.RelativePath,
                    physicalPath,
                    StatusLineFileChangeKind.Delete,
                    default));
                continue;
            }

            retained.Add(owned);
        }

        return new StatusLineDeploymentPlan(
            target,
            stagingRoot,
            format.ReceiptPath,
            new StatusLineReceipt(retained),
            changes);
    }

    /// <inheritdoc />
    public StatusLineReceipt? ReadReceipt(StatusLineTarget target, StatusLineTargetRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return Read(new StatusLineReceiptFormat(roots, target, pathSemantics));
    }

    /// <summary>Applies the plan, writing or deleting exactly the paths it planned.</summary>
    /// <remarks>
    /// The receipt is settled last, so a failure part-way through leaves the previous ownership
    /// record intact rather than claiming files that were never written. An empty resulting receipt
    /// deletes the receipt file instead of writing one that owns nothing.
    /// </remarks>
    public void Apply(StatusLineDeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        foreach (StatusLinePendingChange change in plan.PendingChanges)
        {
            switch (change.Kind)
            {
                case StatusLineFileChangeKind.Write:
                    files.CreateDirectory(RequireParentDirectory(change.PhysicalPath));
                    files.WriteAllBytes(change.PhysicalPath, change.Content.Span);
                    break;
                case StatusLineFileChangeKind.Delete:
                    files.DeleteFile(change.PhysicalPath);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unknown status-line file change kind '{change.Kind}'.");
            }
        }

        RemoveEmptyDirectoriesLeftByDeletions(plan);

        if (plan.Receipt.Files.Count == 0)
            files.DeleteFile(plan.ReceiptPath);
        else
            files.WriteTextAtomically(plan.ReceiptPath,
                StatusLineReceiptFormat.Serialize(plan.Receipt, plan.Target, plan.StagingRoot));
    }

    /// <summary>
    /// Refuses to write over anything at a target path that Kyber does not own, or owns but did not
    /// write.
    /// </summary>
    /// <remarks>
    /// The only occupant a deploy may write over is the file a previous deploy wrote and left
    /// unmodified, which makes a repeated deploy idempotent. That is decided by hashing the bytes
    /// <em>on disk</em> against the receipt's recorded digest: the incoming artifact's digest is the
    /// wrong side of the comparison, because an unchanged artifact set carries the digest the receipt
    /// already holds and so would accept any occupant at all — including one the operator has since
    /// edited. A directory, a hand-written file, or a locally-edited Kyber-owned file is refused
    /// rather than replaced: the operator's bytes are never the ones discarded, and the refusal names
    /// the path so the remedy is obvious.
    /// </remarks>
    private void RefuseUnmanagedOccupant(
        Dictionary<string, StatusLineOwnedFile> ownedByPath,
        string relativePath,
        string physicalPath)
    {
        if (files.DirectoryExists(physicalPath))
        {
            throw new StatusLineDeploymentConflictException(
                $"Unmanaged directory '{physicalPath}' occupies the status-line path " +
                $"'{relativePath}'. Move it aside, then deploy again.");
        }

        if (!files.FileExists(physicalPath))
            return;

        if (ownedByPath.TryGetValue(relativePath, out StatusLineOwnedFile? owned) &&
            IsUnmodified(physicalPath, owned.Sha256))
        {
            return;
        }

        throw new StatusLineDeploymentConflictException(
            $"Unmanaged file '{relativePath}' already exists at '{physicalPath}' and Kyber Utilities " +
            "will not overwrite it. Move the file aside, then deploy again.");
    }

    /// <summary>
    /// Refuses a location the harness loads by itself, or a harness settings file.
    /// </summary>
    /// <remarks>
    /// <see cref="StatusLineTargetRoots"/> already places the staging root outside both, so this is
    /// the second line of defence rather than the only one: the invariant that protects A3 and C2 is
    /// asserted at the point of mutation, where a future change to a root rule cannot quietly break
    /// it.
    /// </remarks>
    private void EnsureOutsideReservedLocations(
        StatusLineTarget target,
        StatusLineTargetRoots roots,
        string physicalPath,
        string subject)
    {
        foreach (string autoLoadDirectory in roots.ResolveAutoLoadDirectories(target))
        {
            if (IsSameOrWithin(autoLoadDirectory, physicalPath))
            {
                throw new StatusLineDeploymentConflictException(
                    $"{subject} resolves to '{physicalPath}', inside the {target} auto-load directory " +
                    $"'{autoLoadDirectory}'. A file there would be active without the user applying " +
                    "anything, which Kyber Utilities never does.");
            }
        }

        foreach (string settingsPath in roots.ResolveSettingsPaths(target))
        {
            if (IsSameOrWithin(settingsPath, physicalPath))
            {
                throw new StatusLineDeploymentConflictException(
                    $"{subject} resolves to '{physicalPath}', which is the {target} settings path " +
                    $"'{settingsPath}'. Kyber Utilities never creates, edits, or merges a settings file.");
            }
        }
    }

    /// <summary>
    /// Removes the directories a deletion emptied, stopping at the staging root.
    /// </summary>
    /// <remarks>
    /// The staging root is Kyber's, so leaving <c>kyber/statusline/statusline/</c> behind after
    /// removing the only file in it would leave the operator a directory they cannot tell is Kyber's,
    /// and leave the next <c>status</c> pointing at a location that holds nothing. Only empty
    /// directories are removed, and the walk never leaves the staging root or removes it, so a file
    /// the user placed beside Kyber's files keeps its directory.
    /// </remarks>
    private void RemoveEmptyDirectoriesLeftByDeletions(StatusLineDeploymentPlan plan)
    {
        HashSet<string> candidates = new(StringComparer.Ordinal);
        foreach (StatusLinePendingChange change in plan.PendingChanges)
        {
            if (change.Kind == StatusLineFileChangeKind.Delete)
                candidates.Add(RequireParentDirectory(change.PhysicalPath));
        }

        foreach (string candidate in candidates)
        {
            string directory = candidate;
            while (files.DirectoryExists(directory) &&
                   !pathSemantics.AreSame(plan.StagingRoot, directory) &&
                   pathSemantics.IsWithin(plan.StagingRoot, directory) &&
                   files.IsDirectoryEmpty(directory))
            {
                files.DeleteDirectory(directory);
                string? parent = Path.GetDirectoryName(directory);
                if (parent is null)
                    break;

                directory = parent;
            }
        }
    }

    /// <summary>Resolves a receipt-recorded path, refusing one that would escape the staging root or traverse symlinks.</summary>
    private string ResolveOwnedPhysicalPath(
        StatusLineTarget target,
        StatusLineTargetRoots roots,
        string stagingRoot,
        string relativePath)
    {
        string physicalPath;
        try
        {
            physicalPath = SquadPathPolicy.ResolveFile(stagingRoot, relativePath, pathSemantics);
        }
        catch (Exception exception) when (
            exception is SquadPathContainmentException or SquadDeploymentConflictException)
        {
            throw new StatusLineDeploymentConflictException(
                $"The status-line receipt for '{target}' records '{relativePath}', which is not a path " +
                $"beneath the staging root '{stagingRoot}'. Delete the receipt and deploy again.",
                exception);
        }

        EnsureNoSymlinks(roots.ResolveHarnessRoot(target), physicalPath);
        return physicalPath;
    }

    /// <summary>
    /// Traverses the path components from the harness root down to <paramref name="physicalPath"/>,
    /// rejecting any symbolic link or reparse point.
    /// </summary>
    private void EnsureNoSymlinks(string harnessRoot, string physicalPath)
    {
        string relative = Path.GetRelativePath(harnessRoot, physicalPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return;
        }

        string current = harnessRoot;
        string[] segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (files.IsLinkOrReparsePoint(current))
            {
                throw new StatusLineDeploymentConflictException(
                    $"The status-line path '{physicalPath}' traverses symbolic link or reparse point '{current}'. " +
                    "Kyber Utilities refuses to deploy or remove through symbolic links.");
            }
        }
    }

    /// <summary>Reads the receipt a format describes, or returns null when this target owns nothing.</summary>
    private StatusLineReceipt? Read(StatusLineReceiptFormat format) =>
        files.ReadTextOrNull(format.ReceiptPath) is { } json ? format.Deserialize(json) : null;

    private static Dictionary<string, StatusLineOwnedFile> OwnedByPath(StatusLineReceipt? receipt)
    {
        Dictionary<string, StatusLineOwnedFile> owned = new(StringComparer.Ordinal);
        foreach (StatusLineOwnedFile file in receipt?.Files ?? [])
        {
            ArgumentNullException.ThrowIfNull(file);
            if (!owned.TryAdd(file.RelativePath, file))
            {
                throw new StatusLineDeploymentConflictException(
                    $"The status-line receipt records '{file.RelativePath}' more than once.");
            }
        }

        return owned;
    }

    /// <summary>
    /// Whether a file still holds the bytes the receipt recorded for it.
    /// </summary>
    /// <remarks>
    /// An unreadable file answers <see langword="false"/>, so the caller keeps it: a removal that
    /// cannot prove it wrote a file must not delete it, and a deploy that cannot prove it owns the
    /// occupant must not replace it. Both are the safe direction, and both are visible in the plan
    /// the operator is shown.
    /// </remarks>
    private bool IsUnmodified(string physicalPath, string sha256)
    {
        try
        {
            return string.Equals(
                Digest(files.ReadAllBytes(physicalPath)),
                sha256,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsSameOrWithin(string root, string candidate)
    {
        return pathSemantics.AreSame(root, candidate) ||
               pathSemantics.IsWithin(root, candidate);
    }

    private static string RequireParentDirectory(string path)
    {
        return Path.GetDirectoryName(path) ??
               throw new InvalidOperationException($"Status-line path '{path}' has no parent directory.");
    }

    private static string Digest(ReadOnlySpan<byte> content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }
}
