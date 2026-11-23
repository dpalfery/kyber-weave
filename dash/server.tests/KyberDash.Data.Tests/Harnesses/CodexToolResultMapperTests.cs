using System.Globalization;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CodexToolResultMapper"/> covering <c>codex.tool_result</c> OTLP
/// log events: the documented tool name, call id, success, and duration map to a
/// <c>tool.invoke</c> <see cref="CollectedRecord"/>, while <c>arguments</c> and the
/// <c>output</c> excerpt are dropped. Attribute names follow the authoritative
/// <c>openai/codex</c> telemetry source (<c>codex-rs/otel/src/tool_result.rs</c>).
/// </summary>
public class CodexToolResultMapperTests
{
    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsToolResult_ToolResultEventName_ReturnsTrue()
    {
        OtlpLog log = ToolResultLog(
            logId: "log-tool-result-1",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = Element("codex.tool_result"),
            });

        Assert.True(CodexToolResultMapper.IsToolResult(log));
    }

    [Fact]
    public void IsToolResult_OtherEventName_ReturnsFalse()
    {
        OtlpLog log = ToolResultLog(
            logId: "log-user-prompt-1",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = Element("codex.user_prompt"),
            });

        Assert.False(CodexToolResultMapper.IsToolResult(log));
    }

    [Fact]
    public void IsToolResult_MissingEventName_ReturnsFalse()
    {
        OtlpLog log = ToolResultLog(
            logId: "log-no-event-1",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal));

        Assert.False(CodexToolResultMapper.IsToolResult(log));
    }

    [Fact]
    public void DocumentedKeys_NameAuthoritativeToolResultAttributes()
    {
        Assert.Contains("tool_name", CodexToolResultMapper.MappedAttributeKeys);
        Assert.Contains("call_id", CodexToolResultMapper.MappedAttributeKeys);
        Assert.Contains("success", CodexToolResultMapper.MappedAttributeKeys);
        Assert.Contains("duration_ms", CodexToolResultMapper.MappedAttributeKeys);

        Assert.Contains("arguments", CodexToolResultMapper.DroppedPayloadAttributeKeys);
        Assert.Contains("output", CodexToolResultMapper.DroppedPayloadAttributeKeys);
    }

    [Fact]
    public void TryMap_SuccessEvent_MapsDocumentedAttributesToToolInvoke()
    {
        OtlpLog log = ToolResultLog(
            logId: "log-tool-result-success-1",
            timestamp: "2026-11-19T10:00:01.000Z",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = Element("codex.tool_result"),
                ["tool_name"] = Element("shell"),
                ["call_id"] = Element("call_shell_1"),
                ["success"] = Element(true),
                ["duration_ms"] = Element("840"),
                ["conversation.id"] = Element("conv-codex-1"),
                ["model"] = Element("gpt-5.2-codex"),
                ["arguments"] = Element("""{"command":["git","status"]}"""),
                ["output"] = Element("On branch main\nnothing to commit"),
            });

        bool handled = CodexToolResultMapper.TryMap(log, ImportedAtUtc, null, out CollectedRecord? record, out CollectedProblem? problem);

        Assert.True(handled);
        Assert.Null(problem);
        Assert.NotNull(record);
        Assert.Equal("log-tool-result-success-1", record.RecordId);
        Assert.Equal("codex-cli", record.HarnessId);
        Assert.Equal(RecordOp.ToolInvoke, record.Op);
        Assert.Equal(RecordKind.Internal, record.Kind);
        Assert.Equal("shell", record.Name);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:01.000Z", CultureInfo.InvariantCulture), record.StartedAt);
        Assert.Equal(RecordStatus.Ok, record.Status);
        Assert.Equal("conv-codex-1", record.SessionKey);
        Assert.Equal("span-ambient-1", record.ParentRecordId);
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", record.TraceId);
        Assert.Equal("gpt-5.2-codex", record.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:01.840Z", CultureInfo.InvariantCulture), record.EndedAt);
        Assert.Equal(840L, record.ReportedDurationMs);

        Assert.Empty(record.Tokens);
        Assert.Null(record.ReportedCost);
        Assert.Empty(record.Parts);
        Assert.Null(record.Agent);
        Assert.Empty(record.SkillsInvoked);
        Assert.Null(record.AgentInstanceId);
        Assert.Empty(record.ToolDefinitions);
        Assert.Null(record.Signals.ErrorMessage);

        Assert.NotNull(record.Tool);
        Assert.Equal("call_shell_1", record.Tool.CallId);
        Assert.Equal("shell", record.Tool.ToolName);
        Assert.Null(record.Tool.McpServerName);
        Assert.Null(record.Tool.McpToolName);
        Assert.Null(record.Tool.Input);
        Assert.Null(record.Tool.Result);
        Assert.True(record.Tool.Succeeded);
        Assert.Null(record.Tool.Error);
        Assert.Null(record.Tool.ExitCode);
    }

    /// <summary>
    /// A non-finite numeric <c>success</c> flag is not a result. IEEE says NaN and
    /// Infinity compare unequal to zero, so a bare <c>!= 0</c> would record success.
    /// </summary>
    [Fact]
    public void TryMap_NonFiniteSuccess_LeavesSucceededUnset()
    {
        using JsonDocument document = JsonDocument.Parse("{\"success\":1e309}");
        OtlpLog log = ToolResultLog(
            logId: "log-tool-result-nonfinite-1",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = Element("codex.tool_result"),
                ["tool_name"] = Element("shell"),
                ["call_id"] = Element("call_shell_nan"),
                ["success"] = document.RootElement.GetProperty("success").Clone(),
            });

        bool handled = CodexToolResultMapper.TryMap(log, ImportedAtUtc, null, out CollectedRecord? record, out CollectedProblem? problem);

        Assert.True(handled);
        Assert.Null(problem);
        Assert.NotNull(record);
        Assert.NotNull(record.Tool);
        Assert.Null(record.Tool.Succeeded);
    }

    [Fact]
    public void TryMap_FailureEvent_MapsErrorStatusWithoutPayload()
    {
        OtlpLog log = ToolResultLog(
            logId: "log-tool-result-failure-1",
            timestamp: "2026-11-19T10:05:00.000Z",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = Element("codex.tool_result"),
                ["tool_name"] = Element("shell"),
                ["call_id"] = Element("call_shell_2"),
                ["success"] = Element("false"),
                ["duration_ms"] = Element(1250),
                ["conversation.id"] = Element("conv-codex-1"),
                ["arguments"] = Element("""{"command":["exit","1"]}"""),
                ["output"] = Element("command failed"),
            });

        bool handled = CodexToolResultMapper.TryMap(log, ImportedAtUtc, null, out CollectedRecord? record, out CollectedProblem? problem);

        Assert.True(handled);
        Assert.Null(problem);
        Assert.NotNull(record);
        Assert.Equal(RecordOp.ToolInvoke, record.Op);
        Assert.Equal(RecordStatus.Error, record.Status);
        Assert.Equal(1250L, record.ReportedDurationMs);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:05:01.250Z", CultureInfo.InvariantCulture), record.EndedAt);

        Assert.NotNull(record.Tool);
        Assert.False(record.Tool.Succeeded);
        Assert.Null(record.Tool.Error);
        Assert.Null(record.Tool.Input);
        Assert.Null(record.Tool.Result);
        Assert.Empty(record.Parts);
    }

    [Fact]
    public void TryMap_MissingSuccessAndDuration_MapsUnsetWithoutDuration()
    {
        OtlpLog log = ToolResultLog(
            logId: "log-tool-result-partial-1",
            timestamp: "2026-11-19T10:06:00.000Z",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = Element("codex.tool_result"),
                ["tool_name"] = Element("shell"),
                ["call_id"] = Element("call_shell_3"),
                ["conversation.id"] = Element("conv-codex-1"),
            });

        bool handled = CodexToolResultMapper.TryMap(log, ImportedAtUtc, null, out CollectedRecord? record, out CollectedProblem? problem);

        Assert.True(handled);
        Assert.Null(problem);
        Assert.NotNull(record);
        Assert.Equal(RecordStatus.Unset, record.Status);
        Assert.Null(record.ReportedDurationMs);
        Assert.Null(record.EndedAt);
        Assert.NotNull(record.Tool);
        Assert.Null(record.Tool.Succeeded);
    }

    [Fact]
    public void TryMap_NonToolResultEvent_ReturnsFalseWithoutRecordOrProblem()
    {
        OtlpLog log = ToolResultLog(
            logId: "log-user-prompt-2",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = Element("codex.user_prompt"),
            });

        bool handled = CodexToolResultMapper.TryMap(log, ImportedAtUtc, null, out CollectedRecord? record, out CollectedProblem? problem);

        Assert.False(handled);
        Assert.Null(record);
        Assert.Null(problem);
    }

    [Fact]
    public void TryMap_MissingToolName_ReportsProblemWithoutPayload()
    {
        OtlpLog log = ToolResultLog(
            logId: "log-tool-result-no-name-1",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = Element("codex.tool_result"),
                ["call_id"] = Element("call_shell_4"),
                ["success"] = Element(true),
                ["duration_ms"] = Element(10),
                ["conversation.id"] = Element("conv-codex-1"),
            });

        bool handled = CodexToolResultMapper.TryMap(log, ImportedAtUtc, null, out CollectedRecord? record, out CollectedProblem? problem);

        Assert.True(handled);
        Assert.Null(record);
        Assert.NotNull(problem);
        Assert.Equal(CodexToolResultMapper.InvalidToolResultCode, problem.Code);
        Assert.Contains("tool_name", problem.Message, StringComparison.Ordinal);
        Assert.Equal("log-tool-result-no-name-1", problem.SpanId);
    }

    [Fact]
    public void TryMap_MissingCallId_ReportsProblemWithoutPayload()
    {
        OtlpLog log = ToolResultLog(
            logId: "log-tool-result-no-call-1",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = Element("codex.tool_result"),
                ["tool_name"] = Element("shell"),
                ["success"] = Element(true),
                ["duration_ms"] = Element(10),
                ["conversation.id"] = Element("conv-codex-1"),
            });

        bool handled = CodexToolResultMapper.TryMap(log, ImportedAtUtc, null, out CollectedRecord? record, out CollectedProblem? problem);

        Assert.True(handled);
        Assert.Null(record);
        Assert.NotNull(problem);
        Assert.Equal(CodexToolResultMapper.InvalidToolResultCode, problem.Code);
        Assert.Contains("call_id", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryMap_InvalidTimestamp_ReportsTimestampProblem()
    {
        OtlpLog log = ToolResultLog(
            logId: "log-tool-result-bad-ts-1",
            timestamp: "not-a-timestamp",
            attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = Element("codex.tool_result"),
                ["tool_name"] = Element("shell"),
                ["call_id"] = Element("call_shell_5"),
                ["success"] = Element(true),
                ["duration_ms"] = Element(10),
                ["conversation.id"] = Element("conv-codex-1"),
            });

        bool handled = CodexToolResultMapper.TryMap(log, ImportedAtUtc, null, out CollectedRecord? record, out CollectedProblem? problem);

        Assert.True(handled);
        Assert.Null(record);
        Assert.NotNull(problem);
        Assert.Equal(OtlpLogMapper.InvalidTimestampCode, problem.Code);
    }

    [Fact]
    public void MapLogs_MixedBatch_MapsToolResultsSkipsOtherEventsAndCollectsProblems()
    {
        List<OtlpLog> logs =
        [
            ToolResultLog(
                logId: "log-batch-ok-1",
                timestamp: "2026-11-19T10:00:01.000Z",
                attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["event.name"] = Element("codex.tool_result"),
                    ["tool_name"] = Element("shell"),
                    ["call_id"] = Element("call_batch_1"),
                    ["success"] = Element(true),
                    ["duration_ms"] = Element(100),
                    ["conversation.id"] = Element("conv-codex-batch"),
                }),
            ToolResultLog(
                logId: "log-batch-other-1",
                attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["event.name"] = Element("codex.user_prompt"),
                }),
            ToolResultLog(
                logId: "log-batch-bad-1",
                attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["event.name"] = Element("codex.tool_result"),
                    ["success"] = Element(true),
                    ["conversation.id"] = Element("conv-codex-batch"),
                }),
        ];

        CollectedBatch batch = CodexToolResultMapper.MapLogs(logs, "codex-otlp", null, ImportedAtUtc);

        Assert.Equal("codex-cli", batch.Source.HarnessId);
        Assert.Equal(SourceKind.Otlp, batch.Source.Kind);
        Assert.Equal("codex-otlp", batch.Source.Locator);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("log-batch-ok-1", record.RecordId);
        Assert.Equal(RecordOp.ToolInvoke, record.Op);
        Assert.Equal("conv-codex-batch", record.SessionKey);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("conv-codex-batch", session.SessionKey);
        Assert.Equal("codex-cli", session.HarnessId);

        CollectedProblem batchProblem = Assert.Single(batch.Problems);
        Assert.Equal(CodexToolResultMapper.InvalidToolResultCode, batchProblem.Code);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        CollectedRecord? stored = store.FindRecord("log-batch-ok-1");
        Assert.NotNull(stored);
        Assert.NotNull(stored.Tool);
        Assert.Equal("call_batch_1", stored.Tool.CallId);
        Assert.True(stored.Tool.Succeeded);
        Assert.Null(stored.Tool.Input);
        Assert.Null(stored.Tool.Result);
    }

    private static OtlpLog ToolResultLog(
        string logId,
        IReadOnlyDictionary<string, JsonElement> attributes,
        string timestamp = "2026-11-19T10:00:00.000Z")
    {
        return new OtlpLog(
            LogId: logId,
            TraceId: "0102030405060708090a0b0c0d0e0f10",
            SpanId: "span-ambient-1",
            SessionId: null,
            Timestamp: timestamp,
            Body: null,
            Attributes: attributes,
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = Element("codex-cli"),
            },
            Scope: new OtlpScope("openai.codex", "1.0.0"));
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);
}
