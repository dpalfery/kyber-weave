using System.Text.Json;
using KyberDash.Api;
using KyberDash.Contracts.Services;
using KyberDash.Domain;
using KyberDash.Mcp;
using Moq;
using Xunit;

namespace KyberDash.Presentation.Tests;

/// <summary>Collects the mocks and transport settings a presentation test configures before start.</summary>
public class PresentationBuilder
{
    public Mock<IDashboardQueryService> Query { get; } = new();

    public Mock<IAnalyticsService> Analytics { get; } = new();

    public Mock<IOtlpCollectorService> Otlp { get; } = new();

    public Mock<ISettingsService> Settings { get; } = new();

    public Mock<IWebAssetService> Assets { get; } = new();

    public Mock<ICaptureService> Capture { get; } = new();

    public Mock<ICleanService> Clean { get; } = new();

    public Mock<IJobSchedulerService> Jobs { get; } = new();

    public Mock<ITranscriptIngestionService> Ingestion { get; } = new();

    public Mock<ICanonProjectionService> Projection { get; } = new();

    public Mock<IDoctorService> Doctor { get; } = new();

    public Mock<IReportService> Reports { get; } = new();

    public Mock<IMenubarService> Menubar { get; } = new();

    public Mock<IEngineLifecycleService> Lifecycle { get; } = new();

    public Mock<IE2eSeederService> Seeder { get; } = new();

    public DashRuntimeSettings SettingsRuntime { get; } = new()
    {
        StateDirectory = Path.GetTempPath(),
        WebPort = 0,
        WebPortExplicit = true,
        OpenBrowser = false,
    };

    /// <summary>One instance per mocked contract, in container-registration order.</summary>
    public IReadOnlyCollection<object> AllSubstitutes() =>
    [
        Query.Object,
        Analytics.Object,
        Otlp.Object,
        Settings.Object,
        Assets.Object,
        Capture.Object,
        Clean.Object,
        Jobs.Object,
        Ingestion.Object,
        Projection.Object,
        Doctor.Object,
        Reports.Object,
        Menubar.Object,
        Lifecycle.Object,
        Seeder.Object,
    ];

    /// <summary>Answers every service call a test does not assert on.</summary>
    public void ApplyCannedResults()
    {
        JsonElement empty = PresentationTestData.EmptyArray;

        Query.Setup(q => q.GetMeta()).Returns(PresentationTestData.Meta);
        Query.Setup(q => q.GetVersion()).Returns(PresentationTestData.Version);
        Query.Setup(q => q.GetSessionPage(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(PresentationTestData.PageEnvelope("sessions"));
        Query.Setup(q => q.GetSubagentPage(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(PresentationTestData.PageEnvelope("subagents"));
        Query.Setup(q => q.GetSessionSummary(It.IsAny<string>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetSessionCharts(It.IsAny<string>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetSessionRequests(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetSessionReconciliation(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetSessionProblems(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetSessionContent(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetTurnFigures(It.IsAny<string>(), It.IsAny<int>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetTurnContent(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetSpanAttributes(It.IsAny<string>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetStoredFieldsInventory(It.IsAny<string>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetStoredFieldRows(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetCompare()).Returns(empty);
        Query.Setup(q => q.GetCompareRuns(It.IsAny<string>(), It.IsAny<string>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetQuarantinePage(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>())).Returns(empty);
        Query.Setup(q => q.GetQuarantineDetail(It.IsAny<string>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetProblemsPage(It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>())).Returns(empty);
        Query.Setup(q => q.GetProblemDetail(It.IsAny<string>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetCoverage()).Returns(empty);
        Query.Setup(q => q.GetReport(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>())).Returns(empty);
        Query.Setup(q => q.GetFindings(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>())).Returns(empty);
        Query.Setup(q => q.GetFindingById(It.IsAny<string>())).Returns((Finding?)null);
        Query.Setup(q => q.GetPredictions(It.IsAny<string?>())).Returns(empty);
        Query.Setup(q => q.GetCalibration()).Returns(empty);
        Query.Setup(q => q.GetHarnesses()).Returns(empty);
        Query.Setup(q => q.GetHarnessById(It.IsAny<string>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetRuns(It.IsAny<string?>())).Returns(empty);
        Query.Setup(q => q.GetRunById(It.IsAny<string>())).Returns((JsonElement?)null);
        Query.Setup(q => q.GetTimeline(It.IsAny<string?>())).Returns(empty);
        Query.Setup(q => q.GetModelCatalog()).Returns(empty);
        Query
            .Setup(q => q.TryRecordPrediction(
                It.IsAny<JsonElement>(),
                out It.Ref<PredictionRow?>.IsAny,
                out It.Ref<string?>.IsAny))
            .Returns(true);

        Analytics
            .Setup(a => a.Execute(It.IsAny<string>(), It.IsAny<JsonElement?>()))
            .Returns(new AnalyticsExecutionResult(200, PresentationTestData.ContractEnvelope(new { ok = true })));

        Otlp.Setup(o => o.PendingBytes).Returns(0);
        Otlp.Setup(o => o.PendingCount).Returns(0);
        Otlp.Setup(o => o.IsPaused).Returns(false);

        Settings.Setup(s => s.GetSettings()).Returns(PresentationTestData.Settings);
        Settings.Setup(s => s.UpdateSettings(It.IsAny<JsonElement>())).Returns(PresentationTestData.Settings);

        Jobs.Setup(j => j.GetStatus()).Returns(PresentationTestData.JobStatus);
        Jobs.Setup(j => j.TriggerRefreshAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new JobOutcome("succeeded"));
        Jobs.Setup(j => j.TriggerImportHistoryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new JobOutcome("succeeded"));

        Capture
            .Setup(c => c.InstallPlugin(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns((0, "plugin: unchanged"));
        Capture
            .Setup(c => c.SetFactoryConsent(It.IsAny<bool>(), It.IsAny<string?>()))
            .Returns((0, "factory consent: updated"));

        Clean
            .Setup(c => c.CleanDatabase(
                It.IsAny<CleanScope>(),
                It.IsAny<Action?>(),
                It.IsAny<Action?>(),
                It.IsAny<Func<IReadOnlyList<string>?, int, int>?>()))
            .Returns(PresentationTestData.EmptyCleanReport);

        Projection
            .Setup(p => p.ProjectAll())
            .Returns((PresentationTestData.EmptyProjection, (IReadOnlyList<HarnessRollupRow>)[]));
        Projection
            .Setup(p => p.ProjectIncremental())
            .Returns(PresentationTestData.EmptyProjection);

        Doctor.Setup(d => d.RunDiagnostics()).Returns(PresentationTestData.DoctorReport);

        Reports
            .Setup(r => r.GenerateReport(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<int>()))
            .Returns((string format, string? harness, string? session, string? run, int limit) =>
            {
                LastReport = new ReportCall(format, harness, session, run, limit);
                return (0, "report body");
            });

        Menubar
            .Setup(m => m.InstallOrUpdateAsync(
                It.IsAny<bool>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PresentationTestData.MenubarOutcome);

        Lifecycle
            .Setup(l => l.EnsureRunningEngineMatchesVersionAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PresentationTestData.EngineReport);
        Lifecycle
            .Setup(l => l.StopRunningEngineAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineEnsureReport(true, "engine: not-running"));
        Lifecycle
            .Setup(l => l.VerifyRunningEngineVersionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EngineEnsureReport(true, "engine: running 2.0.0 (pid 42)", Outcome: EngineVerifyOutcome.Running));
        Lifecycle
            .Setup(l => l.ResolveLiveServerUriAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Uri?)null);

        Seeder
            .Setup(s => s.SeedWorld(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(0);

        // No bundle by default, which is what an unpacked developer build looks like.
        Assets
            .Setup(a => a.TryResolveAsset(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((WebAssetDocument?)null);
    }

    /// <summary>The arguments the report service was last called with.</summary>
    public ReportCall? LastReport { get; private set; }

    /// <summary>
    /// Resolves registrations from the container the CLI built itself. Moq types are trimmed away,
    /// so the host only ever sees the contract implementation. A contract the container does not
    /// register falls back to the mock, which lets a test inspect what the router asked for.
    /// </summary>
    /// <param name="container">The real container built by <see cref="KyberDash.Cli.Program.BuildProvider(string[]?)"/>.</param>
    /// <param name="mocks">The substitutes a test configured.</param>
    /// <returns>A provider that prefers the container and falls back to the mocks.</returns>
    public static IServiceProvider Overlay(IServiceProvider container, PresentationBuilder mocks)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(mocks);

        Dictionary<Type, object> substitutes = [];
        foreach (object substitute in mocks.AllSubstitutes())
            substitutes[PrimaryContract(substitute)] = substitute;

        return new OverlayProvider(container, substitutes);
    }

    /// <summary>Finds the <c>KyberDash.*</c> contract a Moq object implements.</summary>
    /// <param name="substitute">A mock's <c>Object</c>.</param>
    /// <returns>The contract type.</returns>
    public static Type PrimaryContract(object substitute)
    {
        ArgumentNullException.ThrowIfNull(substitute);

        foreach (Type candidate in substitute.GetType().GetInterfaces())
        {
            if (candidate.Namespace is not null
                && candidate.Namespace.StartsWith("KyberDash.", StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        throw new ArgumentException($"'{substitute.GetType().FullName}' does not implement a KyberDash contract.", nameof(substitute));
    }

    /// <summary>Substitutes the test's mocks ahead of the container's registrations.</summary>
    private class OverlayProvider(IServiceProvider container, IReadOnlyDictionary<Type, object> substitutes) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return substitutes.TryGetValue(serviceType, out object? substitute)
                ? substitute
                : container.GetService(serviceType);
        }
    }
}
