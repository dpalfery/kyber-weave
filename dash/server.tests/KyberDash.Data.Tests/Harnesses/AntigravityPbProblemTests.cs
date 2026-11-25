using System.Text;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Antigravity;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Data.Tests.Harnesses;

/// <summary>
/// Unit tests for the unreadable-payload warning that <see cref="AntigravityTranscriptMapper"/> records
/// for <c>antigravity-pb</c> units and for JSONL units whose payload is missing, instead of returning a
/// silent empty batch.
/// </summary>
public class AntigravityPbProblemTests
{
    private const string TranscriptSourceKey = "/home/dev/.gemini/antigravity/brain/conv-1/.system_generated/logs/transcript.jsonl";
    private const string BinarySourceKey = "/home/dev/.gemini/antigravity/brain/conv-1/.system_generated/store/state.pb";

    private static readonly DateTimeOffset ImportedAtUtc = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_PbUnit_ReturnsNoRecordsAndOneWarningProblemNamingTheFile()
    {
        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(BinarySourceKey, AntigravityTranscriptMapper.AntigravityPbFormat, "binary-placeholder");

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Records);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal("warning", problem.Severity);
        Assert.Equal(AntigravityTranscriptMapper.PbUnreadableCode, problem.Code);
        Assert.Equal("ANTIGRAVITY_PB_UNREADABLE", problem.Code);
        Assert.Contains(BinarySourceKey, problem.Location, StringComparison.Ordinal);
        Assert.Contains(BinarySourceKey, problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_PbUnitWithNullPayload_ReturnsOneWarningProblemNamingTheFile()
    {
        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(BinarySourceKey, AntigravityTranscriptMapper.AntigravityPbFormat, rawText: null);

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        Assert.Empty(batch.Records);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal("warning", problem.Severity);
        Assert.Equal(AntigravityTranscriptMapper.PbUnreadableCode, problem.Code);
        Assert.Contains(BinarySourceKey, problem.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_JsonlUnitWithNullRawText_ReturnsOneWarningProblem()
    {
        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(TranscriptSourceKey, AntigravityTranscriptMapper.AntigravityJsonlFormat, rawText: null);

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        Assert.Empty(batch.Records);
        CollectedProblem problem = Assert.Single(batch.Problems);
        Assert.Equal("warning", problem.Severity);
        Assert.Equal(AntigravityTranscriptMapper.PbUnreadableCode, problem.Code);
        Assert.Contains(TranscriptSourceKey, problem.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_JsonlUnitWithWhitespaceRawText_ReturnsTodaysEmptyBatchWithoutProblems()
    {
        AntigravityTranscriptMapper mapper = new();
        NativeSourceUnit unit = CreateUnit(TranscriptSourceKey, AntigravityTranscriptMapper.AntigravityJsonlFormat, "   \r\n\t ");

        CollectedBatch batch = mapper.Map(unit, ImportedAtUtc);

        Assert.Empty(batch.Sessions);
        Assert.Empty(batch.Records);
        Assert.Empty(batch.Problems);
    }

    private static NativeSourceUnit CreateUnit(string sourceKey, string format, string? rawText) =>
        new(
            Unit: new SourceUnit(
                HarnessId: "antigravity",
                SourceKey: sourceKey,
                ProviderId: "google",
                ParserId: "antigravity-reader",
                ParserContractVersion: "1",
                Format: format,
                SourceRootLabel: "~/.gemini/antigravity",
                RevisionToken: "1:100"),
            RawText: rawText,
            SizeBytes: rawText is null ? 0L : Encoding.UTF8.GetByteCount(rawText),
            LastWriteUtc: ImportedAtUtc);
}
