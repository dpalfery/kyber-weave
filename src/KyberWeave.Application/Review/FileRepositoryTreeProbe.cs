using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.FileSystem;
using KyberWeave.Core.Review;

namespace KyberWeave.Data.Review;

/// <summary>Probes a repository root and the symbolic links under it through the file-system seam.</summary>
/// <param name="files">Answers every existence, listing and link question.</param>
/// <param name="paths">Resolves a root the caller spelled relatively.</param>
/// <param name="environment">Names the working directory a relative root resolves against.</param>
public sealed class FileRepositoryTreeProbe(
    IFileSystem files,
    IPathResolver paths,
    IProcessEnvironment environment) : IRepositoryTreeProbe
{
    /// <inheritdoc />
    public RepositoryRootAccess ProbeRoot(string root)
    {
        try
        {
            if (!files.DirectoryExists(root))
                return new RepositoryRootAccess(false);

            // DirectoryExists returns false without throwing on permission-denied roots;
            // enumerate to detect accessibility issues.
            _ = files.EnumerateEntries(root).FirstOrDefault();
            return RepositoryRootAccess.Accessible;
        }
        catch (Exception ex) when (FileFailure.Is(ex))
        {
            return new RepositoryRootAccess(true, ex.Message);
        }
    }

    /// <inheritdoc />
    public bool DirectoryExists(string path) => files.DirectoryExists(path);

    /// <inheritdoc />
    /// <remarks>
    /// Each prefix of the path is checked as its own entry, outermost first, so a link in the middle of the path is
    /// found even when the final file is an ordinary one. The walk splits on the separators this host reads, so it
    /// inspects the same entries the gate would open: a backslash is a separator on Windows and a name character
    /// elsewhere. Only the link's target is read; nothing behind it is opened. A link whose final target does not
    /// exist cannot be resolved and is treated as escaping.
    /// </remarks>
    public bool LeadsOutsideThroughSymlink(string root, string path)
    {
        string full = paths.GetFullPath(root, environment.CurrentDirectory);
        string current = full;
        string[] segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string segment in segments)
        {
            current = Path.Combine(current, segment);
            if (files.GetLinkTarget(current) is null)
                continue;

            try
            {
                // The seam answers null for a link that cannot be followed to an existing final target (a loop or a
                // missing target), and such a link is treated as escaping.
                string? target = files.GetFinalLinkTarget(current);
                if (target is null || !TaskGatePlanning.IsWithin(full, target))
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
