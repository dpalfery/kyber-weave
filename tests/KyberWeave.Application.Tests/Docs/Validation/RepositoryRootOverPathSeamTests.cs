using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Validation;

/// <summary>
/// The docs validators resolve a caller's repository root through the path seam, against the base directory the
/// environment names, so a unit test places the checkout wherever it likes.
/// </summary>
/// <remarks>
/// Each class here once resolved the root with <c>Path.GetFullPath</c>, which reads the process working directory.
/// A test could place its files only where the process happened to run. The checkout in these tests lives under
/// <c>/kw/work/repo</c> while the root argument is the relative spelling <c>repo</c>: nothing but the injected base
/// directory can find it.
/// </remarks>
public sealed class RepositoryRootOverPathSeamTests
{
    private const string WorkingDirectory = "/kw/work";
    private const string RelativeRoot = "repo";

    private readonly InMemoryFileSystem _disk = new();
    private readonly InMemoryRepositoryFileStore _store;
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: WorkingDirectory);

    public RepositoryRootOverPathSeamTests()
    {
        _store = new InMemoryRepositoryFileStore(_disk);
        _paths = new FakePathResolver(_disk);
    }

    [Fact]
    public void DocSpecValidatorChecksASourceRootUnderTheInjectedWorkingDirectory()
    {
        _disk.CreateDirectory($"{WorkingDirectory}/{RelativeRoot}/src/app");

        DiagnosticReport report = new DocSpecValidator(RelativeRoot, OntologyConfig.ProductDefaults, _disk, _paths, _environment)
            .Validate(new DocumentSet
            {
                Documents =
                [
                    new DocumentModel
                    {
                        RelativePath = "docs/a.md",
                        FilePath = $"{WorkingDirectory}/{RelativeRoot}/docs/a.md",
                        HasFrontmatter = true,
                        Frontmatter = new DocumentFrontmatter { SourceRoot = "src/app" }
                    }
                ]
            });

        Assert.DoesNotContain(report.Items, item => item.Code == DocSpecValidator.SourceRootMissing);
    }

    [Fact]
    public void ConfigRegValidatorFindsAnEntryUnderTheInjectedWorkingDirectory()
    {
        _disk.CreateDirectory($"{WorkingDirectory}/{RelativeRoot}/docs/reference");
        _disk.WriteAllText($"{WorkingDirectory}/{RelativeRoot}/docs/reference/auth-design.md", "# Auth\n");
        KyberWeaveConfig config = KyberWeaveConfigDefaults.Product
            .WithOntology(new OntologyConfig { DocsRoots = ["docs"] })
            .WithConfigReg(new ConfigRegConfig
            {
                Additions = [new ConfigRegEntry("auth-design", "docs/reference/auth-design.md")]
            });

        DiagnosticReport report = new ConfigRegValidator(RelativeRoot, config, _store, _paths, _environment).Validate();

        Assert.DoesNotContain(
            report.Items,
            item => item.Code == ConfigRegValidator.UnresolvedPath && item.Message.Contains("auth-design", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanTaskServiceFindsAnActivePlanUnderTheInjectedWorkingDirectory()
    {
        _disk.CreateDirectory($"{WorkingDirectory}/{RelativeRoot}/docs/plans/2026-01-01-widget");
        _disk.WriteAllText(
            $"{WorkingDirectory}/{RelativeRoot}/docs/plans/2026-01-01-widget/tasks.md",
            "# Tasks\n\n**Status:** Ready\n");
        KyberWeaveConfig config = KyberWeaveConfigDefaults.Product
            .WithOntology(new OntologyConfig { DocsRoots = ["docs"] });

        IReadOnlyList<string> plans = new PlanTaskService(_store, _paths, _environment).ActivePlans(RelativeRoot, config);

        Assert.Equal(["docs/plans/2026-01-01-widget/tasks.md"], plans);
    }
}
