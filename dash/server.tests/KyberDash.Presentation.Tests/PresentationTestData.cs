using System.Text.Json;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Presentation.Tests;

/// <summary>
/// Canned answers for every application service a presentation host resolves, so a test only has
/// to configure the calls it actually asserts on.
/// </summary>
/// <remarks>
/// The real container is built first. With <see cref="KyberDash.Application.ServiceCollectionExtensions.AddDashApplication"/>
/// registering the production services, an unmutated mock would return <see langword="null"/> for
/// a non-nullable return and fail inside the endpoint. These defaults keep every unrelated route
/// answerable.
/// </remarks>
public static class PresentationTestData
{
    /// <summary>Settings returned by <see cref="ISettingsService.GetSettings"/>.</summary>
    public static SettingsView Settings { get; } = new(
        FolderImportScheduled: false,
        JobsPaused: false,
        RefreshCadenceMinutes: 5,
        ReceiverHosted: true,
        DashboardPort: 4747);

    /// <summary>Idle job host status.</summary>
    public static JobHostStatus JobStatus { get; } = new(
        "idle",
        null,
        null,
        null,
        Paused: false,
        HostedElsewhere: false,
        null,
        null);

    /// <summary>An empty JSON array payload.</summary>
    public static JsonElement EmptyArray { get; } = JsonSerializer.SerializeToElement(Array.Empty<object>());

    /// <summary>
    /// An empty paging envelope keyed by <paramref name="itemsKey"/> — the shared shape the
    /// sessions and subagents pages answer with.
    /// </summary>
    /// <param name="itemsKey">Name of the items member, mirrored into <c>data</c>.</param>
    /// <returns>The encoded envelope.</returns>
    public static JsonElement PageEnvelope(string itemsKey) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            [itemsKey] = Array.Empty<object>(),
            ["data"] = Array.Empty<object>(),
            ["total"] = 0,
            ["limit"] = 25,
            ["offset"] = 0,
            ["page"] = 1,
        });

    /// <summary>A version payload.</summary>
    public static JsonElement Version { get; } = JsonSerializer.SerializeToElement(new { version = "0.21.0", apiVersion = 1 });

    /// <summary>A metadata payload.</summary>
    public static JsonElement Meta { get; } = JsonSerializer.SerializeToElement(new { apiVersion = 1, harnesses = Array.Empty<string>() });

    /// <summary>A clean-with-nothing-to-delete report.</summary>
    public static CleanReport EmptyCleanReport { get; } = new(
        Harnesses: [],
        Wipe: new StoreWipeReport(0, 0, 0, 0, 0, 0, 0, 0, 0, "2000-01-01T00:00:00.000Z"),
        Reingested: false,
        HistoryWeeks: null);

    /// <summary>Diagnostics with an empty store.</summary>
    public static DoctorDiagnosticsReport DoctorReport { get; } = new(
        DatabasePath: "canon.db",
        Records: 0,
        Sessions: 0,
        ObservedHarnesses: [],
        CircuitBreakerState: "closed");

    /// <summary>An install that changed nothing.</summary>
    public static MenubarInstallOutcome MenubarOutcome { get; } = new(0, "menubar: unchanged");

    /// <summary>No engine running.</summary>
    public static EngineEnsureReport EngineReport { get; } = new(Succeeded: true, Message: "engine: not-running");

    /// <summary>An incremental projection that did nothing.</summary>
    public static ProjectionPassReport EmptyProjection { get; } = new(0, 0, 0, 0, [], []);

    /// <summary>A minimal valid contract-v1 analytics envelope wrapping <paramref name="data"/>.</summary>
    /// <param name="data">The envelope's <c>data</c> member.</param>
    /// <returns>The encoded envelope.</returns>
    public static JsonElement ContractEnvelope(object? data) =>
        JsonSerializer.SerializeToElement(new
        {
            contractVersion = 1,
            scope = new { },
            data,
            measurement = new { },
            coverage = new { },
            freshness = new { },
            evidence = Array.Empty<object>(),
            pagination = new { },
        });
}

/// <summary>The arguments a report render was asked for.</summary>
public class ReportCall
{
    /// <summary>Creates a record of one report render request.</summary>
    /// <param name="format">Rendered format.</param>
    /// <param name="harness">Harness filter.</param>
    /// <param name="session">Session filter.</param>
    /// <param name="run">Run filter.</param>
    /// <param name="limit">Row limit.</param>
    public ReportCall(string format, string? harness, string? session, string? run, int limit)
    {
        Format = format;
        Harness = harness;
        Session = session;
        Run = run;
        Limit = limit;
    }

    /// <summary>Rendered format.</summary>
    public string Format { get; }

    /// <summary>Harness filter.</summary>
    public string? Harness { get; }

    /// <summary>Session filter.</summary>
    public string? Session { get; }

    /// <summary>Run filter.</summary>
    public string? Run { get; }

    /// <summary>Row limit.</summary>
    public int Limit { get; }
}
