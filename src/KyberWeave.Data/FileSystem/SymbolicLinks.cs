namespace KyberWeave.Data.FileSystem;

/// <summary>
/// Follows a symbolic link to the file it finally names, so a length or timestamp probe answers for that file
/// rather than for the link. On Linux and macOS a link's own length is the length of its target path.
/// </summary>
/// <remarks>
/// Duplicates <c>AtomicFile.ResolveLinks</c> in KyberWeave.Kernel, which is private there; consolidating them is
/// tracked in https://github.com/dpalfery/kyber-weave/issues/729.
/// </remarks>
internal static class SymbolicLinks
{
    /// <summary>
    /// Returns the final target of a link at <paramref name="path"/>, or <paramref name="path"/> unchanged when
    /// it is not a link or does not exist. A broken link returns its missing target, so the caller reports the
    /// file as missing.
    /// </summary>
    /// <param name="path">The path to resolve.</param>
    /// <returns>The path of the file the caller should stat.</returns>
    /// <exception cref="IOException">The link chain loops or is too deep.</exception>
    public static string FinalTarget(string path)
    {
        // LinkTarget is null for a regular file and for a path that does not exist; ResolveLinkTarget throws on the latter.
        FileInfo file = new(path);
        if (file.LinkTarget is null)
            return path;

        FileSystemInfo? target = file.ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? path : Path.GetFullPath(target.FullName);
    }
}
