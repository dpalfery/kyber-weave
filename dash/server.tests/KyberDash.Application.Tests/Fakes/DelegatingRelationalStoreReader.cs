using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Domain;

namespace KyberDash.Application.Tests.Fakes;

/// <summary>
/// Reader decorator that forwards every <see cref="IRelationalStoreReader"/> member to the reader
/// it wraps, so a test decorator overrides only the member whose behaviour it injects.
/// </summary>
/// <remarks>
/// Every member is <see langword="virtual"/> because partial overriding is the reason the type
/// exists: a test wraps the store or any other reader and changes one call.
/// </remarks>
internal abstract class DelegatingRelationalStoreReader : IRelationalStoreReader
{
    /// <summary>The reader every member delegates to.</summary>
    protected readonly IRelationalStoreReader inner;

    /// <summary>Wraps <paramref name="inner"/>, which receives every member this type does not override.</summary>
    /// <param name="inner">The reader to delegate to.</param>
    protected DelegatingRelationalStoreReader(IRelationalStoreReader inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
    }

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredSessionAttribution> SessionAttributions() => inner.SessionAttributions();

    /// <inheritdoc />
    public virtual IReadOnlyList<CollectedRecord> SessionRecordsForRebuild(string sessionKey) => inner.SessionRecordsForRebuild(sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<CollectedRecord> AllSessionRecordsForMaintenance() => inner.AllSessionRecordsForMaintenance();

    /// <inheritdoc />
    public virtual IReadOnlyList<string> Harnesses() => inner.Harnesses();

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredSource> Sources(string? harnessId = null) => inner.Sources(harnessId);

    /// <inheritdoc />
    public virtual int CountRecords(string? harnessId = null) => inner.CountRecords(harnessId);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredSourceCount> SourceRecordCounts() => inner.SourceRecordCounts();

    /// <inheritdoc />
    public virtual IReadOnlyList<string> SessionKeysWithRecordSource(string harnessId, string sourceName, int limit) =>
        inner.SessionKeysWithRecordSource(harnessId, sourceName, limit);

    /// <inheritdoc />
    public virtual CollectedRecord? RecordContentFree(string spanId) => inner.RecordContentFree(spanId);

    /// <inheritdoc />
    public virtual string? SpanPromptText(string spanId) => inner.SpanPromptText(spanId);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredTokenCount> TokenCounts(string? spanId = null, string? sessionKey = null) => inner.TokenCounts(spanId, sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredReportedTotal> ReportedTotals(string? spanId = null, string? sessionKey = null) => inner.ReportedTotals(spanId, sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredReportedCost> ReportedCosts(string? spanId = null, string? sessionKey = null) => inner.ReportedCosts(spanId, sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredSkillInvocation> SkillsInvoked(string? spanId = null, string? sessionKey = null) => inner.SkillsInvoked(spanId, sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredToolCall> ToolCalls(string? spanId = null, string? sessionKey = null) => inner.ToolCalls(spanId, sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredContentPart> TurnContentParts(
        string sessionKey,
        int? turnIndex = null,
        string? spanId = null,
        string? part = null,
        int budget = 0,
        bool full = false) =>
        inner.TurnContentParts(sessionKey, turnIndex, spanId, part, budget, full);

    /// <inheritdoc />
    public virtual int TurnContentPartCount(
        string sessionKey,
        int? turnIndex = null,
        string? spanId = null,
        string? part = null) =>
        inner.TurnContentPartCount(sessionKey, turnIndex, spanId, part);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredToolDefinition> ToolDefinitions(string? spanId = null, string? sessionKey = null) => inner.ToolDefinitions(spanId, sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredAgentInvocation> AgentInvocations(string? spanId = null, string? sessionKey = null) => inner.AgentInvocations(spanId, sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredRecordSignals> RecordSignals(string? spanId = null, string? sessionKey = null) => inner.RecordSignals(spanId, sessionKey);

    /// <inheritdoc />
    public virtual CollectedSession? GetSession(string sessionKey) => inner.GetSession(sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<CollectedSession> Sessions(string? harnessId = null) => inner.Sessions(harnessId);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredSessionModelUsage> SessionModelUsages(string? sessionKey = null) => inner.SessionModelUsages(sessionKey);

    /// <inheritdoc />
    public virtual StoredFieldsInventoryView StoredFieldsInventory(string sessionKey) => inner.StoredFieldsInventory(sessionKey);

    /// <inheritdoc />
    public virtual StoredTableRows? StoredFieldsRows(string sessionKey, string table, int page, int limit) => inner.StoredFieldsRows(sessionKey, table, page, limit);

    /// <inheritdoc />
    public virtual IReadOnlyList<SessionListFigure> SessionListFigures(string? harnessId = null, IModelPricingLookup? pricing = null) => inner.SessionListFigures(harnessId, pricing);

    /// <inheritdoc />
    public virtual IReadOnlyList<SessionListFigure> SessionListFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null) => inner.SessionListFigures(sessionKeys, pricing);

    /// <inheritdoc />
    public virtual SessionSummaryFigure? SessionSummaryFigures(string sessionKey, IModelPricingLookup? pricing = null) => inner.SessionSummaryFigures(sessionKey, pricing);

    /// <inheritdoc />
    public virtual IReadOnlyList<SessionReportFigure> SessionReportFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null) => inner.SessionReportFigures(sessionKeys, pricing);

    /// <inheritdoc />
    public virtual ReadTimeTurnSummary? TurnFigures(string sessionKey, int turnIndex, IModelPricingLookup? pricing = null) => inner.TurnFigures(sessionKey, turnIndex, pricing);

    /// <inheritdoc />
    public virtual SessionTurnFigureSet? SessionTurnFigures(string sessionKey, IModelPricingLookup? pricing = null) => inner.SessionTurnFigures(sessionKey, pricing);

    /// <inheritdoc />
    public virtual SessionRequestFigurePageResult SessionRequestFigures(string sessionKey, int page, int limit) => inner.SessionRequestFigures(sessionKey, page, limit);

    /// <inheritdoc />
    public virtual SessionReconciliationFigurePageResult SessionReconciliationFigures(string sessionKey, int page, int limit) => inner.SessionReconciliationFigures(sessionKey, page, limit);

    /// <inheritdoc />
    public virtual SessionProblemPageResult SessionProblems(string sessionKey, int page, int limit) => inner.SessionProblems(sessionKey, page, limit);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredRequestPromptPreview> RequestPromptPreviews(string sessionKey, IReadOnlyCollection<string> requestSpanIds) => inner.RequestPromptPreviews(sessionKey, requestSpanIds);

    /// <inheritdoc />
    public virtual IReadOnlyList<CollectedRecord> SessionRecordsContentFree(string sessionKey) => inner.SessionRecordsContentFree(sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<CollectedRecord> SessionTimelineRecords(string sessionKey) => inner.SessionTimelineRecords(sessionKey);

    /// <inheritdoc />
    public virtual SessionToolFigureSet SessionToolFigures(string sessionKey, IModelPricingLookup? pricing = null) => inner.SessionToolFigures(sessionKey, pricing);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredSkillInvocation> SessionSkillFigures(string sessionKey) => inner.SessionSkillFigures(sessionKey);

    /// <inheritdoc />
    public virtual IReadOnlyList<StoredSkillListing> SessionSkillListings(string sessionKey) => inner.SessionSkillListings(sessionKey);

    /// <inheritdoc />
    public virtual SessionKwSkillLoadPageResult SessionKwSkillLoads(string sessionKey, int page, int limit) => inner.SessionKwSkillLoads(sessionKey, page, limit);
}
