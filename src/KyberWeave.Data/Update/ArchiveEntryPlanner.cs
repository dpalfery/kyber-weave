using KyberWeave.Contracts.Paths;

namespace KyberWeave.Cli.Update;

/// <summary>Decides where a release archive's entry may be written, and refuses one that would land outside the destination.</summary>
internal sealed class ArchiveEntryPlanner(IPathResolver paths)
{
    /// <summary>Resolves an entry's name against the extraction directory.</summary>
    /// <param name="destinationRoot">The fully qualified directory the archive extracts into.</param>
    /// <param name="entryName">The entry's name as the archive spells it.</param>
    /// <returns>The full path the entry is written to; the destination itself for an entry that names it.</returns>
    /// <exception cref="SelfUpdateException">
    /// The name is empty, absolute, has a parent-directory segment, or resolves outside the destination.
    /// </exception>
    public string Resolve(string destinationRoot, string entryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationRoot);
        if (string.IsNullOrEmpty(entryName) || entryName.Contains('\0', StringComparison.Ordinal))
            throw Refused(entryName, "its name is empty or holds a null character");

        string name = entryName.Replace('\\', '/');
        if (name.StartsWith('/') || Path.IsPathRooted(entryName) || (name.Length > 1 && name[1] == ':'))
            throw Refused(entryName, "its name is an absolute path");

        if (name.Split('/').Contains(".."))
            throw Refused(entryName, "its name climbs out of the extraction directory");

        string target = paths.GetFullPath(name, destinationRoot);
        if (!paths.AreSame(destinationRoot, target) && !paths.IsWithin(destinationRoot, target))
            throw Refused(entryName, "it resolves outside the extraction directory");

        return target;
    }

    private static SelfUpdateException Refused(string entryName, string reason) =>
        new($"the archive entry '{entryName}' was refused: {reason}.");
}
