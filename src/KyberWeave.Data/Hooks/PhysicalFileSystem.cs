using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Hooks;

/// <summary>Reads and writes the real file system.</summary>
public sealed class PhysicalFileSystem : IWritableFileSystem
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.Ordinal)
    {
        "bin", "obj", "node_modules", ".git", ".venv", "dist", "out", "coverage", "TestResults", "artifacts", "target"
    };

    public bool FileExists(string path) => File.Exists(path);

    public bool PathExists(string path) => Path.Exists(path);

    public void CreateDirectory(string path) => _ = Directory.CreateDirectory(path);

    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);

    public void CreateNewFile(string path)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public string? GetLinkTarget(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        if (!info.Exists)
        {
            info = new DirectoryInfo(path);
        }

        return info.Exists ? info.LinkTarget : null;
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public IEnumerable<string> EnumerateDirectories(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [];

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
