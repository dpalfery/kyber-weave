using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads bounded windows of repository files and resolves paths that must stay inside the repository root.</summary>
public interface IFileTextFacts
{
    /// <summary>
    /// Reads the window around <paramref name="line"/> of <paramref name="path"/>.
    /// </summary>
    /// <param name="repoRoot">The repository root; the path must resolve under it.</param>
    /// <param name="path">A path relative to the root, or an absolute path under it.</param>
    /// <param name="line">The 1-based line the comment cites.</param>
    /// <returns>The excerpt, or null when the path leaves the root, the file is absent, or the line is past the end.</returns>
    FileExcerpt? ReadExcerpt(string repoRoot, string path, int line);

    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="repoRoot"/> and returns the full path only when it stays
    /// under the root.
    /// </summary>
    /// <returns>The full path, or null when the path would leave the root.</returns>
    string? ResolveWithinRoot(string repoRoot, string path);

    /// <summary>
    /// Resolves <paramref name="path"/> to an existing regular file under <paramref name="repoRoot"/>. The lexical
    /// check alone would let a symbolic link inside the repository point outside it, so every component below the
    /// root is checked for a link before the file is tested for existence.
    /// </summary>
    /// <returns>The full path, or null when the path leaves the root, crosses a link, or names no file.</returns>
    string? ResolveContainedFile(string repoRoot, string path);
}
