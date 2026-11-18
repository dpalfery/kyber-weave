using System.IO;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The severity overrides are read through the file seam: the guidance file lives in memory, so the lookup, the
/// missing-file rule and the unreadable-file rule are proven without a repository on disk.
/// </summary>
public sealed class FileSeverityOverrideSourceTests
{
    private const string Root = "/repo";
    private const string Table = """
        # Review guidance

        ## Severity overrides

        | Id | Finding class | Severity | Applies to | Why |
        |---|---|---|---|---|
        | SO-2 | Missing XML doc comment on a private member | nitpick | | Style only |

        """;

    private readonly InMemoryFileSystem _files = new();

    private FileSeverityOverrideSource Source() =>
        new(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: "/"));

    private string GuidanceFile => Path.GetFullPath(Path.Combine(Root, SeverityOverrides.DefaultGuidancePath));

    private void WriteGuidance(string text)
    {
        _files.CreateDirectory(Root);
        _files.WriteAllText(GuidanceFile, text);
    }

    [Fact]
    public void GuidancePathIsNullWhenTheDeclaredFileIsMissing()
    {
        Assert.Null(Source().GuidancePath(Root, ConfigRegConfig.ProductDefaults));
    }

    [Fact]
    public void GuidancePathIsTheDeclaredFileWhenItExists()
    {
        WriteGuidance(Table);

        Assert.Equal(GuidanceFile, Source().GuidancePath(Root, ConfigRegConfig.ProductDefaults));
    }

    [Fact]
    public void LoadReturnsNoOverridesWhenTheDeclaredFileIsMissing()
    {
        Assert.Same(SeverityOverrides.None, Source().Load(Root, ConfigRegConfig.ProductDefaults));
    }

    [Fact]
    public void LoadReadsTheDeclaredFileThroughTheFileSeam()
    {
        WriteGuidance(Table);

        SeverityOverrides overrides = Source().Load(Root, ConfigRegConfig.ProductDefaults);

        Assert.NotSame(SeverityOverrides.None, overrides);
        Assert.Empty(overrides.Problems);
    }

    [Fact]
    public void LoadReturnsNoOverridesWhenTheFileCannotBeRead()
    {
        WriteGuidance(Table);
        _files.FailWhen(operation => operation.Name == "ReadAllText", new IOException("unreadable"));

        Assert.Same(SeverityOverrides.None, Source().Load(Root, ConfigRegConfig.ProductDefaults));
    }

    [Fact]
    public void GuidancePathReadsARelativeRootAgainstTheWorkingDirectory()
    {
        string path = Path.Combine("/work", "repo", SeverityOverrides.DefaultGuidancePath);
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, Table);
        FileSeverityOverrideSource source = new(
            _files,
            new FakePathResolver(_files),
            new FakeProcessEnvironment(currentDirectory: "/work"));

        Assert.Equal(path, source.GuidancePath("repo", ConfigRegConfig.ProductDefaults));
    }

    [Fact]
    public void ValidateReportsNothingWhenTheDeclaredFileIsMissing()
    {
        Assert.Empty(Source().Validate(Root, ConfigRegConfig.ProductDefaults).Items);
    }
}
