using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Scaffolding;

/// <summary>
/// The standards-template diff reads through the file-system, path and environment seams, so a unit test runs it
/// over an in-memory checkout.
/// </summary>
public sealed class StandardsTemplateDiffTests
{
    private const string WorkingDirectory = "/kw/work";
    private const string Root = "/kw/work/repo";
    private const string Folder = Root + "/6-Docs/standards/csharp";

    private static readonly OntologyConfig Declared = new() { Technologies = ["csharp"] };

    private readonly InMemoryFileSystem _disk = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: WorkingDirectory);

    public StandardsTemplateDiffTests() => _paths = new FakePathResolver(_disk);

    private StandardsTemplateDiff Diff(string repoRoot = "repo") =>
        new(_disk, repoRoot, Declared, _paths, _environment);

    [Fact]
    public void ItReportsTheTemplateFilesAFolderLacksUnderTheInjectedWorkingDirectory()
    {
        _disk.CreateDirectory(Folder);

        StandardsFolderDiff folder = Assert.Single(Diff().Run().Folders);

        Assert.Equal("csharp", folder.Name);
        Assert.Contains("csharp.standards.md", folder.Missing);
        Assert.Empty(folder.HostOnly);
        Assert.Empty(folder.Changed);
    }

    [Fact]
    public void ItReportsAFileWhoseBodyDiffersFromTheTemplate()
    {
        _disk.CreateDirectory(Folder);
        Assert.True(KyberStandardsTemplates.TryRenderPart(
            "csharp", KyberStandardsTemplates.Part.Standard, "unassigned", "2026-01-01", out string? template));
        _disk.WriteAllText($"{Folder}/csharp.standards.md", template + "\nAn authored line.\n");

        StandardsFolderDiff folder = Assert.Single(Diff().Run().Folders);

        Assert.Contains("csharp.standards.md", folder.Changed);
    }

    [Fact]
    public void ItReportsAFileTheTemplateDoesNotShip()
    {
        _disk.CreateDirectory(Folder);
        _disk.WriteAllText($"{Folder}/host-note.md", "# Host only\n");

        StandardsFolderDiff folder = Assert.Single(Diff().Run().Folders);

        Assert.Equal(["host-note.md"], folder.HostOnly);
    }
}
