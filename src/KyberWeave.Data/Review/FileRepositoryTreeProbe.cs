using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Probes a repository root and the symbolic links under it on disk.</summary>
public sealed class FileRepositoryTreeProbe : IRepositoryTreeProbe
{
    /// <inheritdoc />
    public RepositoryRootAccess ProbeRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root))
                return new RepositoryRootAccess(false);

            // Directory.Exists returns false without throwing on permission-denied roots;
            // enumerate to detect accessibility issues.
            _ = Directory.EnumerateFileSystemEntries(root).FirstOrDefault();
            return RepositoryRootAccess.Accessible;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return new RepositoryRootAccess(true, ex.Message);
        }
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path) => Directory.Exists(path);

    /// <inheritdoc />
    /// <remarks>
    /// Each prefix of the path is checked as its own entry, outermost first, so a link in the middle of the path is
    /// found even when the final file is an ordinary one. The walk splits on the separators this host reads, so it
    /// inspects the same entries the gate would open: a backslash is a separator on Windows and a name character
    /// elsewhere. Only the link's target is read; nothing behind it is opened.
    /// </remarks>
    public bool LeadsOutsideThroughSymlink(string root, string path)
    {
        string full = Path.GetFullPath(root);
        string current = full;
        string[] segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not { } linkTarget)
                continue;

            try
            {
                FileSystemInfo? resolved = entry.ResolveLinkTarget(returnFinalTarget: true);
                string target = resolved?.FullName
                    ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, linkTarget));
                if (!TaskGatePlanning.IsWithin(full, target))
                    return true;
            }
            catch (Exception exception) when (FileFailure.Is(exception))
            {
                return true;
            }
        }

        return false;
    }
}
