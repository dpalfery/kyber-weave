using System.Text;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.CopilotCli;
using KyberDash.Data.Tokenization;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for <see cref="CopilotCliJournalReader"/> and <see cref="CopilotCliMapper"/>.
/// </summary>
public class CopilotCliMapperTests
{
    private static readonly string HomeRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "fake-home");
    private static readonly string StateRoot = Path.Combine(HomeRoot, ".kyberdash");
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Discover_SkipsPendingSessionDirectoriesAndHonorsSinceUtc()
    {
        FakeHarnessFileSystem fs = new();
        string validPath = Combine(HomeRoot, ".copilot/session-state/sess-1/events.jsonl");
        string pendingPath = Combine(HomeRoot, ".copilot/session-state/pending-session:temp-1/events.jsonl");
        string stalePath = Combine(HomeRoot, ".copilot/session-state/sess-old/events.jsonl");

        fs.AddFile(validPath, "{}\n", BaselineUtc);
        fs.AddFile(pendingPath, "{}\n", BaselineUtc);
        fs.AddFile(stalePath, "{}\n", BaselineUtc.AddDays(-10));

        CopilotCliJournalReader reader = new();
        SourceDiscoveryResult result = reader.Discover(new SourceDiscoveryContext(HomeRoot, StateRoot, fs, SinceUtc: BaselineUtc.AddDays(-1)));

        Assert.Empty(result.Problems);
        SourceUnit unit = Assert.Single(result.Sources);
        Assert.Equal("copilot-cli", unit.HarnessId);
        Assert.Equal("copilot-cli-jsonl", unit.Format);
        Assert.Equal(fs.GetFullPath(validPath), unit.SourceKey);
    }

    [Fact]
    public void Read_ReturnsNullForPendingSessionPath()
    {
        FakeHarnessFileSystem fs = new();
        string pendingPath = Combine(HomeRoot, ".copilot/session-state/pending-session:abc/events.jsonl");
        fs.AddFile(pendingPath, "{\"type\":\"session.start\"}\n", BaselineUtc);

        CopilotCliJournalReader reader = new();
        SourceUnit pendingUnit = new(
            HarnessId: "copilot-cli",
            SourceKey: fs.GetFullPath(pendingPath),
            ProviderId: "copilot",
            ParserId: "copilot-cli-reader",
            ParserContractVersion: "1",
            Format: "copilot-cli-jsonl",
            SourceRootLabel: "pending-session:abc",
            RevisionToken: "1-1");

        Assert.Null(reader.Read(pendingUnit, fs));
    }

    [Fact]
    public void Map_EmitsLlmInvokeForEveryAssistantMessageWithoutOutputTokens_AndLeavesNoTokenRowWhenAbsent()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-no-out\",\"selectedModel\":\"claude-sonnet-4.5\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:00:05.000Z\",\"data\":{\"messageId\":\"msg-no-out\",\"content\":\"Response without outputTokens\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e3\",\"timestamp\":\"2026-11-19T10:00:10.000Z\",\"data\":{\"messageId\":\"msg-with-out\",\"content\":\"Response with outputTokens\",\"inputTokens\":900,\"outputTokens\":145}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(Combine(HomeRoot, ".copilot/session-state/sess-no-out/events.jsonl"), jsonl);
        CopilotCliMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.Equal("sess-no-out", session.NativeSessionId);
        Assert.Equal("copilot-cli", session.HarnessId);

        Assert.Equal(2, batch.Records.Count);

        CollectedRecord withoutOutput = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, withoutOutput.Op);
        Assert.Equal("claude-sonnet-4.5", withoutOutput.Model);
        Assert.Empty(withoutOutput.Tokens);
        Assert.Null(withoutOutput.ReportedOutputTokens);

        CollectedRecord withOutput = batch.Records[1];
        Assert.Equal(RecordOp.LlmInvoke, withOutput.Op);
        Assert.Equal(2, withOutput.Tokens.Count);
        Assert.Contains(withOutput.Tokens, static t => t.TokenClass == TokenClass.Input && t.Count == 900);
        Assert.Contains(withOutput.Tokens, static t => t.TokenClass == TokenClass.Output && t.Count == 145);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);

        Assert.Empty(store.TokenCounts(spanId: withoutOutput.RecordId));
        IReadOnlyList<StoredTokenCount> secondTokens = store.TokenCounts(spanId: withOutput.RecordId);
        Assert.Equal(2, secondTokens.Count);
    }

    [Fact]
    public void Map_SkipsPendingSessionStartId()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"pending-session:draft-1\",\"selectedModel\":\"gpt-5\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:00:05.000Z\",\"data\":{\"messageId\":\"msg-1\",\"content\":\"Draft reply\",\"outputTokens\":20}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(Combine(HomeRoot, ".copilot/session-state/draft-1/events.jsonl"), jsonl);
        CopilotCliMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Problems);
    }

    [Fact]
    public void Map_HandlesSystemUserReasoningModelChangeAndContextChangeEvents()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-full\",\"copilotVersion\":\"1.0.85\",\"selectedModel\":\"claude-sonnet-4.5\",\"context\":{\"cwd\":\"/work/repo-a\",\"repository\":\"owner/repo\",\"branch\":\"main\"}}}",
            "{\"type\":\"system.message\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"data\":{\"content\":\"Be concise and accurate.\"}}",
            "{\"type\":\"user.message\",\"id\":\"e3\",\"timestamp\":\"2026-11-19T10:00:02.000Z\",\"data\":{\"content\":\"Explain the failure.\"}}",
            "{\"type\":\"assistant.reasoning\",\"id\":\"e4\",\"timestamp\":\"2026-11-19T10:00:03.000Z\",\"data\":{\"reasoningId\":\"r1\",\"content\":\"Checking stack trace.\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e5\",\"timestamp\":\"2026-11-19T10:00:04.000Z\",\"data\":{\"messageId\":\"msg-1\",\"content\":\"Here is the cause.\",\"toolRequests\":[{\"toolCallId\":\"tc-1\",\"name\":\"skill\",\"arguments\":{\"skill\":\"csharp-dev\"}}]}}",
            "{\"type\":\"session.model_change\",\"id\":\"e6\",\"timestamp\":\"2026-11-19T10:01:00.000Z\",\"data\":{\"previousModel\":\"claude-sonnet-4.5\",\"newModel\":\"gpt-5.1\"}}",
            "{\"type\":\"session.context_changed\",\"id\":\"e7\",\"timestamp\":\"2026-11-19T10:01:01.000Z\",\"data\":{\"context\":{\"cwd\":\"/work/repo-b\",\"branch\":\"feat/copilot-cli\"},\"declaredContextWindow\":200000}}",
            "{\"type\":\"user.message\",\"id\":\"e8\",\"timestamp\":\"2026-11-19T10:01:02.000Z\",\"data\":{\"content\":\"Apply the fix.\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e9\",\"timestamp\":\"2026-11-19T10:01:05.000Z\",\"data\":{\"messageId\":\"msg-2\",\"content\":\"Applied the fix.\",\"reasoningText\":\"Editing handler directly.\",\"outputTokens\":42}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(Combine(HomeRoot, ".copilot/session-state/sess-full/events.jsonl"), jsonl);
        CopilotCliMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Empty(batch.Problems);
        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.NotNull(session.Workspace);
        Assert.Equal("/work/repo-b", session.Workspace.WorkingDirectory);
        Assert.Equal("owner/repo", session.Workspace.Repository);
        Assert.Equal("feat/copilot-cli", session.Workspace.Branch);

        Assert.Equal(2, batch.Records.Count);

        CollectedRecord first = batch.Records[0];
        Assert.Equal(RecordOp.LlmInvoke, first.Op);
        Assert.Equal("claude-sonnet-4.5", first.Model);
        Assert.Empty(first.Tokens);
        Assert.Equal(["csharp-dev"], first.SkillsInvoked);
        Assert.Equal(4, first.Parts.Count);
        Assert.Equal(ContentBucket.SystemPrompt, first.Parts[0].Bucket);
        Assert.Equal("Be concise and accurate.", first.Parts[0].PlainText);
        Assert.Equal(ContentBucket.UserMessage, first.Parts[1].Bucket);
        Assert.Equal("Explain the failure.", first.Parts[1].PlainText);
        Assert.Equal(ContentBucket.Reasoning, first.Parts[2].Bucket);
        Assert.Equal("Checking stack trace.", first.Parts[2].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, first.Parts[3].Bucket);
        Assert.Equal("Here is the cause.", first.Parts[3].PlainText);

        CollectedRecord second = batch.Records[1];
        Assert.Equal(RecordOp.LlmInvoke, second.Op);
        Assert.Equal("gpt-5.1", second.Model);
        Assert.Equal(200000, second.Signals.DeclaredContextWindow);
        CollectedTokenCount outputToken = Assert.Single(second.Tokens);
        Assert.Equal(TokenClass.Output, outputToken.TokenClass);
        Assert.Equal(42, outputToken.Count);
        Assert.Equal(4, second.Parts.Count);
        Assert.Equal(ContentBucket.SystemPrompt, second.Parts[0].Bucket);
        Assert.Equal("Be concise and accurate.", second.Parts[0].PlainText);
        Assert.Equal(ContentBucket.UserMessage, second.Parts[1].Bucket);
        Assert.Equal("Apply the fix.", second.Parts[1].PlainText);
        Assert.Equal(ContentBucket.Reasoning, second.Parts[2].Bucket);
        Assert.Equal("Editing handler directly.", second.Parts[2].PlainText);
        Assert.Equal(ContentBucket.ConversationHistory, second.Parts[3].Bucket);
        Assert.Equal("Applied the fix.", second.Parts[3].PlainText);
    }

    [Fact]
    public void Map_IsolatesSubagentPromptsAndReasoningFromRootAgent()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-agents\",\"selectedModel\":\"gpt-5\"}}",
            "{\"type\":\"system.message\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"data\":{\"content\":\"Root prompt\"}}",
            "{\"type\":\"system.message\",\"id\":\"e3\",\"timestamp\":\"2026-11-19T10:00:02.000Z\",\"agentId\":\"sub-1\",\"data\":{\"content\":\"Subagent prompt\"}}",
            "{\"type\":\"user.message\",\"id\":\"e4\",\"timestamp\":\"2026-11-19T10:00:03.000Z\",\"agentId\":\"sub-1\",\"data\":{\"content\":\"Subagent task\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e5\",\"timestamp\":\"2026-11-19T10:00:04.000Z\",\"agentId\":\"sub-1\",\"data\":{\"messageId\":\"msg-sub\",\"content\":\"Subagent done\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e6\",\"timestamp\":\"2026-11-19T10:00:05.000Z\",\"data\":{\"messageId\":\"msg-root\",\"content\":\"Root done\"}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(Combine(HomeRoot, ".copilot/session-state/sess-agents/events.jsonl"), jsonl);
        CopilotCliMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        Assert.Equal(2, batch.Records.Count);

        CollectedRecord subRecord = batch.Records[0];
        Assert.Equal("sub-1", subRecord.AgentInstanceId);
        Assert.Contains(subRecord.Parts, static p => p.Bucket == ContentBucket.SystemPrompt && p.PlainText == "Subagent prompt");
        Assert.DoesNotContain(subRecord.Parts, static p => p.PlainText == "Root prompt");

        CollectedRecord rootRecord = batch.Records[1];
        Assert.Null(rootRecord.AgentInstanceId);
        Assert.Contains(rootRecord.Parts, static p => p.Bucket == ContentBucket.SystemPrompt && p.PlainText == "Root prompt");
        Assert.DoesNotContain(rootRecord.Parts, static p => p.Bucket == ContentBucket.UserMessage);
    }

    [Fact]
    public void Map_WithholdsJsonDocumentPartsAndRecordsProblemForMalformedLine()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-json\",\"selectedModel\":\"gpt-5\"}}",
            "{not valid json",
            "{\"type\":\"user.message\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"data\":{\"content\":\"{\\\"secret\\\":true}\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e3\",\"timestamp\":\"2026-11-19T10:00:02.000Z\",\"data\":{\"messageId\":\"msg-1\",\"content\":\"Done.\"}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(Combine(HomeRoot, ".copilot/session-state/sess-json/events.jsonl"), jsonl);
        CopilotCliMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal("warning", problem.Severity);
        Assert.Equal("COPILOT_CLI_MALFORMED_LINE", problem.Code);

        CollectedRecord record = Assert.Single(batch.Records);
        CollectedContentPart userPart = Assert.Single(record.Parts, static p => p.Bucket == ContentBucket.UserMessage);
        Assert.Null(userPart.PlainText);
        Assert.NotNull(userPart.Withheld);

        InMemoryRelationalStore store = new();
        store.WriteBatch(batch);
        Assert.Equal(1, store.CountRecords());
    }

    [Fact]
    public void Map_PreservesExplicitZeroOutputTokens_AndDetectsUserCorrectionAndGitRootFallback()
    {
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-zero\",\"selectedModel\":\"gpt-5\",\"context\":{\"cwd\":\"/work/kyber-weave\",\"gitRoot\":\"/work/kyber-weave\",\"branch\":\"main\"}}}",
            "{\"type\":\"user.message\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"data\":{\"content\":\"No, do not edit that file.\"}}",
            "{\"type\":\"assistant.message\",\"id\":\"e3\",\"timestamp\":\"2026-11-19T10:00:02.000Z\",\"data\":{\"messageId\":\"msg-zero\",\"content\":\"Understood.\",\"outputTokens\":0}}",
            "{\"type\":\"assistant.message\",\"id\":\"e4\",\"timestamp\":\"2026-11-19T10:00:03.000Z\",\"data\":{\"messageId\":\"msg-null\",\"content\":\"Standing by.\",\"outputTokens\":null}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(Combine(HomeRoot, ".copilot/session-state/sess-zero/events.jsonl"), jsonl);
        CopilotCliMapper mapper = new();
        CollectedBatch batch = mapper.Map(unit, BaselineUtc);

        CollectedSession session = Assert.Single(batch.Sessions);
        Assert.NotNull(session.Workspace);
        Assert.Equal("kyber-weave", session.Workspace.Repository);

        Assert.Equal(2, batch.Records.Count);

        CollectedRecord explicitZero = batch.Records[0];
        Assert.Equal("prefix_negation", explicitZero.Signals.CorrectionRule);
        CollectedTokenCount zeroOutput = Assert.Single(explicitZero.Tokens);
        Assert.Equal(TokenClass.Output, zeroOutput.TokenClass);
        Assert.Equal(0, zeroOutput.Count);

        CollectedRecord nullOutput = batch.Records[1];
        Assert.Equal(RecordOp.LlmInvoke, nullOutput.Op);
        Assert.Empty(nullOutput.Tokens);
    }

    /// <summary>
    /// The session's resolved model with its own published tokenizer counts the user-message part:
    /// the measured tokens equal the model's own tokenizer figure, not the stand-in's.
    /// </summary>
    [Fact]
    public void Map_CountsUserMessagePartsWithTheSessionModelOwnTokenizer()
    {
        string userDoc = "{\"こんにちは\":\"世界\"}";
        string jsonl = string.Join('\n',
        [
            "{\"type\":\"session.start\",\"id\":\"e1\",\"timestamp\":\"2026-11-19T10:00:00.000Z\",\"data\":{\"sessionId\":\"sess-own\",\"selectedModel\":\"gpt-3.5-turbo\"}}",
            $"{{\"type\":\"user.message\",\"id\":\"e2\",\"timestamp\":\"2026-11-19T10:00:01.000Z\",\"data\":{{\"content\":{System.Text.Json.JsonSerializer.Serialize(userDoc)}}}}}",
            "{\"type\":\"assistant.message\",\"id\":\"e3\",\"timestamp\":\"2026-11-19T10:00:02.000Z\",\"data\":{\"messageId\":\"msg-own\",\"content\":\"Done.\"}}",
        ]) + "\n";

        NativeSourceUnit unit = MakeNativeUnit(Combine(HomeRoot, ".copilot/session-state/sess-own/events.jsonl"), jsonl);
        CollectedBatch batch = new CopilotCliMapper().Map(unit, BaselineUtc);

        CollectedRecord record = Assert.Single(batch.Records);
        Assert.Equal("gpt-3.5-turbo", record.Model);
        CollectedContentPart userPart = Assert.Single(record.Parts, static p => p.Bucket == ContentBucket.UserMessage);
        Assert.NotNull(userPart.Withheld);
        Assert.Equal(LocalTextTokenizer.Shared.Count(userDoc, "gpt-3.5-turbo").Count, userPart.Withheld!.MeasuredTokens);
        Assert.NotEqual(LocalTextTokenizer.Shared.Count(userDoc, null).Count, userPart.Withheld.MeasuredTokens);
    }

    private static NativeSourceUnit MakeNativeUnit(string path, string rawText)
    {
        SourceUnit sourceUnit = new(
            HarnessId: "copilot-cli",
            SourceKey: Path.GetFullPath(path),
            ProviderId: "copilot",
            ParserId: "copilot-cli-reader",
            ParserContractVersion: "1",
            Format: "copilot-cli-jsonl",
            SourceRootLabel: Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty),
            RevisionToken: "1-1",
            LastWriteUtc: BaselineUtc,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText));

        return new NativeSourceUnit(
            Unit: sourceUnit,
            RawText: rawText,
            SizeBytes: Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: BaselineUtc);
    }

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private sealed class FakeHarnessFileSystem : IHarnessFileSystem
    {
        private readonly Dictionary<string, FakeFileEntry> files = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = new(StringComparer.Ordinal);

        public void AddFile(string path, string content, DateTimeOffset lastWriteUtc)
        {
            string normalized = NormalizePath(path);
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            files[normalized] = new FakeFileEntry(content, bytes.LongLength, lastWriteUtc);

            string? dir = Path.GetDirectoryName(normalized);
            while (!string.IsNullOrEmpty(dir))
            {
                _ = directories.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        public bool DirectoryExists(string path) =>
            directories.Contains(NormalizePath(path));

        public bool FileExists(string path) =>
            files.ContainsKey(NormalizePath(path));

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive)
        {
            string normalizedRoot = NormalizePath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            List<string> matches = [];
            foreach (string filePath in files.Keys.OrderBy(static p => p, StringComparer.Ordinal))
            {
                string? parent = Path.GetDirectoryName(filePath);
                bool inScope = recursive
                    ? string.Equals(parent, normalizedRoot, StringComparison.Ordinal)
                      || (parent is not null && parent.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    : string.Equals(parent, normalizedRoot, StringComparison.Ordinal);

                if (inScope && MatchesPattern(Path.GetFileName(filePath), searchPattern))
                {
                    matches.Add(filePath);
                }
            }

            return matches;
        }

        public IEnumerable<string> EnumerateDirectories(string directoryPath)
        {
            string normalizedRoot = NormalizePath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            return directories
                .Where(dir => string.Equals(Path.GetDirectoryName(dir), normalizedRoot, StringComparison.Ordinal))
                .OrderBy(static p => p, StringComparer.Ordinal);
        }

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            if (files.TryGetValue(NormalizePath(filePath), out FakeFileEntry? entry))
            {
                lengthBytes = entry.LengthBytes;
                lastWriteUtc = entry.LastWriteUtc;
                return true;
            }

            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath)
        {
            string normalized = NormalizePath(filePath);
            if (!files.TryGetValue(normalized, out FakeFileEntry? entry))
            {
                throw new FileNotFoundException("Fake file not found.", normalized);
            }

            return entry.Content;
        }

        public string GetFullPath(string path) => NormalizePath(path);

        private static string NormalizePath(string path) =>
            Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));

        private static bool MatchesPattern(string fileName, string pattern)
        {
            if (pattern == "*" || pattern == "*.*")
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
        }

        private sealed record FakeFileEntry(string Content, long LengthBytes, DateTimeOffset LastWriteUtc);
    }
}
