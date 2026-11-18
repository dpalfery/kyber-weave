using KyberWeave.Cli.Commands;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Closeout;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.Time.Testing;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The remaining docs verbs reach their clock, their paths and their console through a seam, so a unit test states
/// the date, the directory and the output without reading the system clock or the process console.
/// </summary>
public sealed class DocsCommandSeamTests
{
    private const string WorkingDirectory = "/kw/work";
    private const string Plan = "repo/docs/plans/2026-01-01-plan.md";

    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly InMemoryFileSystem _files = new();
    private readonly FakeKyberWeaveConfigRepository _configs = new();
    private readonly FakePathResolver _paths;
    private readonly FakeProcessEnvironment _environment = new(currentDirectory: WorkingDirectory);

    public DocsCommandSeamTests() => _paths = new FakePathResolver(_files);

    [Fact]
    public void CloseoutTakesItsArchiveDateFromTheInjectedClock()
    {
        RecordingCloseout closeout = new();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 3, 4, 23, 30, 0, TimeSpan.Zero));

        int exit = Closeout(closeout, clock).Execute(null!, CloseoutSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(new DateOnly(2026, 3, 4), closeout.ArchiveDate);
        Assert.Contains("\"archived\"", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACloseoutWithoutAWorkingDirectoryResolvesItsRootAgainstTheEnvironmentSeam()
    {
        RecordingCloseout closeout = new();
        DocsCloseoutSettings settings = CloseoutSettings();
        settings.Path = "plans/x.md";
        settings.WorkingDirectory = null;

        int exit = Closeout(closeout, new FakeTimeProvider()).Execute(null!, settings, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal([WorkingDirectory], _configs.LoadedRoots);
    }

    [Fact]
    public void AHumanFormatCloseoutAnnouncesThePlanOnTheInjectedConsole()
    {
        RecordingCloseout closeout = new();
        DocsCloseoutSettings settings = CloseoutSettings();
        settings.Format = "lines";

        int exit = Closeout(closeout, new FakeTimeProvider()).Execute(null!, settings, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains($"Plan: {Plan}", _console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACloseoutDateOptionOverridesTheClock()
    {
        RecordingCloseout closeout = new();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 3, 4, 23, 30, 0, TimeSpan.Zero));
        DocsCloseoutSettings settings = CloseoutSettings();
        settings.Date = "2025-12-31";

        _ = Closeout(closeout, clock).Execute(null!, settings, CancellationToken.None);

        Assert.Equal(new DateOnly(2025, 12, 31), closeout.ArchiveDate);
    }

    [Fact]
    public void FinalizeTakesItsApprovalDateFromTheInjectedClock()
    {
        RecordingFinalize finalize = new();
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 7, 8, 1, 0, 0, TimeSpan.Zero));

        int exit = Finalize(finalize, clock).Execute(null!, FinalizeSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(new DateOnly(2026, 7, 8), finalize.ApprovalDate);
    }

    private static DocsCloseoutSettings CloseoutSettings() => new()
    {
        Path = Plan,
        Format = "json",
        WorkingDirectory = WorkingDirectory,
    };

    private static DocsFinalizeSettings FinalizeSettings() => new()
    {
        Path = Plan,
        Format = "json",
        WorkingDirectory = WorkingDirectory,
        ApprovalMode = "autonomous",
        Approval = "APPROVED",
    };

    private DocsFinalizeCommand Finalize(RecordingFinalize finalize, TimeProvider clock) => new(
        finalize,
        new InMemoryRepositoryFileStore(_files),
        _configs,
        clock,
        _console,
        Plans());

    private DocsCloseoutCommand Closeout(RecordingCloseout closeout, TimeProvider clock) => new(
        closeout,
        new InMemoryRepositoryFileStore(_files),
        _configs,
        clock,
        _console,
        Plans());

    private PlanArguments Plans() => new(
        new ArbiterRepoRoot(_files, _paths, _environment),
        _files,
        _paths,
        _environment,
        _console);

    private sealed class RecordingCloseout : IPlanCloseoutService
    {
        public DateOnly? ArchiveDate { get; private set; }

        public PlanCloseoutOutcome Run(
            string repoRoot, KyberWeaveConfig config, string planPath, DateOnly archiveDate, bool archive)
        {
            ArchiveDate = archiveDate;
            return new PlanCloseoutOutcome(new DiagnosticReport(), false);
        }
    }

    private sealed class RecordingFinalize : IPlanFinalizeService
    {
        public DateOnly? ApprovalDate { get; private set; }

        public PlanFinalizeOutcome Run(
            string repoRoot,
            KyberWeaveConfig config,
            string planPath,
            PlanApprovalMode mode,
            string? approval,
            DateOnly approvalDate)
        {
            ApprovalDate = approvalDate;
            return new PlanFinalizeOutcome(new DiagnosticReport(), false, false, "tasks.yml");
        }
    }
}
