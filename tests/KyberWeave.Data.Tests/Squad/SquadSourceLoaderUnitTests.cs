using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Validation;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Proves <see cref="SquadSourceLoader"/> through the in-memory file system: the synthetic Squad source is copied into
/// memory once per test, so loading, link containment and encoding checks never touch a real directory after the copy.
/// </summary>
public sealed class SquadSourceLoaderUnitTests
{
    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly string _root;
    private readonly string _outside;
    private readonly SquadSourceLoader _loader;

    public SquadSourceLoaderUnitTests()
    {
        _root = Path.Combine(_files.Root, "kw", "squad");
        _outside = Path.Combine(_files.Root, "kw", "outside");
        _loader = new SquadSourceLoader(
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(currentDirectory: Path.Combine(_files.Root, "kw", "work")));
        Copy(SyntheticValidSquadReleaseSource.SyntheticCorpusRoot, _root);
        _files.CreateDirectory(_outside);
    }

    [Fact]
    public void ValidSourceLoadsItsManifestBundleAndAgents()
    {
        SquadSource source = _loader.Load(_root);

        Assert.False(string.IsNullOrWhiteSpace(source.Manifest.Name));
        Assert.Equal(source.Manifest.DefaultBundle, source.Bundle.Name);
        Assert.NotEmpty(source.Agents);
    }

    [Fact]
    public void RootThatDoesNotExistIsRefusedWithAMessageNamingIt()
    {
        SquadSourceValidationException failure =
            Assert.Throws<SquadSourceValidationException>(() => _loader.Load(Path.Combine(_files.Root, "kw", "missing")));

        Assert.Contains("/kw/missing", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankRootIsAnArgumentError()
    {
        _ = Assert.Throws<ArgumentException>(() => _loader.Load(" "));
    }

    [Fact]
    public void SchemaFileThatIsMissingIsRefusedByName()
    {
        _files.DeleteFile($"{_root}/schemas/squad.schema.json");

        SquadSourceValidationException failure =
            Assert.Throws<SquadSourceValidationException>(() => _loader.Load(_root));

        Assert.Contains("schemas/squad.schema.json", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFileLinkedToAFileOutsideTheRootIsRefused()
    {
        _files.WriteAllText($"{_outside}/toolchain.yml", _files.ReadAllText($"{_root}/toolchain.yml"));
        _files.DeleteFile($"{_root}/toolchain.yml");
        _files.CreateFileLink($"{_root}/toolchain.yml", $"{_outside}/toolchain.yml");

        SquadSourceValidationException failure =
            Assert.Throws<SquadSourceValidationException>(() => _loader.Load(_root));

        Assert.Contains("outside the Squad product root", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFileLinkedToAFileInsideTheRootStillLoads()
    {
        string schema = _files.ReadAllText($"{_root}/schemas/squad.schema.json");
        _files.DeleteFile($"{_root}/schemas/squad.schema.json");
        _files.WriteAllText($"{_root}/schemas/squad.schema.copy.json", schema);
        _files.CreateFileLink($"{_root}/schemas/squad.schema.json", $"{_root}/schemas/squad.schema.copy.json");

        SquadSource source = _loader.Load(_root);

        Assert.False(string.IsNullOrWhiteSpace(source.Manifest.Name));
    }

    [Fact]
    public void SourceFileThatIsNotValidUtf8IsRefused()
    {
        _files.WriteAllBytes($"{_root}/toolchain.yml", [0xC3, 0x28, 0xFF]);

        SquadSourceValidationException failure =
            Assert.Throws<SquadSourceValidationException>(() => _loader.Load(_root));

        Assert.Contains("not valid UTF-8", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteOrderMarkAndWindowsLineEndingsDoNotChangeTheLoadedSource()
    {
        SquadSource plain = _loader.Load(_root);
        string toolchain = _files.ReadAllText($"{_root}/toolchain.yml");
        _files.WriteAllBytes(
            $"{_root}/toolchain.yml",
            [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(toolchain.Replace("\n", "\r\n", StringComparison.Ordinal))]);

        SquadSource decorated = _loader.Load(_root);

        Assert.Equal(plain.Toolchain.RequiredFeatures, decorated.Toolchain.RequiredFeatures);
        Assert.Equal(plain.Toolchain.ValidatedRelease, decorated.Toolchain.ValidatedRelease);
    }

    private void Copy(string realDirectory, string memoryDirectory)
    {
        _files.CreateDirectory(memoryDirectory);
        foreach (string directory in Directory.EnumerateDirectories(realDirectory))
        {
            Copy(directory, Path.Combine(memoryDirectory, Path.GetFileName(directory)));
        }

        foreach (string file in Directory.EnumerateFiles(realDirectory))
        {
            _files.WriteAllBytes(Path.Combine(memoryDirectory, Path.GetFileName(file)), File.ReadAllBytes(file));
        }
    }
}
