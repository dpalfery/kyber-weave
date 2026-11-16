namespace KyberWeave.Contracts.Repositories;

/// <summary>Finds the project file a source file belongs to.</summary>
public interface IProjectFileLocator
{
    /// <summary>The nearest project file above a file (a .csproj, package.json, pyproject.toml, Cargo.toml or go.mod).</summary>
    /// <param name="repositoryRoot">The repository root; null or blank names no project.</param>
    /// <param name="file">The repository-relative file.</param>
    /// <returns>The project file, repository-relative; null when there is none.</returns>
    string? NearestProjectFile(string? repositoryRoot, string file);
}
