using System.IO.Compression;
using System.Text;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Testing.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Squad.Packaging;

/// <summary>
/// The packer over the in-memory file system: the archive it composes from a source tree, its determinism and the
/// archive it leaves behind when a write is refused are all proven without a disk.
/// </summary>
public sealed class SquadPackerTests
{
    private static readonly string Source = Path.GetFullPath("/repo/products/kyber-squad");
    private static readonly string Out = Path.GetFullPath("/repo/artifacts");

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private readonly FakeSquadCompanionSkills _companions = new();

    private SquadPacker Packer() => new(_files, _companions);

    private void WriteSourceFile(string relativePath, string content)
    {
        string path = Path.Combine(Source, relativePath.Replace('/', Path.DirectorySeparatorChar));
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }

    private string ArchivePath(string version) => Path.Combine(Out, $"kyber-squad-{version}.zip");

    private Dictionary<string, string> ReadTextEntries(string archivePath)
    {
        using MemoryStream buffer = new(_files.ReadAllBytes(archivePath));
        using ZipArchive archive = new(buffer, ZipArchiveMode.Read);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using Stream stream = entry.Open();
                using MemoryStream content = new();
                stream.CopyTo(content);
                return Encoding.UTF8.GetString(content.ToArray());
            },
            StringComparer.Ordinal);
    }

    [Fact]
    public void PackApmArchivesEveryFileUnderTheSourceWithPortablePathsAndNormalizedNewlines()
    {
        WriteSourceFile("squad.yml", "schema: kyber-squad.squad/v1\r\nname: demo\r\n");
        WriteSourceFile("agents/dev.md", "---\nname: dev\n---\nbody\n");
        WriteSourceFile("out/generated.txt", "generated\n");

        string archivePath = Packer().PackApm(Source, Out, "1.2.3");

        Assert.Equal(ArchivePath("1.2.3"), archivePath);
        Dictionary<string, string> entries = ReadTextEntries(archivePath);
        Assert.Equal(
            ["agents/dev.md", "out/generated.txt", "squad.yml"],
            entries.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("schema: kyber-squad.squad/v1\nname: demo\n", entries["squad.yml"]);
        Assert.Equal("generated\n", entries["out/generated.txt"]);
    }

    [Fact]
    public void PackApmReadsTheCompanionSkillsThroughTheirSeamAndPacksThemBesideTheSquadSkills()
    {
        WriteSourceFile("squad.yml", "name: demo\n");
        string companionRoot = Path.GetFullPath("/repo/.apm/skills");
        string companionFile = Path.Combine(companionRoot, "kyber-docs", "SKILL.md");
        _files.CreateDirectory(Path.GetDirectoryName(companionFile)!);
        _files.WriteAllText(companionFile, "---\nname: kyber-docs\n---\nbody\n");
        FakeSquadCompanionSkills companions = new(("skills/kyber-docs/SKILL.md", companionFile));

        string archivePath = new SquadPacker(_files, companions).PackApm(Source, Out, "1.2.3", companionRoot);

        Assert.Equal([(Source, companionRoot)], companions.Calls);
        Assert.Equal(
            "---\nname: kyber-docs\n---\nbody\n",
            ReadTextEntries(archivePath)["skills/kyber-docs/SKILL.md"]);
    }

    [Fact]
    public void PackApmOfAMissingSourceThrowsInsteadOfWritingAnEmptyArchive()
    {
        string missing = Path.GetFullPath("/repo/products/no-such-squad");

        Assert.Throws<DirectoryNotFoundException>(() => Packer().PackApm(missing, Out, "1.2.3"));
        Assert.False(_files.FileExists(ArchivePath("1.2.3")));
    }

    [Fact]
    public void PackApmDoesNotDescendIntoADirectoryLinkSoALoopCannotRecurseForever()
    {
        WriteSourceFile("squad.yml", "name: demo\n");
        WriteSourceFile("skills/demo/SKILL.md", "---\nname: demo\n---\nbody\n");
        _files.CreateDirectoryLink(Path.Combine(Source, "loop"), Source);

        string archivePath = Packer().PackApm(Source, Out, "1.2.3");

        Assert.Equal(
            ["skills/demo/SKILL.md", "squad.yml"],
            ReadTextEntries(archivePath).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PackApmOfTheSameTreeTwiceWritesTheSameBytes()
    {
        WriteSourceFile("squad.yml", "name: demo\n");
        WriteSourceFile("skills/demo/SKILL.md", "---\nname: demo\n---\nbody\n");

        Packer().PackApm(Source, Out, "1.2.3");
        byte[] first = _files.ReadAllBytes(ArchivePath("1.2.3"));
        Packer().PackApm(Source, Path.GetFullPath("/repo/artifacts-again"), "1.2.3");

        Assert.Equal(first, _files.ReadAllBytes(Path.GetFullPath("/repo/artifacts-again/kyber-squad-1.2.3.zip")));
    }

    [Fact]
    public void ARefusedAtomicWriteKeepsThePreviousArchiveAndLeavesNoTemporaryFile()
    {
        byte[] previous = Encoding.UTF8.GetBytes("previous archive bytes");
        _files.CreateDirectory(Out);
        _files.WriteAllBytes(ArchivePath("9.9.9"), previous);
        List<(string RelativePath, byte[] Content)> entries = [("first.md", Encoding.UTF8.GetBytes("first\n"))];
        _files.FailWhen(operation => string.Equals(operation.Name, "WriteAllBytesAtomic", StringComparison.Ordinal));

        Assert.ThrowsAny<IOException>(() => Packer().WriteArchive(ArchivePath("9.9.9"), entries));

        Assert.Equal(previous, _files.ReadAllBytes(ArchivePath("9.9.9")));
        Assert.Equal([ArchivePath("9.9.9")], _files.EnumerateFiles(Out, "*", recursive: false));
    }

    /// <summary>
    /// An entry that throws fails the pack while the archive is still being composed, so nothing has reached the output
    /// folder. The archive replacement itself is one atomic write, and
    /// <see cref="ARefusedAtomicWriteKeepsThePreviousArchiveAndLeavesNoTemporaryFile"/> covers a refusal there.
    /// </summary>
    [Fact]
    public void AnEntryFailureDuringCompositionKeepsThePreviousArchiveAndWritesNoPartialArchive()
    {
        byte[] previous = Encoding.UTF8.GetBytes("previous archive bytes");
        _files.CreateDirectory(Out);
        _files.WriteAllBytes(ArchivePath("9.9.9"), previous);
        List<(string RelativePath, byte[] Content)> entries =
        [
            ("first.md", Encoding.UTF8.GetBytes("first\n")),
            ("second.md", Encoding.UTF8.GetBytes("second\n"))
        ];

        IOException failure = Assert.Throws<IOException>(() =>
            Packer().WriteArchive(ArchivePath("9.9.9"), entries, beforeEntry: name =>
            {
                if (string.Equals(name, "second.md", StringComparison.Ordinal))
                {
                    throw new IOException("Simulated disk-full write.");
                }
            }));

        Assert.Equal("Simulated disk-full write.", failure.Message);
        Assert.Equal(previous, _files.ReadAllBytes(ArchivePath("9.9.9")));
    }
}
