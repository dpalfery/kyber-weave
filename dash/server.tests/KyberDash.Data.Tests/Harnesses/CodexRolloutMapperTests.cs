using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Codex;
using KyberDash.Data.Tokenization;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CodexRolloutMapper"/> covering Codex rollout tool call pairing,
/// argument/input measurement vs. plain-text preservation, and static collector DTO invariants.
/// </summary>
public class CodexRolloutMapperTests
{
    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HarnessId_IsCodexCli()
    {
        CodexRolloutMapper mapper = new();
        Assert.Equal("codex-cli", mapper.HarnessId);
    }

    [Fact]
    public void Map_FunctionCallAndOutputPairedByCallId_EmitsChildToolInvokeWithMeasurementsOnlyForJsonArgs()
    {
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-sess-1","model":"gpt-5.2-codex","cwd":"/workspace/repo","base_instructions":{"text":"System instructions"}}}""",
                """{"timestamp":"2026-11-19T10:00:00.500Z","type":"world_state","payload":{"state":{"agents_md":{"text":"Repo AGENTS guide"}}}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Run git status"}]}}""",
                """{"timestamp":"2026-11-19T10:00:02.000Z","type":"response_item","payload":{"type":"function_call","call_id":"call_fn_1","name":"shell","arguments":"{\"timeout\":5000,\"command\":\"git status\"}"}}""",
                """{"timestamp":"2026-11-19T10:00:02.250Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call_fn_1","output":"On branch main\nnothing to commit"}}""",
                """{"timestamp":"2026-11-19T10:00:03.000Z","type":"event_msg","payload":{"type":"token_count","info":{"model":"gpt-5.2-codex","last_token_usage":{"input_tokens":100,"cached_input_tokens":30,"output_tokens":20,"reasoning_output_tokens":4}}}}""",
                """{"timestamp":"2026-11-19T10:00:03.100Z","type":"event_msg","payload":{"type":"task_complete"}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-1.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        Assert.Equal("codex-cli", batch.Source.HarnessId);
        Assert.Equal(SourceKind.File, batch.Source.Kind);
        Assert.Equal(unit.Unit.SourceKey, batch.Source.Locator);
        Assert.Empty(batch.Problems);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("codex-sess-1", session.NativeSessionId);
        Assert.Equal("/workspace/repo", session.Workspace?.WorkingDirectory);

        Assert.Equal(2, batch.Records.Count);
        CollectedRecord parent = Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke);
        CollectedRecord child = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);

        Assert.Equal(RecordStatus.Ok, parent.Status);
        Assert.Equal("gpt-5.2-codex", parent.Model);
        Assert.Equal(70, Assert.Single(parent.Tokens, static t => t.TokenClass == TokenClass.Input).Count);
        Assert.Equal(30, Assert.Single(parent.Tokens, static t => t.TokenClass == TokenClass.CacheRead).Count);
        Assert.Equal(20, Assert.Single(parent.Tokens, static t => t.TokenClass == TokenClass.Output).Count);
        Assert.Equal(4, Assert.Single(parent.Tokens, static t => t.TokenClass == TokenClass.Reasoning).Count);
        Assert.Contains(parent.Parts, static p => p.Bucket == ContentBucket.SystemPrompt && p.PlainText == "System instructions");
        Assert.Contains(parent.Parts, static p => p.Bucket == ContentBucket.InstructionContext && p.PlainText == "Repo AGENTS guide");
        Assert.Contains(parent.Parts, static p => p.Bucket == ContentBucket.ConversationHistory && p.PlainText == "Run git status");

        Assert.Equal(parent.RecordId, child.ParentRecordId);
        Assert.Equal(parent.SessionKey, child.SessionKey);
        Assert.Equal(parent.TraceId, child.TraceId);
        Assert.Equal("shell", child.Name);
        Assert.Equal(RecordStatus.Ok, child.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:02.000Z", CultureInfo.InvariantCulture), child.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:02.250Z", CultureInfo.InvariantCulture), child.EndedAt);
        Assert.Equal(250L, child.ReportedDurationMs);

        Assert.NotNull(child.Tool);
        Assert.Equal("call_fn_1", child.Tool.CallId);
        Assert.Equal("shell", child.Tool.ToolName);
        Assert.True(child.Tool.Succeeded);
        Assert.Null(child.Tool.Error);

        string expectedRawArgs = "{\"timeout\":5000,\"command\":\"git status\"}";
        Assert.NotNull(child.Tool.Input);
        Assert.Null(child.Tool.Input.PlainText);
        Assert.Null(child.Tool.Input.OriginalUtf8BytesWhenTruncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(expectedRawArgs), child.Tool.Input.Utf8Bytes);
        Assert.Equal(expectedRawArgs.Length, child.Tool.Input.Characters);
        Assert.Equal(Sha256Hex("{\"command\":\"git status\",\"timeout\":5000}"), child.Tool.Input.Sha256Fingerprint);

        Assert.NotNull(child.Tool.Result);
        Assert.Equal("On branch main\nnothing to commit", child.Tool.Result.PlainText);
        Assert.Equal(Encoding.UTF8.GetByteCount("On branch main\nnothing to commit"), child.Tool.Result.Utf8Bytes);
        CollectedContentPart resultPart = Assert.Single(child.Parts);
        Assert.Equal(ContentBucket.ToolResultContent, resultPart.Bucket);
        Assert.Equal("On branch main\nnothing to commit", resultPart.PlainText);
    }

    [Fact]
    public void Map_CustomToolCallWithPlainTextInput_PreservesInputTextAndPairsWithJsonOutput()
    {
        string patchInput = "*** Begin Patch\n*** Update File: Program.cs\n@@\n-old();\n+new();\n*** End Patch";
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-sess-2","model":"gpt-5.2-codex"}}""",
                "{\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"call_id\":\"call_patch_1\",\"name\":\"apply_patch\",\"input\":" + System.Text.Json.JsonSerializer.Serialize(patchInput) + "}}",
                """{"timestamp":"2026-11-19T10:00:01.400Z","type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"call_patch_1","output":"{\"output\":\"Success. Updated 1 file.\"}"}}""",
                """{"timestamp":"2026-11-19T10:00:02.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":50,"cached_input_tokens":0,"output_tokens":10}}}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-2.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        CollectedRecord child = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.Equal("apply_patch", child.Name);
        Assert.Equal(RecordStatus.Ok, child.Status);
        Assert.Equal(400L, child.ReportedDurationMs);

        Assert.NotNull(child.Tool);
        Assert.Equal("call_patch_1", child.Tool.CallId);
        Assert.Equal("apply_patch", child.Tool.ToolName);
        Assert.True(child.Tool.Succeeded);

        Assert.NotNull(child.Tool.Input);
        Assert.Equal(patchInput, child.Tool.Input.PlainText);
        Assert.Equal(Encoding.UTF8.GetByteCount(patchInput), child.Tool.Input.Utf8Bytes);
        Assert.Equal(patchInput.Length, child.Tool.Input.Characters);
        Assert.Equal(Sha256Hex(patchInput), child.Tool.Input.Sha256Fingerprint);

        Assert.NotNull(child.Tool.Result);
        Assert.Null(child.Tool.Result.PlainText);
        Assert.True(child.Tool.Result.Utf8Bytes > 0);
        CollectedContentPart resultPart = Assert.Single(child.Parts);
        Assert.Equal(ContentBucket.ToolResultContent, resultPart.Bucket);
        Assert.Null(resultPart.PlainText);
        Assert.NotNull(resultPart.Withheld);
    }

    [Fact]
    public void Map_PairsByIdFallbackAndAcrossTurnBoundary_ForFunctionAndCustomToolCalls()
    {
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-sess-3","model":"gpt-5.2-codex"}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"function_call","id":"fc_id_1","name":"read_file","arguments":"{\"path\":\"README.md\"}"}}""",
                """{"timestamp":"2026-11-19T10:00:01.100Z","type":"response_item","payload":{"type":"function_call_output","id":"fc_id_1","output":"readme text"}}""",
                """{"timestamp":"2026-11-19T10:00:02.000Z","type":"response_item","payload":{"type":"custom_tool_call","id":"ct_id_2","name":"apply_patch","input":"patch line"}}""",
                """{"timestamp":"2026-11-19T10:00:02.500Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":20,"cached_input_tokens":0,"output_tokens":5}}}}""",
                """{"timestamp":"2026-11-19T10:00:03.000Z","type":"response_item","payload":{"type":"custom_tool_call_output","id":"ct_id_2","output":"patch applied"}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-3.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        List<CollectedRecord> tools = [.. batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke)];
        Assert.Equal(2, tools.Count);

        CollectedRecord first = tools[0];
        Assert.Equal("fc_id_1", first.Tool?.CallId);
        Assert.Equal("read_file", first.Tool?.ToolName);
        Assert.Null(first.Tool?.Input?.PlainText);
        Assert.Equal("readme text", first.Tool?.Result?.PlainText);
        Assert.True(first.Tool?.Succeeded);

        CollectedRecord second = tools[1];
        Assert.Equal("ct_id_2", second.Tool?.CallId);
        Assert.Equal("apply_patch", second.Tool?.ToolName);
        Assert.Equal("patch line", second.Tool?.Input?.PlainText);
        Assert.Equal("patch applied", second.Tool?.Result?.PlainText);
        Assert.True(second.Tool?.Succeeded);
    }

    [Fact]
    public void Map_CustomToolCallWithJsonInput_WithholdsPlainTextAndRetainsMeasurements()
    {
        string jsonInput = "{\"b\":2,\"a\":1}";
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-sess-json-custom"}}""",
                "{\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"call_id\":\"ct_json\",\"name\":\"structured_edit\",\"input\":" + System.Text.Json.JsonSerializer.Serialize(jsonInput) + "}}",
                """{"timestamp":"2026-11-19T10:00:01.050Z","type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"ct_json","output":"done"}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-json-custom.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        CollectedRecord child = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.NotNull(child.Tool?.Input);
        Assert.Null(child.Tool.Input.PlainText);
        Assert.Equal(Encoding.UTF8.GetByteCount(jsonInput), child.Tool.Input.Utf8Bytes);
        Assert.Equal(jsonInput.Length, child.Tool.Input.Characters);
        Assert.Equal(Sha256Hex("{\"a\":1,\"b\":2}"), child.Tool.Input.Sha256Fingerprint);
    }

    [Fact]
    public void Map_UnpairedCallAndErrorOutput_SetStatusOutcomeAndSafeErrorMessage()
    {
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-sess-err"}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"function_call","call_id":"call_unpaired","name":"grep","arguments":"{\"pattern\":\"foo\"}"}}""",
                """{"timestamp":"2026-11-19T10:00:02.000Z","type":"response_item","payload":{"type":"function_call","call_id":"call_err","name":"shell","arguments":"{\"command\":\"false\"}"}}""",
                """{"timestamp":"2026-11-19T10:00:02.100Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call_err","is_error":true,"exit_code":2,"output":"{\"error\":\"boom\"}"}}""",
                """{"timestamp":"2026-11-19T10:00:03.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":10,"cached_input_tokens":0,"output_tokens":5}}}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-err.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        List<CollectedRecord> tools = [.. batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke)];
        Assert.Equal(2, tools.Count);

        CollectedRecord unpaired = tools[0];
        Assert.Equal(RecordStatus.Unset, unpaired.Status);
        Assert.Null(unpaired.Tool?.Succeeded);
        Assert.Null(unpaired.Tool?.Result);
        Assert.Null(unpaired.Tool?.Error);

        CollectedRecord errored = tools[1];
        Assert.Equal(RecordStatus.Error, errored.Status);
        Assert.False(errored.Tool?.Succeeded);
        Assert.Equal(2L, errored.Tool?.ExitCode);
        Assert.NotNull(errored.Tool?.Error);
        Assert.False(JsonDocumentText.IsDocument(errored.Tool.Error.Message));
        Assert.NotNull(errored.Signals.ErrorMessage);
        Assert.False(JsonDocumentText.IsDocument(errored.Signals.ErrorMessage));
    }

    [Fact]
    public void Map_OversizedPlainTextInputAndOutput_TruncateAt64KiBAndRecordOriginalBytes()
    {
        string largeInput = new('i', CanonSynth.MaxToolResultBytes + 128);
        string largeOutput = new('o', CanonSynth.MaxToolResultBytes + 64);
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-sess-large"}}""",
                "{\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"call_id\":\"call_big\",\"name\":\"apply_patch\",\"input\":" + System.Text.Json.JsonSerializer.Serialize(largeInput) + "}}",
                "{\"timestamp\":\"2026-11-19T10:00:01.500Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call_output\",\"call_id\":\"call_big\",\"output\":" + System.Text.Json.JsonSerializer.Serialize(largeOutput) + "}}",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-large.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        CollectedRecord child = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.NotNull(child.Tool?.Input?.PlainText);
        Assert.True(Encoding.UTF8.GetByteCount(child.Tool.Input.PlainText) <= CanonSynth.MaxToolResultBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(largeInput), child.Tool.Input.OriginalUtf8BytesWhenTruncated);

        Assert.NotNull(child.Tool.Result?.PlainText);
        Assert.True(Encoding.UTF8.GetByteCount(child.Tool.Result.PlainText) <= CanonSynth.MaxToolResultBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(largeOutput), child.Tool.Result.OriginalUtf8BytesWhenTruncated);
    }

    [Fact]
    public void Map_MalformedJsonAndUnknownEntryType_EmitSafeProblemsAndMapValidTurns()
    {
        string rawJsonl = string.Join(
            '\n',
            [
                "{not-valid-json",
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"future_unknown_entry","payload":{"secret":"do-not-leak-payload"}}""",
                """{"timestamp":"2026-11-19T10:00:00.500Z","type":"session_meta","payload":{"session_id":"codex-sess-safe"}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"function_call","call_id":"call_ok","name":"shell","arguments":"{\"cmd\":\"pwd\"}"}}""",
                """{"timestamp":"2026-11-19T10:00:01.100Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call_ok","output":"/repo"}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-problems.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        Assert.Equal(2, batch.Problems.Count);
        Assert.Contains(batch.Problems, static p => p.Code == "MALFORMED_JSON_LINE");
        Assert.Contains(batch.Problems, static p => p.Code == "UNKNOWN_EVENT_TYPE");
        Assert.All(batch.Problems, static p =>
        {
            Assert.DoesNotContain("do-not-leak-payload", p.Message, StringComparison.Ordinal);
            Assert.False(JsonDocumentText.IsDocument(p.Message));
        });

        CollectedRecord child = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.Equal("call_ok", child.Tool?.CallId);
        Assert.Equal("/repo", child.Tool?.Result?.PlainText);
    }

    [Fact]
    public void Map_PersistsCleanlyToInMemoryRelationalStore_WithoutJsonInTextColumns()
    {
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-sess-store","model":"gpt-5.2-codex","cwd":"/work"}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"function_call","call_id":"c1","name":"shell","arguments":"{\"cmd\":\"ls\"}"}}""",
                """{"timestamp":"2026-11-19T10:00:01.100Z","type":"response_item","payload":{"type":"function_call_output","call_id":"c1","output":"file1\nfile2"}}""",
                """{"timestamp":"2026-11-19T10:00:02.000Z","type":"response_item","payload":{"type":"custom_tool_call","call_id":"c2","name":"apply_patch","input":"*** Begin Patch\n*** End Patch"}}""",
                """{"timestamp":"2026-11-19T10:00:02.100Z","type":"response_item","payload":{"type":"custom_tool_call_output","call_id":"c2","output":"{\"ok\":true}"}}""",
                """{"timestamp":"2026-11-19T10:00:03.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":40,"cached_input_tokens":10,"output_tokens":15}}}}""",
                """{"timestamp":"2026-11-19T10:00:03.100Z","type":"event_msg","payload":{"type":"task_complete"}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-store.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        Assert.Equal(3, store.CountRecords("codex-cli"));
        Assert.NotNull(store.GetSession("codex-sess-store"));
    }

    /// <summary>
    /// A turn whose session resolves a model with its own published tokenizer counts the tool
    /// result part with that model's own tokenizer, not the labelled stand-in.
    /// </summary>
    [Fact]
    public void Map_CountsToolResultPartsWithTheTurnModelOwnTokenizer()
    {
        string outputDoc = "{\"こんにちは\":\"世界\"}";
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-sess-own","model":"gpt-3.5-turbo","cwd":"/workspace/repo"}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"function_call","call_id":"call_own_1","name":"shell","arguments":"{\"timeout\":5000}"}}""",
                """{"timestamp":"2026-11-19T10:00:02.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"call_own_1","output":"{\"こんにちは\":\"世界\"}"}}""",
                """{"timestamp":"2026-11-19T10:00:03.000Z","type":"event_msg","payload":{"type":"task_complete"}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-model-own.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        CollectedRecord toolRecord = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.Equal("gpt-3.5-turbo", Assert.Single(batch.Records, static r => r.Op == RecordOp.LlmInvoke).Model);
        CollectedContentPart part = Assert.Single(toolRecord.Parts);
        Assert.Equal(ContentBucket.ToolResultContent, part.Bucket);
        Assert.NotNull(part.Withheld);
        Assert.Equal(LocalTextTokenizer.Shared.Count(outputDoc, "gpt-3.5-turbo").Count, part.Withheld!.MeasuredTokens);
        Assert.NotEqual(LocalTextTokenizer.Shared.Count(outputDoc, null).Count, part.Withheld.MeasuredTokens);
    }

    private static NativeSourceUnit CreateUnit(string fileName, string rawText)
    {
        string sourceKey = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "codex-sessions", fileName);
        SourceUnit unit = new(
            HarnessId: "codex-cli",
            SourceKey: sourceKey,
            ProviderId: "codex",
            ParserId: "codex-transcript-reader",
            ParserContractVersion: "1",
            Format: "codex-jsonl",
            SourceRootLabel: "~/.codex/sessions",
            RevisionToken: "rev-1",
            LastWriteUtc: ImportedAtUtc,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText),
            Kind: SourceKind.File);

        return new NativeSourceUnit(
            Unit: unit,
            RawText: rawText,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: ImportedAtUtc);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
