using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Review;

/// <summary>Finds the project file of a source file by walking up the directories on disk.</summary>
public sealed class FileProjectFileLocator(IFileSystem files, IPathResolver paths, IProcessEnvironment environment) : IProjectFileLocator
{
    /// <inheritdoc />
    public string? NearestProjectFile(string? repositoryRoot, string file)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return null;
        }

        string root = paths.GetFullPath(repositoryRoot, environment.CurrentDirectory);
        string? directory = Path.GetDirectoryName(paths.GetFullPath(file, root));
        while (directory is not null && directory.StartsWith(root, StringComparison.Ordinal))
        {
            if (files.DirectoryExists(directory))
            {
                string? found = files.EnumerateFiles(directory, "*", recursive: false)
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
