using System.Text;
using KyberDash.Application;
using KyberDash.Application.Tests;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Contracts.Services;
using KyberDash.Core.Exceptions;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Claude;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Ingest;

/// <summary>
/// A forked or resumed Claude Code transcript copies its parent's lines, the parent's session id and
/// message ids included, so the parent file and the fork map to the same span ids. Both files import,
/// in either order and on every re-import, leaving one record per span and no
/// <c>SOURCE_UNREADABLE</c> problem.
/// </summary>
public class ForkedTranscriptImportTests
{
    private static readonly string HomeRoot = Path.Combine(Path.GetPathRoot(Environment.CurrentDirectory) ?? "/", "forked-transcript-home");
    private static readonly string StateRoot = Path.Combine(HomeRoot, ".kyberdash");
    private static readonly DateTimeOffset BaselineUtc = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private const string ParentSessionId = "parent-session";
    private const string ForkSessionId = "fork-session";
    private const string ParentSpanId = "synth:claude-code:parent-session:msg_parent_1";

    /// <summary>
    /// The parent and its fork write the same spans from two sources: both orders succeed, and a
    /// re-import of each leaves one record per span with one copy of its child rows.
    /// </summary>
    /// <param name="forkFirst">Whether the fork's file is written before the parent's.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentAndFork_ShareSpanIds_WriteOnceEach_InEitherOrder(bool forkFirst)
    {
        InMemoryRelationalStore store = new();
        CollectedBatch parent = Map(ParentPath, ParentTranscript());
        CollectedBatch fork = Map(ForkPath, ForkTranscript());
        Assert.Contains(fork.Records, static r => r.RecordId == ParentSpanId);

        CollectedBatch[] order = forkFirst ? [fork, parent, fork, parent] : [parent, fork, parent, fork];
        foreach (CollectedBatch batch in order)
        {
            store.WriteBatch(batch);
        }

        int distinctSpans = parent.Records.Select(static r => r.RecordId)
            .Union(fork.Records.Select(static r => r.RecordId), StringComparer.Ordinal)
            .Count();
        Assert.Equal(distinctSpans, store.CountRecords());
        Assert.Equal(distinctSpans, store.CountRows("record_session_link"));
        Assert.NotNull(store.FindRecord(ParentSpanId));
    }

    /// <summary>
    /// A refresh over a parent file and its fork commits both files and records no problem, and a
    /// file that failed before has its old <c>SOURCE_UNREADABLE</c> problem removed once it imports.
    /// </summary>
    [Fact]
    public void Refresh_ParentAndFork_CommitsBoth_AndClearsTheEarlierFailure()
    {
        InMemoryRelationalStore store = new();
        FakeHarnessFileSystem fs = new();
        fs.AddFile(ParentPath, ParentTranscript(), BaselineUtc.AddMinutes(-20));
        fs.AddFile(ForkPath, ForkTranscript(), BaselineUtc.AddMinutes(-10));
        string staleKey = CanonRefreshEngine.SourceUnreadableProblemKey(TranscriptReaderRepository.ClaudeHarnessId, ForkPath);
        store.WriteProblem(new CollectedProblem(
            ProblemKey: staleKey,
            Severity: "error",
            Code: TranscriptIngestionService.SourceUnreadableCode,
            Message: "sqlite3 error 19: UNIQUE constraint failed: record.span_id",
            SpanId: null,
            Location: ForkPath,
            SessionKey: null,
            HarnessId: TranscriptReaderRepository.ClaudeHarnessId,
            TimestampUtc: "2026-11-25T11:00:00Z",
            Stack: null));

        TranscriptIngestionService service = new(
            store,
            store,
            new HarnessSourceRegistry(),
            [new ClaudeTranscriptMapper()],
            fs,
            new DashOptions { StateDirectory = StateRoot },
            homeDirectory: HomeRoot,
            clock: () => BaselineUtc);

        RefreshReport report = service.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks);

        IReadOnlyList<RefreshSourceStatusRow> claudeRows = [.. report.Rows.Where(static r => r.HarnessId == TranscriptReaderRepository.ClaudeHarnessId)];
        Assert.Equal(2, claudeRows.Count);
        Assert.All(claudeRows, static r => Assert.Equal("committed", r.Status));
        Assert.Empty(store.ListIngestProblems(TranscriptReaderRepository.ClaudeHarnessId));
        Assert.NotNull(store.FindRecord(ParentSpanId));
    }

    /// <summary>
    /// A fork that owns a span shared with its parent, re-imported after it changed, whose new write the
    /// store refuses, leaves the shared span in place: the parent's checkpoint is unchanged, so nothing
    /// else would write it back.
    /// </summary>
    [Fact]
    public void Refresh_ForkOwningASharedSpan_WhoseRewriteIsRefused_KeepsTheSpan()
    {
        InMemoryRelationalStore store = new();
        RefusingWriter writer = new(store);
        FakeHarnessFileSystem fs = new();
        fs.AddFile(ParentPath, ParentTranscript(), BaselineUtc.AddMinutes(-20));
        fs.AddFile(LateForkPath, ForkTranscript(), BaselineUtc.AddMinutes(-10));
        TranscriptIngestionService service = new(
            writer,
            store,
            new HarnessSourceRegistry(),
            [new ClaudeTranscriptMapper()],
            fs,
            new DashOptions { StateDirectory = StateRoot },
            homeDirectory: HomeRoot,
            clock: () => BaselineUtc);

        RefreshReport first = service.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks);
        Assert.All(first.Rows, static r => Assert.Equal("committed", r.Status));
        Assert.NotNull(store.FindRecord(ParentSpanId));

        fs.AddFile(LateForkPath, ForkTranscript() + "\n", BaselineUtc.AddMinutes(-5));
        writer.RefuseLocator = LateForkPath;

        RefreshReport second = service.RefreshHarnessSources(CanonRefreshEngine.DefaultHistoryWeeks);

        Assert.Equal("failed", Assert.Single(second.Rows, static r => r.SourceKey == LateForkPath).Status);
        Assert.NotNull(store.FindRecord(ParentSpanId));
    }

    private static string ParentPath => Combine(HomeRoot, $".claude/projects/-repo/{ParentSessionId}.jsonl");

    /// <summary>A fork whose path sorts after the parent's, so it imports second and owns the shared spans.</summary>
    private static string LateForkPath => Combine(HomeRoot, $".claude/projects/-repo/z-{ForkSessionId}.jsonl");

    private static string ForkPath => Combine(HomeRoot, $".claude/projects/-repo/{ForkSessionId}.jsonl");

    private static string ParentTranscript() =>
        AssistantLine(ParentSessionId, "msg_parent_1", "toolu_parent_1", "2026-11-25T10:00:00.000Z") + "\n";

    /// <summary>The parent's line copied as-is, then one turn of the fork's own.</summary>
    private static string ForkTranscript() =>
        ParentTranscript() + AssistantLine(ForkSessionId, "msg_fork_1", "toolu_fork_1", "2026-11-25T10:30:00.000Z") + "\n";

    private static string AssistantLine(string sessionId, string messageId, string toolUseId, string timestamp) =>
        "{\"type\":\"assistant\",\"sessionId\":\"" + sessionId + "\",\"timestamp\":\"" + timestamp + "\","
        + "\"message\":{\"id\":\"" + messageId + "\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\","
        + "\"content\":[{\"type\":\"text\",\"text\":\"listing\"},"
        + "{\"type\":\"tool_use\",\"id\":\"" + toolUseId + "\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}],"
        + "\"usage\":{\"input_tokens\":100,\"output_tokens\":20}}}";

    private static CollectedBatch Map(string sourceKey, string jsonl) =>
        new ClaudeTranscriptMapper().Map(
            new NativeSourceUnit(
                Unit: new SourceUnit(
                    HarnessId: TranscriptReaderRepository.ClaudeHarnessId,
                    SourceKey: sourceKey,
                    ProviderId: TranscriptReaderRepository.ClaudeProviderId,
                    ParserId: TranscriptReaderRepository.ClaudeParserId,
                    ParserContractVersion: TranscriptReaderRepository.ParserContractVersion,
                    Format: TranscriptReaderRepository.ClaudeFormat,
                    SourceRootLabel: "~/.claude/projects",
                    RevisionToken: "1:100"),
                RawText: jsonl,
                SizeBytes: Encoding.UTF8.GetByteCount(jsonl),
                LastWriteUtc: BaselineUtc),
            BaselineUtc);

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Delegates to the store, and refuses a batch from <see cref="RefuseLocator"/> as the store refuses JSON text.</summary>
    private sealed class RefusingWriter(IRelationalStoreWriter inner) : IRelationalStoreWriter
    {
        public string? RefuseLocator { get; set; }

        public void WriteBatch(CollectedBatch batch)
        {
            if (string.Equals(batch.Source.Locator, RefuseLocator, StringComparison.Ordinal))
            {
                throw new DashValidationException(
                    SqliteRelationalStore.JsonTextRefusedCode,
                    "Refused JSON object or array text in 'content_part_text.part_text'.");
            }

            inner.WriteBatch(batch);
        }

        public void ReplaceSource(CollectedBatch batch)
        {
            if (string.Equals(batch.Source.Locator, RefuseLocator, StringComparison.Ordinal))
            {
                throw new DashValidationException(
                    SqliteRelationalStore.JsonTextRefusedCode,
                    "Refused JSON object or array text in 'content_part_text.part_text'.");
            }

            inner.ReplaceSource(batch);
        }

        public void DeleteSource(string sourceId) => inner.DeleteSource(sourceId);

        public void ClearEstimatedRecordTokens(string harnessId, string sourceLocator, EstimatedRecordTokens estimated) =>
            inner.ClearEstimatedRecordTokens(harnessId, sourceLocator, estimated);

        public void WriteProblem(CollectedProblem problem) => inner.WriteProblem(problem);

        public void DeleteProblem(string problemKey) => inner.DeleteProblem(problemKey);
    }

    private sealed class FakeHarnessFileSystem : IHarnessFileSystem
    {
        private readonly Dictionary<string, (string Content, long LengthBytes, DateTimeOffset LastWriteUtc)> files = new(StringComparer.Ordinal);
        private readonly HashSet<string> directories = new(StringComparer.Ordinal);

        public void AddFile(string path, string content, DateTimeOffset lastWriteUtc)
        {
            string normalized = Path.GetFullPath(path);
            files[normalized] = (content, Encoding.UTF8.GetByteCount(content), lastWriteUtc);

            string? dir = Path.GetDirectoryName(normalized);
            while (!string.IsNullOrEmpty(dir))
            {
                _ = directories.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        public bool DirectoryExists(string path) =>
            directories.Contains(Path.GetFullPath(path));

        public bool FileExists(string path) =>
            files.ContainsKey(Path.GetFullPath(path));

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern, bool recursive)
        {
            string normalizedRoot = Path.GetFullPath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            List<string> matches = [];
            foreach (string filePath in files.Keys.OrderBy(static k => k, StringComparer.Ordinal))
            {
                string? parent = Path.GetDirectoryName(filePath);
                if (parent is null)
                {
                    continue;
                }

                bool inScope = recursive
                    ? string.Equals(parent, normalizedRoot, StringComparison.Ordinal)
                      || parent.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
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
            string normalizedRoot = Path.GetFullPath(directoryPath);
            if (!directories.Contains(normalizedRoot))
            {
                return [];
            }

            return directories
                .Where(d => string.Equals(Path.GetDirectoryName(d), normalizedRoot, StringComparison.Ordinal))
                .OrderBy(static d => d, StringComparer.Ordinal)
                .ToList();
        }

        public bool TryGetFileStats(string filePath, out long lengthBytes, out DateTimeOffset lastWriteUtc)
        {
            if (files.TryGetValue(Path.GetFullPath(filePath), out (string Content, long LengthBytes, DateTimeOffset LastWriteUtc) entry))
            {
                lengthBytes = entry.LengthBytes;
                lastWriteUtc = entry.LastWriteUtc;
                return true;
            }

            lengthBytes = 0;
            lastWriteUtc = default;
            return false;
        }

        public string ReadAllText(string filePath) => files[Path.GetFullPath(filePath)].Content;

        public string ReadAllText(string filePath, long maxBytes)
        {
            string text = ReadAllText(filePath);
            return System.Text.Encoding.UTF8.GetByteCount(text) <= maxBytes
                ? text
                : throw new InvalidDataException($"'{filePath}' holds more than {maxBytes} bytes.");
        }

        public string GetFullPath(string path) => Path.GetFullPath(path);

        private static bool MatchesPattern(string fileName, string pattern)
        {
            if (string.Equals(pattern, "*", StringComparison.Ordinal))
            {
                return true;
            }

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                return fileName.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
}
