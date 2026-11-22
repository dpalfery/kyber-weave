using System.Globalization;
using KyberDash.Contracts.Repositories;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Domain;
using KyberDash.Domain.Schema;
using Xunit;

namespace KyberDash.Application.Tests.Fakes;

/// <summary>
/// Contract and relational invariant tests for <see cref="InMemoryRelationalStore"/> across
/// <see cref="IRelationalStoreWriter"/>, <see cref="IRelationalStoreReader"/>,
/// <see cref="IProjectionStore"/>, and <see cref="IOperationalStore"/>.
/// </summary>
public class InMemoryRelationalStoreTests
{
    private static readonly DateTimeOffset BaseStartUtc =
        DateTimeOffset.Parse("2026-11-19T08:00:00Z", CultureInfo.InvariantCulture);

    private static readonly DateTimeOffset BaseEndUtc =
        DateTimeOffset.Parse("2026-11-19T08:01:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void InMemoryRelationalStore_IsPublicAndUnsealed_AndImplementsAllFourInterfaces()
    {
        Type storeType = typeof(InMemoryRelationalStore);

        Assert.True(storeType.IsPublic);
        Assert.False(storeType.IsSealed);
        Assert.True(typeof(IRelationalStoreWriter).IsAssignableFrom(storeType));
        Assert.True(typeof(IRelationalStoreReader).IsAssignableFrom(storeType));
        Assert.True(typeof(IProjectionStore).IsAssignableFrom(storeType));
        Assert.True(typeof(IOperationalStore).IsAssignableFrom(storeType));
    }

    [Fact]
    public void WriteBatch_AndReaderQueries_RoundTripsFullNormalizedGraph()
    {
        InMemoryRelationalStore store = new();
        CollectedSource source = new("claude-code", SourceKind.File, "/tmp/session-1.jsonl");
        CollectedSession session = new(
            SessionKey: "sess-1",
            HarnessId: "claude-code",
            NativeSessionId: "native-sess-1",
            Workspace: new CollectedWorkspace("/repo/app", "kyber-weave", "main"),
            Title: "Inspect relational schema",
            ReportedUsage:
            [
                new CollectedModelUsage(0, "claude-sonnet-4-5", TokenClass.Input, 1200),
                new CollectedModelUsage(0, "claude-sonnet-4-5", TokenClass.Output, 350),
            ]);

        CollectedRecord llmRecord = new(
            RecordId: "span-llm-1",
            HarnessId: "claude-code",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "claude.messages",
            StartedAt: BaseStartUtc,
            Status: RecordStatus.Ok,
            SessionKey: "sess-1",
            ParentRecordId: "span-root-0",
            TraceId: "trace-1",
            Model: "claude-sonnet-4-5",
            EndedAt: BaseEndUtc,
            ReportedDurationMs: 1250,
            Tokens:
            [
                new CollectedTokenCount(TokenClass.Input, 900),
                new CollectedTokenCount(TokenClass.CacheRead, 300),
                new CollectedTokenCount(TokenClass.Output, 200),
            ],
            ReportedCost: new CollectedCost(0.042, "USD"),
            Parts:
            [
                new CollectedContentPart(0, ContentBucket.SystemPrompt, "You are concise.", null, 12, null, "system", true, null),
                new CollectedContentPart(1, ContentBucket.ToolResultContent, null, new CollectedWithheldText(512, 512, 128), null, "fs", "tool", false, 1024),
            ],
            Tool: null,
            Agent: null,
            SkillsInvoked: ["go-dev"],
            AgentInstanceId: "agent-inst-1",
            ToolDefinitions:
            [
                new CollectedToolDefinition(0, "read_file", null, 85),
                new CollectedToolDefinition(1, "codegraph_explore", "codegraph", 210),
            ],
            Signals: new CollectedRecordSignals("warning note", 1, "rule-no-var", 200000),
            ReportedInputTokens: 1200,
            ReportedOutputTokens: 200);

        string sha = new('a', 64);
        CollectedRecord toolRecord = new(
            RecordId: "span-tool-1",
            HarnessId: "claude-code",
            Op: RecordOp.ToolInvoke,
            Kind: RecordKind.Internal,
            Name: "mcp.call",
            StartedAt: BaseStartUtc.AddSeconds(10),
            Status: RecordStatus.Error,
            SessionKey: "sess-1",
            ParentRecordId: "span-llm-1",
            TraceId: "trace-1",
            Model: null,
            EndedAt: BaseEndUtc,
            ReportedDurationMs: 45,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: new CollectedToolCall(
                CallId: "call-1",
                ToolName: "mcp__codegraph__codegraph_explore",
                McpServerName: "codegraph",
                McpToolName: "codegraph_explore",
                Input: new CollectedToolContent("query=WriteBatch", 16, 16, sha, null),
                Result: new CollectedToolContent("failed lookup", 13, 13, null, 4096),
                Succeeded: false,
                Error: new CollectedError("index missing", "ENOENT"),
                ExitCode: 2),
            Agent: new CollectedAgentInvocation("explorer", "span-llm-1", false, "aborted"),
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null));

        CollectedProblem problem = new(
            ProblemKey: "prob-1",
            Severity: "warning",
            Code: "MISSING_END",
            Message: "End timestamp inferred",
            SpanId: "span-llm-1",
            Location: "line:42",
            SessionKey: "sess-1",
            HarnessId: "claude-code",
            TimestampUtc: "2026-11-19T08:00:05Z",
            Stack: "at Parser.Read()");

        store.WriteBatch(new CollectedBatch(source, [session], [llmRecord, toolRecord], [problem]));

        Assert.Equal(["claude-code"], store.Harnesses());
        StoredSource storedSource = Assert.Single(store.Sources("claude-code"));
        Assert.Equal(source, storedSource.Source);
        Assert.Equal(2, store.CountRecords("claude-code"));

        CollectedSession? loadedSession = store.GetSession("sess-1");
        Assert.NotNull(loadedSession);
        Assert.Equal("sess-1", loadedSession.SessionKey);
        Assert.Equal("claude-code", loadedSession.HarnessId);
        Assert.Equal("native-sess-1", loadedSession.NativeSessionId);
        Assert.Equal(new CollectedWorkspace("/repo/app", "kyber-weave", "main"), loadedSession.Workspace);
        Assert.Equal("Inspect relational schema", loadedSession.Title);
        Assert.Equal(2, loadedSession.ReportedUsage.Count);
        Assert.Equal(2, store.SessionModelUsages("sess-1").Count);

        CollectedRecord? loadedLlm = store.GetRecord("span-llm-1");
        Assert.NotNull(loadedLlm);
        Assert.Equal("claude-sonnet-4-5", loadedLlm.Model);
        Assert.Equal(1200, loadedLlm.ReportedInputTokens);
        Assert.Equal(200, loadedLlm.ReportedOutputTokens);
        Assert.Equal(new CollectedCost(0.042, "USD"), loadedLlm.ReportedCost);
        Assert.Equal(2, loadedLlm.Parts.Count);
        Assert.Equal(2, loadedLlm.ToolDefinitions.Count);
        Assert.Equal(["go-dev"], loadedLlm.SkillsInvoked);
        Assert.Equal(new CollectedRecordSignals("warning note", 1, "rule-no-var", 200000), loadedLlm.Signals);

        CollectedRecord? loadedTool = store.GetRecord("span-tool-1");
        Assert.NotNull(loadedTool);
        Assert.NotNull(loadedTool.Tool);
        Assert.Equal("codegraph", loadedTool.Tool.McpServerName);
        Assert.Equal(sha, loadedTool.Tool.Input?.Sha256Fingerprint);
        Assert.Equal(new CollectedError("index missing", "ENOENT"), loadedTool.Tool.Error);
        Assert.Equal(2, loadedTool.Tool.ExitCode);
        Assert.Equal(new CollectedAgentInvocation("explorer", "span-llm-1", false, "aborted"), loadedTool.Agent);

        Assert.Equal(2, store.Records(sessionKey: "sess-1").Count);
        Assert.Equal(3, store.TokenCounts(sessionKey: "sess-1").Count);
        Assert.Equal(2, store.ReportedTotals(sessionKey: "sess-1").Count);
        Assert.Single(store.ReportedCosts(sessionKey: "sess-1"));
        Assert.Single(store.SkillsInvoked(sessionKey: "sess-1"));
        Assert.Single(store.ToolCalls(sessionKey: "sess-1"));
        Assert.Equal(2, store.ContentParts(sessionKey: "sess-1").Count);
        Assert.Equal(2, store.ToolDefinitions(sessionKey: "sess-1").Count);
        Assert.Single(store.AgentInvocations(sessionKey: "sess-1"));
        Assert.Single(store.RecordSignals(sessionKey: "sess-1"));
        Assert.Equal(1, store.CountIngestProblems("claude-code"));
        Assert.Single(store.ListIngestProblems(spanId: "span-llm-1"));
    }

    [Fact]
    public void WriteBatch_OptionalFieldsOmitted_LeavesZeroChildRowsAndReportsMissingParentKeys()
    {
        InMemoryRelationalStore store = new();
        CollectedSource source = new("codex", SourceKind.Otlp, "http://127.0.0.1:4318/v1/traces");
        CollectedSession session = new(
            SessionKey: "sess-min",
            HarnessId: "codex",
            NativeSessionId: "native-min",
            Workspace: null,
            Title: null,
            ReportedUsage: []);

        CollectedRecord minimalRecord = new(
            RecordId: "span-min-1",
            HarnessId: "codex",
            Op: RecordOp.Unspecified,
            Kind: RecordKind.Unspecified,
            Name: "minimal",
            StartedAt: BaseStartUtc,
            Status: RecordStatus.Unset,
            SessionKey: "sess-min",
            ParentRecordId: null,
            TraceId: null,
            Model: null,
            EndedAt: null,
            ReportedDurationMs: null,
            Tokens: [],
            ReportedCost: null,
            Parts: [],
            Tool: null,
            Agent: null,
            SkillsInvoked: [],
            AgentInstanceId: null,
            ToolDefinitions: [],
            Signals: new CollectedRecordSignals(null, null, null, null),
            ReportedInputTokens: null,
            ReportedOutputTokens: null);

        store.WriteBatch(new CollectedBatch(source, [session], [minimalRecord], []));

        string[] emptyChildTables =
        [
            "session_workspace",
            "session_repository",
            "session_branch",
            "session_title",
            "session_model_usage",
            "record_parent",
            "record_trace",
            "record_model",
            "record_end",
            "record_reported_duration",
            "record_token_count",
            "record_reported_total",
            "record_reported_cost",
            "record_skill_invocation",
            "record_agent_instance",
            "record_error",
            "record_edit_failure",
            "record_correction",
            "record_correction_rule",
            "record_declared_context_window",
            "content_part",
            "tool_definition",
            "tool_call",
            "agent_invocation",
        ];

        foreach (string tableName in emptyChildTables)
        {
            Assert.Equal(0, store.CountRows(tableName));
        }

        Assert.Equal(1, store.CountRows("record_session_link"));
        Assert.Empty(store.RecordSignals(spanId: "span-min-1"));

        StoredFieldsView fields = store.StoredFields("sess-min");
        Assert.Equal("sess-min", fields.SessionKey);
        Assert.Equal(RelationalSchemaCatalog.Tables.Count, fields.Tables.Count);

        StoredTableFields recordModelEntry = Assert.Single(
            fields.Tables,
            static table => string.Equals(table.TableName, "record_model", StringComparison.Ordinal));
        Assert.Equal(["span_id", "model_id"], recordModelEntry.ColumnNames);
        Assert.Empty(recordModelEntry.Rows);
        Assert.Equal(["span-min-1"], recordModelEntry.MissingParentKeys);

        StoredTableFields sessionTitleEntry = Assert.Single(
            fields.Tables,
            static table => string.Equals(table.TableName, "session_title", StringComparison.Ordinal));
        Assert.Empty(sessionTitleEntry.Rows);
        Assert.Equal(["sess-min"], sessionTitleEntry.MissingParentKeys);
    }

    [Fact]
    public void WriteBatch_RejectsDuplicateRecordPrimaryKey_WithConstraintViolatedAndRollsBackAtomically()
    {
        InMemoryRelationalStore store = new();
        CollectedSource source = new("claude-code", SourceKind.File, "/tmp/batch-a.jsonl");
        CollectedRecord firstRecord = CreateMinimalRecord("span-dup", "claude-code", "sess-a");
        store.WriteBatch(new CollectedBatch(source, [CreateMinimalSession("sess-a", "claude-code")], [firstRecord], []));

        CollectedRecord newRecord = CreateMinimalRecord("span-new", "claude-code", "sess-a");
        CollectedRecord duplicateRecord = CreateMinimalRecord("span-dup", "claude-code", "sess-a");

        DashValidationException ex = Assert.Throws<DashValidationException>(() =>
            store.WriteBatch(new CollectedBatch(source, [], [newRecord, duplicateRecord], [])));

        Assert.Equal("STORE_CONSTRAINT_VIOLATED", ex.Code);
        Assert.Equal(1, store.CountRecords());
        Assert.Null(store.GetRecord("span-new"));
    }

    [Fact]
    public void InsertRow_RejectsDuplicatePrimaryKeyAndUniqueIndex_WithConstraintViolated()
    {
        InMemoryRelationalStore store = new();
        store.InsertRow("harness", ("harness_id", "codex"));

        DashValidationException duplicateHarness = Assert.Throws<DashValidationException>(() =>
            store.InsertRow("harness", ("harness_id", "codex")));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", duplicateHarness.Code);

        store.InsertRow(
            "source",
            ("source_id", "src-1"),
            ("harness_id", "codex"),
            ("source_kind", "file"),
            ("locator", "/tmp/a.jsonl"));

        DashValidationException duplicateUniqueLocator = Assert.Throws<DashValidationException>(() =>
            store.InsertRow(
                "source",
                ("source_id", "src-2"),
                ("harness_id", "codex"),
                ("source_kind", "file"),
                ("locator", "/tmp/a.jsonl")));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", duplicateUniqueLocator.Code);
        Assert.Equal(1, store.CountRows("source"));
    }

    [Fact]
    public void Store_RejectsOrphanForeignKeys_AcrossInterfacesAndTables_WithConstraintViolated()
    {
        InMemoryRelationalStore store = new();

        DashValidationException batchUnknownHarness = Assert.Throws<DashValidationException>(() =>
            store.WriteBatch(new CollectedBatch(
                new CollectedSource("claude-code", SourceKind.File, "/tmp/a.jsonl"),
                [],
                [CreateMinimalRecord("span-1", "unknown-harness", null)],
                [])));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", batchUnknownHarness.Code);
        Assert.Equal(0, store.CountRows("harness"));

        DashValidationException orphanProjection = Assert.Throws<DashValidationException>(() =>
            store.ReplaceSessionProjection(new StoredSessionProjection(
                "missing-sess",
                "Missing",
                false,
                0,
                "2026-11-19T08:00:00Z",
                "2026-11-19T08:01:00Z",
                null,
                null,
                false,
                [],
                [])));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", orphanProjection.Code);

        DashValidationException orphanTurns = Assert.Throws<DashValidationException>(() =>
            store.ReplaceSessionTurns("missing-sess", [new StoredSessionTurn("missing-sess", 0, "span-1")]));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", orphanTurns.Code);

        DashValidationException orphanExecution = Assert.Throws<DashValidationException>(() =>
            store.ReplaceExecutions([new StoredExecution("exec-1", "missing-run", "claude-code", true)]));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", orphanExecution.Code);

        DashValidationException orphanRollup = Assert.Throws<DashValidationException>(() =>
            store.ReplaceHarnessRollups([new StoredHarnessRollup("missing-harness", 0, 0, 0, 0, 0, [])]));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", orphanRollup.Code);

        DashValidationException orphanProvenance = Assert.Throws<DashValidationException>(() =>
            store.PutRecordProvenance([
                new RecordProvenanceRow(
                    "missing-span",
                    "claude-code",
                    "src",
                    null,
                    null,
                    "rev1",
                    "v1",
                    "2026-11-19T08:00:00Z",
                    "loc"),
            ]));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", orphanProvenance.Code);

        DashValidationException orphanCompositeFk = Assert.Throws<DashValidationException>(() =>
            store.InsertRow(
                "tool_definition_server",
                ("span_id", "missing-span"),
                ("position", 0),
                ("server_name", "srv")));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", orphanCompositeFk.Code);
    }

    [Fact]
    public void InsertRow_AndTypedOperations_RejectNullMissingAndViolatedChecks_WithConstraintViolated()
    {
        InMemoryRelationalStore store = new();
        store.InsertRow("harness", ("harness_id", "codex"));

        DashValidationException nullPk = Assert.Throws<DashValidationException>(() =>
            store.InsertRow("harness", ("harness_id", null)));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", nullPk.Code);

        DashValidationException missingColumn = Assert.Throws<DashValidationException>(() =>
            store.InsertRow(
                "source",
                ("source_id", "src-1"),
                ("harness_id", "codex"),
                ("source_kind", "file")));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", missingColumn.Code);

        DashValidationException emptyNonEmptyText = Assert.Throws<DashValidationException>(() =>
            store.InsertRow("harness", ("harness_id", "")));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", emptyNonEmptyText.Code);

        DashValidationException badEnum = Assert.Throws<DashValidationException>(() =>
            store.InsertRow(
                "source",
                ("source_id", "src-1"),
                ("harness_id", "codex"),
                ("source_kind", "http"),
                ("locator", "/tmp/a")));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", badEnum.Code);

        DashValidationException badCadence = Assert.Throws<DashValidationException>(() =>
            store.SaveSettings(new SettingsView(true, false, 1441, false, 4747)));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", badCadence.Code);

        DashValidationException badPort = Assert.Throws<DashValidationException>(() =>
            store.SaveSettings(new SettingsView(true, false, 5, false, 70000)));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", badPort.Code);

        DashValidationException badShaLength = Assert.Throws<DashValidationException>(() =>
            store.WriteBatch(new CollectedBatch(
                new CollectedSource("codex", SourceKind.File, "/tmp/x.jsonl"),
                [],
                [
                    CreateMinimalRecord("span-bad-sha", "codex", null) with
                    {
                        Op = RecordOp.ToolInvoke,
                        Tool = new CollectedToolCall(
                            "call-1",
                            "bash",
                            null,
                            null,
                            new CollectedToolContent("ls", 2, 2, "too-short", null),
                            null,
                            true,
                            null,
                            0),
                    },
                ],
                [])));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", badShaLength.Code);
        Assert.Equal(0, store.CountRecords());
    }

    [Fact]
    public void ExclusivePair_ContentPartTextAndWithheld_CannotCoexist()
    {
        InMemoryRelationalStore store = new();
        SeedRecordWithContentPart(store, "span-cp-1", 0);

        store.InsertRow(
            "content_part_text",
            ("span_id", "span-cp-1"),
            ("position", 0),
            ("part_text", "hello"));

        DashValidationException withheldAfterText = Assert.Throws<DashValidationException>(() =>
            store.InsertRow(
                "content_part_withheld",
                ("span_id", "span-cp-1"),
                ("position", 0),
                ("utf8_bytes", 10),
                ("characters", 10),
                ("measured_tokens", 3)));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", withheldAfterText.Code);

        SeedRecordWithContentPart(store, "span-cp-2", 0);
        store.InsertRow(
            "content_part_withheld",
            ("span_id", "span-cp-2"),
            ("position", 0),
            ("utf8_bytes", 10),
            ("characters", 10),
            ("measured_tokens", 3));

        DashValidationException textAfterWithheld = Assert.Throws<DashValidationException>(() =>
            store.InsertRow(
                "content_part_text",
                ("span_id", "span-cp-2"),
                ("position", 0),
                ("part_text", "hello")));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", textAfterWithheld.Code);
    }

    [Fact]
    public void ExclusivePair_SessionContextAndUnmeasurable_CannotCoexist()
    {
        InMemoryRelationalStore store = new();
        store.WriteBatch(new CollectedBatch(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/ctx.jsonl"),
            [CreateMinimalSession("sess-ctx", "claude-code")],
            [],
            []));

        StoredSessionContext context = new(200000, "catalog", 0, 1.0, 0.25, [], [], [], []);
        DashValidationException bothInProjection = Assert.Throws<DashValidationException>(() =>
            store.ReplaceSessionProjection(new StoredSessionProjection(
                "sess-ctx",
                "Ctx",
                false,
                0,
                "2026-11-19T08:00:00Z",
                "2026-11-19T08:01:00Z",
                null,
                null,
                false,
                [],
                [],
                Context: context,
                ContextUnmeasurableReason: "missing model")));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", bothInProjection.Code);
        Assert.Null(store.GetSessionProjection("sess-ctx"));

        store.ReplaceSessionProjection(new StoredSessionProjection(
            "sess-ctx",
            "Ctx",
            false,
            0,
            "2026-11-19T08:00:00Z",
            "2026-11-19T08:01:00Z",
            null,
            null,
            false,
            [],
            [],
            Context: context));

        DashValidationException unmeasurableAfterContext = Assert.Throws<DashValidationException>(() =>
            store.InsertRow(
                "session_context_unmeasurable",
                ("session_id", "sess-ctx"),
                ("reason", "conflict")));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", unmeasurableAfterContext.Code);
    }

    [Fact]
    public void ExclusivePair_SessionSchemaAndUnmeasurable_CannotCoexist()
    {
        InMemoryRelationalStore store = new();
        store.WriteBatch(new CollectedBatch(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/schema.jsonl"),
            [CreateMinimalSession("sess-schema", "claude-code")],
            [],
            []));

        StoredSessionSchema schema = new(1, 1, [], []);
        DashValidationException bothInProjection = Assert.Throws<DashValidationException>(() =>
            store.ReplaceSessionProjection(new StoredSessionProjection(
                "sess-schema",
                "Schema",
                false,
                0,
                "2026-11-19T08:00:00Z",
                "2026-11-19T08:01:00Z",
                null,
                null,
                false,
                [],
                [],
                Schema: schema,
                SchemaUnmeasurableReason: "no tools")));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", bothInProjection.Code);

        store.ReplaceSessionProjection(new StoredSessionProjection(
            "sess-schema",
            "Schema",
            false,
            0,
            "2026-11-19T08:00:00Z",
            "2026-11-19T08:01:00Z",
            null,
            null,
            false,
            [],
            [],
            SchemaUnmeasurableReason: "unmeasured"));

        DashValidationException schemaAfterUnmeasurable = Assert.Throws<DashValidationException>(() =>
            store.InsertRow(
                "session_schema",
                ("session_id", "sess-schema"),
                ("invocation_count", 0),
                ("turn_count", 0)));
        Assert.Equal("STORE_CONSTRAINT_VIOLATED", schemaAfterUnmeasurable.Code);
    }

    [Theory]
    [InlineData("{\"k\":1}")]
    [InlineData("  \n\t{ \"nested\": [1, true, null] } \r\n")]
    [InlineData("[]")]
    [InlineData(" [1, \"two\", {\"three\":3}] ")]
    public void Store_RejectsJsonObjectOrArrayDocumentsInAnyTextColumn_WithJsonTextRefused(string jsonDocument)
    {
        InMemoryRelationalStore store = new();
        CollectedSource source = new("claude-code", SourceKind.File, "/tmp/json.jsonl");

        DashValidationException contentPartEx = Assert.Throws<DashValidationException>(() =>
            store.WriteBatch(new CollectedBatch(
                source,
                [],
                [
                    CreateMinimalRecord("span-json-part", "claude-code", null) with
                    {
                        Parts =
                        [
                            new CollectedContentPart(0, ContentBucket.UserPrompt, jsonDocument, null, null, null, null, null, null),
                        ],
                    },
                ],
                [])));
        Assert.Equal("STORE_JSON_TEXT_REFUSED", contentPartEx.Code);

        DashValidationException sessionTitleEx = Assert.Throws<DashValidationException>(() =>
            store.WriteBatch(new CollectedBatch(
                source,
                [new CollectedSession("sess-json", "claude-code", "native-json", null, jsonDocument, [])],
                [],
                [])));
        Assert.Equal("STORE_JSON_TEXT_REFUSED", sessionTitleEx.Code);

        DashValidationException problemEx = Assert.Throws<DashValidationException>(() =>
            store.WriteProblem(new CollectedProblem("prob-json", "error", "JSON", jsonDocument)));
        Assert.Equal("STORE_JSON_TEXT_REFUSED", problemEx.Code);

        DashValidationException findingEx = Assert.Throws<DashValidationException>(() =>
            store.ReplaceFindings([
                new StoredFinding(
                    "find-json",
                    "det-1",
                    "Title",
                    "Mechanism",
                    "high",
                    jsonDocument,
                    "Caveat",
                    1.0,
                    []),
            ]));
        Assert.Equal("STORE_JSON_TEXT_REFUSED", findingEx.Code);
        Assert.Equal(0, store.CountRecords());
    }

    [Theory]
    [InlineData("Output was {\"ok\":true} done")]
    [InlineData("42")]
    [InlineData("{not valid json}")]
    [InlineData("\"quoted scalar\"")]
    public void Store_AllowsPlainProseAndScalarStrings_InTextColumns(string allowedText)
    {
        InMemoryRelationalStore store = new();
        CollectedSource source = new("claude-code", SourceKind.File, "/tmp/prose.jsonl");

        CollectedRecord record = CreateMinimalRecord("span-prose", "claude-code", "sess-prose") with
        {
            Parts =
            [
                new CollectedContentPart(0, ContentBucket.UserPrompt, allowedText, null, null, null, null, null, null),
            ],
        };

        store.WriteBatch(new CollectedBatch(
            source,
            [new CollectedSession("sess-prose", "claude-code", "native-prose", null, allowedText, [])],
            [record],
            []));

        Assert.Equal(allowedText, store.GetRecord("span-prose")?.Parts[0].PlainText);
        Assert.Equal(allowedText, store.GetSession("sess-prose")?.Title);
    }

    [Fact]
    public void DeleteSource_CascadesThroughRecordsPartsToolsAgentsAndProvenance_WithoutRemovingSessionOrHarness()
    {
        InMemoryRelationalStore store = new();
        CollectedSource sourceA = new("claude-code", SourceKind.File, "/tmp/a.jsonl");
        CollectedSource sourceB = new("claude-code", SourceKind.File, "/tmp/b.jsonl");

        CollectedRecord recordA = CreateMinimalRecord("span-a", "claude-code", "sess-shared") with
        {
            Model = "claude-sonnet-4-5",
            Tokens = [new CollectedTokenCount(TokenClass.Input, 100)],
            Parts = [new CollectedContentPart(0, ContentBucket.UserMessage, "hi a", null, null, "srv", "user", true, 4)],
            ToolDefinitions = [new CollectedToolDefinition(0, "t1", "srv", 20)],
            Tool = new CollectedToolCall("c1", "t1", "srv", "t1", new CollectedToolContent("in", 2, 2, null, null), null, true, new CollectedError("err", "E1"), 1),
            Agent = new CollectedAgentInvocation("sub", "root", true, null),
            Signals = new CollectedRecordSignals("err", 1, "rule", 1000),
        };
        CollectedRecord recordB = CreateMinimalRecord("span-b", "claude-code", "sess-shared") with
        {
            Model = "claude-sonnet-4-5",
            Parts = [new CollectedContentPart(0, ContentBucket.UserMessage, "hi b", null, null, null, null, null, null)],
        };

        store.WriteBatch(new CollectedBatch(sourceA, [CreateMinimalSession("sess-shared", "claude-code")], [recordA], []));
        store.WriteBatch(new CollectedBatch(sourceB, [], [recordB], []));
        store.PutRecordProvenance([
            new RecordProvenanceRow("span-a", "claude-code", "a", "native", "nr-a", "r1", "p1", "2026-11-19T08:00:00Z", "line:1"),
            new RecordProvenanceRow("span-b", "claude-code", "b", "native", "nr-b", "r1", "p1", "2026-11-19T08:00:00Z", "line:1"),
        ]);

        StoredSource sourceRowA = Assert.Single(store.Sources("claude-code"), static s => s.Source.Locator == "/tmp/a.jsonl");
        store.DeleteSource(sourceRowA.SourceId);

        Assert.Single(store.Harnesses());
        Assert.NotNull(store.GetSession("sess-shared"));
        Assert.Single(store.Sources("claude-code"));
        Assert.Null(store.GetRecord("span-a"));
        Assert.NotNull(store.GetRecord("span-b"));
        Assert.Equal(1, store.CountRecords("claude-code"));
        Assert.Empty(store.TokenCounts(spanId: "span-a"));
        Assert.Empty(store.ToolCalls(spanId: "span-a"));
        Assert.Empty(store.ToolDefinitions(spanId: "span-a"));
        Assert.Empty(store.AgentInvocations(spanId: "span-a"));
        Assert.Empty(store.RecordSignals(spanId: "span-a"));
        Assert.Single(store.ContentParts(sessionKey: "sess-shared"));
        Assert.Equal(0, store.CountRows("tool_call_error_code"));
        Assert.Equal(0, store.CountRows("record_correction_rule"));
        Assert.Equal(0, store.CountRows("record_correction"));
        Assert.Single(store.ListRecordProvenance("claude-code", "b"));
        Assert.Empty(store.ListRecordProvenance("claude-code", "a"));
    }

    [Fact]
    public void ProjectionStore_RoundTripsAndCascadesDeletes()
    {
        InMemoryRelationalStore store = new();
        store.WriteBatch(new CollectedBatch(
            new CollectedSource("claude-code", SourceKind.File, "/tmp/proj.jsonl"),
            [CreateMinimalSession("sess-p", "claude-code")],
            [CreateMinimalRecord("span-p1", "claude-code", "sess-p")],
            []));

        StoredSessionProjection projection = new(
            SessionId: "sess-p",
            Label: "Proj Session",
            IsSubagent: true,
            SpanCount: 1,
            StartedAtUtc: "2026-11-19T08:00:00Z",
            EndedAtUtc: "2026-11-19T08:01:00Z",
            ParentSessionId: "sess-root",
            AgentName: "dev",
            IncludedInParent: true,
            RequestSpanIds: ["span-p1"],
            Turns: [new StoredSessionTurn("sess-p", 0, "span-p1")],
            Context: new StoredSessionContext(
                200000,
                "catalog",
                50,
                1.5,
                0.2,
                ["system_prompt"],
                [new StoredSessionContextDerivedCount("turns", 1)],
                [0],
                [
                    new StoredSessionContextTurn(
                        0,
                        1000,
                        950,
                        50,
                        "unknown",
                        100,
                        300,
                        [new StoredTurnBucketTokens("system_prompt", 400)],
                        [new StoredTurnServerDefinitionTokens("codegraph", 200)],
                        Headroom: 199000,
                        Pressure: 0.005,
                        AccumulationRate: 0.1,
                        StrippedBlockCount: 1,
                        StrippedTokens: 20,
                        PreviousFreshInput: 100,
                        FreshJumpFactor: 3.0,
                        NonResidentTokens: 40),
                ]),
            ContextUnmeasurableReason: null,
            Schema: new StoredSessionSchema(
                3,
                1,
                [new StoredSchemaRankedTool(0, "codegraph_explore", 200, true, "codegraph")],
                [new StoredSchemaServerTokens("codegraph", 200)],
                UnusedTokenResidencies: 50),
            SchemaUnmeasurableReason: null,
            TimelineSubagentSpanIds: ["span-sub"],
            TimelineAuxiliarySpanIds: ["span-aux"]);

        store.ReplaceSessionProjections([projection]);
        StoredSessionProjection? loadedProjection = store.GetSessionProjection("sess-p");
        Assert.NotNull(loadedProjection);
        Assert.Equal("Proj Session", loadedProjection.Label);
        Assert.True(loadedProjection.IncludedInParent);
        Assert.Single(loadedProjection.Turns);
        Assert.NotNull(loadedProjection.Context);
        Assert.Equal(199000, loadedProjection.Context.Turns[0].Headroom);
        Assert.NotNull(loadedProjection.Schema);
        Assert.Equal(50, loadedProjection.Schema.UnusedTokenResidencies);
        Assert.Single(store.GetSessionTurns("sess-p"));

        store.ReplaceRuns([new StoredRun("run-1", "claude-code", "explicit", "explicit_run_id", 2, "Run One", "/repo", "2026-11-19T08:00:00Z", "2026-11-19T08:05:00Z")]);
        store.ReplaceExecutions([
            new StoredExecution("exec-root", "run-1", "claude-code", true, "sess-p", null, "conductor", "2026-11-19T08:00:00Z", "2026-11-19T08:05:00Z"),
            new StoredExecution("exec-child", "run-1", "claude-code", false, null, "exec-root", "dev", null, null),
        ]);
        store.ReplaceHarnessRollups([
            new StoredHarnessRollup("claude-code", 1, 1, 2, 0, 1, [new StoredHarnessRollupMetric("cache_hit_rate", 0.75, "measured")]),
        ]);
        store.ReplaceFindings([
            new StoredFinding(
                "find-1",
                "dormant-tool-schema",
                "Dormant tools",
                "Unused tools inflated prompt",
                "high",
                "Remove unused tools",
                "May limit fallback calls",
                0.9,
                [new FindingEvidenceLink("span-p1", 0, "Observed 200 unused tokens")],
                EstimatedWasteTokens: 200,
                ErrorBar: new FindingErrorBar(180, 220),
                RunId: "run-1",
                SessionId: "sess-p",
                MeasurementClass: "measured",
                ConfidenceBasis: "exact counts",
                WhatWouldRaiseIt: "More turns",
                CompactionHazard: new StoredCompactionHazard(200000, "catalog")),
        ]);
        store.ReplacePrediction(new StoredPrediction("pred-1", "find-1", "run-1", 190, 0.85, "high", "2026-11-19T08:05:00Z", "run-0", 175, 0.92));

        Assert.Single(store.ListRuns("claude-code"));
        Assert.Equal(2, store.ListExecutionsForRun("run-1").Count);
        Assert.Single(store.ListFindings(sessionId: "sess-p"));
        Assert.Single(store.ListPredictions(findingId: "find-1"));
        Assert.NotNull(store.GetHarnessRollup("claude-code"));

        store.DeleteSessionProjection("sess-p");
        Assert.Null(store.GetSessionProjection("sess-p"));
        Assert.Equal(0, store.CountRows("session_context"));
        Assert.Equal(0, store.CountRows("session_context_turn_headroom"));
        Assert.Equal(0, store.CountRows("session_schema_ranked_tool_server"));

        store.DeleteRows("execution", ("execution_id", "exec-root"));
        Assert.Equal(0, store.CountRows("execution_parent"));
        Assert.Equal(0, store.CountRows("execution_session"));
        StoredExecution? remainingChild = store.GetExecution("exec-child");
        Assert.NotNull(remainingChild);
        Assert.Null(remainingChild.ParentExecutionId);

        store.DeleteRun("run-1");
        Assert.Null(store.GetRun("run-1"));
        Assert.Empty(store.ListExecutionsForRun("run-1"));
        Assert.Equal(0, store.CountRows("run_bounds"));
        Assert.Equal(0, store.CountRows("execution_agent"));

        store.DeleteFindingsForSession("sess-p");
        Assert.Null(store.GetFinding("find-1"));
        Assert.Equal(0, store.CountRows("finding_compaction_hazard"));

        store.DeleteHarnessRollup("claude-code");
        Assert.Null(store.GetHarnessRollup("claude-code"));
        Assert.Equal(0, store.CountRows("harness_rollup_metric"));

        store.DeletePrediction("pred-1");
        Assert.Null(store.GetPrediction("pred-1"));
        Assert.Equal(0, store.CountRows("prediction_calibration"));
    }

    [Fact]
    public void OperationalStore_RoundTripsAllStateSettingsCatalogMetricsAndAuditTables()
    {
        InMemoryRelationalStore store = new();

        SettingsView defaults = store.ReadSettings();
        Assert.Equal(new SettingsView(false, false, 5, false, 4747), defaults);

        SettingsView updated = new(true, true, 15, true, 5151);
        store.SaveSettings(updated);
        Assert.Equal(updated, store.ReadSettings());

        store.SetStoreState(new StoreStateView(24, 3));
        Assert.Equal(new StoreStateView(24, 3), store.GetStoreState());

        store.SetWriteMarker(42);
        Assert.Equal(42, store.GetWriteMarker());

        store.SetLastCleanAtUtc("2026-11-19T08:00:00Z");
        Assert.Equal("2026-11-19T08:00:00Z", store.GetLastCleanAtUtc());

        store.SetContentRetentionDays(30);
        Assert.Equal(30, store.GetContentRetentionDays());

        store.SetContentPurgeMarkUtc("2026-10-19T08:00:00Z");
        Assert.Equal("2026-10-19T08:00:00Z", store.GetContentPurgeMarkUtc());

        store.SetProjectionState(new ProjectionStateView(7, 42));
        Assert.Equal(new ProjectionStateView(7, 42), store.GetProjectionState());

        store.SetProjectionDirtyState(new DirtyProjectionState(10, 42, ["s1", "s2"], false));
        DirtyProjectionState? dirty = store.GetProjectionDirtyState();
        Assert.NotNull(dirty);
        Assert.Equal(["s1", "s2"], dirty.Keys);
        store.ClearProjectionDirtyState();
        Assert.Null(store.GetProjectionDirtyState());

        store.ReplaceSessionKeyAliases([new SessionKeyAlias("alias-1", "sess-1")]);
        Assert.Single(store.ListSessionKeyAliases("sess-1"));

        store.UpsertRefreshRun(new RefreshRunRow("rr-1", "2026-11-19T08:00:00Z", "2026-11-19T08:01:00Z", "ok", 123, "manual", "1 unit", 2));
        Assert.Single(store.ListRefreshRuns());
        Assert.Equal(2, store.GetRefreshRun("rr-1")?.HistoryWeeks);

        SourceCheckpoint checkpoint = new(
            "claude-code",
            "src-key",
            "anthropic",
            "jsonl",
            "1.0",
            "jsonl",
            string.Empty,
            string.Empty,
            "2026-11-19T00:00:00Z",
            "2026-11-19T08:00:00Z",
            "2026-11-19T08:00:00Z",
            "2026-11-19T08:00:00Z",
            "ok",
            null,
            1,
            4);
        store.UpsertSourceCheckpoint(checkpoint);
        Assert.Equal(checkpoint, store.GetSourceCheckpoint("claude-code", "src-key"));

        store.WriteProblem(new CollectedProblem("pk-1", "error", "ERR", "bad line", "span-x", "loc", "sess-1", "claude-code", "2026-11-19T08:00:00Z", "trace"));
        StoredIngestProblem listedProblem = Assert.Single(store.ListIngestProblems("claude-code"));
        Assert.Equal(listedProblem, store.GetIngestProblem(listedProblem.ProblemId));

        store.PutQuarantinedSpan(new StoredQuarantinedSpan("q-1", "unclaimed", ["custom.ns"], "otlp", "custom.op", "2026-11-19T08:00:00Z", "claude-code"));
        Assert.Equal(1, store.CountQuarantinedSpans("claude-code"));
        Assert.Single(store.ListQuarantinedSpans("claude-code"));

        store.PutCachedTokenCounts([new StoredTokenCacheEntry("hash-1", 123, "claude-sonnet-4-5")]);
        Assert.Equal(123, store.GetCachedTokenCount("hash-1"));

        StoredModelCatalogSnapshot catalog = new(
            "fresh",
            [new StoredModelCatalogVendorStatus("anthropic", "ok", "2026-11-19T08:00:00Z", null)],
            [new StoredModelContextWindowEntry("claude-sonnet-4-5", "claude-sonnet-4-5", 200000, "anthropic", new Uri("https://docs.anthropic.com/models"), "2026-11-19T08:00:00Z", "rev-1")]);
        store.ReplaceModelContextWindowCatalog(catalog);
        StoredModelCatalogSnapshot readCatalog = store.GetModelContextWindowCatalog();
        Assert.Equal("fresh", readCatalog.Status);
        Assert.Single(readCatalog.Entries);

        store.PutMetricPoints([
            new StoredMetricPoint("mp-1", "tokens.total", "sum", "tokens", 2, "1000", "900", "2026-11-19T08:00:00Z", 55.5, 3, 55.5, "otlp"),
        ]);
        Assert.Single(store.ListMetricPoints("tokens.total"));

        store.RecordIngest("/tmp/file.jsonl", 5, "2026-11-19T08:00:00Z");
        Assert.Single(store.ListIngestLog("/tmp/file.jsonl"));
    }

    private static CollectedSession CreateMinimalSession(string sessionKey, string harnessId) =>
        new(sessionKey, harnessId, $"native-{sessionKey}", null, null, []);

    private static CollectedRecord CreateMinimalRecord(string recordId, string harnessId, string? sessionKey) =>
        new(
            recordId,
            harnessId,
            RecordOp.LlmInvoke,
            RecordKind.Client,
            "chat",
            BaseStartUtc,
            RecordStatus.Ok,
            sessionKey,
            null,
            null,
            null,
            null,
            null,
            [],
            null,
            [],
            null,
            null,
            [],
            null,
            [],
            new CollectedRecordSignals(null, null, null, null));

    private static void SeedRecordWithContentPart(InMemoryRelationalStore store, string spanId, int position)
    {
        store.WriteBatch(new CollectedBatch(
            new CollectedSource("claude-code", SourceKind.File, $"/tmp/{spanId}.jsonl"),
            [],
            [CreateMinimalRecord(spanId, "claude-code", null)],
            []));

        store.InsertRow(
            "content_part",
            ("span_id", spanId),
            ("position", position),
            ("bucket", "user_prompt"));
    }
}
