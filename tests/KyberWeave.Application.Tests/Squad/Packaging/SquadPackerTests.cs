using System.IO.Compression;
using System.Text;
using KyberWeave.Core.Squad.Packaging;
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

    private SquadPacker Packer() => new(_files);

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
    public void AFailedWriteKeepsThePreviousArchiveAndWritesNoPartialArchive()
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
