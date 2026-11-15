using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Dash;
using Xunit;

namespace KyberWeave.Tests.Dash;

/// <summary>
/// Verifies Slice 5 fallback transcript synthesis, child <c>tool.invoke</c> 64 KiB bounding,
/// cross-path OTLP/file deduplication with <c>DEDUP_DISAGREEMENT</c> recording, streaming
/// JSONL transcript readers, source-unit checkpointing, 14-day content retention purging, and
/// <see cref="CanonRefresh.RefreshHarnessSources"/> end-to-end projection.
/// </summary>
public sealed class CanonSynthParityTests
{
    [Fact]
    public void SynthesizeCall_AppliesProviderConventionsReasoningFoldAndCopilotAbsentOutput()
    {
        Assert.True(CanonSynth.ProviderConventions.Count >= 44);
        Assert.Equal("exclusive", CanonSynth.ConventionFor("claude"));
        Assert.Equal("exclusive", CanonSynth.ConventionFor("unseen-future-provider"));

        // Reasoning is already inclusive for claude/codex/copilot, exclusive for pi/kimi.
        ParsedProviderCall claudeCall = new()
        {
            Timestamp = "2026-11-14T10:00:00.000Z",
            Provider = "claude",
            Model = "claude-sonnet-4-20250514",
            InputTokens = 120,
            OutputTokens = 80,
            CacheReadInputTokens = 500,
            CacheCreationInputTokens = 40,
            ReasoningTokens = 30,
            CostUsd = 0.045,
            CostIsEstimated = false,
            SessionId = "sess-1",
            TurnId = "msg-1",
            SessionTitle = "Fix build pipeline",
            Skills = ["docs", "docs", "lint"],
        };

        CanonicalRecord claudeRecord = CanonSynth.SynthesizeCall(claudeCall);
        Assert.Equal("synth:claude:sess-1:msg-1", claudeRecord.SpanId);
        Assert.Equal("synth:claude:sess-1", claudeRecord.TraceId);
        Assert.Equal("codeburn/claude", claudeRecord.Source);
        Assert.Equal("llm.invoke", claudeRecord.Op);
        Assert.Equal(80, claudeRecord.Tokens.Output);
        Assert.Equal(30, claudeRecord.Tokens.Reasoning);
        Assert.Equal(660, claudeRecord.Tokens.ReportedInput);
        Assert.Equal(CostBasis.Harness, claudeRecord.Cost.Basis);
        Assert.Equal(CostStatus.Priced, claudeRecord.Cost.Status);
        Assert.NotNull(claudeRecord.Raw);
        Assert.Equal("Fix build pipeline", claudeRecord.Raw![CanonSynth.SessionLabelKey].GetString());
        Assert.Equal(2, claudeRecord.Raw[CanonSynth.SkillsInvokedKey].GetArrayLength());

        ParsedProviderCall piCall = new()
        {
            Timestamp = "2026-11-14T10:01:00.000Z",
            Provider = "pi",
            Model = "gpt-5",
            InputTokens = 100,
            OutputTokens = 60,
            ReasoningTokens = 40,
            CostUsd = 0,
            CostIsEstimated = true,
            SessionId = "sess-pi",
            DeduplicationKey = "pi:sess-pi:call-9",
        };

        CanonicalRecord piRecord = CanonSynth.SynthesizeCall(piCall);
        Assert.Equal(100, piRecord.Tokens.Output);
        Assert.Equal(100, piRecord.Tokens.ReportedOutput);
        Assert.Equal(40, piRecord.Tokens.Reasoning);
        Assert.Equal(CostBasis.Published, piRecord.Cost.Basis);
        Assert.Equal(CostStatus.NoRate, piRecord.Cost.Status);

        // Cursor pairing-only turnId must stay out of NativeRecordIdentity.
        ParsedProviderCall cursorWithTurnId = new()
        {
            Timestamp = "2026-11-14T10:02:00.000Z",
            Provider = "cursor",
            Model = "claude-4-sonnet",
            InputTokens = 50,
            OutputTokens = 25,
            SessionId = "cursor-s1",
            TurnId = "pairing-only-id",
            DeduplicationKey = "cursor:cursor-s1",
        };
        ParsedProviderCall cursorWithoutTurnId = cursorWithTurnId with { TurnId = null };
        (string cursorIdWith, string? digestWith) = CanonSynth.NativeRecordIdentity(cursorWithTurnId, ordinal: 0);
        (string cursorIdWithout, string? digestWithout) = CanonSynth.NativeRecordIdentity(cursorWithoutTurnId, ordinal: 0);
        Assert.Equal(cursorIdWithout, cursorIdWith);
        Assert.NotNull(digestWith);
        Assert.Equal(digestWithout, digestWith);

        // Copilot shutdown rollup marks output not_measurable instead of claiming a measured zero.
        ParsedProviderCall copilotShutdown = new()
        {
            Timestamp = "2026-11-14T10:03:00.000Z",
            Provider = "copilot",
            Model = "gpt-4.1",
            InputTokens = 300,
            OutputTokens = 0,
            CostUsd = 0.02,
            SessionId = "cop-1",
            DeduplicationKey = "copilot:cop-1:shutdown:gpt-4.1:0",
        };

        CanonicalRecord copilotRecord = CanonSynth.SynthesizeCall(copilotShutdown);
        Assert.Equal(CostBasis.Published, copilotRecord.Cost.Basis);
        Assert.NotNull(copilotRecord.Measurability);
        Assert.True(copilotRecord.Measurability!.TryGetValue("output", out JsonElement outMeas));
        Assert.Equal("not_measurable", outMeas.GetProperty("availability").GetString());
    }

    [Fact]
    public void SynthesizeToolCalls_Bounds64KiBPayloadsAndExtractsStructuredErrors()
    {
        ParsedProviderCall parentCall = new()
        {
            Timestamp = "2026-11-14T11:00:00.000Z",
            Provider = "claude",
            Model = "claude-sonnet-4-20250514",
            InputTokens = 200,
            OutputTokens = 50,
            SessionId = "sess-tools",
            TurnId = "turn-tools-1",
        };

        ReaderTurn turn = new()
        {
            Parts = [new ContentPart("conversation_history", "Run tests", Order: 0)],
            ToolCalls =
            [
                new ReaderToolCall
                {
                    Id = "tc-big",
                    Name = "write_file",
                    Arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string>
                    {
                        ["path"] = "src/Program.cs",
                        ["content"] = new string('x', 80_000),
                    }),
                    Timestamp = "2026-11-14T11:00:01.000Z",
                },
            ],
            ToolResults =
            [
                new ReaderToolResult
                {
                    ToolCallId = "tc-big",
                    Content = new string('é', 40_000),
                    IsError = true,
                    Timestamp = "2026-11-14T11:00:02.250Z",
                },
            ],
        };

        CanonicalRecord parentRecord = CanonSynth.SynthesizeCall(parentCall, readerTurn: turn);
        IReadOnlyList<CanonicalRecord> childRecords = CanonSynth.SynthesizeToolCalls(parentRecord, parentCall, turn);

        CanonicalRecord toolRecord = Assert.Single(childRecords);
        Assert.StartsWith($"{parentRecord.SpanId}-t", toolRecord.SpanId, StringComparison.Ordinal);
        Assert.Equal(parentRecord.SpanId, toolRecord.ParentSpanId);
        Assert.Equal("tool.invoke", toolRecord.Op);
        Assert.Equal("error", toolRecord.Status);
        Assert.Equal(1250, toolRecord.DurationMs);

        Assert.NotNull(toolRecord.Parts);
        ContentPart resultPart = Assert.Single(toolRecord.Parts!);
        Assert.True(Encoding.UTF8.GetByteCount(resultPart.Text) <= CanonSynth.MaxToolResultBytes);
        Assert.True(resultPart.Truncated);

        Assert.NotNull(toolRecord.Raw);
        Assert.True(toolRecord.Raw!["gen_ai.tool.arguments_truncated"].GetBoolean());
        Assert.False(string.IsNullOrEmpty(toolRecord.Raw["gen_ai.tool.arguments_hash"].GetString()));
        JsonElement boundedArgs = toolRecord.Raw["arguments"];
        Assert.Equal(JsonValueKind.Object, boundedArgs.ValueKind);
        Assert.Equal("src/Program.cs", boundedArgs.GetProperty("path").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(boundedArgs.GetRawText()) <= CanonSynth.MaxToolArgumentsBytes);
    }

    [Fact]
    public void Deduplicate_CollapsesOtlpAndFileTurnsAndRecordsDisagreement()
    {
        using TempDirectory directory = new();
        CanonStore store = new(Path.Combine(directory.Path, "canon.db"));

        ParsedProviderCall fileCall = new()
        {
            Timestamp = "2026-11-14T12:00:00.000Z",
            Provider = "claude-code",
            Model = "claude-sonnet-4-20250514",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.01,
            SessionId = "s-shared",
            TurnId = "msg-42",
        };
        ReaderTurn fileTurn = new()
        {
            Parts = [new ContentPart("conversation_history", "File transcript body", Order: 0)],
        };
        CanonicalRecord synthRecord = CanonSynth.SynthesizeCall(fileCall, readerTurn: fileTurn);

        CanonicalRecord otlpRecord = new()
        {
            SpanId = "abcdef0123456789",
            TraceId = "0102030405060708090a0b0c0d0e0f10",
            Source = "claude-code",
            Harness = "claude-code",
            SessionId = "s-shared",
            Name = "claude-sonnet-4-20250514",
            Op = "llm.invoke",
            Kind = "client",
            Timestamp = "2026-11-14T12:00:00.000Z",
            DurationMs = 500,
            Status = "ok",
            Tokens = new TokenUsage(220, 0, 0, 50, 220, 50),
            Content = new Dictionary<string, string>(StringComparer.Ordinal),
            Parts = null,
            Cost = new CostBlock(CostBasis.Published, CostStatus.Priced, 0.02, "USD"),
            Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["session.id"] = JsonSerializer.SerializeToElement("s-shared"),
                ["gen_ai.response.id"] = JsonSerializer.SerializeToElement("msg-42"),
            },
        };

        CanonicalRecord geminiRecord = otlpRecord with { SpanId = "synth:gemini:g1:m1", Harness = "gemini" };

        Assert.Equal("synth:claude-code:s-shared", CanonSynthDedup.DeduplicationKeyFor(synthRecord));
        Assert.Equal("synth:claude-code:s-shared", CanonSynthDedup.DeduplicationKeyFor(otlpRecord));

        IReadOnlyList<CanonicalRecord> result = CanonSynthDedup.Deduplicate([synthRecord, geminiRecord], [otlpRecord], store);
        CanonicalRecord survivor = Assert.Single(result);
        Assert.Equal("abcdef0123456789", survivor.SpanId);
        Assert.Equal(220, survivor.Tokens.FreshInput);
        Assert.NotNull(survivor.Parts);
        Assert.Equal("File transcript body", survivor.Parts![0].Text);
        Assert.Equal("File transcript body", survivor.Content["conversation_history"]);

        IReadOnlyList<SpanProblemRow> problems = store.GetProblems("abcdef0123456789");
        SpanProblemRow problem = Assert.Single(problems);
        Assert.Equal(CanonSynthDedup.DedupDisagreementCode, problem.Code);
        Assert.Equal("warning", problem.Severity);
        Assert.Contains("fresh input (100 vs 220)", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamingTranscriptReaders_ParseClaudeAndCodexJsonlWithoutFullFileInflation()
    {
        using TempDirectory directory = new();
        string claudePath = Path.Combine(directory.Path, "claude-session.jsonl");
        string codexPath = Path.Combine(directory.Path, "codex-session.jsonl");

        File.WriteAllLines(
            claudePath,
            [
                "{not valid json",
                "{\"type\":\"ai-title\",\"aiTitle\":\"Fix reader\"}",
                "{\"type\":\"user\",\"sessionId\":\"c-sess-1\",\"timestamp\":\"2026-11-14T13:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"No, use the cached index instead\"}]}}",
                "{\"type\":\"assistant\",\"sessionId\":\"c-sess-1\",\"timestamp\":\"2026-11-14T13:00:02.000Z\",\"attributionMcpServer\":\"github\",\"message\":{\"id\":\"m-1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-20250514\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"Checking cache.\"},{\"type\":\"tool_use\",\"id\":\"tu-1\",\"name\":\"read_file\",\"input\":{\"path\":\"README.md\"}}],\"usage\":{\"input_tokens\":50,\"output_tokens\":20,\"cache_read_input_tokens\":100,\"cache_creation_input_tokens\":10}}}",
                "{\"type\":\"assistant\",\"sessionId\":\"c-sess-1\",\"timestamp\":\"2026-11-14T13:00:05.000Z\",\"message\":{\"id\":\"m-1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-20250514\",\"content\":[{\"type\":\"text\",\"text\":\"Done.\"}],\"usage\":{\"input_tokens\":50,\"output_tokens\":20,\"cache_read_input_tokens\":100,\"cache_creation_input_tokens\":10}}}",
                "{\"type\":\"user\",\"sessionId\":\"c-sess-1\",\"timestamp\":\"2026-11-14T13:00:06.000Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"tu-1\",\"is_error\":false,\"content\":\"# Title\"}]}}",
            ],
            Encoding.UTF8);

        IReadOnlyList<SourceRecordEnvelope> claudeEnvelopes = CanonTranscriptReaders.ReadClaudeEnvelopes(claudePath);
        SourceRecordEnvelope claudeEnv = Assert.Single(claudeEnvelopes);
        Assert.Equal("claude-code", claudeEnv.HarnessId);
        Assert.Equal("c-sess-1", claudeEnv.NativeSessionId);
        Assert.Equal("m-1", claudeEnv.NativeRecordId);
        Assert.True(claudeEnv.ReaderTurn?.IsCorrection);
        Assert.NotNull(claudeEnv.ReaderTurn?.Parts);
        Assert.Contains(claudeEnv.ReaderTurn!.Parts, p => p.Part == "conversation_history" && p.Server == "github");
        Assert.Single(claudeEnv.ReaderTurn.ToolCalls!);

        File.WriteAllLines(
            codexPath,
            [
                "{\"type\":\"session_meta\",\"timestamp\":\"2026-11-14T14:00:00.000Z\",\"payload\":{\"session_id\":\"x-sess-1\",\"base_instructions\":{\"text\":\"System rules\"}}}",
                "{\"type\":\"world_state\",\"timestamp\":\"2026-11-14T14:00:01.000Z\",\"payload\":{\"state\":{\"agents_md\":{\"text\":\"AGENTS guide\"}}}}",
                "{\"type\":\"response_item\",\"timestamp\":\"2026-11-14T14:00:02.000Z\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Inspect tests\"}]}}",
                "{\"type\":\"response_item\",\"timestamp\":\"2026-11-14T14:00:03.000Z\",\"payload\":{\"type\":\"function_call\",\"call_id\":\"fc-1\",\"name\":\"run_tests\",\"arguments\":\"{\\\"filter\\\":\\\"Dash\\\"}\"}}",
                "{\"type\":\"response_item\",\"timestamp\":\"2026-11-14T14:00:04.000Z\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"fc-1\",\"output\":\"22 passed\"}}",
                "{\"type\":\"event_msg\",\"timestamp\":\"2026-11-14T14:00:05.000Z\",\"payload\":{\"type\":\"token_count\",\"model_context_window\":128000,\"info\":{\"model\":\"gpt-5\",\"last_token_usage\":{\"input_tokens\":500,\"cached_input_tokens\":300,\"output_tokens\":80,\"reasoning_output_tokens\":20}}}}",
            ],
            Encoding.UTF8);

        IReadOnlyList<SourceRecordEnvelope> codexEnvelopes = CanonTranscriptReaders.ReadCodexEnvelopes(codexPath);
        SourceRecordEnvelope codexEnv = Assert.Single(codexEnvelopes);
        Assert.Equal("codex-cli", codexEnv.HarnessId);
        Assert.Equal("x-sess-1", codexEnv.NativeSessionId);
        Assert.Equal(200, codexEnv.Call.InputTokens);
        Assert.Equal(300, codexEnv.Call.CacheReadInputTokens);
        Assert.Equal(128_000, codexEnv.ReaderTurn?.ContextWindow);
        Assert.Contains(codexEnv.ReaderTurn!.Parts, p => p.Part == "system_prompt" && p.Text == "System rules");
        Assert.Contains(codexEnv.ReaderTurn.Parts, p => p.Part == "instruction_context" && p.Text == "AGENTS guide");
        Assert.Contains(codexEnv.ReaderTurn.Parts, p => p.Part == "tool_result_content" && p.Text == "22 passed");

        IReadOnlyList<CanonicalRecord> codexRecords = CanonSynth.SynthesizeEnvelopes(codexEnvelopes);
        Assert.Equal(2, codexRecords.Count);
        Assert.Equal("llm.invoke", codexRecords[0].Op);
        Assert.Equal("tool.invoke", codexRecords[1].Op);
    }

    [Fact]
    public void CanonRefresh_CommitsCheckpointsSkipsUnchangedUnitsPurges14DayContentAndProjects()
    {
        using TempDirectory directory = new();
        CanonStore store = new(Path.Combine(directory.Path, "canon.db"));

        string transcriptPath = Path.Combine(directory.Path, "refresh-claude.jsonl");
        File.WriteAllLines(
            transcriptPath,
            [
                "{\"type\":\"user\",\"sessionId\":\"r-sess-1\",\"timestamp\":\"2026-11-10T10:00:00.000Z\",\"message\":{\"role\":\"user\",\"content\":\"Hello\"}}",
                "{\"type\":\"assistant\",\"sessionId\":\"r-sess-1\",\"timestamp\":\"2026-11-10T10:00:05.000Z\",\"message\":{\"id\":\"rm-1\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-20250514\",\"content\":[{\"type\":\"text\",\"text\":\"World\"}],\"usage\":{\"input_tokens\":40,\"output_tokens\":10,\"cache_read_input_tokens\":0,\"cache_creation_input_tokens\":0}}}",
            ],
            Encoding.UTF8);

        // Seed an expired (>14 days old) record to verify rolling retention purge preserves raw.
        CanonicalRecord oldRecord = new()
        {
            SpanId = "synth:claude-code:old-sess:old-1",
            TraceId = "synth:claude-code:old-sess",
            Source = "codeburn/claude-code",
            Harness = "claude-code",
            SessionId = "old-sess",
            Name = "claude:claude-sonnet-4-20250514",
            Op = "llm.invoke",
            Kind = "internal",
            Timestamp = "2026-10-01T10:00:00.000Z",
            DurationMs = 10,
            Status = "ok",
            Tokens = new TokenUsage(10, 0, 0, 5, 10, 5),
            Content = new Dictionary<string, string>(StringComparer.Ordinal) { ["conversation_history"] = "Sensitive ancient text" },
            Parts = [new ContentPart("conversation_history", "Sensitive ancient text", Order: 0)],
            Cost = new CostBlock(CostBasis.Unknown, CostStatus.NoRate),
            Raw = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["keepMarker"] = JsonSerializer.SerializeToElement("preserved"),
            },
        };
        store.UpsertRecords([oldRecord]);

        int loadCalls = 0;
        RefreshSourceInput sourceInput = new()
        {
            HarnessId = "claude-code",
            SourceKey = transcriptPath,
            ProviderId = "claude",
            ParserId = "claude-transcript-reader",
            ParserContractVersion = "1",
            Format = "claude-jsonl",
            SourceRootLabel = "claude-projects",
            RevisionToken = "rev-1",
            LoadEnvelopes = () =>
            {
                loadCalls++;
                return CanonTranscriptReaders.ReadClaudeEnvelopes(transcriptPath, sourceRevision: "rev-1");
            },
        };

        DateTimeOffset fixedNow = new(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        RefreshReport first = CanonRefresh.RefreshHarnessSources(store, [sourceInput], now: fixedNow, historyWeeks: 2, trigger: "test");
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(1, loadCalls);
        Assert.Equal("committed", Assert.Single(first.Rows).Status);
        Assert.Equal(1, first.PurgedContentCount);
        Assert.NotNull(store.GetSession("r-sess-1"));

        CanonicalRecord? purgedOld = store.GetRecord(oldRecord.SpanId);
        Assert.NotNull(purgedOld);
        Assert.Empty(purgedOld!.Content);
        Assert.Null(purgedOld.Parts);
        Assert.Equal("preserved", purgedOld.Raw!["keepMarker"].GetString());

        Assert.Equal("14", store.GetMetadata(CanonRefresh.ContentRetentionDaysKey));
        Assert.Equal("2026-11-01T00:00:00.000Z", store.GetMetadata(CanonRefresh.ContentPurgedThroughKey));

        // Second pass with identical revision & contract skips re-reading the source unit.
        RefreshReport second = CanonRefresh.RefreshHarnessSources(store, [sourceInput], now: fixedNow, historyWeeks: 2, trigger: "test");
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(1, loadCalls);
        Assert.Equal("skipped", Assert.Single(second.Rows).Status);
        Assert.Equal(0, second.PurgedContentCount);

        IReadOnlyList<RefreshRunRow> runs = store.ListRefreshRuns();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.Equal("success", r.Status));

        SourceCheckpoint? checkpoint = store.GetSourceCheckpoint("claude-code", transcriptPath);
        Assert.NotNull(checkpoint);
        Assert.False(CanonRefresh.CheckpointIsReusable(checkpoint, new CoverageRequest("rev-2", "1")));
        Assert.False(CanonRefresh.CheckpointIsReusable(checkpoint, new CoverageRequest("rev-1", "2")));
        IReadOnlyList<CoverageInterval> expandedGaps = CanonRefresh.UncoveredIntervals(
            checkpoint,
            new CoverageInterval("2026-10-20T00:00:00.000Z", "2026-11-15T00:00:00.000Z"),
            new CoverageRequest("rev-1", "1"));
        CoverageInterval gap = Assert.Single(expandedGaps);
        Assert.Equal("2026-10-20T00:00:00.000Z", gap.FromUtc);
    }
}
