using System.Text;
using System.Text.Json;
using KyberDash.Application.Tests.Fakes;
using KyberDash.Contracts.Repositories;
using KyberDash.Data.Collector;
using KyberDash.Data.Collector.Sources;
using KyberDash.Data.Harnesses.Claude;
using KyberDash.Data.Tokenization;
using KyberDash.Data.Harnesses.Otlp;
using KyberDash.Data.Repositories;
using KyberDash.Domain;
using Xunit;

namespace KyberDash.Application.Tests.Projection;

/// <summary>
/// A Claude Code listing stored in <c>record_skill_listing</c> reaches the inactive-skill-reference
/// detector: the store read carries <see cref="CollectedRecord.SkillsListed"/>, hydration exposes it
/// as <c>kyber.skills.listed</c> next to <c>kyber.skills.invoked</c>, and twin-turn dedupe keeps it
/// when an OTLP twin wins.
/// </summary>
public class ClaudeSkillListingAnalysisTests
{
    private const string InactiveSkillDetector = "inactive-skill-reference";
    private static readonly DateTimeOffset StartedAt = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Hydration writes the listing in listing order, with no <c>isInitial</c> the store never kept.</summary>
    [Fact]
    public void Hydrate_ListedSkills_WritesTheListingInOrderWithoutIsInitial()
    {
        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(Turn() with { SkillsListed = ["zeta", "alpha", "mid:skill"] });

        JsonElement listed = hydrated.Raw![CanonSynth.SkillsListedKey];
        Assert.False(listed.TryGetProperty("isInitial", out _));
        Assert.Equal(
            ["zeta", "alpha", "mid:skill"],
            listed.GetProperty("skills").EnumerateArray().Select(static s => s.GetProperty("name").GetString()));
        Assert.All(listed.GetProperty("skills").EnumerateArray(), static s => Assert.False(s.TryGetProperty("description", out _)));
        Assert.False(hydrated.Raw!.ContainsKey(CanonSynth.SkillsInvokedKey));
    }

    /// <summary>Hydration writes each invoked skill once.</summary>
    [Fact]
    public void Hydrate_InvokedSkills_WritesTheDistinctNames()
    {
        CanonicalRecord hydrated = CanonRecordHydration.Hydrate(Turn() with { SkillsInvoked = ["conductor", "review", "conductor"] });

        Assert.Equal(
            ["conductor", "review"],
            hydrated.Raw![CanonSynth.SkillsInvokedKey].EnumerateArray().Select(static s => s.GetString()));
        Assert.False(hydrated.Raw!.ContainsKey(CanonSynth.SkillsListedKey));
    }

    /// <summary>A record with no listing and no invocation writes neither key, so other harnesses' detector input is unchanged.</summary>
    [Fact]
    public void Hydrate_NoSkills_WritesNeitherKey()
    {
        CanonicalRecord bare = CanonRecordHydration.Hydrate(Turn());
        CanonicalRecord empty = CanonRecordHydration.Hydrate(Turn() with { SkillsListed = [], SkillsInvoked = [] });

        foreach (CanonicalRecord hydrated in new[] { bare, empty })
        {
            Assert.False(hydrated.Raw!.ContainsKey(CanonSynth.SkillsListedKey));
            Assert.False(hydrated.Raw!.ContainsKey(CanonSynth.SkillsInvokedKey));
        }
    }

    /// <summary>
    /// A Claude Code transcript that lists two skills and invokes one projects exactly one
    /// inactive-skill-reference finding, for the skill it never invoked.
    /// </summary>
    [Fact]
    public void Project_TranscriptListingASkillItNeverInvoked_RaisesTheFindingForThatSkillOnly()
    {
        InMemoryRelationalStore store = new();
        store.WriteBatch(MapTranscript("cc-skills", ListingAndInvocationJsonl("cc-skills", requestId: null)));

        NewEngine(store).ProjectIncremental();

        StoredFinding finding = Assert.Single(
            ((IProjectionStore)store).ListFindings(sessionId: "cc-skills"),
            static f => f.DetectorId == InactiveSkillDetector);
        Assert.Contains("\"code-review:code-review\"", finding.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("conductor", finding.Title, StringComparison.Ordinal);
    }

    /// <summary>A transcript that invokes every skill it lists raises no inactive-skill-reference finding.</summary>
    [Fact]
    public void Project_TranscriptInvokingEveryListedSkill_RaisesNoFinding()
    {
        InMemoryRelationalStore store = new();
        string jsonl = ListingAndInvocationJsonl("cc-all", requestId: null)
            .Replace("\"names\":[\"conductor\",\"code-review:code-review\"]", "\"names\":[\"conductor\"]", StringComparison.Ordinal);
        store.WriteBatch(MapTranscript("cc-all", jsonl));

        NewEngine(store).ProjectIncremental();

        Assert.DoesNotContain(
            ((IProjectionStore)store).ListFindings(sessionId: "cc-all"),
            static f => f.DetectorId == InactiveSkillDetector);
    }

    /// <summary>
    /// When an OTLP <c>api_request</c> and the transcript turn carrying the listing are the same turn,
    /// dedupe keeps the OTLP row and carries the transcript's listing and invocation onto it, so the
    /// finding still fires for the uninvoked skill.
    /// </summary>
    [Fact]
    public void Project_OtlpTwinKeptOverTheListingTurn_KeepsTheListingAndRaisesTheFinding()
    {
        InMemoryRelationalStore store = new();
        WriteClaudeOtlpRequest(store, "cc-twin", "req_twin_1");
        store.WriteBatch(MapTranscript("cc-twin", ListingAndInvocationJsonl("cc-twin", requestId: "req_twin_1")));

        CanonicalRecord kept = Assert.Single(
            CanonSessionProjectorEngine.DedupeTwinTurns(
                [.. ((IRelationalStoreReader)store).SessionRecordsForRebuild("cc-twin").Select(CanonRecordHydration.Hydrate)],
                "cc-twin"),
            static r => r.Op == "llm.invoke");
        Assert.False(kept.Source.StartsWith(CanonSynth.FileSourcePrefix, StringComparison.Ordinal));
        Assert.True(kept.Raw!.ContainsKey(CanonSynth.SkillsListedKey));
        Assert.True(kept.Raw!.ContainsKey(CanonSynth.SkillsInvokedKey));

        NewEngine(store).ProjectIncremental();

        StoredFinding finding = Assert.Single(
            ((IProjectionStore)store).ListFindings(sessionId: "cc-twin"),
            static f => f.DetectorId == InactiveSkillDetector);
        Assert.Contains("\"code-review:code-review\"", finding.Title, StringComparison.Ordinal);
    }

    /// <summary>
    /// A transcript turn with no content left (after the content purge) still hands its listing and
    /// invocations to the OTLP twin that absorbs it, and invoked names from both twins are unioned.
    /// </summary>
    [Fact]
    public void DedupeTwinTurns_PartlessTranscriptTwin_StillHandsItsSkillsToTheOtlpKeeper()
    {
        CanonicalRecord otlp = CanonRecordHydration.Hydrate(Turn() with
        {
            RecordId = "otlp-1",
            RequestId = "req_partless",
            SourceName = "claude-code",
            SkillsInvoked = ["review"],
        });
        CanonicalRecord file = CanonRecordHydration.Hydrate(Turn() with
        {
            RecordId = "file-1",
            RequestId = "req_partless",
            SourceName = CanonSynth.FileSourcePrefix + "claude-code",
            SkillsListed = ["conductor", "review", "idle"],
            SkillsInvoked = ["conductor", "review"],
        });
        Assert.Empty(file.Parts!);

        CanonicalRecord kept = Assert.Single(CanonSessionProjectorEngine.DedupeTwinTurns([otlp, file], "sess-1"));

        Assert.Equal("otlp-1", kept.SpanId);
        Assert.Equal(
            ["conductor", "review", "idle"],
            kept.Raw![CanonSynth.SkillsListedKey].GetProperty("skills").EnumerateArray().Select(static s => s.GetProperty("name").GetString()));
        Assert.Equal(
            ["review", "conductor"],
            kept.Raw![CanonSynth.SkillsInvokedKey].EnumerateArray().Select(static s => s.GetString()));
    }

    /// <summary>
    /// One transcript turn: a <c>skill_listing</c> naming <c>conductor</c> and
    /// <c>code-review:code-review</c>, then an assistant turn that invokes <c>conductor</c>.
    /// </summary>
    private static string ListingAndInvocationJsonl(string sessionKey, string? requestId)
    {
        string requestField = requestId is null ? string.Empty : $"\"requestId\":\"{requestId}\",";
        return string.Join('\n',
        [
            $"{{\"type\":\"attachment\",\"sessionId\":\"{sessionKey}\",\"timestamp\":\"2026-11-20T09:00:00.000Z\",\"attachment\":{{\"type\":\"skill_listing\",\"content\":\"- conductor: Orchestrates.\\n- code-review:code-review: Reviews.\",\"skillCount\":2,\"isInitial\":true,\"names\":[\"conductor\",\"code-review:code-review\"]}}}}",
            $"{{\"type\":\"user\",\"sessionId\":\"{sessionKey}\",\"timestamp\":\"2026-11-20T09:00:00.050Z\",\"message\":{{\"role\":\"user\",\"content\":\"Run the conductor.\"}}}}",
            $"{{\"type\":\"assistant\",\"sessionId\":\"{sessionKey}\",{requestField}\"timestamp\":\"2026-11-20T09:00:00.100Z\",\"message\":{{\"id\":\"msg_{sessionKey}\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[{{\"type\":\"tool_use\",\"id\":\"tc-{sessionKey}\",\"name\":\"Skill\",\"input\":{{\"skill\":\"conductor\"}}}}],\"usage\":{{\"input_tokens\":650,\"output_tokens\":110}}}}}}",
        ]);
    }

    private static CollectedBatch MapTranscript(string sessionKey, string jsonl) =>
        new ClaudeTranscriptMapper(LocalTextTokenizer.Shared).Map(
            new NativeSourceUnit(
                Unit: new SourceUnit(
                    HarnessId: TranscriptReaderRepository.ClaudeHarnessId,
                    SourceKey: $"/home/dev/.claude/projects/repo/{sessionKey}.jsonl",
                    ProviderId: TranscriptReaderRepository.ClaudeProviderId,
                    ParserId: TranscriptReaderRepository.ClaudeParserId,
                    ParserContractVersion: TranscriptReaderRepository.ParserContractVersion,
                    Format: TranscriptReaderRepository.ClaudeFormat,
                    SourceRootLabel: "~/.claude/projects",
                    RevisionToken: "1:100"),
                RawText: jsonl,
                SizeBytes: Encoding.UTF8.GetByteCount(jsonl),
                LastWriteUtc: ImportedAt),
            ImportedAt);

    /// <summary>A Claude Code <c>api_request</c> OTLP log carrying <paramref name="requestId"/>.</summary>
    private static void WriteClaudeOtlpRequest(InMemoryRelationalStore store, string sessionKey, string requestId)
    {
        OtlpLog log = new(
            LogId: $"log-{sessionKey}",
            TraceId: "dddddddddddddddddddddddddddddddd",
            SpanId: null,
            SessionId: sessionKey,
            Timestamp: "2026-11-20T09:00:00.000Z",
            Body: "api request",
            Attributes: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["event.name"] = JsonSerializer.SerializeToElement("claude_code.api_request"),
                ["claude_code.event"] = JsonSerializer.SerializeToElement("api_request"),
                ["gen_ai.request.model"] = JsonSerializer.SerializeToElement("claude-sonnet-4-5"),
                ["gen_ai.usage.input_tokens"] = JsonSerializer.SerializeToElement(700),
                ["gen_ai.usage.output_tokens"] = JsonSerializer.SerializeToElement(120),
                ["request_id"] = JsonSerializer.SerializeToElement(requestId),
            },
            Resource: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["service.name"] = JsonSerializer.SerializeToElement("claude-code"),
            },
            Scope: new OtlpScope("claude_code", "1.0.0"));
        OtlpLogMapResult result = OtlpLogMapper.Map([log]);
        store.WriteBatch(Assert.Single(result.Batches));
    }

    private static CanonSessionProjectorEngine NewEngine(InMemoryRelationalStore store) =>
        new(store, store, store, new FakePricingRepository(), new FakeTextTokenizer());

    private static CollectedRecord Turn() =>
        new(
            RecordId: "span-1",
            HarnessId: "claude-code",
            Op: RecordOp.LlmInvoke,
            Kind: RecordKind.Client,
            Name: "turn",
            StartedAt: StartedAt,
            Status: RecordStatus.Ok,
            SessionKey: "sess-1",
            ParentRecordId: null,
            TraceId: null,
            Model: "claude-sonnet-4-5",
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
            Signals: new CollectedRecordSignals(null, null, null, null));
}
