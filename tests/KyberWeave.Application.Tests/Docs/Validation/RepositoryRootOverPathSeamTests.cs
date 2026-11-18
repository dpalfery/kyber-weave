using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Review;
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
        DocSpecValidator validator = new(RelativeRoot, OntologyConfig.ProductDefaults, _disk, _paths, _environment);

        Assert.DoesNotContain(validator.Validate(DocumentWithSourceRoot()).Items, item => item.Code == DocSpecValidator.SourceRootMissing);

        // The counter-case proves the check ran against the injected directory at all: with the directory removed, the
        // same validator reports the same source root.
        _disk.DeleteTreeNoFollow($"{WorkingDirectory}/{RelativeRoot}/src/app");
        Assert.Contains(validator.Validate(DocumentWithSourceRoot()).Items, item => item.Code == DocSpecValidator.SourceRootMissing);
    }

    private static DocumentSet DocumentWithSourceRoot() => new()
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
    };

    [Fact]
    public void ConfigRegValidatorFindsAnEntryUnderTheInjectedWorkingDirectory()
    {
        _disk.CreateDirectory($"{WorkingDirectory}/{RelativeRoot}/docs/reference");
        _disk.WriteAllText($"{WorkingDirectory}/{RelativeRoot}/docs/reference/auth-design.md", "# Auth\n");
        KyberWeaveConfig config = ConfigWithAuthDesign();
        ConfigRegValidator validator = new(RelativeRoot, config, _store, _paths, _environment);

        DiagnosticReport present = validator.Validate();

        Assert.DoesNotContain(
            present.Items,
            item => item.Code == ConfigRegValidator.UnresolvedPath && item.Message.Contains("auth-design", StringComparison.Ordinal));

        // The counter-case proves the check ran against the injected directory at all: with the file removed, the
        // same validator reports it.
        _disk.DeleteFile($"{WorkingDirectory}/{RelativeRoot}/docs/reference/auth-design.md");
        Assert.Contains(
            validator.Validate().Items,
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

    [Fact]
    public void PlanTaskServiceRunReadsThePlanUnderTheInjectedWorkingDirectory()
    {
        _disk.CreateDirectory($"{WorkingDirectory}/{RelativeRoot}/docs/plans");
        _disk.WriteAllText(
            $"{WorkingDirectory}/{RelativeRoot}/docs/plans/2026-01-01-widget.md",
            "---\nid: plans/2026-01-01-widget\ntitle: Widget\ndoc-type: plan\nstatus: draft\n---\n\n"
            + "## Tasks\n\n- [ ] 1. Do the thing\n");
        KyberWeaveConfig config = KyberWeaveConfigDefaults.Product
            .WithOntology(new OntologyConfig { DocsRoots = ["docs"] });

        PlanTaskService service = new(_store, _paths, _environment);

        PlanTaskFileOutcome outcome = service.Run(RelativeRoot, config, "docs/plans/2026-01-01-widget.md", PlanTaskFileMode.Preview);

        Assert.DoesNotContain(outcome.Report.Items, item => item.Code == PlanTaskFileGenerator.PlanNotReadable);

        // The counter-case proves the read went through the injected directory at all: the same call for a plan that
        // is not there reports it unreadable.
        Assert.Contains(
            service.Run(RelativeRoot, config, "docs/plans/2026-01-01-absent.md", PlanTaskFileMode.Preview).Report.Items,
            item => item.Code == PlanTaskFileGenerator.PlanNotReadable);
    }

    [Fact]
    public void TheSchemaGateChecksOneCheckoutWhenTheRootIsRelative()
    {
        _disk.CreateDirectory($"{WorkingDirectory}/{RelativeRoot}/docs/plans");
        _disk.WriteAllText(
            $"{WorkingDirectory}/{RelativeRoot}/docs/plans/widget.md.tasks.yml.new",
            "{\"execution-digest\":\"abc\"}");
        KyberWeaveConfig config = KyberWeaveConfigDefaults.Product
            .WithOntology(new OntologyConfig { DocsRoots = ["docs"] });
        RecordingSeverityOverrides severity = new();

        DiagnosticReport report = new DocsSchemaGate(
            _store,
            new NoGitChanges(),
            severity,
            TimeProvider.System,
            _paths,
            _environment).Validate(
            RelativeRoot,
            config,
            new DocumentSet { Documents = [] },
            () => throw new InvalidOperationException("no closeout loader in this test"),
            mergeReady: false);

        Assert.Contains(report.Items, item => item.Code == PlanTaskFileRules.SidecarTaskFile);
        Assert.Equal($"{WorkingDirectory}/{RelativeRoot}", severity.Root);
    }

    private static KyberWeaveConfig ConfigWithAuthDesign() =>
        KyberWeaveConfigDefaults.Product
            .WithOntology(new OntologyConfig { DocsRoots = ["docs"] })
            .WithConfigReg(new ConfigRegConfig
            {
                Additions = [new ConfigRegEntry("auth-design", "docs/reference/auth-design.md")]
            });

    private sealed class RecordingSeverityOverrides : ISeverityOverrideSource
    {
        public string? Root { get; private set; }

        public string? GuidancePath(string root, ConfigRegConfig configReg) => null;

        public SeverityOverrides Load(string root, ConfigRegConfig configReg) => SeverityOverrides.None;

        public DiagnosticReport Validate(string root, ConfigRegConfig configReg)
        {
            Root = root;
            return new DiagnosticReport();
        }
    }

    private sealed class NoGitChanges : IGitChangeReader
    {
        public IReadOnlyList<string> Resolve(string workingDirectory, string baseRef) => [];

        public string CurrentBranch(string workingDirectory) => string.Empty;

        public string HeadCommit(string workingDirectory) => string.Empty;

        public string MergeBaseCommit(string workingDirectory, string baseRef) => string.Empty;

        public bool IsAncestorOfHead(string workingDirectory, string commit) => false;

        public IReadOnlyList<string> ResolveSince(string workingDirectory, string commit) => [];

        public IReadOnlyList<ChangedPath> ResolveSinceMergeBase(string workingDirectory, string baseRef) => [];

        public (string MergeBase, string Patch) DiffSinceMergeBase(string workingDirectory, string baseRef) =>
            (string.Empty, string.Empty);

        public string DiffSince(string workingDirectory, string commit) => string.Empty;
    }
}
