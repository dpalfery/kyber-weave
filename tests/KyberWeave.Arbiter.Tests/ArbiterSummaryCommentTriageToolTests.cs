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
/// Contract for task 3.20 (requirement 20 amendment), served tool: <c>arbiter_classify_bot_review_comment</c> gains
/// <c>summary</c> (bool) and <c>author</c>. With <c>summary: true</c> the body is a PR-level summary and the JSON answer
/// carries <c>items[]</c>, each with <c>path</c>, <c>line</c>, <c>issue</c>, <c>class</c>, <c>confidence</c> and
/// <c>decisionId</c>. The tool name is unchanged; the pinned tool set stays at five.
/// RED: <c>ArbiterTools.ClassifyBotReviewCommentAsync</c> has no <c>summary</c> or <c>author</c> parameter yet.
/// </summary>
/// <remarks>
/// Names (GREEN must match): the parameters are appended to the existing signature as
/// <c>bool summary = false, string? author = null</c>. In summary mode <c>path</c> and <c>line</c> are ignored and may be
/// empty or zero. Bot logins come from <c>arbiter.comment-triage.bot-logins</c> in the config <c>LoadConfig</c> returns.
/// </remarks>
public sealed class ArbiterSummaryCommentTriageToolTests : IDisposable
{
    private const string BotLogin = "kilo-code-bot";
    private const string KiloSummary =
        "Three findings on this change.\n\n" +
        "| Severity | File | Line | Issue |\n" +
        "|---|---|---|---|\n" +
        "| warning | `src/Widget.cs` | L12 | Rename this variable to something clearer. |\n" +
        "| nit | `src/Gadget.cs` | 8-10 | Prefer a constant for the magic number. |\n" +
        "| warning | `src/Widget.cs:20` | 20 | The null check is missing before the dereference. |\n";

    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    private void WriteCitedFiles()
    {
        foreach (string relative in new[] { "src/Widget.cs", "src/Gadget.cs" })
        {
            _ = _host.Write(Path.Combine(_host.RepoRoot, relative), string.Join("\n", Enumerable.Range(1, 30).Select(i => $"var v{i} = {i};")));
        }
    }

    private ArbiterTools Tools(IArbiterProvider provider, IReadOnlyList<string> botLogins)
    {
        ArbiterHookDecisionEngine engine = _host.Engine(
            providerFactory: _ => provider);
        return _host.Tools(
            engine,
            loadConfig: _ => new KyberWeaveConfig
            {
                Arbiter = ArbiterConfigDefaults.Product with
                {
                    Enabled = true,
                    CommentTriage = new ArbiterCommentTriageConfig(botLogins),
                },
            });
    }

    /// <summary>A configured bot's summary yields one item per table row, each with the fields the caller needs.</summary>
    [Fact]
    public async Task ASummaryFromAConfiguredBotReturnsItemsWithTheirFields()
    {
        WriteCitedFiles();

        using JsonDocument json = JsonDocument.Parse(await Tools(FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick"), [BotLogin])
            .ClassifyBotReviewCommentAsync(string.Empty, 0, KiloSummary, null, summary: true, author: BotLogin));

        JsonElement items = json.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal("src/Widget.cs", items[0].GetProperty("path").GetString());
        Assert.Equal(12, items[0].GetProperty("line").GetInt32());
        Assert.Equal("Rename this variable to something clearer.", items[0].GetProperty("issue").GetString());
        Assert.Equal("real", items[0].GetProperty("class").GetString());
        Assert.Equal("nitpick", items[0].GetProperty("wouldClass").GetString());
        Assert.False(string.IsNullOrEmpty(items[0].GetProperty("decisionId").GetString()));
    }

    /// <summary>A human author's summary is answered with real items and the provider is not asked.</summary>
    [Fact]
    public async Task ASummaryFromAHumanAuthorReturnsRealItemsWithoutAProviderCall()
    {
        WriteCitedFiles();
        FakeArbiterProvider provider = FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick");

        using JsonDocument json = JsonDocument.Parse(await Tools(provider, [BotLogin])
            .ClassifyBotReviewCommentAsync(string.Empty, 0, KiloSummary, null, summary: true, author: "dave"));

        JsonElement items = json.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        for (int index = 0; index < items.GetArrayLength(); index++)
        {
            Assert.Equal("real", items[index].GetProperty("class").GetString());
        }

        Assert.Equal(0, provider.CallCount);
    }

    /// <summary>A summary with no author is real when bot logins are configured, never assumed to be a bot.</summary>
    [Fact]
    public async Task ASummaryWithNoAuthorIsRealWhenBotLoginsAreConfigured()
    {
        WriteCitedFiles();

        using JsonDocument json = JsonDocument.Parse(await Tools(FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick"), [BotLogin])
            .ClassifyBotReviewCommentAsync(string.Empty, 0, KiloSummary, null, summary: true, author: null));

        JsonElement items = json.RootElement.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal("real", items[0].GetProperty("class").GetString());
    }

    /// <summary>A summary-mode call with a null body is an error answer, not a thrown exception.</summary>
    [Fact]
    public async Task ASummaryWithANullBodyReturnsAnErrorAnswer()
    {
        using JsonDocument json = JsonDocument.Parse(await Tools(FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick"), [BotLogin])
            .ClassifyBotReviewCommentAsync(string.Empty, 0, null!, null, summary: true, author: BotLogin));

        Assert.True(json.RootElement.TryGetProperty("error", out _));
        Assert.False(json.RootElement.TryGetProperty("items", out _));
    }

    /// <summary>The inline call is unchanged: with summary left false it still answers class and decision id, not items.</summary>
    [Fact]
    public async Task AnInlineCallStillAnswersClassAndNoItems()
    {
        WriteCitedFiles();

        using JsonDocument json = JsonDocument.Parse(await Tools(FakeArbiterProvider.AnsweringRule(CommentTriageService.CommentRuleId, "nitpick"), [BotLogin])
            .ClassifyBotReviewCommentAsync("src/Widget.cs", 12, "Rename this.", null));

        Assert.True(json.RootElement.TryGetProperty("class", out _));
        Assert.False(json.RootElement.TryGetProperty("items", out _));
    }
}
