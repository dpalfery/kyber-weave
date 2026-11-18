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
    private const string Root = "/kw/squad";
    private const string Outside = "/kw/outside";

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);
    private readonly SquadSourceLoader _loader;

    public SquadSourceLoaderUnitTests()
    {
        _loader = new SquadSourceLoader(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: "/kw/work"));
        Copy(SyntheticValidSquadReleaseSource.SyntheticCorpusRoot, Root);
        _files.CreateDirectory(Outside);
    }

    [Fact]
    public void ValidSourceLoadsItsManifestBundleAndAgents()
    {
        SquadSource source = _loader.Load(Root);

        Assert.False(string.IsNullOrWhiteSpace(source.Manifest.Name));
        Assert.Equal(source.Manifest.DefaultBundle, source.Bundle.Name);
        Assert.NotEmpty(source.Agents);
    }

    [Fact]
    public void RootThatDoesNotExistIsRefusedWithAMessageNamingIt()
    {
        SquadSourceValidationException failure =
            Assert.Throws<SquadSourceValidationException>(() => _loader.Load("/kw/missing"));

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
        _files.DeleteFile($"{Root}/schemas/squad.schema.json");

        SquadSourceValidationException failure =
            Assert.Throws<SquadSourceValidationException>(() => _loader.Load(Root));

        Assert.Contains("schemas/squad.schema.json", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFileLinkedToAFileOutsideTheRootIsRefused()
    {
        _files.WriteAllText($"{Outside}/toolchain.yml", _files.ReadAllText($"{Root}/toolchain.yml"));
        _files.DeleteFile($"{Root}/toolchain.yml");
        _files.CreateFileLink($"{Root}/toolchain.yml", $"{Outside}/toolchain.yml");

        SquadSourceValidationException failure =
            Assert.Throws<SquadSourceValidationException>(() => _loader.Load(Root));

        Assert.Contains("outside the Squad product root", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceFileLinkedToAFileInsideTheRootStillLoads()
    {
        string schema = _files.ReadAllText($"{Root}/schemas/squad.schema.json");
        _files.DeleteFile($"{Root}/schemas/squad.schema.json");
        _files.WriteAllText($"{Root}/schemas/squad.schema.copy.json", schema);
        _files.CreateFileLink($"{Root}/schemas/squad.schema.json", $"{Root}/schemas/squad.schema.copy.json");

        SquadSource source = _loader.Load(Root);

        Assert.False(string.IsNullOrWhiteSpace(source.Manifest.Name));
    }

    [Fact]
    public void SourceFileThatIsNotValidUtf8IsRefused()
    {
        _files.WriteAllBytes($"{Root}/toolchain.yml", [0xC3, 0x28, 0xFF]);

        SquadSourceValidationException failure =
            Assert.Throws<SquadSourceValidationException>(() => _loader.Load(Root));

        Assert.Contains("not valid UTF-8", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteOrderMarkAndWindowsLineEndingsDoNotChangeTheLoadedSource()
    {
        SquadSource plain = _loader.Load(Root);
        string toolchain = _files.ReadAllText($"{Root}/toolchain.yml");
        _files.WriteAllBytes(
            $"{Root}/toolchain.yml",
            [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(toolchain.Replace("\n", "\r\n", StringComparison.Ordinal))]);

        SquadSource decorated = _loader.Load(Root);

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
