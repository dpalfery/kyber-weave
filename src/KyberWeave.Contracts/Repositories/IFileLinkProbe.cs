namespace KyberWeave.Contracts.Repositories;

/// <summary>Finds symbolic links on a path, so a path check cannot be fooled into reading outside the tree it allows.</summary>
public interface IFileLinkProbe
{
    /// <summary>Whether any existing entry between <paramref name="root"/> and <paramref name="fullPath"/> is a symbolic link.</summary>
    /// <param name="root">The directory the path is checked from, ending in <c>/</c>.</param>
    /// <param name="fullPath">The normalized path beneath <paramref name="root"/>.</param>
    bool HasLinkBelow(string root, string fullPath);
}
