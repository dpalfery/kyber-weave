using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Application.Hooks;

internal static class ProjectLocator
{
    private static readonly string[] SolutionPatterns = ["*.sln", "*.slnx"];
    private static readonly string[] ProjectPatterns = ["*.csproj", "*.fsproj", "*.vbproj"];

    public static string? FindRepositoryRoot(IFileSystem fileSystem, string startDirectory)
    {
        string? directory = startDirectory;
        while (directory is not null)
        {
            string git = Path.Combine(directory, ".git");
            if (fileSystem.DirectoryExists(git) || fileSystem.FileExists(git)
                || fileSystem.DirectoryExists(Path.Combine(directory, ".kyber-weave")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    public static string? FindMarker(IFileSystem fileSystem, string root, string fileDirectory) =>
        FindUp(fileSystem, root, fileDirectory, SolutionPatterns);

    public static string? FindProject(IFileSystem fileSystem, string root, string fileDirectory) =>
        FindUp(fileSystem, root, fileDirectory, ProjectPatterns);

    private static string? FindUp(IFileSystem fileSystem, string root, string startDirectory, string[] patterns)
    {
        string? directory = startDirectory;
        while (directory is not null && directory.StartsWith(root, StringComparison.Ordinal))
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
