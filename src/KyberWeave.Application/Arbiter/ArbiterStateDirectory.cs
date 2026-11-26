using System.Security.Cryptography;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Core.Arbiter;

/// <summary>
/// Where the Arbiter keeps one repository's ledger and decision log. This is the only place that choice is made: the
/// hook engine records through it and the repository locator reads through it, so neither can name a directory the
/// other does not.
/// </summary>
/// <remarks>
/// <para>
/// A project hook records under the repository, in <c>artifacts/arbiter</c>. The user-level hook keeps its state under
/// the operator's home instead, one directory per project named by <see cref="ProjectRootKey" />, so it never writes a
/// ledger into a repository that has no project install.
/// </para>
/// <para>
/// The key hashes the root's full path as it is spelled, with no symbolic link resolved: it has to stay, byte for byte,
/// the key the stores already on disk are named by.
/// </para>
/// </remarks>
public static class ArbiterStateDirectory
{
    /// <summary>The project's own store: <c>artifacts/arbiter</c> under the repository root.</summary>
    /// <param name="repoRoot">The repository root.</param>
    /// <returns>The arbiter state directory a project hook records in.</returns>
    public static string ForProject(string repoRoot) => Path.Combine(repoRoot, "artifacts", "arbiter");

    /// <summary>
    /// The user-level store of one repository: <c>.kyber-weave/arbiter/projects/&lt;key&gt;</c> under the home directory.
    /// </summary>
    /// <param name="homeDirectory">The operator's home directory.</param>
    /// <param name="repoRoot">The repository root, relative or fully qualified.</param>
    /// <param name="baseDirectory">The fully qualified directory a relative root is resolved against.</param>
    /// <returns>The arbiter state directory the user-level hook records in.</returns>
    public static string ForGlobal(string homeDirectory, string repoRoot, string baseDirectory) =>
        Path.Combine(homeDirectory, ".kyber-weave", "arbiter", "projects", ProjectRootKey(repoRoot, baseDirectory));

    /// <summary>
    /// Whether <paramref name="directory" /> holds a ledger or a decision log, the two files a hook records through.
    /// </summary>
    /// <param name="directory">An arbiter state directory.</param>
    /// <param name="files">Probes the file system.</param>
    /// <returns>
    /// True when the ledger or the decision log is there, readable or not. False for a directory that holds neither
    /// and for one that is not there, neither of which is a store a hook has recorded in.
    /// </returns>
    public static bool HasLedgerOrDecisionLog(string directory, IFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return files.FileExists(Path.Combine(directory, InFlightLedger.FileName))
            || files.FileExists(Path.Combine(directory, DecisionLog.FileName));
    }

    /// <summary>The per-project key a user-level store directory is named by: the hash of the root's full path.</summary>
    /// <param name="projectRoot">The project root, relative or fully qualified.</param>
    /// <param name="baseDirectory">The fully qualified directory a relative root is resolved against.</param>
    /// <returns>The lower-case hex SHA-256 of the root's full path.</returns>
    public static string ProjectRootKey(string projectRoot, string baseDirectory)
    {
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot, baseDirectory));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash);
    }
}
