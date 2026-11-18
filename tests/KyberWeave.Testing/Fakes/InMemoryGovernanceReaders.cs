using KyberWeave.Application.Agents;
using KyberWeave.Application.Skills;
using KyberWeave.Tests.FileSystem;

namespace KyberWeave.Tests.Fakes;

/// <summary>The skill and agent readers over an in-memory file system, for a test that reads governance files without a disk.</summary>
public sealed class InMemoryGovernanceReaders
{
    /// <summary>Creates readers over the given file system, or over a new empty one.</summary>
    /// <param name="files">The file system the readers answer from; a new one when null.</param>
    public InMemoryGovernanceReaders(InMemoryFileSystem? files = null)
    {
        Files = files ?? new InMemoryFileSystem();
        Root = Path.Combine(Files.Root, "work");
        FakePathResolver paths = new(Files);
        FakeProcessEnvironment environment = new(currentDirectory: Root);
        Skills = new SkillFileReader(Files, paths, environment);
        Agents = new AgentFileReader(Files, paths, environment);
    }

    /// <summary>Gets the file system the readers answer from.</summary>
    public InMemoryFileSystem Files { get; }

    /// <summary>Gets a directory under the file system's root, which is also the readers' working directory.</summary>
    public string Root { get; }

    /// <summary>Gets the SKILL.md reader.</summary>
    public SkillFileReader Skills { get; }

    /// <summary>Gets the agent definition reader.</summary>
    public AgentFileReader Agents { get; }

    /// <summary>Writes a file, creating its directory first.</summary>
    /// <param name="path">The file's full path.</param>
    /// <param name="content">The text to write.</param>
    public void Write(string path, string content)
    {
        Files.CreateDirectory(Path.GetDirectoryName(path)!);
        Files.WriteAllText(path, content);
    }
}
