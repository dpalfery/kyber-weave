using System.Text.Json;
using KyberDash.Contracts.Services;
using KyberDash.Domain;

namespace KyberDash.Application;

/// <summary>
/// Read-side query service backing the REST dashboard API via <see cref="KyberBridgeEngine"/>.
/// </summary>
/// <remarks>
/// Every call first brings projections up to date, so a dashboard never renders a stale session
/// list after OTLP traffic landed. Filtering, pagination, and JSON shaping belong to the bridge
/// engine; this service only decides when to project and forwards the request.
/// </remarks>
public class DashboardQueryService : IDashboardQueryService
{
    private readonly KyberBridgeEngine bridge;

    /// <summary>
    /// Initializes a new <see cref="DashboardQueryService"/>.
    /// </summary>
    /// <param name="bridge">Bridge engine answering every dashboard read.</param>
    public DashboardQueryService(KyberBridgeEngine bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        this.bridge = bridge;
    }

    /// <inheritdoc />
    public void EnsureProjected() => bridge.EnsureProjected();

    /// <inheritdoc />
    public JsonElement GetSessionPage(int page = 1, int limit = KyberBridgeEngine.DefaultSessionPageLimit, string? harness = null, string? search = null) =>
        bridge.GetSessionPage(page, limit, harness, search);

    /// <inheritdoc />
    public JsonElement GetSubagentPage(string parentId, int page = 1, int limit = KyberBridgeEngine.DefaultSubagentPageLimit) =>
        bridge.GetSubagentPage(parentId, page, limit);

    /// <inheritdoc />
    public JsonElement? GetSessionSummary(string sessionId) => bridge.GetSessionSummary(sessionId);

    /// <inheritdoc />
    public JsonElement? GetSessionCharts(string sessionId) => bridge.GetSessionCharts(sessionId);

    /// <inheritdoc />
    public JsonElement? GetSessionRequests(string sessionId, int page = 1, int limit = 25) =>
        bridge.GetSessionRequests(sessionId, page, limit);

    /// <inheritdoc />
    public JsonElement? GetSessionReconciliation(string sessionId, int page = 1, int limit = 25) =>
        bridge.GetSessionReconciliation(sessionId, page, limit);

    /// <inheritdoc />
    public JsonElement? GetSessionProblems(string sessionId, int page = 1, int limit = 25) =>
        bridge.GetSessionProblems(sessionId, page, limit);

    /// <inheritdoc />
    public JsonElement? GetSessionTools(string sessionId, int page = 1, int limit = 25) =>
        bridge.GetSessionTools(sessionId, page, limit);

    /// <inheritdoc />
    public JsonElement? GetSessionServers(string sessionId, int page = 1, int limit = 25) =>
        bridge.GetSessionServers(sessionId, page, limit);

    /// <inheritdoc />
    public JsonElement? GetSessionSkills(string sessionId, int page = 1, int limit = 25) =>
        bridge.GetSessionSkills(sessionId, page, limit);

    /// <inheritdoc />
    public JsonElement? GetSessionKwSkills(string sessionId, int page = 1, int limit = 25) =>
        bridge.GetSessionKwSkills(sessionId, page, limit);

    /// <inheritdoc />
    public JsonElement? GetSessionTimeline(string sessionId, string? parentNodeId = null, int page = 1, int limit = 25) =>
        bridge.GetSessionTimeline(sessionId, parentNodeId, page, limit);

    /// <inheritdoc />
    public JsonElement? GetTurnFigures(string sessionId, int turnIndex) => bridge.GetTurnFigures(sessionId, turnIndex);

    /// <inheritdoc />
    public JsonElement? GetTurnContent(string sessionId, int turnIndex, int budget = 32_768, bool full = false) =>
        bridge.GetTurnContent(sessionId, turnIndex, budget, full);

    /// <inheritdoc />
    public JsonElement? GetSessionContent(
        string sessionId,
        int? turn = null,
        string? spanId = null,
        string? part = null,
        bool full = false) =>
        bridge.GetSessionContent(sessionId, turn, spanId, part, full);

    /// <inheritdoc />
    public JsonElement? GetSpanAttributes(string spanId) => bridge.GetSpanAttributes(spanId);

    /// <inheritdoc />
    public JsonElement GetFindings(
        string? runId = null,
        string? sessionId = null,
        string? detector = null,
        string? harness = null,
        int limit = 50,
        int offset = 0,
        int page = 1) =>
        bridge.GetFindings(runId, sessionId, detector, harness, limit, offset, page);

    /// <inheritdoc />
    public Finding? GetFindingById(string findingId) => bridge.GetFindingById(findingId);

    /// <inheritdoc />
    public JsonElement GetHarnesses() => bridge.GetHarnesses();

    /// <inheritdoc />
    public JsonElement? GetHarnessById(string harnessId) => bridge.GetHarnessById(harnessId);

    /// <inheritdoc />
    public JsonElement GetRuns(string? harness = null) => bridge.GetRuns(harness);

    /// <inheritdoc />
    public JsonElement? GetRunById(string runId) => bridge.GetRunById(runId);

    /// <inheritdoc />
    public JsonElement GetCompare() => bridge.GetCompare();

    /// <inheritdoc />
    public JsonElement? GetCompareRuns(string runA, string runB) => bridge.GetCompareRuns(runA, runB);

    /// <inheritdoc />
    public JsonElement GetQuarantinePage(string? harness, int limit, int offset, int page) =>
        bridge.GetQuarantinePage(harness, limit, offset, page);

    /// <inheritdoc />
    public JsonElement? GetQuarantineDetail(string spanId) => bridge.GetQuarantineDetail(spanId);

    /// <inheritdoc />
    public JsonElement GetProblemsPage(string? harness, int limit, int offset, int page) =>
        bridge.GetProblemsPage(harness, limit, offset, page);

    /// <inheritdoc />
    public JsonElement? GetProblemDetail(string rawId) => bridge.GetProblemDetail(rawId);

    /// <inheritdoc />
    public JsonElement GetMeta() => bridge.GetMeta();

    /// <inheritdoc />
    public JsonElement GetVersion() => bridge.GetVersion();

    /// <inheritdoc />
    public JsonElement GetCoverage() => bridge.GetCoverage();

    /// <inheritdoc />
    public JsonElement GetReport(string? harness = null, string? sessionId = null, string? runId = null, int limit = 50) =>
        bridge.GetReport(harness, sessionId, runId, limit);

    /// <inheritdoc />
    public JsonElement GetReport(string? harness, string? sessionId, string? runId, int limit, int days) =>
        bridge.GetReport(harness, sessionId, runId, limit, days);

    /// <inheritdoc />
    public JsonElement GetTimeline(string? sessionId = null) => bridge.GetTimeline(sessionId);

    /// <inheritdoc />
    public JsonElement GetPredictions(string? runId = null) => bridge.GetPredictions(runId);

    /// <inheritdoc />
    public bool TryRecordPrediction(JsonElement body, out PredictionRow? created, out string? errorMessage) =>
        bridge.TryRecordPrediction(body, out created, out errorMessage);

    /// <inheritdoc />
    public JsonElement GetCalibration() => bridge.GetCalibration();

    /// <inheritdoc />
    public JsonElement GetModelCatalog() => bridge.GetModelCatalog();

    /// <inheritdoc />
    public JsonElement? GetStoredFieldsInventory(string sessionKey) => bridge.GetStoredFieldsInventory(sessionKey);

    /// <inheritdoc />
    public JsonElement? GetStoredFieldRows(string sessionKey, string table, int page = 1, int limit = 25) =>
        bridge.GetStoredFieldRows(sessionKey, table, page, limit);
}
