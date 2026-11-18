using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Data.Arbiter;
using KyberWeave.Core.Arbiter.Plans;
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
using KyberWeave.Tests.Fakes;
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

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private string Root => _host.RepoRoot;

    private string ArbiterDirectory => _host.ArbiterDirectory;

    private static DateTimeOffset Now => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private void WriteCitedFile()
    {
        _ = _host.Write(Path.Combine(Root, CitedPath), string.Join("\n", Enumerable.Range(1, 30).Select(i => $"var v{i} = {i};")));
    }

    /// <summary>The effective rules with the comment rule disabled by a host override, as the loader builds them.</summary>
    private static IReadOnlyList<ArbiterRule> RulesWithCommentDisabled() =>
        KyberWeaveConfigAssembler.FromYaml("arbiter:\n  enabled: true\n  rules:\n    - id: " + CommentRule + "\n      enabled: false\n")
            .Arbiter.Rules;

    /// <summary>The effective rules with the comment rule absent from the set.</summary>
    private static IReadOnlyList<ArbiterRule> RulesWithoutCommentRule() =>
        ArbiterConfigDefaults.Product.Rules.Where(rule => rule.Id != CommentRule).ToList();

    private CommentTriageService Service(IArbiterProvider provider, IReadOnlyList<ArbiterRule> rules, IReadOnlyList<string>? botLogins = null) =>
        new(provider, Root, ArbiterDirectory, new FixedClock(Now), new FakeIdGenerator(), _host.Get<IFileTextFacts>(), _host.Get<IArbiterStateFactory>(), botLogins: botLogins, rules: rules);

    private static CommentTriageRequest Request() => new(CitedPath, 12, "Rename this.", null);

    /// <summary>A disabled rule makes no provider call, returns real with no shadow and no would-class, and reports not-evaluated.</summary>
    [Fact]
    public async Task ADisabledCommentRuleMakesNoProviderCallAndReportsNotEvaluated()
    {
        WriteCitedFile();
        FakeArbiterProvider provider = FakeArbiterProvider.AnsweringRule(CommentRule, Nitpick);

        CommentTriageResult result = await Service(provider, RulesWithCommentDisabled()).ClassifyAsync(Request());

        Assert.Equal(0, provider.CallCount);
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
        FakeArbiterProvider provider = FakeArbiterProvider.AnsweringRule(CommentRule, Nitpick);

        CommentTriageResult result = await Service(provider, RulesWithoutCommentRule()).ClassifyAsync(Request());

        Assert.Equal(0, provider.CallCount);
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

        CommentTriageResult result = await Service(FakeArbiterProvider.AnsweringRule(CommentRule, Nitpick), RulesWithCommentDisabled()).ClassifyAsync(Request());

        ArbiterDecisionRecord record = Assert.Single(_host.Decisions, entry => entry.Id == result.DecisionId);
        Assert.Empty(record.Rules);
        Assert.Equal(ArbiterProviderStatuses.NotEvaluated, record.Provider.Status);
    }

    /// <summary>Every summary row of a disabled rule is real, not-evaluated, with no shadow and no provider call.</summary>
    [Fact]
    public async Task EverySummaryRowOfADisabledRuleIsRealAndNotEvaluated()
    {
        WriteCitedFile();
        FakeArbiterProvider provider = FakeArbiterProvider.AnsweringRule(CommentRule, Nitpick);

        CommentSummaryResult result = await Service(provider, RulesWithCommentDisabled(), [BotLogin])
            .ClassifySummaryAsync(new CommentSummaryRequest(Summary, BotLogin));

        Assert.Equal(0, provider.CallCount);
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

        CommentTriageResult result = await Service(FakeArbiterProvider.AnsweringRule(CommentRule, Real), KyberWeaveConfigAssembler.FromYaml("arbiter:\n  enabled: true\n").Arbiter.Rules)
            .ClassifyAsync(Request());

        Assert.Equal(Real, result.Class);
        Assert.Equal(ArbiterProviderStatuses.Answered, result.Status);
    }

    private ArbiterTools Tools(IArbiterProvider provider, string root, IReadOnlyList<ArbiterRule> rules)
    {
        ArbiterHookDecisionEngine engine = _host.Engine(providerFactory: _ => provider);
        KyberWeaveConfig config = new()
        {
            Arbiter = ArbiterConfigDefaults.Product with
            {
                Enabled = true,
                Rules = rules,
                CommentTriage = new ArbiterCommentTriageConfig([BotLogin]),
            },
        };
        return _host.Tools(engine, root, _ => config);
    }

    /// <summary>The tool's inline JSON for a disabled rule carries status not-evaluated, and an answered call carries answered.</summary>
    [Fact]
    public void TheToolJsonReportsNotEvaluatedForADisabledRuleAndAnsweredOtherwise()
    {
        WriteCitedFile();

        using JsonDocument disabled = JsonDocument.Parse(Tools(FakeArbiterProvider.AnsweringRule(CommentRule, Nitpick), Root, RulesWithCommentDisabled())
            .ClassifyBotReviewComment(CitedPath, 12, "Rename this.", null));
        using JsonDocument answered = JsonDocument.Parse(Tools(FakeArbiterProvider.AnsweringRule(CommentRule, Real), Root,
                KyberWeaveConfigAssembler.FromYaml("arbiter:\n  enabled: true\n").Arbiter.Rules)
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

        using JsonDocument json = JsonDocument.Parse(Tools(FakeArbiterProvider.AnsweringRule(CommentRule, Nitpick), Root, RulesWithCommentDisabled())
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
        ArbiterCommentCommand disabledCommand = _host.CommentCommand((_, _) => FakeArbiterProvider.AnsweringRule(CommentRule, Nitpick));
        CommandExecution disabled = Capture(() => disabledCommand.Execute(
            null!,
            new ArbiterCommentSettings { Root = Root, Path = CitedPath, Line = 12, Body = "Rename this." }));

        Assert.Equal(0, disabled.ExitCode);
        Assert.Contains("class real", disabled.Output, StringComparison.Ordinal);
        Assert.Contains("status not-evaluated", disabled.Output, StringComparison.Ordinal);

        _host.Files.DeleteFile(Path.Combine(Root, ".kyber-weave", "kyber-weave.yml"));
        ArbiterCommentCommand answeredCommand = _host.CommentCommand((_, _) => FakeArbiterProvider.AnsweringRule(CommentRule, Real));
        CommandExecution answered = Capture(() => answeredCommand.Execute(
            null!,
            new ArbiterCommentSettings { Root = Root, Path = CitedPath, Line = 12, Body = "Rename this." }));

        Assert.Equal(0, answered.ExitCode);
        Assert.Contains("class real", answered.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("not-evaluated", answered.Output, StringComparison.Ordinal);
    }

    private void WriteHostConfig(string yaml)
    {
        _ = _host.WriteRepoConfig(yaml, Root);
    }

    private CommandExecution Capture(Func<int> execute)
    {
        ArbiterCommandRun run = _host.Run(execute);
        return new CommandExecution(run.ExitCode, run.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
