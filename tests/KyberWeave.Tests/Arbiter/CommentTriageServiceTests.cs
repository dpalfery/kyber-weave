using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.13 (bot comment triage): <c>CommentTriageService.ClassifyAsync</c> asks the provider, through
/// <c>KW-ARB-COMMENT-001</c>, whether a bot review comment is <c>real</c>, a <c>nitpick</c>, or <c>already-fixed</c>
/// in the code as it is now. Every call records a decision and returns its id. Fail-safe: any answer that is unsure,
/// below the 0.85 floor, not sent, or failed returns <c>real</c>, so a worker never skips a comment on a shaky answer.
/// RED: <c>CommentTriageService</c>, <c>CommentTriageRequest</c>, <c>CommentTriageResult</c> do not exist yet.
/// </summary>
/// <remarks>
/// Names (GREEN must match): <c>new CommentTriageService(IArbiterProvider provider, string repoRoot,
/// string arbiterDirectory, TimeProvider clock)</c>; <c>Task&lt;CommentTriageResult&gt; ClassifyAsync(CommentTriageRequest
/// request, CancellationToken ct = default)</c>; <c>CommentTriageRequest(string Path, int Line, string Body, string? CommitSha)</c>;
/// <c>CommentTriageResult(string Class, double? Confidence, string DecisionId)</c>. Classes: <c>real</c>, <c>nitpick</c>,
/// <c>already-fixed</c>.
/// </remarks>
public sealed class CommentTriageServiceTests : IDisposable
{
    private const string CommentRule = "KW-ARB-COMMENT-001";
    private const string Real = "real";
    private const string Nitpick = "nitpick";
    private const string AlreadyFixed = "already-fixed";
    private const string CitedPath = "src/Widget.cs";

    private readonly TempDirectory _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Root => Path.Combine(_sandbox.Path, "repo");

    private string ArbiterDirectory => Path.Combine(_sandbox.Path, "artifacts", "arbiter");

    private static DateTimeOffset Now => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Answers the comment question with one answer at one confidence. <paramref name="requestSent"/> <c>false</c>
    /// models a provider that did not answer; <paramref name="throws"/> models a transport failure.
    /// </summary>
    private sealed class ScriptedCommentProvider(string answer, double confidence, bool requestSent = true, bool throws = false)
        : IArbiterProvider
    {
        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            if (throws)
            {
                throw new HttpRequestException("provider unreachable");
            }

            Dictionary<string, string> answers = rules
                .Where(rule => rule.Ask is not null && rule.Id == CommentRule)
                .ToDictionary(rule => rule.Id, _ => answer, StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = answers.Keys
                .ToDictionary(id => id, _ => new ArbiterStep1Score(null, confidence, 0.85), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.14.0", null, requestSent, scores));
        }
    }

    private void WriteCitedFile()
    {
        string full = Path.Combine(Root, CitedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Join("\n", Enumerable.Range(1, 30).Select(i => $"var v{i} = {i};")));
    }

    private CommentTriageService Service(IArbiterProvider provider) =>
        new(provider, Root, ArbiterDirectory, new FixedClock(Now));

    private static CommentTriageRequest Request() =>
        new(CitedPath, 12, "Rename this variable to something clearer.", null);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A confident nitpick answer is recorded and reported, but the rule ships in shadow, so the class handed back is real
    /// and the nitpick is named as what the model would have given.
    /// </summary>
    [Fact]
    public async Task AConfidentNitpickAnswerIsRealWhileTheRuleIsInShadow()
    {
        WriteCitedFile();

        CommentTriageResult result = await Service(new ScriptedCommentProvider(Nitpick, 0.95))
            .ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.True(result.Shadow);
        Assert.Equal(Nitpick, result.WouldClass);
        Assert.Equal(0.95, result.Confidence);
        Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll(), record => record.Id == result.DecisionId);
    }

    /// <summary>
    /// When the bot's complaint is already fixed, the shadow rule still hands back real; the would-class names already-fixed.
    /// </summary>
    [Fact]
    public async Task AConfidentAlreadyFixedAnswerIsRealWhileTheRuleIsInShadow()
    {
        WriteCitedFile();

        CommentTriageResult result = await Service(new ScriptedCommentProvider(AlreadyFixed, 0.95))
            .ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.Equal(AlreadyFixed, result.WouldClass);
    }

    /// <summary>A confident real answer is passed through as real.</summary>
    [Fact]
    public async Task AConfidentRealAnswerClassifiesTheCommentAsReal()
    {
        WriteCitedFile();

        CommentTriageResult result = await Service(new ScriptedCommentProvider(Real, 0.95))
            .ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
    }

    /// <summary>
    /// A nitpick answer below the 0.85 floor is not trusted, so the comment stays real. Failure this guards: an unsure
    /// model waves away a genuine defect the bot found.
    /// </summary>
    [Fact]
    public async Task ANitpickBelowTheFloorFailsSafeToReal()
    {
        WriteCitedFile();

        CommentTriageResult result = await Service(new ScriptedCommentProvider(Nitpick, 0.6))
            .ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
    }

    /// <summary>A provider that did not send the request has answered nothing, so the comment stays real.</summary>
    [Fact]
    public async Task AProviderThatDidNotSendTheRequestFailsSafeToReal()
    {
        WriteCitedFile();

        CommentTriageResult result = await Service(new ScriptedCommentProvider(AlreadyFixed, 0.95, requestSent: false))
            .ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
    }

    /// <summary>A transport failure during the call leaves the comment real and does not stop the worker.</summary>
    [Fact]
    public async Task AProviderFailureFailsSafeToReal()
    {
        WriteCitedFile();

        CommentTriageResult result = await Service(new ScriptedCommentProvider(Nitpick, 0.95, throws: true))
            .ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
    }

    /// <summary>
    /// A cited file that no longer exists cannot show the comment is already fixed or a nitpick, so the comment stays
    /// real. Failure this guards: a comment on a deleted file is closed as a nitpick with no code to check it against.
    /// </summary>
    [Fact]
    public async Task AMissingCitedFileFailsSafeToReal()
    {
        Directory.CreateDirectory(Root);

        CommentTriageResult result = await Service(new ScriptedCommentProvider(AlreadyFixed, 0.95))
            .ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.False(string.IsNullOrEmpty(result.DecisionId));
    }
}
