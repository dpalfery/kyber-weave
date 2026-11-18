using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.Hooks;

namespace KyberWeave.Application.Hooks;

internal static class ProjectLocator
{
    private static readonly string[] SolutionPatterns = ["*.sln", "*.slnx"];
    private static readonly string[] ProjectPatterns = ["*.csproj", "*.fsproj", "*.vbproj"];

    public static string? FindRepositoryRoot(IFileSystem fileSystem, string startDirectory)
    {
        string? fallback = null;
        for (string? directory = startDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            string git = Path.Combine(directory, ".git");
            if (fileSystem.DirectoryExists(git) || fileSystem.FileExists(git))
            {
                return directory;
            }

            if (fallback is null && fileSystem.DirectoryExists(Path.Combine(directory, ".kyber-weave")))
            {
                fallback = directory;
            }
        }

        return fallback;
    }

    public static string? FindMarker(IFileSystem fileSystem, IPathResolver paths, string root, string fileDirectory) =>
        FindUp(fileSystem, paths, root, fileDirectory, SolutionPatterns);

    public static string? FindProject(IFileSystem fileSystem, IPathResolver paths, string root, string fileDirectory) =>
        FindUp(fileSystem, paths, root, fileDirectory, ProjectPatterns);

    private static string? FindUp(IFileSystem fileSystem, IPathResolver paths, string root, string startDirectory, string[] patterns)
    {
        string? directory = startDirectory;
        while (directory is not null && PathContainment.IsWithin(paths, root, directory))
        {
            foreach (string pattern in patterns)
            {
                string? match = fileSystem.EnumerateFiles(directory, pattern, recursive: false).Order(StringComparer.Ordinal).FirstOrDefault();
                if (match is not null)
                {
                    return Path.GetRelativePath(root, match).Replace('\\', '/');
                }
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }
}
