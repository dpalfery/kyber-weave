using KyberWeave.Application.Review;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Review;
using KyberWeave.Domain.CodeGraph;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Review;

/// <summary>
/// The review commands and their report writer resolve the paths a caller spelled through the path seam: a relative
/// spelling names a file under the injected working directory, and every command loads the configuration for that
/// resolved root rather than for the process's own directory.
/// </summary>
public sealed class ReviewPathSeamTests
{
    private readonly InMemoryFileSystem _files = new();

    private readonly FakeProcessEnvironment _environment = new(currentDirectory: "/work");

    private FakePathResolver Paths => new(_files);

    [Fact]
    public void TheReportWriterWritesARelativeOutPathUnderTheInjectedWorkingDirectory()
    {
        DiagnosticReport report = new();

        ReviewReportWriter.TryWrite(_files, Paths, _environment, "out/report.json", "{}", "KW-TEST-001", report);

        Assert.False(report.HasErrors);
        Assert.True(_files.FileExists("/work/out/report.json"));
        Assert.Equal("{}", _files.ReadAllText("/work/out/report.json"));
    }

    [Fact]
    public void TheReportWriterReportsAnOutPathGivenAnEmptyValue()
    {
        DiagnosticReport report = new();

        ReviewReportWriter.TryWrite(_files, Paths, _environment, " ", "{}", "KW-TEST-001", report);

        Assert.True(report.HasErrors);
        Assert.Empty(_files.EnumerateFiles("/work", "*", recursive: false));
    }

    [Fact]
    public void TheCatalogueCommandLoadsTheConfigurationForTheResolvedRoot()
    {
        RecordingConfigs configs = new();
        ReviewCatalogueService service = new(
            configs, new UnusedLister(), _files, Paths, _environment);

        ReviewCommandResult result = service.Run(new ReviewCatalogueRequest("repo", null, []), TextWriter.Null);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/work/repo", configs.Root);
    }

    [Fact]
    public void AnEarlierGateReportSpelledRelativelyIsMergedIntoThisRun()
    {
        _files.CreateDirectory("/work/out");
        _files.WriteAllText(
            "/work/out/report.json",
            ReviewJson.Write(new GateReport(GateReport.CurrentSchema, [new GateResult("build", true, 0, "passed")])));
        ReviewGate[] declared = [new ReviewGate("build", ["dotnet", "build"]), new ReviewGate("test", ["dotnet", "test"])];
        GateReport ran = new(GateReport.CurrentSchema, [new GateResult("test", true, 0, "passed")]);

        GateReport merged = ReviewGatesService.MergedWithExisting(
            _files, Paths, _environment, declared, ran, "out/report.json");

        Assert.Equal(["build", "test"], merged.Gates.Select(gate => gate.Id));
        Assert.Equal("passed", merged.Gates[0].Summary);
    }

    [Fact]
    public void AnEmptyOutPathKeepsNoEarlierReportAndDoesNotThrow()
    {
        GateReport ran = new(GateReport.CurrentSchema, [new GateResult("build", true, 0, "passed")]);

        GateReport merged = ReviewGatesService.MergedWithExisting(
            _files, Paths, _environment, [new ReviewGate("build", ["dotnet", "build"])], ran, " ");

        Assert.Equal(["build"], merged.Gates.Select(gate => gate.Id));
    }

    [Fact]
    public void TheDuplicatesCommandLoadsTheConfigurationForTheResolvedRoot()
    {
        RecordingConfigs configs = new();
        ReviewDuplicatesService service = new(
            new UnusedDetector(), new UnusedGraphs(), configs, _files, Paths, _environment);

        ReviewCommandResult result = service.Run(new ReviewDuplicatesRequest("repo", null, null));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal("/work/repo", configs.Root);
    }

    [Fact]
    public void TheGateOsFilterReadsThePlatformFromTheEnvironmentSeam()
    {
        Assert.Equal(
            "windows",
            ReviewGateReporting.CurrentOs(new FakeProcessEnvironment(operatingSystem: OperatingSystemKind.Windows)));
        Assert.Equal(
            "unix",
            ReviewGateReporting.CurrentOs(new FakeProcessEnvironment(operatingSystem: OperatingSystemKind.MacOS)));
        Assert.Equal(
            "unix",
            ReviewGateReporting.CurrentOs(new FakeProcessEnvironment(operatingSystem: OperatingSystemKind.Linux)));
    }

    /// <summary>Records the root it was asked to load and refuses, so the command stops before any other seam is used.</summary>
    private sealed class RecordingConfigs : IConfigLoadService
    {
        public string? Root { get; private set; }

        public bool TryLoad(
            string repoRoot,
            string? configPath,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out string? resolvedConfigPath)
        {
            Root = repoRoot;
            config = new KyberWeaveConfig { Arbiter = KyberWeave.Core.Arbiter.ArbiterConfigDefaults.Product };
            resolvedConfigPath = null;
            report.Add(new Diagnostic("KW-TEST-002", Severity.Error, "refused by the test", "review"));
            return false;
        }

        public bool TryLoadWithDocsRoots(
            string repoRoot,
            string? configPath,
            IReadOnlyList<string> docsRoots,
            DiagnosticReport report,
            out KyberWeaveConfig config,
            out OntologyConfig ontology) =>
            throw new InvalidOperationException("unexpected call");
    }

    private sealed class UnusedLister : IRepositoryFileLister
    {
        public IReadOnlyList<string> List(string repositoryRoot) => throw new InvalidOperationException("unexpected call");
    }

    private sealed class UnusedDetector : IDuplicateDetectionService
    {
        public DuplicateReport Detect(
            string repoRoot,
            IReadOnlyList<CodeGraphNode> symbols,
            ReviewDuplicates options,
            string indexPath,
            DateTime? indexModifiedUtc) => throw new InvalidOperationException("unexpected call");

        public DuplicateReport Analyze(
            string repoRoot,
            ICodeGraphResolver graph,
            ICodeGraphSymbolEnumerator symbols,
            ReviewDuplicates options) => throw new InvalidOperationException("unexpected call");
    }

    private sealed class UnusedGraphs : ICodeGraphPortsFactory
    {
        public CodeGraphPorts ForRepository(string repositoryRoot) => throw new InvalidOperationException("unexpected call");
    }
}
