using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Codex;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CodexRolloutMapper"/> covering <c>local_shell_call</c> and
/// <c>local_shell_call_output</c> status mapping, <c>action</c> measurement withholding,
/// and plain <c>output</c> preservation.
/// </summary>
public class CodexLocalShellTests
{
    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_CompletedLocalShellCall_RecordsSuccessOutcomeSizeFingerprintAndPlainOutput()
    {
        string rawAction = """{"working_directory":"/workspace/repo","type":"exec","command":["git","status"],"timeout_ms":5000}""";
        string canonicalAction = """{"command":["git","status"],"timeout_ms":5000,"type":"exec","working_directory":"/workspace/repo"}""";
        string plainOutput = "On branch main\nnothing to commit";

        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-local-shell-1","model":"gpt-5.2-codex","cwd":"/workspace/repo"}}""",
                $$$"""{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"local_shell_call","id":"lsh_1","call_id":"call_lsh_1","status":"completed","action":{{{rawAction}}}}}""",
                """{"timestamp":"2026-11-19T10:00:01.250Z","type":"response_item","payload":{"type":"local_shell_call_output","call_id":"call_lsh_1","output":"On branch main\nnothing to commit"}}""",
                """{"timestamp":"2026-11-19T10:00:02.000Z","type":"event_msg","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":40,"cached_input_tokens":10,"output_tokens":15}}}}""",
                """{"timestamp":"2026-11-19T10:00:02.100Z","type":"event_msg","payload":{"type":"task_complete"}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-local-shell-completed.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        Assert.Empty(batch.Problems);
        CollectedRecord child = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.Equal("local_shell", child.Name);
        Assert.Equal(RecordStatus.Ok, child.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:01.000Z", CultureInfo.InvariantCulture), child.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-19T10:00:01.250Z", CultureInfo.InvariantCulture), child.EndedAt);
        Assert.Equal(250L, child.ReportedDurationMs);

        Assert.NotNull(child.Tool);
        Assert.Equal("call_lsh_1", child.Tool.CallId);
        Assert.Equal("local_shell", child.Tool.ToolName);
        Assert.True(child.Tool.Succeeded);
        Assert.Null(child.Tool.Error);

        Assert.NotNull(child.Tool.Input);
        Assert.Null(child.Tool.Input.PlainText);
        Assert.Null(child.Tool.Input.OriginalUtf8BytesWhenTruncated);
        Assert.Equal(Encoding.UTF8.GetByteCount(rawAction), child.Tool.Input.Utf8Bytes);
        Assert.Equal(rawAction.Length, child.Tool.Input.Characters);
        Assert.Equal(Sha256Hex(canonicalAction), child.Tool.Input.Sha256Fingerprint);

        Assert.NotNull(child.Tool.Result);
        Assert.Equal(plainOutput, child.Tool.Result.PlainText);
        Assert.Equal(Encoding.UTF8.GetByteCount(plainOutput), child.Tool.Result.Utf8Bytes);
        Assert.Equal(plainOutput.Length, child.Tool.Result.Characters);

        CollectedContentPart resultPart = Assert.Single(child.Parts);
        Assert.Equal(ContentBucket.ToolResultContent, resultPart.Bucket);
        Assert.Equal(plainOutput, resultPart.PlainText);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        CollectedRecord? storedChild = store.GetRecord(child.RecordId);
        Assert.NotNull(storedChild);
        Assert.NotNull(storedChild.Tool);
        Assert.True(storedChild.Tool.Succeeded);
        Assert.Null(storedChild.Tool.Input?.PlainText);
        Assert.Equal(Sha256Hex(canonicalAction), storedChild.Tool.Input?.Sha256Fingerprint);
        Assert.Equal(plainOutput, storedChild.Tool.Result?.PlainText);
    }

    [Fact]
    public void Map_CompletedLocalShellCallWithoutOutput_StillRecordsSuccessOutcome()
    {
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-local-shell-2"}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"local_shell_call","call_id":"call_done_no_out","status":"completed","action":{"type":"exec","command":["true"]}}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-local-shell-completed-no-output.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        CollectedRecord child = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.Equal("local_shell", child.Name);
        Assert.Equal(RecordStatus.Ok, child.Status);
        Assert.True(child.Tool?.Succeeded);
        Assert.Null(child.Tool?.Result);
        Assert.Null(child.Tool?.Input?.PlainText);
        Assert.NotNull(child.Tool?.Input?.Sha256Fingerprint);
    }

    [Fact]
    public void Map_IncompleteLocalShellCall_RecordsFailureOutcomeAndSafeErrorMessage()
    {
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-local-shell-3"}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"local_shell_call","call_id":"call_incomplete_unpaired","status":"incomplete","action":{"type":"exec","command":["sleep","120"]}}}""",
                """{"timestamp":"2026-11-19T10:00:02.000Z","type":"response_item","payload":{"type":"local_shell_call","call_id":"call_incomplete_paired","status":"incomplete","action":{"type":"exec","command":["cat","/root/secret"]}}}""",
                """{"timestamp":"2026-11-19T10:00:02.100Z","type":"response_item","payload":{"type":"local_shell_call_output","call_id":"call_incomplete_paired","output":"Permission denied"}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-local-shell-incomplete.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        List<CollectedRecord> tools = [.. batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke)];
        Assert.Equal(2, tools.Count);

        CollectedRecord unpaired = tools[0];
        Assert.Equal("local_shell", unpaired.Name);
        Assert.Equal(RecordStatus.Error, unpaired.Status);
        Assert.False(unpaired.Tool?.Succeeded);
        Assert.NotNull(unpaired.Tool?.Error);
        Assert.False(JsonDocumentText.IsDocument(unpaired.Tool.Error.Message));
        Assert.NotNull(unpaired.Signals.ErrorMessage);
        Assert.Null(unpaired.Tool.Result);

        CollectedRecord paired = tools[1];
        Assert.Equal("local_shell", paired.Name);
        Assert.Equal(RecordStatus.Error, paired.Status);
        Assert.False(paired.Tool?.Succeeded);
        Assert.NotNull(paired.Tool?.Error);
        Assert.Equal("Permission denied", paired.Tool.Error.Message);
        Assert.Equal("Permission denied", paired.Signals.ErrorMessage);
        Assert.Equal("Permission denied", paired.Tool.Result?.PlainText);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        CollectedRecord? storedUnpaired = store.GetRecord(unpaired.RecordId);
        Assert.NotNull(storedUnpaired?.Tool);
        Assert.False(storedUnpaired.Tool.Succeeded);
    }

    [Fact]
    public void Map_InProgressLocalShellCall_OmitsOutcomeRow()
    {
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-local-shell-4"}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"local_shell_call","call_id":"call_running","status":"in_progress","action":{"type":"exec","command":["dotnet","test"]}}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-local-shell-in-progress.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        CollectedRecord child = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.Equal("local_shell", child.Name);
        Assert.Equal(RecordStatus.Unset, child.Status);
        Assert.NotNull(child.Tool);
        Assert.Null(child.Tool.Succeeded);
        Assert.Null(child.Tool.Error);
        Assert.Null(child.Signals.ErrorMessage);
        Assert.Null(child.Tool.Input?.PlainText);
        Assert.True(child.Tool.Input?.Utf8Bytes > 0);
        Assert.NotNull(child.Tool.Input?.Sha256Fingerprint);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        CollectedRecord? storedChild = store.GetRecord(child.RecordId);
        Assert.NotNull(storedChild?.Tool);
        Assert.Null(storedChild.Tool.Succeeded);
        Assert.Equal(RecordStatus.Unset, storedChild.Status);
    }

    [Fact]
    public void Map_CompletedLocalShellCallWithNonZeroExitCodeOutput_RecordsFailureOutcome()
    {
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-local-shell-5"}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"local_shell_call","id":"lsh_id_only","status":"completed","action":{"type":"exec","command":["false"]}}}""",
                """{"timestamp":"2026-11-19T10:00:01.050Z","type":"response_item","payload":{"type":"local_shell_call_output","id":"lsh_id_only","exit_code":2,"output":"command failed"}}""",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-local-shell-exit-code.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        CollectedRecord child = Assert.Single(batch.Records, static r => r.Op == RecordOp.ToolInvoke);
        Assert.Equal("lsh_id_only", child.Tool?.CallId);
        Assert.Equal(RecordStatus.Error, child.Status);
        Assert.False(child.Tool?.Succeeded);
        Assert.Equal(2L, child.Tool?.ExitCode);
        Assert.Equal("command failed", child.Tool?.Error?.Message);
        Assert.Equal("command failed", child.Tool?.Result?.PlainText);
    }

    [Fact]
    public void Map_LocalShellCallOutputWithJsonOrOversizedPlainText_EnforcesContentGuards()
    {
        string largeOutput = new('x', CanonSynth.MaxToolResultBytes + 64);
        string rawJsonl = string.Join(
            '\n',
            [
                """{"timestamp":"2026-11-19T10:00:00.000Z","type":"session_meta","payload":{"session_id":"codex-local-shell-6"}}""",
                """{"timestamp":"2026-11-19T10:00:01.000Z","type":"response_item","payload":{"type":"local_shell_call","call_id":"call_json_out","status":"completed","action":{"type":"exec","command":["echo","json"]}}}""",
                """{"timestamp":"2026-11-19T10:00:01.050Z","type":"response_item","payload":{"type":"local_shell_call_output","call_id":"call_json_out","output":"{\"ok\":true}"}}""",
                """{"timestamp":"2026-11-19T10:00:02.000Z","type":"response_item","payload":{"type":"local_shell_call","call_id":"call_large_out","status":"completed","action":{"type":"exec","command":["cat","big.txt"]}}}""",
                "{\"timestamp\":\"2026-11-19T10:00:02.050Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"local_shell_call_output\",\"call_id\":\"call_large_out\",\"output\":" + System.Text.Json.JsonSerializer.Serialize(largeOutput) + "}}",
            ]);

        NativeSourceUnit unit = CreateUnit("rollout-local-shell-guards.jsonl", rawJsonl);
        CollectedBatch batch = new CodexRolloutMapper().Map(unit, ImportedAtUtc);

        List<CollectedRecord> tools = [.. batch.Records.Where(static r => r.Op == RecordOp.ToolInvoke)];
        Assert.Equal(2, tools.Count);

        CollectedRecord jsonOut = tools[0];
        Assert.Null(jsonOut.Tool?.Result?.PlainText);
        Assert.True(jsonOut.Tool?.Result?.Utf8Bytes > 0);
        CollectedContentPart jsonPart = Assert.Single(jsonOut.Parts);
        Assert.Null(jsonPart.PlainText);
        Assert.NotNull(jsonPart.Withheld);

        CollectedRecord largeOut = tools[1];
        Assert.NotNull(largeOut.Tool?.Result?.PlainText);
        Assert.True(Encoding.UTF8.GetByteCount(largeOut.Tool.Result.PlainText) <= CanonSynth.MaxToolResultBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(largeOutput), largeOut.Tool.Result.OriginalUtf8BytesWhenTruncated);
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
