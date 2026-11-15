using Xunit;
using System.Text.Json;
using KyberDash.Domain;

namespace KyberDash.Data.Tests;

/// <summary>Canned domain values shared by the repository tests.</summary>
internal static class TestData
{
    /// <summary>Parses <paramref name="json"/> into a detached <see cref="JsonElement"/>.</summary>
    /// <param name="json">JSON text.</param>
    /// <returns>The parsed element.</returns>
    internal static JsonElement Json(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>Builds a canonical record with sane defaults for the fields a test does not care about.</summary>
    /// <param name="spanId">Span identifier.</param>
    /// <param name="harness">Attributed harness.</param>
    /// <param name="sessionId">Session identifier; the trace id is used when omitted.</param>
    /// <param name="timestamp">ISO-8601 UTC timestamp.</param>
    /// <returns>The record.</returns>
    internal static CanonicalRecord Record(
        string spanId,
        string harness = "claude-code",
        string? sessionId = "session-1",
        string timestamp = "2026-01-01T00:00:00.000Z") =>
        new()
        {
            SpanId = spanId,
            TraceId = "trace-" + spanId,
            Source = "otlp",
            Harness = harness,
            SessionId = sessionId,
            Name = "model.call",
            Op = "llm.invoke",
            Kind = "client",
            Timestamp = timestamp,
            DurationMs = 12,
            Status = "ok",
            Tokens = new TokenUsage(FreshInput: 10, CacheRead: 2, CacheCreation: 3, Output: 5, ReportedInput: 15, ReportedOutput: 5),
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["conversation_history"] = "hello" },
            Parts = [new ContentPart("conversation_history", "hello", Tokens: 2, Role: "user", Order: 0)],
            Cost = new CostBlock(CostBasis.Published, CostStatus.Priced, 0.25, "USD"),
            Measurability = new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["tokens"] = Json("{\"availability\":\"measured\"}") },
            Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["gen_ai.system"] = Json("\"anthropic\""),
                ["provenance"] = Json("{\"recordDigest\":\"abc\"}"),
            },
        };

    /// <summary>Builds a session row whose payload embeds no turns.</summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="harness">Attributed harness.</param>
    /// <param name="turns">Turns to persist alongside the session.</param>
    /// <returns>The row.</returns>
    internal static SessionSummaryRow Session(
        string sessionId,
        string harness = "claude-code",
        params int[] turns)
    {
        List<JsonElement>? turnPayloads = null;
        if (turns.Length > 0)
        {
            turnPayloads = [];
            foreach (int turn in turns)
                turnPayloads.Add(Json($"{{\"index\":{turn},\"text\":\"turn {turn}\"}}"));
        }

        return new SessionSummaryRow(
            SessionId: sessionId,
            Harness: harness,
            Label: "label-" + sessionId,
            IsSubagent: false,
            ParentSession: null,
            AgentName: "agent",
            Repo: "repo",
            Branch: "main",
            Started: "2026-01-01T00:00:00.000Z",
            Ended: "2026-01-01T00:10:00.000Z",
            SummaryJson: null,
            ProblemsCount: null,
            ContextLimitSource: null,
            Payload: Json("{\"title\":\"demo\"}"),
            Turns: turnPayloads);
    }

    /// <summary>Builds a diagnostic finding with a payload field the store re-reads.</summary>
    /// <param name="id">Finding identifier.</param>
    /// <param name="sessionId">Session identifier the finding belongs to.</param>
    /// <param name="runId">Run identifier the finding belongs to.</param>
    /// <returns>The finding.</returns>
    internal static Finding Finding(string id, string sessionId = "session-1", string? runId = "run-1") =>
        new(
            Id: id,
            DetectorId: "context-pressure",
            Title: "Peak context pressure",
            Mechanism: "context grew past the safety boundary",
            EvidenceLinks: [new FindingEvidenceLink("span-1", 0, "peak turn")],
            Confidence: "high",
            EstimatedWasteTokens: 1234,
            Recommendation: "Compact earlier.",
            ErrorBar: new FindingErrorBar(100, 2000),
            OutcomeRiskCaveat: "Inferred from telemetry.",
            RunId: runId,
            SessionId: sessionId,
            RankScore: 0.75,
            MeasurementClass: "inferred",
            ConfidenceBasis: "deterministic",
            WhatWouldRaiseIt: "A harness-reported window.");
}
