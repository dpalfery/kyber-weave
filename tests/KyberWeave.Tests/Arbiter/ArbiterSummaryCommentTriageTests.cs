using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Providers;
using KyberWeave.Core.Arbiter.Rules;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task 3.20 (requirement 20 amendment): triage also covers bot-authored PR-level summary comments. A summary
/// carries a markdown findings table; each row is classified real, nitpick or already-fixed through the same excerpt and
/// provider path as an inline comment. Fail-safe: anything not classifiable stays real, and a human author is never classified.
/// RED: <c>CommentTriageService.ClassifySummaryAsync</c>, its request, result and item records, the bot-logins constructor
/// parameter and <c>CommentTriageService.MaxSummaryRows</c> do not exist yet.
/// </summary>
/// <remarks>
/// Names (GREEN must match): <c>new CommentTriageService(IArbiterProvider provider, string repoRoot, string arbiterDirectory,
/// TimeProvider clock, string? ruleSet = null, IReadOnlyList&lt;string&gt;? botLogins = null)</c>;
/// <c>Task&lt;CommentSummaryResult&gt; ClassifySummaryAsync(CommentSummaryRequest request, CancellationToken ct = default)</c>;
/// <c>CommentSummaryRequest(string Body, string? Author)</c>; <c>CommentSummaryResult(IReadOnlyList&lt;CommentSummaryItem&gt; Items)</c>;
/// <c>CommentSummaryItem(string Path, int Line, string Issue, string Class, double? Confidence, string DecisionId)</c>;
/// <c>const int CommentTriageService.MaxSummaryRows = 50</c>. Bot logins match case-insensitively. A row that was not sent to the
/// classifier has an empty <c>DecisionId</c>; a row that was sent has the id of the decision it wrote.
/// </remarks>
public sealed class ArbiterSummaryCommentTriageTests : IDisposable
{
    private const string BotLogin = "kilo-code-bot";
    private const string HumanLogin = "dave";
    private const string Nitpick = "nitpick";
    private const string Real = "real";

    private const string KiloSummary =
        "## Code review summary\n" +
        "Three findings on this change.\n\n" +
        "| Severity | File | Line | Issue |\n" +
        "|---|---|---|---|\n" +
        "| warning | `src/Widget.cs` | L12 | Rename this variable to something clearer. |\n" +
        "| nit | `src/Gadget.cs` | 8-10 | Prefer a constant for the magic number. |\n" +
        "| warning | `src/Widget.cs:20` | 20 | The null check is missing before the dereference. |\n";

    private readonly TempDirectory _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Root => Path.Combine(_sandbox.Path, "repo");

    private string ArbiterDirectory => Path.Combine(_sandbox.Path, "artifacts", "arbiter");

    private static DateTimeOffset Now => new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Answers every comment question with one answer and confidence, and counts the calls.</summary>
    private sealed class CountingProvider(string answer, double confidence, bool throws = false) : IArbiterProvider
    {
        public int Calls { get; private set; }

        public Task<ArbiterStep1BatchResult> AskStep1Async(
            IReadOnlyList<ArbiterRule> rules,
            ArbiterFactSet facts,
            IReadOnlyDictionary<string, string>? step0Answers = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private void WriteFile(string relative, int lines = 30)
    {
        string full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Join("\n", Enumerable.Range(1, lines).Select(i => $"var v{i} = {i};")));
    }

    private void WriteKiloFiles()
    {
        WriteFile("src/Widget.cs");
        WriteFile("src/Gadget.cs");
    }

    private CommentTriageService Service(IArbiterProvider provider, IReadOnlyList<string>? botLogins) =>
        new(provider, Root, ArbiterDirectory, new FixedClock(Now), botLogins: botLogins);

    private static CommentSummaryRequest Summary(string? author, string body = KiloSummary) => new(body, author);

    /// <summary>Kilo's real shape: a header line, then a table of three rows with no inline threads, from a bot author the operator configured.</summary>
    [Fact]
    public async Task AConfiguredBotsSummaryTableYieldsOneClassifiedItemPerRow()
    {
        WriteKiloFiles();
        CountingProvider provider = new(Nitpick, 0.95);

        CommentSummaryResult result = await Service(provider, [BotLogin]).ClassifySummaryAsync(Summary(BotLogin));

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(["src/Widget.cs", "src/Gadget.cs", "src/Widget.cs"], result.Items.Select(item => item.Path).ToArray());
        Assert.Equal([12, 8, 20], result.Items.Select(item => item.Line).ToArray());
        Assert.All(result.Items, item => Assert.Equal(Nitpick, item.Class));
        Assert.All(result.Items, item => Assert.False(string.IsNullOrEmpty(item.DecisionId)));
        Assert.Equal(3, provider.Calls);
    }

    /// <summary>Each item keeps the issue text the row states, so the caller can show the worker what the bot asked.</summary>
    [Fact]
    public async Task EachItemCarriesTheIssueTextOfItsRow()
    {
        WriteKiloFiles();

        CommentSummaryResult result = await Service(new CountingProvider(Nitpick, 0.95), [BotLogin])
            .ClassifySummaryAsync(Summary(BotLogin));

        Assert.Equal("Rename this variable to something clearer.", result.Items[0].Issue);
        Assert.Equal("Prefer a constant for the magic number.", result.Items[1].Issue);
    }

    /// <summary>A human author is never classified: every item stays real and the provider is not asked.</summary>
    [Fact]
    public async Task AHumanAuthorsSummaryIsRealWithoutAnyProviderCall()
    {
        WriteKiloFiles();
        CountingProvider provider = new(Nitpick, 0.95);

        CommentSummaryResult result = await Service(provider, [BotLogin]).ClassifySummaryAsync(Summary(HumanLogin));

        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(Real, item.Class));
        Assert.Equal(0, provider.Calls);
    }

    /// <summary>An absent author is not assumed to be a bot: with bot logins configured, every item stays real.</summary>
    [Fact]
    public async Task AnAbsentAuthorIsRealWhenBotLoginsAreConfigured()
    {
        WriteKiloFiles();
        CountingProvider provider = new(Nitpick, 0.95);

        CommentSummaryResult result = await Service(provider, [BotLogin]).ClassifySummaryAsync(Summary(null));

        Assert.All(result.Items, item => Assert.Equal(Real, item.Class));
        Assert.Equal(0, provider.Calls);
    }

    /// <summary>With no bot logins configured, even a bot-looking author is fail-safe real: the default never classifies a summary.</summary>
    [Fact]
    public async Task WithNoBotLoginsConfiguredEverySummaryItemIsReal()
    {
        WriteKiloFiles();
        CountingProvider provider = new(Nitpick, 0.95);

        CommentSummaryResult result = await Service(provider, []).ClassifySummaryAsync(Summary(BotLogin));

        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(Real, item.Class));
        Assert.Equal(0, provider.Calls);
    }

    /// <summary>Bot logins match case-insensitively, as GitHub logins do.</summary>
    [Fact]
    public async Task ABotLoginMatchesCaseInsensitively()
    {
        WriteKiloFiles();

        CommentSummaryResult result = await Service(new CountingProvider(Nitpick, 0.95), [BotLogin])
            .ClassifySummaryAsync(Summary("KILO-CODE-BOT"));

        Assert.All(result.Items, item => Assert.Equal(Nitpick, item.Class));
    }

    /// <summary>The file, line and range spellings a bot writes all resolve to a path and a start line.</summary>
    [Fact]
    public async Task BacktickedFilesLineLettersRangesAndPathColonLinesAreTolerated()
    {
        WriteFile("src/Widget.cs");
        const string body =
            "| Severity | File | Line | Issue |\n" +
            "|---|---|---|---|\n" +
            "| warning | `src/Widget.cs` | L12 | First. |\n" +
            "| warning | `src/Widget.cs` | 12-14 | Second. |\n" +
            "| warning | `src/Widget.cs:20` | 20 | Third. |\n";

        CommentSummaryResult result = await Service(new CountingProvider(Nitpick, 0.95), [BotLogin])
            .ClassifySummaryAsync(new CommentSummaryRequest(body, BotLogin));

        Assert.Equal(
            [("src/Widget.cs", 12), ("src/Widget.cs", 12), ("src/Widget.cs", 20)],
            result.Items.Select(item => (item.Path, item.Line)).ToArray());
    }

    /// <summary>A malformed row means the table cannot be trusted, so the result has no items and the caller treats the whole comment as real.</summary>
    [Fact]
    public async Task AMalformedRowYieldsNoItems()
    {
        WriteKiloFiles();
        const string body =
            "| Severity | File | Line | Issue |\n" +
            "|---|---|---|---|\n" +
            "| warning | `src/Widget.cs` | L12 | First. |\n" +
            "| warning | `src/Widget.cs` |\n";

        CommentSummaryResult result = await Service(new CountingProvider(Nitpick, 0.95), [BotLogin])
            .ClassifySummaryAsync(new CommentSummaryRequest(body, BotLogin));

        Assert.Empty(result.Items);
    }

    /// <summary>A comment with no findings table has no items; it is a plain comment and the caller keeps it real.</summary>
    [Fact]
    public async Task ASummaryWithNoTableYieldsNoItems()
    {
        CommentSummaryResult result = await Service(new CountingProvider(Nitpick, 0.95), [BotLogin])
            .ClassifySummaryAsync(new CommentSummaryRequest("Looks good overall, two notes below.", BotLogin));

        Assert.Empty(result.Items);
    }

    /// <summary>Rows past the cap are kept real without being asked, so a long table costs at most the cap in provider calls.</summary>
    [Fact]
    public async Task RowsBeyondTheCapAreRealAndUnasked()
    {
        WriteFile("src/Widget.cs", lines: 80);
        string rows = string.Concat(Enumerable.Range(1, CommentTriageService.MaxSummaryRows + 5)
            .Select(i => $"| warning | `src/Widget.cs` | {i} | Issue {i}. |\n"));
        string body = "| Severity | File | Line | Issue |\n|---|---|---|---|\n" + rows;
        CountingProvider provider = new(Nitpick, 0.95);

        CommentSummaryResult result = await Service(provider, [BotLogin])
            .ClassifySummaryAsync(new CommentSummaryRequest(body, BotLogin));

        Assert.Equal(CommentTriageService.MaxSummaryRows + 5, result.Items.Count);
        Assert.Equal(CommentTriageService.MaxSummaryRows, provider.Calls);
        Assert.All(result.Items.Skip(CommentTriageService.MaxSummaryRows), item => Assert.Equal(Real, item.Class));
    }

    /// <summary>A nitpick below the floor is not trusted, so every row it answers stays real.</summary>
    [Fact]
    public async Task AnUnsureAnswerKeepsEveryRowReal()
    {
        WriteKiloFiles();

        CommentSummaryResult result = await Service(new CountingProvider(Nitpick, 0.6), [BotLogin])
            .ClassifySummaryAsync(Summary(BotLogin));

        Assert.All(result.Items, item => Assert.Equal(Real, item.Class));
    }

    /// <summary>A provider failure keeps every row real, and the call does not throw.</summary>
    [Fact]
    public async Task AProviderFailureKeepsEveryRowReal()
    {
        WriteKiloFiles();

        CommentSummaryResult result = await Service(new CountingProvider(Nitpick, 0.95, throws: true), [BotLogin])
            .ClassifySummaryAsync(Summary(BotLogin));

        Assert.Equal(3, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal(Real, item.Class));
    }

    /// <summary>A row whose cited file is missing stays real, while the rows whose files exist are still classified.</summary>
    [Fact]
    public async Task AMissingCitedFileKeepsOnlyThatRowReal()
    {
        WriteFile("src/Widget.cs");
        const string body =
            "| Severity | File | Line | Issue |\n" +
            "|---|---|---|---|\n" +
            "| warning | `src/Widget.cs` | 12 | Present file. |\n" +
            "| warning | `src/Missing.cs` | 4 | Absent file. |\n";

        CommentSummaryResult result = await Service(new CountingProvider(Nitpick, 0.95), [BotLogin])
            .ClassifySummaryAsync(new CommentSummaryRequest(body, BotLogin));

        Assert.Equal(Nitpick, result.Items[0].Class);
        Assert.Equal(Real, result.Items[1].Class);
    }

    /// <summary>A row sent to the classifier has the id of the decision it wrote, and that decision is in the log.</summary>
    [Fact]
    public async Task AClassifiedRowCitesItsDecisionRecord()
    {
        WriteKiloFiles();

        CommentSummaryResult result = await Service(new CountingProvider(Nitpick, 0.95), [BotLogin])
            .ClassifySummaryAsync(Summary(BotLogin));

        string decisionId = result.Items[0].DecisionId;
        Assert.Single(new DecisionLog(ArbiterDirectory).ReadAll(), record => record.Id == decisionId);
    }
}
