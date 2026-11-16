using KyberWeave.Application.Agents;
using KyberWeave.Application.Skills;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests;

/// <summary>
/// A real-file-system adapter and the governance readers over it, for tests. The product
/// adapter lives in Data, which the product test project does not reference.
/// </summary>
internal static class GovernanceTestFiles
{
    /// <summary>The one real-file-system adapter instance.</summary>
    public static IFileSystem Files { get; } = new PhysicalTestFileSystem();

    /// <summary>Reads SKILL.md files and the on-disk facts the pure parser needs.</summary>
    public static SkillFileReader Skills { get; } = new(Files);

    /// <summary>Discovers and reads agent definitions.</summary>
    public static AgentFileReader Agents { get; } = new(Files);
}

/// <summary>The real file system, used only so tests exercise the same reads production does.</summary>
internal sealed class PhysicalTestFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public IEnumerable<string> EnumerateFiles(string directory, string searchPattern, bool recursive) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, searchPattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            : [];

    public IEnumerable<string> EnumerateDirectories(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [];
}
