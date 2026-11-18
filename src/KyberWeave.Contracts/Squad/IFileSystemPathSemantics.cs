namespace KyberWeave.Core.Squad.Deployment;

/// <summary>
///     The path identity and containment checks a deployment needs, expressed through the spelling,
///     aliases and physical location the containing filesystem reports rather than an operating-system-wide
///     case rule.
/// </summary>
/// <remarks>
///     The rules are pure once the filesystem has answered, so the interface is the seam: a host passes the
///     real filesystem, and a test passes a double to pin a case rule or an alias the machine does not have.
/// </remarks>
public interface IFileSystemPathSemantics
{
    /// <summary>Whether two spellings name the same path.</summary>
    /// <param name="first">The first path.</param>
    /// <param name="second">The second path.</param>
    /// <returns>True when the filesystem treats both as the same entry.</returns>
    bool AreSame(string first, string second);

    /// <summary>Whether <paramref name="candidate" /> lies beneath <paramref name="root" />, following aliases.</summary>
    /// <param name="root">The directory the candidate must stay under.</param>
    /// <param name="candidate">The path being checked.</param>
    /// <returns>True when the candidate is physically inside the root.</returns>
    bool IsWithin(string root, string candidate);

    /// <summary>The canonical physical spelling of a path, following every symbolic link along it.</summary>
    /// <param name="path">The path to canonicalize. Its trailing segments need not exist.</param>
    /// <returns>The canonical spelling, normalized to Unicode form C.</returns>
    string Canonicalize(string path);

    /// <summary>The canonical physical path of an existing deployment root.</summary>
    /// <param name="targetRoot">The directory a deployment is bound to.</param>
    /// <returns>The canonical spelling of the directory.</returns>
    /// <exception cref="SquadDeploymentConflictException">
    ///     The root is not an existing directory, or it changed while its physical path was resolved.
    /// </exception>
    string ResolvePhysicalRoot(string targetRoot);
}
