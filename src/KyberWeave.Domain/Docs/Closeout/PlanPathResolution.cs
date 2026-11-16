namespace KyberWeave.Core.Docs.Closeout;

/// <summary>What the plan path resolver found for a plan or specification argument.</summary>
/// <param name="Path">The repository-relative path of the one file the argument names, or null.</param>
/// <param name="Candidates">When the argument matches several files, their repository-relative paths; otherwise empty.</param>
/// <param name="SearchedFolders">The planning folders that were searched, repository-relative.</param>
public sealed record PlanPathResolution(string? Path, IReadOnlyList<string> Candidates, IReadOnlyList<string> SearchedFolders)
{
    /// <summary>True when the argument named exactly one file.</summary>
    public bool Found => Path is not null;

    /// <summary>True when the argument matched more than one file.</summary>
    public bool Ambiguous => Candidates.Count > 1;
}
