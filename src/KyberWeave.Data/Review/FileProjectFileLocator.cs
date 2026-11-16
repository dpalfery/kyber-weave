using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Review;

/// <summary>Finds the project file of a source file by walking up the directories on disk.</summary>
public sealed class FileProjectFileLocator : IProjectFileLocator
{
    /// <inheritdoc />
    public string? NearestProjectFile(string? repositoryRoot, string file)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return null;
        }

        string root = Path.GetFullPath(repositoryRoot);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, file)));
        while (directory is not null && directory.StartsWith(root, StringComparison.Ordinal))
        {
            if (Directory.Exists(directory))
            {
                string? found = Directory.EnumerateFiles(directory)
                    .FirstOrDefault(path => Path.GetExtension(path) is ".csproj" or ".fsproj" or ".vbproj"
                                            || Path.GetFileName(path) is "package.json" or "pyproject.toml" or "Cargo.toml" or "go.mod");
                if (found is not null)
                {
                    return Path.GetRelativePath(root, found).Replace('\\', '/');
                }
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
