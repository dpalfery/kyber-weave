using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KyberDash.Application;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Services;
using KyberDash.Domain;
using Moq;
using Xunit;

namespace KyberDash.Application.Tests.Ingest;

/// <summary>
/// Verifies that an import-history job reports the failed source units of its refresh report:
/// a job in which one unit failed is <c>partial</c> with <c>failedUnits</c> 1, never <c>succeeded</c>,
/// and a clean import is <c>succeeded</c> with <c>failedUnits</c> 0.
/// </summary>
public class ImportHistoryOutcomeTests
{
    /// <summary>An import in which one source unit failed reports the partial state and the count of failed units.</summary>
    [Fact]
    public async Task TriggerImportHistory_WhenOneUnitFails_ReportsPartialWithOneFailedUnit()
    {
        ImportHarness harness = new(
            new RefreshSourceStatusRow("droid", "sess-a", "committed", 3),
            new RefreshSourceStatusRow("droid", "sess-b", "failed", 0, "unreadable"));

        JobOutcome outcome = harness.Jobs.TriggerImportHistory();

        Assert.Equal("partial", outcome.Outcome);
        Assert.Equal(1, outcome.FailedUnits);
        Assert.Equal(1, harness.Jobs.GetStatus().LastFailedUnits);
    }

    /// <summary>A clean import reports succeeded with no failed units.</summary>
    [Fact]
    public async Task TriggerImportHistory_WhenEveryUnitIsClean_ReportsSucceededWithZeroFailedUnits()
    {
        ImportHarness harness = new(
            new RefreshSourceStatusRow("droid", "sess-a", "committed", 3),
            new RefreshSourceStatusRow("cursor", "sess-c", "skipped", 0));

        JobOutcome outcome = harness.Jobs.TriggerImportHistory();

        Assert.Equal("succeeded", outcome.Outcome);
        Assert.Equal(0, outcome.FailedUnits);
        Assert.Equal(0, harness.Jobs.GetStatus().LastFailedUnits);
    }

    /// <summary>The fakes one import test runs over: a scheduler whose ingestion returns a fixed refresh report.</summary>
    private sealed class ImportHarness
    {
        public ImportHarness(params RefreshSourceStatusRow[] rows)
        {
            Store = new InMemoryRelationalStore();
            Settings = new FakeSettingsRepository();
            ServerState = new FakeServerStateRepository();
            Ingestion = new Mock<ITranscriptIngestionService>();
            Ingestion
                .Setup(service => service.RefreshHarnessSources(It.IsAny<int>()))
                .Returns(new RefreshReport(
                    RunId: "run-import",
                    HistoryWeeks: 2,
                    CommandStartedAt: "2026-11-24T12:00:00.000Z",
                    Rows: rows,
                    PurgedContentCount: 0,
                    Sessions: 0,
                    Runs: 0,
                    Executions: 0,
                    Rollups: 0,
                    ExitCode: rows.Any(static row => row.Status == "failed") ? 1 : 0));
            Jobs = new JobSchedulerService(
                Store,
                Store,
                new Mock<ICanonProjectionService>().Object,
                Ingestion.Object,
                Settings,
                ServerState,
                new DashOptions { StateDirectory = ServerState.DefaultStateDirectory });
        }

        public InMemoryRelationalStore Store { get; }

        public FakeSettingsRepository Settings { get; }

        public FakeServerStateRepository ServerState { get; }

        public Mock<ITranscriptIngestionService> Ingestion { get; }

        public JobSchedulerService Jobs { get; }
    }
}
