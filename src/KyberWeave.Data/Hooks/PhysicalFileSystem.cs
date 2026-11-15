using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Hooks;

/// <summary>Reads the real file system.</summary>
internal sealed class PhysicalFileSystem : IFileSystem
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.Ordinal)
    {
        "bin", "obj", "node_modules", ".git", ".venv", "dist", "out", "coverage", "TestResults", "artifacts", "target"
    };

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(directory, searchPattern))
        {
            yield return file;
        }

        if (!recursive)
        {
            yield break;
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            if (SkippedDirectories.Contains(Path.GetFileName(child)))
            {
                continue;
            }

            foreach (string file in EnumerateFiles(child, searchPattern, recursive: true))
            {
                yield return file;
            }
        }
    }
}
