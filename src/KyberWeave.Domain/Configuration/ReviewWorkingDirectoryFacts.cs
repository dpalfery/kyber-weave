namespace KyberWeave.Core.Configuration;

/// <summary>What the file system says about a gate's <c>working-directory</c>, which the review configuration parser checks but cannot ask.</summary>
/// <param name="DirectoryExists">Whether a directory exists, given its full path.</param>
/// <param name="LeadsOutsideThroughSymlink">Whether a repository-relative path passes through a symbolic link that leads outside the root, given the root and the path.</param>
/// <param name="FullPath">The full path of a spelling, with a relative one resolved against the current directory.</param>
public sealed record ReviewWorkingDirectoryFacts(
    Func<string, bool> DirectoryExists,
    Func<string, string, bool> LeadsOutsideThroughSymlink,
    Func<string, string> FullPath);
