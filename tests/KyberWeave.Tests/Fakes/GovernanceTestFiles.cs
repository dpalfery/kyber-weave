using KyberWeave.Application.Agents;
using KyberWeave.Application.Skills;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Host;
using KyberWeave.Data.Paths;

namespace KyberWeave.Tests;

/// <summary>
/// A real-file-system adapter and the governance readers over it, for tests. The product
/// adapter lives in Data, which the product test project does not reference.
/// </summary>
internal static class GovernanceTestFiles
{
    /// <summary>The one real-file-system adapter instance.</summary>
    public static IFileSystem Files { get; } = new PhysicalTestFileSystem();

    private static readonly PhysicalPathResolver Paths = new(Files);

    private static readonly ProcessEnvironment Environment = new();

    /// <summary>Reads SKILL.md files and the on-disk facts the pure parser needs.</summary>
    public static SkillFileReader Skills { get; } = new(Files);

    /// <summary>Discovers and reads agent definitions.</summary>
    public static AgentFileReader Agents { get; } = new(Files, Paths, Environment);
}

/// <summary>The real file system, used only so tests exercise the same reads production does.</summary>
internal sealed class PhysicalTestFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public IEnumerable<string> ReadLines(string path) => File.ReadLines(path);

    public Stream OpenRead(string path) => File.OpenRead(path);

    public long GetLength(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public IEnumerable<FileSystemEntry> EnumerateEntries(string directory) =>
        Directory.Exists(directory)
            ? new DirectoryInfo(directory)
                .EnumerateFileSystemInfos()
                .Select(entry => new FileSystemEntry(
                    entry.FullName,
                    entry is DirectoryInfo,
                    entry.LinkTarget is not null))
            : [];

    public string? GetLinkTarget(string path) => new FileInfo(path).LinkTarget switch
    {
        string target => target,
        null => new DirectoryInfo(path).LinkTarget
    };

    public string? GetFinalLinkTarget(string path)
    {
        FileSystemInfo info = new FileInfo(path);
        if (info.LinkTarget is null)
        {
            info = new DirectoryInfo(path);
            if (info.LinkTarget is null)
            {
                return null;
            }
        }

        try
        {
            FileSystemInfo? target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target is not null && Path.Exists(target.FullName) ? target.FullName : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive, bool followLinks) =>
        EnumerateFiles(directory, searchPattern, recursive);

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, searchPattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            : [];

    public IEnumerable<string> EnumerateDirectories(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [];
}
