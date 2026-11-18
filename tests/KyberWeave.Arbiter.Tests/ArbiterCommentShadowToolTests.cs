using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Arbiter.Mcp;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using KyberWeave.Core.Configuration;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for the served tool <c>arbiter_classify_bot_review_comment</c> while the comment rule is in shadow. Its JSON
/// carries <c>shadow</c> (bool) and <c>wouldClass</c> (string or null) beside <c>class</c>, both for the inline answer and
/// for each summary item. <c>class</c> is <c>real</c> while the rule is in shadow. A host override that switches shadow off
/// is honoured: the tool then returns the model's class with <c>shadow</c> false and <c>wouldClass</c> null.
/// </summary>
/// <remarks>
/// The field names follow the tool's camelCase keys (<c>decisionId</c>, <c>class</c>). <c>wouldClass</c> is serialised as
/// JSON null, not omitted, so a reader always finds the key.
/// </remarks>
public sealed class ArbiterCommentShadowToolTests : IDisposable
{
    private const string BotLogin = "kilo-code-bot";
    private const string HumanLogin = "dave";
    private const string CitedPath = "src/Widget.cs";
    private const string Summary =
        "| Severity | File | Line | Issue |\n" +
        "|---|---|---|---|\n" +
        "| warning | `src/Widget.cs` | L12 | Rename this variable to something clearer. |\n";

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private void WriteCitedFile()
    {
        _ = _host.Write(Path.Combine(_host.RepoRoot, CitedPath), string.Join("\n", Enumerable.Range(1, 30).Select(i => $"var v{i} = {i};")));
    }

    /// <summary>The served tools over a configuration. The default is the shipped rules with the bot login configured.</summary>
    private ArbiterTools Tools(IArbiterProvider provider, KyberWeaveConfig? config = null)
    {
        ArbiterHookDecisionEngine engine = _host.Engine(
            providerFactory: _ => provider);
        KyberWeaveConfig configured = config ?? new KyberWeaveConfig
        {
            Arbiter = ArbiterConfigDefaults.Product with
            {
                Enabled = true,
                CommentTriage = new ArbiterCommentTriageConfig([BotLogin]),
            },
        };
        return _host.Tools(engine, loadConfig: _ => configured);
    }

    /// <summary>A host configuration that sets the comment rule's shadow state, on top of the shipped rules.</summary>
    private static KyberWeaveConfig ConfigWithCommentShadow(bool shadow)
    {
        string yaml = "arbiter:\n  enabled: true\n  comment-triage:\n    bot-logins:\n      - " + BotLogin +
            "\n  rules:\n    - id: KW-ARB-COMMENT-001\n      shadow: " + (shadow ? "true" : "false") + "\n";
        return KyberWeaveConfigAssembler.FromYaml(yaml);
    }

    /// <summary>An inline answer in shadow reports class real, shadow true, and the nitpick it would have given.</summary>
    [Fact]
    public void AnInlineAnswerInShadowReportsRealShadowAndTheWouldClass()
    {
        WriteCitedFile();

        using JsonDocument json = JsonDocument.Parse(Tools(FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick"))
            .ClassifyBotReviewComment(CitedPath, 12, "Rename this.", null));

        Assert.Equal("real", json.RootElement.GetProperty("class").GetString());
        Assert.True(json.RootElement.GetProperty("shadow").GetBoolean());
        Assert.Equal("nitpick", json.RootElement.GetProperty("wouldClass").GetString());
    }

    /// <summary>The inline JSON always carries the shadow keys, so a reader can rely on them; wouldClass is null when nothing was answered.</summary>
    [Fact]
    public void TheInlineJsonAlwaysCarriesShadowAndWouldClassKeys()
    {
        using JsonDocument json = JsonDocument.Parse(Tools(FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick"))
            .ClassifyBotReviewComment("src/Missing.cs", 12, "Rename this.", null));

        Assert.True(json.RootElement.TryGetProperty("shadow", out JsonElement shadow));
        Assert.Equal(JsonValueKind.True, shadow.ValueKind);
        Assert.True(json.RootElement.TryGetProperty("wouldClass", out JsonElement wouldClass));
        Assert.Equal(JsonValueKind.Null, wouldClass.ValueKind);
        Assert.Equal("real", json.RootElement.GetProperty("class").GetString());
    }

    /// <summary>Each summary item carries shadow and wouldClass, and the class stays real.</summary>
    [Fact]
    public void EachSummaryItemCarriesShadowAndWouldClass()
    {
        WriteCitedFile();

        using JsonDocument json = JsonDocument.Parse(Tools(FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick"))
            .ClassifyBotReviewComment(string.Empty, 0, Summary, null, summary: true, author: BotLogin));

        JsonElement item = json.RootElement.GetProperty("items")[0];
        Assert.Equal("real", item.GetProperty("class").GetString());
        Assert.True(item.GetProperty("shadow").GetBoolean());
        Assert.Equal("nitpick", item.GetProperty("wouldClass").GetString());
    }

    /// <summary>A human author's summary item is real, shadow, with no would-class, and the provider is not asked.</summary>
    [Fact]
    public void AHumanSummaryItemIsShadowWithANullWouldClass()
    {
        WriteCitedFile();
        FakeArbiterProvider provider = FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick");

        using JsonDocument json = JsonDocument.Parse(Tools(provider)
            .ClassifyBotReviewComment(string.Empty, 0, Summary, null, summary: true, author: HumanLogin));

        JsonElement item = json.RootElement.GetProperty("items")[0];
        Assert.Equal("real", item.GetProperty("class").GetString());
        Assert.True(item.GetProperty("shadow").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("wouldClass").ValueKind);
        Assert.Equal(0, provider.CallCount);
    }

    /// <summary>A host override that switches shadow off is honoured: the model's class is returned with shadow false and no would-class.</summary>
    [Fact]
    public void AHostOverrideSwitchingShadowOffReturnsTheModelsClass()
    {
        WriteCitedFile();

        using JsonDocument json = JsonDocument.Parse(Tools(FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick"), ConfigWithCommentShadow(shadow: false))
            .ClassifyBotReviewComment(CitedPath, 12, "Rename this.", null));

        Assert.Equal("nitpick", json.RootElement.GetProperty("class").GetString());
        Assert.False(json.RootElement.GetProperty("shadow").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("wouldClass").ValueKind);
    }

    /// <summary>With shadow switched off by configuration, every summary item carries the model's class.</summary>
    [Fact]
    public void AHostOverrideSwitchingShadowOffReturnsTheModelsClassForEverySummaryItem()
    {
        WriteCitedFile();

        using JsonDocument json = JsonDocument.Parse(Tools(FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick"), ConfigWithCommentShadow(shadow: false))
            .ClassifyBotReviewComment(string.Empty, 0, Summary, null, summary: true, author: BotLogin));

        JsonElement item = json.RootElement.GetProperty("items")[0];
        Assert.Equal("nitpick", item.GetProperty("class").GetString());
        Assert.False(item.GetProperty("shadow").GetBoolean());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("wouldClass").ValueKind);
    }
}
