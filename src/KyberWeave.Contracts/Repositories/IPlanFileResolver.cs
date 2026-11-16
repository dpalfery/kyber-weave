

namespace KyberWeave.Contracts.Repositories;

/// <summary>Resolves a plan file named by a dispatch to a path inside the repository, refusing anything that escapes it.</summary>
public interface IPlanFileResolver
{
    /// <summary>
    /// Resolves <paramref name="planFile"/> inside <paramref name="repositoryRoot"/>.
    /// </summary>
    /// <returns>
    /// The lexical full path of the file, or null when it names nothing inside the
    /// repository. The returned path is the one the caller opens; it is deliberately not
    /// the link-resolved one, so that a path accepted through an in-repository symlink
    /// still reads the file the repository names. A rejection is a normal outcome, not a
    /// failure: the caller reads it as <c>plan.exists: false</c> and lets the rules decide
    /// on the absence.
    /// </returns>
    string? Resolve(string? planFile, string repositoryRoot);
}
