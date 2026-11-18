namespace KyberWeave.Contracts.Paths;

/// <summary>
/// The path seam: how the product turns a caller's spelling into a full path, decides whether two spellings name
/// the same entry, decides whether one path lies inside another, and asks the file system for the canonical
/// spelling of a path whose segments may be linked or spelled in another case.
/// </summary>
/// <remarks>
/// <para>
/// No member reads the process working directory. Every member except <see cref="GetFullPath" /> requires fully
/// qualified paths and raises <see cref="ArgumentException" /> for a relative one, because a relative path can
/// only be resolved against a base the caller names — and a base the caller does not name is a test that depends
/// on where it runs.
/// </para>
/// <para>
/// The answers follow the file system that holds the path rather than a rule for the operating system as a whole:
/// case folding matches the volume's rule, and a symbolic link is honoured, so a path cannot reach outside the
/// directory it is checked against by naming a link.
/// </para>
/// </remarks>
public interface IPathResolver
{
    /// <summary>Turns a path into a full path, resolving a relative one against an explicit base directory.</summary>
    /// <param name="path">The path, relative or fully qualified.</param>
    /// <param name="baseDirectory">
    /// The fully qualified directory a relative <paramref name="path" /> is resolved against. It is ignored when
    /// <paramref name="path" /> is already fully qualified, and the process working directory is never used in its
    /// place.
    /// </param>
    /// <returns>The full path, with <c>.</c> and <c>..</c> resolved and a trailing separator removed.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path" /> is empty, or <paramref name="baseDirectory" /> is empty or not fully qualified.
    /// </exception>
    string GetFullPath(string path, string baseDirectory);

    /// <summary>Decides whether two spellings name the same path.</summary>
    /// <param name="first">The first fully qualified path.</param>
    /// <param name="second">The second fully qualified path.</param>
    /// <returns>
    /// True when the two spellings agree once case folding and a trailing separator are ignored. A symbolic link is
    /// not followed: use <see cref="Canonicalize" /> to compare what two paths resolve to.
    /// </returns>
    /// <exception cref="ArgumentException">Either path is empty or not fully qualified.</exception>
    bool AreSame(string first, string second);

    /// <summary>Decides whether one path lies strictly inside a directory, following symbolic links.</summary>
    /// <param name="root">The fully qualified directory the candidate must stay inside.</param>
    /// <param name="candidate">The fully qualified path being checked.</param>
    /// <returns>
    /// True when the candidate is physically inside the root. A directory is not inside itself, so a root passed as
    /// its own candidate answers false; every path but a filesystem root is inside that root.
    /// </returns>
    /// <exception cref="ArgumentException">Either path is empty or not fully qualified.</exception>
    bool IsWithin(string root, string candidate);

    /// <summary>Gets the canonical spelling of a path, following every symbolic link along it.</summary>
    /// <param name="path">
    /// The fully qualified path. Its trailing segments need not exist: the existing prefix is canonicalized and the
    /// rest keeps the caller's spelling.
    /// </param>
    /// <returns>The canonical spelling, normalized to Unicode form C.</returns>
    /// <exception cref="ArgumentException">The path is empty or not fully qualified.</exception>
    string Canonicalize(string path);

    /// <summary>Gets the canonical spelling of an existing directory a deployment or a document tree is bound to.</summary>
    /// <param name="targetRoot">The fully qualified directory.</param>
    /// <returns>The canonical spelling of the directory, with every symbolic link along it followed.</returns>
    /// <exception cref="ArgumentException">The path is empty or not fully qualified.</exception>
    /// <exception cref="DirectoryNotFoundException">
    /// Nothing is at the path, the path names a file, or the directory changed while its physical path was resolved.
    /// </exception>
    string ResolvePhysicalRoot(string targetRoot);
}
