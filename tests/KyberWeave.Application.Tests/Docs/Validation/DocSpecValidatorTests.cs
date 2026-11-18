using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.FileSystem;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Validation;

/// <summary>The disk checks of the documentation validator, over a checkout held in memory.</summary>
public sealed class DocSpecValidatorTests
{
    private const string Root = "/repo";

    private readonly InMemoryFileSystem _disk = new();

    private readonly FakePathResolver _paths;

    private readonly FakeProcessEnvironment _environment = new(currentDirectory: Root);

    public DocSpecValidatorTests() => _paths = new FakePathResolver(_disk);

    private DiagnosticReport Validate(OntologyConfig config, params DocumentModel[] documents) =>
        new DocSpecValidator(Root, config, _disk, _paths, _environment).Validate(new DocumentSet { Documents = documents });

    [Fact]
    public void ASourceRootThatDoesNotExistIsReported()
    {
        DiagnosticReport report = Validate(OntologyConfig.ProductDefaults, Document("docs/a.md", sourceRoot: "src/missing"));

        Assert.Contains(report.Items, item => item.Code == DocSpecValidator.SourceRootMissing);
    }

    [Fact]
    public void ASourceRootThatIsADirectoryOrAFileIsAccepted()
    {
        _disk.CreateDirectory(Root + "/src/app");
        _disk.WriteAllText(Root + "/tool.sh", "#!/bin/sh");

        Assert.DoesNotContain(
            Validate(OntologyConfig.ProductDefaults, Document("docs/a.md", sourceRoot: "src/app")).Items,
            item => item.Code == DocSpecValidator.SourceRootMissing);
        Assert.DoesNotContain(
            Validate(OntologyConfig.ProductDefaults, Document("docs/b.md", sourceRoot: "tool.sh")).Items,
            item => item.Code == DocSpecValidator.SourceRootMissing);
    }

    [Fact]
    public void AStandardsFolderBehindALinkIsReported()
    {
        OntologyConfig config = Technology("csharp");
        _disk.CreateDirectory(Root + "/elsewhere");
        _disk.CreateDirectory(Root + "/" + config.DocsRoot + "/standards");
        _disk.CreateDirectoryLink(Root + "/" + config.DocsRoot + "/standards/csharp", Root + "/elsewhere");

        DiagnosticReport report = Validate(config);

        Assert.Contains(
            report.Items,
            item => item.Code == DocSpecValidator.MissingFolderFile && item.Message.Contains("symbolic link", StringComparison.Ordinal));
    }

    [Fact]
    public void AStandardsFolderWithNoStandardFileIsReportedAsMissingIt()
    {
        OntologyConfig config = Technology("csharp");
        _disk.CreateDirectory(Root + "/" + config.DocsRoot + "/standards/csharp");

        DiagnosticReport report = Validate(config);

        Assert.Contains(
            report.Items,
            item => item.Code == DocSpecValidator.MissingFolderFile && item.Message.Contains("is missing 'csharp", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeclaredTechnologyWithoutAFolderIsSkipped()
    {
        DiagnosticReport report = Validate(Technology("csharp"));

        Assert.DoesNotContain(report.Items, item => item.Code == DocSpecValidator.MissingFolderFile);
    }

    private static OntologyConfig Technology(string name) =>
        new() { Technologies = [name] };

    private static DocumentModel Document(string path, string? sourceRoot = null) =>
        new()
        {
            RelativePath = path,
            FilePath = Root + "/" + path,
            HasFrontmatter = true,
            Frontmatter = new DocumentFrontmatter { SourceRoot = sourceRoot }
        };
}
