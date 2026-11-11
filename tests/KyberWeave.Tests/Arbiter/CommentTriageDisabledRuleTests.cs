using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for a disabled <c>KW-ARB-COMMENT-001</c>. When the effective rule is disabled (host override <c>enabled: false</c>)
/// or absent from the effective rule set, the triage makes no provider call, returns <c>real</c> for every comment and row
/// with <c>shadow</c> false and <c>wouldClass</c> null, and reports <c>status</c> <c>not-evaluated</c>, the evaluator's own
/// provider status word. An answered comment reports <c>answered</c>. The decision record is written, with no rule entry,
/// as the evaluator writes one when no rule takes part.
/// </summary>
/// <remarks>
/// The evaluator drops a disabled rule before it asks anything, so the disabled rule never appears in the record's rules.
/// <c>Status</c> is the provider status the decision record carries, so the tool, the CLI and the log read the same word.
/// </remarks>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class CommentTriageDisabledRuleTests : IDisposable
{
    private const string BotLogin = "kilo-code-bot";
    private const string CommentRule = "KW-ARB-COMMENT-001";
    private const string Real = "real";
    private const string Nitpick = "nitpick";
    private const string CitedPath = "src/Widget.cs";
    private const string Summary =
        "| Severity | File | Line | Issue |\n" +
        "|---|---|---|---|\n" +
        "| warning | `src/Widget.cs` | L12 | Rename this. |\n" +
        "| nit | `src/Widget.cs` | 8-10 | Prefer a constant. |\n";

    private readonly TempDirectory _sandbox = new();
    private readonly TempDirectory _home = new();

    public void Dispose()
    {
        _sandbox.Dispose();
        _home.Dispose();
    }

    private string Root => Path.Combine(_sandbox.Path, "repo");

    private string ArbiterDirectory => Path.Combine(_sandbox.Path, "artifacts", "arbiter");

    private static DateTimeOffset Now => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Answers the comment question with one class at one confidence and counts the calls.</summary>
    private sealed class CountingProvider(string answer = Nitpick, double confidence = 0.95) : IArbiterProvider
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
                .Where(rule => rule.Ask is not null && rule.Id == CommentRule)
                .ToDictionary(rule => rule.Id, _ => answer, StringComparer.Ordinal);
            Dictionary<string, ArbiterStep1Score> scores = answers.Keys
                .ToDictionary(id => id, _ => new ArbiterStep1Score(null, confidence, 0.85), StringComparer.Ordinal);
            return Task.FromResult(new ArbiterStep1BatchResult(answers, "jev-1.14.0", null, true, scores));
        }
    }

    private void WriteCitedFile()
    {
        string full = Path.Combine(Root, CitedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Join("\n", Enumerable.Range(1, 30).Select(i => $"var v{i} = {i};")));
    }

    /// <summary>The effective rules with the comment rule disabled by a host override, as the loader builds them.</summary>
    private static IReadOnlyList<ArbiterRule> RulesWithCommentDisabled() =>
        KyberWeaveConfigLoader.LoadFromYaml("arbiter:\n  enabled: true\n  rules:\n    - id: " + CommentRule + "\n      enabled: false\n")
            .Arbiter.Rules;

    /// <summary>The effective rules with the comment rule absent from the set.</summary>
    private static IReadOnlyList<ArbiterRule> RulesWithoutCommentRule() =>
        ArbiterConfig.ProductDefaults.Rules.Where(rule => rule.Id != CommentRule).ToList();

    private CommentTriageService Service(IArbiterProvider provider, IReadOnlyList<ArbiterRule> rules, IReadOnlyList<string>? botLogins = null) =>
        new(provider, Root, ArbiterDirectory, new FixedClock(Now), botLogins: botLogins, rules: rules);

    private static CommentTriageRequest Request() => new(CitedPath, 12, "Rename this.", null);

    /// <summary>A disabled rule makes no provider call, returns real with no shadow and no would-class, and reports not-evaluated.</summary>
    [Fact]
    public async Task ADisabledCommentRuleMakesNoProviderCallAndReportsNotEvaluated()
    {
        WriteCitedFile();
        CountingProvider provider = new();

        CommentTriageResult result = await Service(provider, RulesWithCommentDisabled()).ClassifyAsync(Request());

        Assert.Equal(0, provider.Calls);
        Assert.Equal(Real, result.Class);
        Assert.False(result.Shadow);
        Assert.Null(result.WouldClass);
        Assert.Equal(ArbiterProviderStatuses.NotEvaluated, result.Status);
    }

    /// <summary>An absent rule behaves like a disabled one: no call, real, not-evaluated.</summary>
    [Fact]
    public async Task AnAbsentCommentRuleMakesNoProviderCallAndReportsNotEvaluated()
    {
        WriteCitedFile();
        CountingProvider provider = new();

        CommentTriageResult result = await Service(provider, RulesWithoutCommentRule()).ClassifyAsync(Request());

        Assert.Equal(0, provider.Calls);
        Assert.Equal(Real, result.Class);
        Assert.False(result.Shadow);
        Assert.Null(result.WouldClass);
        Assert.Equal(ArbiterProviderStatuses.NotEvaluated, result.Status);
    }

    /// <summary>A disabled rule still writes its decision record, with no rule entry and the not-evaluated provider status.</summary>
    [Fact]
    public async Task ADisabledCommentRuleWritesADecisionWithNoRuleEntry()
    {
        WriteCitedFile();

        CommentTriageResult result = await Service(new CountingProvider(), RulesWithCommentDisabled()).ClassifyAsync(Request());

        ArbiterDecisionRecord record = Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll(), entry => entry.Id == result.DecisionId);
        Assert.Empty(record.Rules);
        Assert.Equal(ArbiterProviderStatuses.NotEvaluated, record.Provider.Status);
    }

    /// <summary>Every summary row of a disabled rule is real, not-evaluated, with no shadow and no provider call.</summary>
    [Fact]
    public async Task EverySummaryRowOfADisabledRuleIsRealAndNotEvaluated()
    {
        WriteCitedFile();
        CountingProvider provider = new();

        CommentSummaryResult result = await Service(provider, RulesWithCommentDisabled(), [BotLogin])
            .ClassifySummaryAsync(new CommentSummaryRequest(Summary, BotLogin));

        Assert.Equal(0, provider.Calls);
        Assert.Equal(2, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(Real, item.Class));
        Assert.All(result.Items, item => Assert.False(item.Shadow));
        Assert.All(result.Items, item => Assert.Null(item.WouldClass));
        Assert.All(result.Items, item => Assert.Equal(ArbiterProviderStatuses.NotEvaluated, item.Status));
    }

    /// <summary>An enabled rule that answers reports answered, so a reader can tell it from a disabled rule.</summary>
    [Fact]
    public async Task AnAnsweredCommentIsReportedAsAnsweredNotAsNotEvaluated()
    {
        WriteCitedFile();

        CommentTriageResult result = await Service(new CountingProvider(Real, 0.95), KyberWeaveConfigLoader.LoadFromYaml("arbiter:\n  enabled: true\n").Arbiter.Rules)
            .ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.Equal(ArbiterProviderStatuses.Answered, result.Status);
    }

    private static ArbiterTools Tools(IArbiterProvider provider, string root, TempDirectory home, IReadOnlyList<ArbiterRule> rules)
    {
        ArbiterHookDecisionEngine engine = new(providerFactory: _ => provider, homeDirectory: () => home.Path);
        KyberWeaveConfig config = new()
        {
            Arbiter = ArbiterConfig.ProductDefaults with
            {
                Enabled = true,
                Rules = rules,
                CommentTriage = new ArbiterCommentTriageConfig([BotLogin]),
            },
        };
        return new ArbiterTools(new ArbiterServeContext(root, engine, TextWriter.Null, _ => config, _ => true));
    }

    /// <summary>The tool's inline JSON for a disabled rule carries status not-evaluated, and an answered call carries answered.</summary>
    [Fact]
    public void TheToolJsonReportsNotEvaluatedForADisabledRuleAndAnsweredOtherwise()
    {
        WriteCitedFile();

        using JsonDocument disabled = JsonDocument.Parse(Tools(new CountingProvider(), Root, _home, RulesWithCommentDisabled())
            .ClassifyBotReviewComment(CitedPath, 12, "Rename this.", null));
        using JsonDocument answered = JsonDocument.Parse(Tools(new CountingProvider(Real, 0.95), Root, _home,
                KyberWeaveConfigLoader.LoadFromYaml("arbiter:\n  enabled: true\n").Arbiter.Rules)
            .ClassifyBotReviewComment(CitedPath, 12, "Rename this.", null));

        Assert.Equal("real", disabled.RootElement.GetProperty("class").GetString());
        Assert.Equal("not-evaluated", disabled.RootElement.GetProperty("status").GetString());
        Assert.Equal("real", answered.RootElement.GetProperty("class").GetString());
        Assert.Equal("answered", answered.RootElement.GetProperty("status").GetString());
    }

    /// <summary>The tool's summary items for a disabled rule carry status not-evaluated.</summary>
    [Fact]
    public void TheToolSummaryItemsReportNotEvaluatedForADisabledRule()
    {
        WriteCitedFile();

        using JsonDocument json = JsonDocument.Parse(Tools(new CountingProvider(), Root, _home, RulesWithCommentDisabled())
            .ClassifyBotReviewComment(string.Empty, 0, Summary, null, summary: true, author: BotLogin));

        JsonElement item = json.RootElement.GetProperty("items")[0];
        Assert.Equal("real", item.GetProperty("class").GetString());
        Assert.Equal("not-evaluated", item.GetProperty("status").GetString());
    }

    /// <summary>The CLI prints a status line for a disabled rule, and none for an answered comment.</summary>
    [Fact]
    public void TheCliShowsNotEvaluatedForADisabledRuleAndNoStatusLineWhenAnswered()
    {
        WriteCitedFile();
        WriteHostConfig("arbiter:\n  enabled: true\n  rules:\n    - id: " + CommentRule + "\n      enabled: false\n");
        ArbiterCommentCommand disabledCommand = new() { ProviderFactory = (_, _) => new CountingProvider() };
        CommandExecution disabled = Capture(() => disabledCommand.Execute(
            null!,
            new ArbiterCommentSettings { Root = Root, Path = CitedPath, Line = 12, Body = "Rename this." }));

        Assert.Equal(0, disabled.ExitCode);
        Assert.Contains("class real", disabled.Output, StringComparison.Ordinal);
        Assert.Contains("status not-evaluated", disabled.Output, StringComparison.Ordinal);

        File.Delete(Path.Combine(Root, ".kyber-weave", "kyber-weave.yml"));
        ArbiterCommentCommand answeredCommand = new() { ProviderFactory = (_, _) => new CountingProvider(Real, 0.95) };
        CommandExecution answered = Capture(() => answeredCommand.Execute(
            null!,
            new ArbiterCommentSettings { Root = Root, Path = CitedPath, Line = 12, Body = "Rename this." }));

        Assert.Equal(0, answered.ExitCode);
        Assert.Contains("class real", answered.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("not-evaluated", answered.Output, StringComparison.Ordinal);
    }

    private void WriteHostConfig(string yaml)
    {
        string directory = Path.Combine(Root, ".kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kyber-weave.yml"), yaml);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
