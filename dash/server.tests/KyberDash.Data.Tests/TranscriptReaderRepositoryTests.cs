using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests;

/// <summary>Streaming JSONL parsing and local source discovery for <see cref="TranscriptReaderRepository"/>.</summary>
public sealed class TranscriptReaderRepositoryTests : IDisposable
{
    private const string ClaudeUsageLine =
        "{\"type\":\"assistant\",\"sessionId\":\"session-a\",\"timestamp\":\"2026-01-01T00:00:01Z\",\"message\":" +
        "{\"id\":\"msg-1\",\"role\":\"assistant\",\"model\":\"claude-opus-5\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]," +
        "\"usage\":{\"input_tokens\":10,\"output_tokens\":5,\"cache_read_input_tokens\":3,\"cache_creation_input_tokens\":1}}}";

    private readonly TempDirectory directory = new();

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReadClaudeTurns_ParsesUsageAndContentParts()
    {
        string file = WriteTranscript("abc.jsonl",
        [
            "{\"type\":\"user\",\"sessionId\":\"session-a\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"please fix\"}}",
            ClaudeUsageLine,
        ]);

        IReadOnlyList<ClaudeTurnGroup> groups = new TranscriptReaderRepository().ReadClaudeTurns(file);

        ClaudeTurnGroup group = Assert.Single(groups);
        Assert.Equal("session-a", group.SessionId);
        Assert.Equal("msg-1", group.NativeRecordId);
        Assert.Equal("claude-opus-5", group.Model);
        Assert.Equal(10, group.InputTokens);
        Assert.Equal(5, group.OutputTokens);
        Assert.Equal(3, group.CacheReadTokens);
        Assert.Equal(1, group.CacheCreationTokens);
        Assert.Equal("2026-01-01T00:00:01Z", group.Timestamp);
        Assert.Equal(2, group.Turn.Parts.Count);
    }

    [Fact]
    public void ReadClaudeTurns_AppliesTheWindowFilter()
    {
        string file = WriteTranscript("window.jsonl", [ClaudeUsageLine]);

        TranscriptReaderRepository repository = new();

        Assert.Single(repository.ReadClaudeTurns(file, DateTimeOffset.Parse("2025-12-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Empty(repository.ReadClaudeTurns(file, DateTimeOffset.Parse("2026-02-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Empty(repository.ReadClaudeTurns(file, null, DateTimeOffset.Parse("2025-12-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ReadClaudeTurns_ReturnsEmptyForMissingFileOrMalformedLines()
    {
        TranscriptReaderRepository repository = new();

        Assert.Empty(repository.ReadClaudeTurns(Path.Combine(directory.Path, "missing.jsonl")));
        Assert.Empty(repository.ReadClaudeTurns(WriteTranscript("broken.jsonl", ["{ broken", string.Empty])));
    }

    [Fact]
    public void ReadClaudeEnvelopes_ClassifiesTurnsAsClaudeRecords()
    {
        string file = WriteTranscript("envelopes.jsonl", [ClaudeUsageLine]);

        IReadOnlyList<SourceRecordEnvelope> envelopes = new TranscriptReaderRepository().ReadClaudeEnvelopes(
            file,
            sourceKey: "unit-1",
            sourceRevision: "rev-1",
            importedAt: "2026-01-01T00:00:00.000Z");

        SourceRecordEnvelope envelope = Assert.Single(envelopes);
        Assert.Equal(TranscriptReaderRepository.ClaudeHarnessId, envelope.HarnessId);
        Assert.Equal("unit-1", envelope.SourceKey);
        Assert.Equal("rev-1", envelope.SourceRevision);
        Assert.Equal("2026-01-01T00:00:00.000Z", envelope.ImportedAt);
        Assert.Equal("claude-jsonl", envelope.Format);
        Assert.Equal(HarnessSourceRegistry.ClaudeTranscriptV2ParserContractVersion, envelope.ParserContractVersion);
        Assert.Equal("claude", envelope.Call.Provider);
        Assert.Equal("claude:session-a:msg-1", envelope.Call.DeduplicationKey);
        Assert.Same(envelope.Call, envelope.Call);
    }

    [Fact]
    public void ReadClaudeEnvelopes_SkipsTurnsWithoutUsage()
    {
        string file = WriteTranscript("nousage.jsonl",
        [
            "{\"type\":\"user\",\"sessionId\":\"session-a\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}",
        ]);

        Assert.Empty(new TranscriptReaderRepository().ReadClaudeEnvelopes(file));
    }

    [Fact]
    public void ReadCodexTurns_BuildsCumulativeSnapshots()
    {
        string file = WriteTranscript("rollout-1.jsonl", CodexTranscript());

        IReadOnlyList<ReaderTurn> turns = new TranscriptReaderRepository().ReadCodexTurns(file);

        Assert.NotEmpty(turns);
        ReaderTurn first = turns[0];
        Assert.Equal("codex-session", first.SessionId);
        Assert.Contains(first.Parts, static part => string.Equals(part.Part, "system_prompt", StringComparison.Ordinal));
        Assert.Contains(first.Parts, static part => string.Equals(part.Part, "conversation_history", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadCodexEnvelopes_ReportsFreshAndCachedInputTokens()
    {
        string file = WriteTranscript("rollout-2.jsonl", CodexTranscript());

        IReadOnlyList<SourceRecordEnvelope> envelopes = new TranscriptReaderRepository().ReadCodexEnvelopes(file);

        SourceRecordEnvelope envelope = Assert.Single(envelopes);
        Assert.Equal(TranscriptReaderRepository.CodexHarnessId, envelope.HarnessId);
        Assert.Equal("codex-jsonl", envelope.Format);
        Assert.Equal("codex", envelope.Call.Provider);
        Assert.Equal(70, envelope.Call.InputTokens);
        Assert.Equal(30, envelope.Call.CacheReadInputTokens);
        Assert.Equal(20, envelope.Call.OutputTokens);
        Assert.Equal("codex:codex-session:turn-1", envelope.Call.DeduplicationKey);
    }

    [Fact]
    public void ReadCodexEnvelopes_AppliesTheWindowFilter()
    {
        string file = WriteTranscript("rollout-3.jsonl", CodexTranscript());

        TranscriptReaderRepository repository = new();
        Assert.Single(repository.ReadCodexEnvelopes(file, null, null, null, DateTimeOffset.Parse("2025-12-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Empty(repository.ReadCodexEnvelopes(file, null, null, null, DateTimeOffset.Parse("2026-02-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ReadCodexEnvelopes_ReturnsEmptyForMissingFile()
    {
        Assert.Empty(new TranscriptReaderRepository().ReadCodexEnvelopes(Path.Combine(directory.Path, "missing.jsonl")));
    }

    [Fact]
    public void DiscoverLocalSources_FindsBothHarnessRoots()
    {
        string home = directory.Combine("home");
        WriteTranscriptAt(home, ".claude/projects/repo/abcd.jsonl", [ClaudeUsageLine]);
        WriteTranscriptAt(home, ".claude/projects/repo/abcd/sub-agents/child.jsonl", [ClaudeUsageLine]);
        WriteTranscriptAt(home, ".codex/sessions/2026/01/01/rollout-x.jsonl", CodexTranscript());

        IReadOnlyList<RefreshSourceInput> sources = new TranscriptReaderRepository(home).DiscoverLocalSources();

        Assert.Equal(3, sources.Count);
        Assert.Equal(2, sources.Count(static source => string.Equals(source.HarnessId, TranscriptReaderRepository.ClaudeHarnessId, StringComparison.Ordinal)));
        Assert.Contains(sources, static source => string.Equals(source.ParserId, TranscriptReaderRepository.CodexParserId, StringComparison.Ordinal));
        Assert.All(sources, static source => Assert.NotEmpty(source.RevisionToken));
        Assert.All(sources, static source => Assert.NotEmpty(source.LoadEnvelopes()));
    }

    [Fact]
    public void DiscoverLocalSources_RespectsTheSinceFilter()
    {
        string home = directory.Combine("home");
        WriteTranscriptAt(home, ".claude/projects/repo/fresh.jsonl", [ClaudeUsageLine], DateTimeOffset.UtcNow);

        TranscriptReaderRepository repository = new(home);

        Assert.Single(repository.DiscoverLocalSources(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Empty(repository.DiscoverLocalSources(DateTimeOffset.UtcNow.AddHours(1)));
    }

    [Fact]
    public void DiscoverLocalSources_IsEmptyWhenRootsAreMissing()
    {
        Assert.Empty(new TranscriptReaderRepository(directory.Combine("no-home")).DiscoverLocalSources());
    }

    [Fact]
    public void DiscoverLocalSources_LoadEnvelopesUsesTheDiscoveredFile()
    {
        string home = directory.Combine("home");
        WriteTranscriptAt(home, ".claude/projects/repo/abcd.jsonl", [ClaudeUsageLine]);

        RefreshSourceInput source = Assert.Single(new TranscriptReaderRepository(home).DiscoverLocalSources());

        Assert.Equal("claude-jsonl", source.Format);
        Assert.Equal(HarnessSourceRegistry.ClaudeTranscriptV2ParserContractVersion, source.ParserContractVersion);
        Assert.Equal(Path.GetFullPath(Path.Combine(home, ".claude/projects/repo/abcd.jsonl")), source.SourceKey);
        Assert.Single(source.LoadEnvelopes());
    }

    private static string[] CodexTranscript() =>
    [
        "{\"timestamp\":\"2026-01-01T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"codex-session\",\"model\":\"gpt-6\",\"base_instructions\":{\"text\":\"you are codex\"}}}",
        "{\"timestamp\":\"2026-01-01T00:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"content\":[{\"type\":\"input_text\",\"text\":\"do the thing\"}]}}",
        "{\"timestamp\":\"2026-01-01T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"model_context_window\":200000,\"info\":{\"model\":\"gpt-6\",\"last_token_usage\":{\"input_tokens\":100,\"cached_input_tokens\":30,\"output_tokens\":20,\"reasoning_output_tokens\":4}}}}",
        "{\"timestamp\":\"2026-01-01T00:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\"}}",
    ];

    private string WriteTranscript(string fileName, string[] lines, DateTimeOffset? writtenUtc = null)
    {
        string path = Path.Combine(directory.Path, fileName);
        File.WriteAllLines(path, lines);
        if (writtenUtc.HasValue)
            File.SetLastWriteTimeUtc(path, writtenUtc.Value.UtcDateTime);

        return path;
    }

    private string WriteTranscriptAt(string home, string relativePath, string[] lines, DateTimeOffset? writtenUtc = null)
    {
        string path = Path.Combine(home, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        if (writtenUtc.HasValue)
            File.SetLastWriteTimeUtc(path, writtenUtc.Value.UtcDateTime);

        return path;
    }
}
