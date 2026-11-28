using KyberWeave.Application.Configuration;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Closeout;

/// <summary>
/// The plan closeout, the plan finalize and the archive closeout gate resolve a caller's repository root through
/// the path seam, against the base directory the environment names.
/// </summary>
/// <remarks>
/// The checkout lives under <c>/kw/work/repo</c> while the root argument is the relative spelling <c>repo</c>, so
/// only the injected working directory can find it. Each test pairs the working directory that finds the plan with
/// one that does not.
/// </remarks>
public sealed class PlanLifecycleRootOverPathSeamTests
{
    private const string WorkingDirectory = "/kw/work";
    private const string OtherDirectory = "/kw/other";
    private const string RelativeRoot = "repo";
    private const string Plan = "6-Docs/plans/2026-01-01-widget.md";
    private const string Archived = "6-Docs/archive/plans/2026-01-01-widget.md";

    private static readonly DateOnly Date = new(2026, 1, 2);

    private readonly InMemoryFileSystem _disk = new();
    private readonly FakePathResolver _paths;
    private readonly KyberWeaveConfig _config = KyberWeaveConfigDefaults.Product;

    public PlanLifecycleRootOverPathSeamTests()
    {
        _paths = new FakePathResolver(_disk);
        _disk.CreateDirectory($"{WorkingDirectory}/{RelativeRoot}/6-Docs/plans");
        _disk.WriteAllText($"{WorkingDirectory}/{RelativeRoot}/{Plan}", "# Widget\n");
        _disk.WriteAllText($"{WorkingDirectory}/{RelativeRoot}/6-Docs/plans/README.md", "# Plans\n");
    }

    private PlanFinalize Finalizer(string workingDirectory) =>
        new(
            RelativeRoot,
            _config,
            () => throw new InvalidOperationException("The corpus is not loaded before the plan is checked."),
            new InMemoryRepositoryFileStore(_disk),
            new ScriptedProcessExecutor(_ => throw new InvalidOperationException("No process runs before the plan is checked.")),
            new RefusingSchemaGate(),
            _paths,
            new FakeProcessEnvironment(currentDirectory: workingDirectory));

    private PlanCloseout Closer(string workingDirectory) =>
        new(
            RelativeRoot,
            _config,
            () => throw new InvalidOperationException("The corpus is not loaded before the plan is checked."),
            new InMemoryRepositoryFileStore(_disk),
            new ScriptedProcessExecutor(_ => throw new InvalidOperationException("No process runs before the plan is checked.")),
            new RefusingExclusiveWriter(),
            _paths,
            new FakeProcessEnvironment(currentDirectory: workingDirectory));

    [Fact]
    public async Task FinalizeFindsThePlanUnderTheInjectedWorkingDirectory()
    {
        PlanFinalizeOutcome found = await Finalizer(WorkingDirectory).RunAsync(Plan, PlanApprovalMode.Supervised, "approved", Date, CancellationToken.None);
        PlanFinalizeOutcome missing = await Finalizer(OtherDirectory).RunAsync(Plan, PlanApprovalMode.Supervised, "approved", Date, CancellationToken.None);

        // The plan exists but is not a Draft, so the run reaches the status check and refuses there.
        Assert.Contains(found.Report.Items, item => item.Code == PlanFinalize.NotDraft);
        Assert.DoesNotContain(found.Report.Items, item => item.Code == PlanFinalize.NotAnActivePlan);
        Assert.Contains(missing.Report.Items, item => item.Code == PlanFinalize.NotAnActivePlan);
    }

    [Fact]
    public async Task CloseoutFindsThePlanUnderTheInjectedWorkingDirectory()
    {
        _disk.CreateDirectory($"{WorkingDirectory}/{RelativeRoot}/6-Docs/archive/plans");
        _disk.WriteAllText($"{WorkingDirectory}/{RelativeRoot}/{Archived}", "# earlier archive entry\n");

        PlanCloseoutOutcome found = await Closer(WorkingDirectory).RunAsync(Plan, Date, archive: false, CancellationToken.None);
        PlanCloseoutOutcome missing = await Closer(OtherDirectory).RunAsync(Plan, Date, archive: false, CancellationToken.None);

        // The plan exists and its archive target is taken, so the run refuses on the target and not on the plan.
        Assert.Contains(found.Report.Items, item => item.Code == PlanCloseout.ArchiveTargetExists);
        Assert.Contains(missing.Report.Items, item => item.Code == PlanCloseout.NotAnActivePlan);
        Assert.DoesNotContain(missing.Report.Items, item => item.Code == PlanCloseout.ArchiveTargetExists);
    }

    [Fact]
    public void TheArchiveGateSelectsAnArchivedEntryUnderTheInjectedWorkingDirectory()
    {
        _disk.CreateDirectory($"{WorkingDirectory}/{RelativeRoot}/6-Docs/archive/plans");
        _disk.WriteAllText($"{WorkingDirectory}/{RelativeRoot}/{Archived}", "# archived\n");
        ChangedPath[] changes = [new ChangedPath(Archived, ChangeKind.Added)];
        CountingArchiveLoader fromWork = new();
        CountingArchiveLoader fromOther = new();

        _ = Gate(WorkingDirectory, fromWork).Validate(new DocumentSet { Documents = [] }, changes);
        _ = Gate(OtherDirectory, fromOther).Validate(new DocumentSet { Documents = [] }, changes);

        Assert.Equal([Archived], fromWork.Loaded);
        Assert.Empty(fromOther.Loaded);
    }

    private ArchiveCloseoutValidator Gate(string workingDirectory, IDocumentSource loader) =>
        new(
            RelativeRoot,
            _config,
            () => loader,
            _disk,
            _paths,
            new FakeProcessEnvironment(currentDirectory: workingDirectory));

    private sealed class CountingArchiveLoader : IDocumentSource
    {
        public List<string> Loaded { get; } = [];

        public DocumentSet Load() => new() { Documents = [] };

        public DocumentModel LoadArchivedDocument(string relativePath)
        {
            Loaded.Add(relativePath);
            return new DocumentModel { RelativePath = relativePath, FilePath = relativePath, HasFrontmatter = false };
        }
    }

    private sealed class RefusingSchemaGate : IDocsSchemaGate
    {
        public DiagnosticReport Validate(
            string repoRoot,
            KyberWeaveConfig config,
            DocumentSet corpus,
            Func<IDocumentSource> closeoutLoaderFactory,
            bool mergeReady,
            string? gitBase = null) =>
            throw new InvalidOperationException("The schema gate does not run before the plan is checked.");
    }

    private sealed class RefusingExclusiveWriter : IExclusiveFileWriter
    {
        public bool TryWriteNew(string path, string text) =>
            throw new InvalidOperationException("Nothing is written before the plan is checked.");

        public bool TryWriteNew(string path, string text, Action<string> beforeWrite) =>
            throw new InvalidOperationException("Nothing is written before the plan is checked.");
    }
}
