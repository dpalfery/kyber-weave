using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Arbiter.Sections;
using KyberWeave.Core.Arbiter.Stores;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// RED tests for the Kilo review leads on PR 591 that the code confirms as true. Each test names the comment it proves.
/// Fail-safe is the governing rule: an unreadable, missing, malformed or failed input keeps today's expensive behaviour
/// (the finding stays real, the section stays, the agent is spawned) and never throws or skips silently.
/// </summary>
/// <remarks>
/// Leads confirmed FALSE and therefore not tested here: 4537885257 (the prior-findings fact is already capped at three
/// in <c>TriggerFactBuilder.WithPriorFindings</c>, and <c>section.candidates</c> is capped at
/// <c>StateBudget.MaxQuestions</c> in <c>SectionSelector</c>); 4537885235 path traversal (the store keys by SHA-256 digest,
/// so <c>../../evil</c> is a hash, not a path); 4537885385 (the comment path is contained downstream by
/// <c>FileTextFacts.ResolveContainedFile</c>, so a traversal is recorded as real and never read).
/// </remarks>
public sealed class ArbiterKiloLeadsTests : IDisposable
{
    private readonly TempDirectory _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private static DateTimeOffset Now => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Holds a file open with no sharing, so a read of it fails with an I/O error while the file still exists.</summary>
    private static FileStream LockAgainstReads(string path)
    {
        FileStream holder = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => File.OpenRead(path).Dispose());
        return holder;
    }

    private async Task<string> WriteCitedFileAsync(string relative, string? content = null)
    {
        string full = Path.Combine(_sandbox.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content ?? string.Join("\n", Enumerable.Range(1, 30).Select(i => $"var v{i} = {i};")));
        return full;
    }

    /// <summary>A finding whose id line has no value is not a finding; it must not become one with an empty id.</summary>
    /// <remarks>Kilo 4537885270: the id pattern is <c>\S*</c>, so <c>id:</c> with nothing after it matches.</remarks>
    [Fact]
    public void AnIdLineWithNoValueIsNotParsedAsAFinding()
    {
        IReadOnlyList<ReturnedFinding> findings = ReturnedFinding.ParseAll("- id:\n  severity: high\n- id: real-one\n  severity: low");

        Assert.DoesNotContain(findings, finding => finding.Id.Length == 0);
        Assert.Equal(["real-one"], findings.Select(finding => finding.Id).ToArray());
    }

    /// <summary>A cited file that exists but cannot be opened reads as absent evidence, not as an exception.</summary>
    /// <remarks>Kilo 4537885280: <c>File.OpenRead</c> sits outside any try, so an I/O failure escapes the reader.</remarks>
    [Fact]
    public async Task AnUnreadableCitedFileReadsAsAbsentNotAsAThrow()
    {
        string full = await WriteCitedFileAsync("src/Locked.cs");
        using FileStream holder = LockAgainstReads(full);

        FileExcerpt? excerpt = FileTextFacts.ReadExcerpt(_sandbox.Path, "src/Locked.cs", 12);

        Assert.Null(excerpt);
    }

    /// <summary>A bot comment whose cited file cannot be read is kept real, with no provider call, instead of failing the classification.</summary>
    /// <remarks>Kilo 4537885358: <c>ReadExcerpt</c> runs outside the provider try in <c>CommentTriageService.ClassifyAsync</c>.</remarks>
    [Fact]
    public async Task AnUnreadableCitedFileKeepsTheCommentReal()
    {
        string full = await WriteCitedFileAsync("src/Locked.cs");
        using FileStream holder = LockAgainstReads(full);
        CountingNitpickProvider provider = new();
        CommentTriageService service = new(provider, _sandbox.Path, Path.Combine(_sandbox.Path, "artifacts", "arbiter"), new FixedClock(Now));

        CommentTriageResult result = await service.ClassifyAsync(new CommentTriageRequest("src/Locked.cs", 12, "Rename this.", null));

        Assert.Equal("real", result.Class);
        Assert.Equal(0, provider.Calls);
    }

    /// <summary>A programming bug is not a provider failure, and a request that timed out is one the fail-safe must absorb.</summary>
    /// <remarks>
    /// Kilo 4537885289: <c>InvalidOperationException</c> is absorbed (hides bugs), and <c>TaskCanceledException</c>, which
    /// <c>HttpClient</c> raises on a timeout, is not (so a timeout escapes the fail-safe).
    /// </remarks>
    [Fact]
    public void AProgrammingBugIsNotAbsorbedAsAProviderFailure()
    {
        Assert.False(ProviderFailures.IsAbsorbable(new InvalidOperationException("collection was modified")));
    }

    /// <summary>An HTTP client timeout is a provider failure the fail-safe absorbs, not an error that escapes.</summary>
    [Fact]
    public void AProviderTimeoutIsAbsorbedAsAProviderFailure()
    {
        Assert.True(ProviderFailures.IsAbsorbable(new TaskCanceledException("the request timed out")));
    }

    /// <summary>An unknown finding location is undecidable: the prior-findings fact is absent, not an empty list that reads as "no neighbours".</summary>
    /// <remarks>Kilo 4537885302: <c>WithPriorFindings</c> writes an empty <c>review.prior-findings</c> when file or line is unknown.</remarks>
    [Fact]
    public void AnUnknownFindingLocationLeavesThePriorFindingsFactAbsent()
    {
        ArbiterFactSet facts = new(new Dictionary<string, ArbiterFact>(StringComparer.Ordinal));

        ArbiterFactSet result = TriggerFactBuilder.WithPriorFindings(facts, "dup-1", ["id: other\nfile: src/A.cs\nline: 3"]);

        Assert.False(result.TryGet("review.prior-findings", out _));
    }

    /// <summary>An unreadable document is a fail-safe answer: every section is kept, and the call does not throw.</summary>
    /// <remarks>Kilo 4537885311: <c>File.ReadAllTextAsync</c> sits outside any try in <c>SectionSelector.SelectAsync</c>.</remarks>
    [Fact]
    public async Task AnUnreadableDocumentFailsSafeToEverySection()
    {
        string full = await WriteCitedFileAsync("docs/locked.md", "# Guide\nIntro.\n## Install\nSteps.\n");
        using FileStream holder = LockAgainstReads(full);

        SectionSelection selection = await new SectionSelector(new NoneProvider()).SelectAsync(full, "Change the install steps.");

        Assert.True(selection.FailedSafe);
    }

    /// <summary>A retention period too large to convert to a time span is clamped, so an append still stores its item.</summary>
    /// <remarks>Kilo 4537885335: <c>TimeSpan.FromDays(20000000)</c> overflows and <c>AppendAsync</c> catches only I/O errors.</remarks>
    [Fact]
    public async Task AnOversizedRetentionPeriodStillStoresTheAppend()
    {
        string arbiterDirectory = Path.Combine(_sandbox.Path, "artifacts", "arbiter");
        ArbiterRunStore seeded = new(arbiterDirectory, new ArbiterStoreOptions(RetainDays: 14), () => Now);
        Assert.Equal(ArbiterStoreStatus.Stored, await seeded.AppendFindingAsync("run-1", "id: first"));

        ArbiterRunStore oversized = new(arbiterDirectory, new ArbiterStoreOptions(RetainDays: 20_000_000), () => Now);
        ArbiterStoreStatus status = await oversized.AppendFindingAsync("run-1", "id: second");

        Assert.Equal(ArbiterStoreStatus.Stored, status);
    }

    /// <summary>A failure with no session is not the same store as a failure with an empty session.</summary>
    /// <remarks>Kilo 4537885347: <c>FailureDirectory</c> maps both an absent and an empty session to the same key.</remarks>
    [Fact]
    public async Task AnAbsentSessionDoesNotShareItsFailureStoreWithAnEmptySession()
    {
        string arbiterDirectory = Path.Combine(_sandbox.Path, "artifacts", "arbiter");
        ArbiterRunStore store = new(arbiterDirectory, null, () => Now);
        await store.AppendFailureAsync(null, null, null, "failure from an unnamed session");

        IReadOnlyList<string> readBack = store.ReadFailures(null, null, string.Empty);

        Assert.Empty(readBack);
    }

    /// <summary>A bot comment with a null body is classed through the tool as an error answer, not a thrown exception.</summary>
    /// <remarks>Kilo 4537885372: <c>body.Length</c> dereferences a null body in <c>ClassifyBotReviewComment</c>.</remarks>
    [Fact]
    public async Task AClassifyCallWithANullBodyReturnsAnErrorAnswer()
    {
        using TempDirectory home = new();
        ArbiterHookDecisionEngine engine = new(
            providerFactory: _ => new NullAnswerProvider(),
            homeDirectory: () => home.Path);
        ArbiterTools tools = new(new ArbiterServeContext(
            _sandbox.Path,
            engine,
            TextWriter.Null,
            Composition.LoadHostConfig,
            _ => true));
        await WriteCitedFileAsync("docs/guide.md");

        string json = tools.ClassifyBotReviewComment("docs/guide.md", 2, null!, null);

        Assert.Contains("\"error\"", json, StringComparison.Ordinal);
    }

    /// <summary>Raw input cannot break a digest's one-line-per-field shape: a newline in it adds no field.</summary>
    /// <remarks>Kilo 4537885396: <c>Intake</c> interpolates the input verbatim, so an embedded newline forges a field.</remarks>
    [Fact]
    public void AnIntakeInputWithANewlineCannotForgeARecommendationField()
    {
        string digest = SubstituteDigests.Intake("x\nRECOMMENDATION: PLAN", "SPEC", "decision-1");

        string[] lines = digest.Split('\n');
        Assert.Equal(7, lines.Length);
        Assert.Single(lines, line => line.StartsWith("RECOMMENDATION:", StringComparison.Ordinal));
        Assert.Equal("RECOMMENDATION: SPEC", lines.Single(line => line.StartsWith("RECOMMENDATION:", StringComparison.Ordinal)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Counts provider calls and answers a confident nitpick, so a kept-real result is shown to come from the fail-safe, not from an answer.</summary>
    private sealed class CountingNitpickProvider : IArbiterProvider
    {
        public int Calls { get; private set; }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Dictionary<string, string> answers = rules
                .Where(rule => rule.Ask is not null && rule.Id == CommentTriageService.CommentRuleId)
                .ToDictionary(rule => rule.Id, _ => "nitpick", StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = answers.Keys
                .ToDictionary(id => id, _ => new ArbiterStep1Score(null, 0.95, 0.85), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.14.0", null, true, scores));
        }
    }

    /// <summary>Answers nothing; the null-body test never reaches the provider.</summary>
    private sealed class NullAnswerProvider : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArbiterStep1BatchResult(new Dictionary<string, string>(StringComparer.Ordinal), null, null, false));
    }
}
