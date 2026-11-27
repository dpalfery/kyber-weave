using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.FileSystem;

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

    /// <summary>The project store path relative to the repository root, with forward slashes.</summary>
    public const string ProjectRelativeDirectory = "artifacts/arbiter";

    /// <summary>The dispatch claims folder inside an arbiter state directory.</summary>
    /// <param name="arbiterDirectory">An arbiter state directory, such as <see cref="ForProject" />'s answer.</param>
    /// <returns>The absolute claims directory path.</returns>
    public static string ClaimsDirectory(string arbiterDirectory) => Path.Combine(arbiterDirectory, "claims");

    /// <summary>The model targets table one harness is recorded in.</summary>
    /// <param name="arbiterDirectory">An arbiter state directory, such as <see cref="ForProject" />'s answer.</param>
    /// <param name="harness">The harness token.</param>
    /// <returns>The absolute file path.</returns>
    public static string ModelTargetsFile(string arbiterDirectory, string harness) =>
        Path.Combine(arbiterDirectory, $"model-targets.{harness}.json");

    /// <summary>The project-scope model targets table path relative to the repository root, with forward slashes.</summary>
    /// <param name="harness">The harness token.</param>
    /// <returns>The relative file path.</returns>
    public static string ProjectRelativeModelTargetsPath(string harness) =>
        $"{ProjectRelativeDirectory}/model-targets.{harness}.json";

    /// <summary>The global-scope model targets table path relative to the harness's global root, with forward slashes.</summary>
    /// <param name="harness">The harness token.</param>
    /// <returns>The relative file path.</returns>
    public static string GlobalRelativeModelTargetsPath(string harness) =>
        $"{UserStatePaths.DirectoryName}/arbiter/model-targets.{harness}.json";

    /// <summary>
    /// The user-level store of one repository: <c>.kyber-weave/arbiter/projects/&lt;key&gt;</c> under the home directory.
    /// </summary>
    /// <param name="homeDirectory">The operator's home directory.</param>
    /// <param name="repoRoot">The repository root, relative or fully qualified.</param>
    /// <param name="baseDirectory">The fully qualified directory a relative root is resolved against.</param>
    /// <returns>The arbiter state directory the user-level hook records in.</returns>
    public static string ForGlobal(string homeDirectory, string repoRoot, string baseDirectory) =>
        Path.Combine(UserStatePaths.ArbiterProjectsDirectory(homeDirectory), ProjectRootKey(repoRoot, baseDirectory));

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
