using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for the shadow state of <c>KW-ARB-COMMENT-001</c> in the bot-comment triage. The shipped rule is in shadow, so
/// the class handed to the caller is always <c>real</c>, and the result says what the model would have delivered in
/// <c>WouldClass</c>. A host that switches shadow off gets the model's class, as before. The decision record is unchanged.
/// </summary>
/// <remarks>
/// Names (GREEN must match): <c>CommentTriageResult(string Class, double? Confidence, string DecisionId, bool Shadow,
/// string? WouldClass)</c>; <c>CommentSummaryItem(string Path, int Line, string Issue, string Class, double? Confidence,
/// string DecisionId, bool Shadow, string? WouldClass)</c>; the service takes the effective rules as an optional last
/// constructor argument, <c>IReadOnlyList&lt;ArbiterRule&gt;? rules = null</c>, which defaults to the shipped rules.
/// <c>WouldClass</c> is null when the rule is not in shadow, and when the model gave no class at all.
/// </remarks>
public sealed class CommentTriageShadowTests : IDisposable
{
    private const string BotLogin = "kilo-code-bot";
    private const string HumanLogin = "dave";
    private const string Real = "real";
    private const string Nitpick = "nitpick";
    private const string AlreadyFixed = "already-fixed";
    private const string CitedPath = "src/Widget.cs";

    private const string Summary =
        "## Code review summary\n" +
        "| Severity | File | Line | Issue |\n" +
        "|---|---|---|---|\n" +
        "| warning | `src/Widget.cs` | L12 | Rename this variable to something clearer. |\n" +
        "| nit | `src/Widget.cs` | 8-10 | Prefer a constant for the magic number. |\n";

    private readonly TempDirectory _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Root => Path.Combine(_sandbox.Path, "repo");

    private string ArbiterDirectory => Path.Combine(_sandbox.Path, "artifacts", "arbiter");

    private static DateTimeOffset Now => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Answers every comment question with one answer and confidence.</summary>
    private sealed class ScriptedProvider(string answer, double confidence, bool throws = false) : IArbiterProvider
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
                .Where(rule => rule.Ask is not null && rule.Id == CommentTriageService.CommentRuleId)
                .ToDictionary(rule => rule.Id, _ => answer, StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = answers.Keys
                .ToDictionary(id => id, _ => new ArbiterStep1Score(null, confidence, 0.85), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.14.0", null, true, scores));
        }
    }

    /// <summary>Counts the provider calls and answers nothing, the way a provider that is not configured does.</summary>
    private sealed class CountingNoAnswerProvider : IArbiterProvider
    {
        public int Calls { get; private set; }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ArbiterStep1BatchResult(new Dictionary<string, string>(StringComparer.Ordinal), null, null, false));
        }
    }

    /// <summary>The shipped rules with the comment rule's shadow state set by a host configuration override.</summary>
    private static IReadOnlyList<ArbiterRule> RulesWithCommentShadow(bool shadow)
    {
        string yaml = "arbiter:\n  enabled: true\n  rules:\n    - id: KW-ARB-COMMENT-001\n      shadow: " +
            (shadow ? "true" : "false") + "\n";
        return KyberWeaveConfigLoader.LoadFromYaml(yaml).Arbiter.Rules;
    }

    private void WriteCitedFiles()
    {
        foreach (string relative in new[] { CitedPath, "src/Gadget.cs" })
        {
            string full = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, string.Join("\n", Enumerable.Range(1, 30).Select(i => $"var v{i} = {i};")));
        }
    }

    private CommentTriageService Service(IArbiterProvider provider, IReadOnlyList<string>? botLogins = null, IReadOnlyList<ArbiterRule>? rules = null) =>
        new(provider, Root, ArbiterDirectory, new FixedClock(Now), botLogins: botLogins, rules: rules);

    private static CommentTriageRequest Request(string path = CitedPath) =>
        new(path, 12, "Rename this variable to something clearer.", null);

    /// <summary>A confident nitpick in shadow is returned as real, names the nitpick it would have given, and says the rule is shadow.</summary>
    [Fact]
    public async Task ASingleCommentInShadowIsReturnedRealAndNamesWhatTheModelWouldHaveSaid()
    {
        WriteCitedFiles();

        CommentTriageResult result = await Service(new ScriptedProvider(Nitpick, 0.95)).ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.True(result.Shadow);
        Assert.Equal(Nitpick, result.WouldClass);
        Assert.Equal(0.95, result.Confidence);
    }

    /// <summary>The same shadow answer for already-fixed is also returned as real.</summary>
    [Fact]
    public async Task ASingleCommentInShadowWouldHaveSaidAlreadyFixedIsStillReturnedReal()
    {
        WriteCitedFiles();

        CommentTriageResult result = await Service(new ScriptedProvider(AlreadyFixed, 0.95)).ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.Equal(AlreadyFixed, result.WouldClass);
    }

    /// <summary>
    /// A nitpick below the floor would not have been delivered with shadow off, so the class it would have delivered is
    /// real. Failure this guards: WouldClass reporting the raw answer the floor refused.
    /// </summary>
    [Fact]
    public async Task ANitpickBelowTheFloorInShadowWouldHaveBeenReal()
    {
        WriteCitedFiles();

        CommentTriageResult result = await Service(new ScriptedProvider(Nitpick, 0.6)).ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.Equal(Real, result.WouldClass);
    }

    /// <summary>The decision record is unchanged by shadow: it keeps the model's answer, a neutral effect and the would-effect.</summary>
    [Fact]
    public async Task TheDecisionRecordStillCarriesTheAnswerAndTheShadowEffect()
    {
        WriteCitedFiles();

        CommentTriageResult result = await Service(new ScriptedProvider(Nitpick, 0.95)).ClassifyAsync(Request());

        ArbiterDecisionRecord record = Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll(), entry => entry.Id == result.DecisionId);
        Assert.Equal(Nitpick, record.Outcome);
        ArbiterDecisionRule rule = Assert.Single(record.Rules);
        Assert.Equal(CommentTriageService.CommentRuleId, rule.Id);
        Assert.Equal(Nitpick, rule.Answer);
        Assert.True(rule.Shadow);
        Assert.NotEqual(Nitpick, rule.Effect);
        Assert.NotNull(rule.WouldEffect);
    }

    /// <summary>Every summary row sent to the classifier is returned real, and each names the nitpick it would have given.</summary>
    [Fact]
    public async Task EverySummaryRowInShadowIsRealAndNamesWhatTheModelWouldHaveSaid()
    {
        WriteCitedFiles();

        CommentSummaryResult result = await Service(new ScriptedProvider(Nitpick, 0.95), [BotLogin])
            .ClassifySummaryAsync(new CommentSummaryRequest(Summary, BotLogin));

        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(Real, item.Class));
        Assert.All(result.Items, item => Assert.True(item.Shadow));
        Assert.All(result.Items, item => Assert.Equal(Nitpick, item.WouldClass));
        Assert.All(result.Items, item => Assert.False(string.IsNullOrEmpty(item.DecisionId)));
    }

    /// <summary>A row the classifier never saw is real, reports the rule's shadow state, and has no would-class.</summary>
    [Fact]
    public async Task AHumanSummaryRowReportsShadowWithNoWouldClass()
    {
        WriteCitedFiles();
        CountingNoAnswerProvider provider = new();

        CommentSummaryResult result = await Service(provider, [BotLogin])
            .ClassifySummaryAsync(new CommentSummaryRequest(Summary, HumanLogin));

        Assert.All(result.Items, item => Assert.Equal(Real, item.Class));
        Assert.All(result.Items, item => Assert.True(item.Shadow));
        Assert.All(result.Items, item => Assert.Null(item.WouldClass));
        Assert.Equal(0, provider.Calls);
    }

    /// <summary>A host that switches shadow off gets the model's class, with no shadow flag and no would-class.</summary>
    [Fact]
    public async Task WithShadowSwitchedOffByConfigTheModelsClassIsReturned()
    {
        WriteCitedFiles();

        CommentTriageResult result = await Service(new ScriptedProvider(Nitpick, 0.95), rules: RulesWithCommentShadow(false))
            .ClassifyAsync(Request());

        Assert.Equal(Nitpick, result.Class);
        Assert.False(result.Shadow);
        Assert.Null(result.WouldClass);
    }

    /// <summary>With shadow switched off, every summary row carries the model's class.</summary>
    [Fact]
    public async Task WithShadowSwitchedOffByConfigEverySummaryRowCarriesTheModelsClass()
    {
        WriteCitedFiles();

        CommentSummaryResult result = await Service(new ScriptedProvider(Nitpick, 0.95), [BotLogin], RulesWithCommentShadow(false))
            .ClassifySummaryAsync(new CommentSummaryRequest(Summary, BotLogin));

        Assert.All(result.Items, item => Assert.Equal(Nitpick, item.Class));
        Assert.All(result.Items, item => Assert.False(item.Shadow));
        Assert.All(result.Items, item => Assert.Null(item.WouldClass));
    }

    /// <summary>A cited file that is missing stays real; the rule is still reported as shadow, with nothing answered.</summary>
    [Fact]
    public async Task AMissingCitedFileInShadowIsRealWithNoWouldClass()
    {
        Directory.CreateDirectory(Root);

        CommentTriageResult result = await Service(new ScriptedProvider(AlreadyFixed, 0.95)).ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.True(result.Shadow);
        Assert.Null(result.WouldClass);
    }

    /// <summary>With no provider configured nothing is answered: real, shadow, and no would-class.</summary>
    [Fact]
    public async Task WithNoProviderInShadowIsRealWithNoWouldClass()
    {
        WriteCitedFiles();

        CommentTriageResult result = await Service(new NoneProvider()).ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.True(result.Shadow);
        Assert.Null(result.WouldClass);
    }

    /// <summary>A provider failure stays real with no would-class, and the call does not throw.</summary>
    [Fact]
    public async Task AProviderFailureInShadowIsRealWithNoWouldClass()
    {
        WriteCitedFiles();

        CommentTriageResult result = await Service(new ScriptedProvider(Nitpick, 0.95, throws: true)).ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.True(result.Shadow);
        Assert.Null(result.WouldClass);
    }
}
