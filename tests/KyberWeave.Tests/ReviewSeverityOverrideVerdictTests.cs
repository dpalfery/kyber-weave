using System.Text.Json;
using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Cli;
using Xunit;
using static KyberWeave.Tests.ReviewDiagnosticAssert;

namespace KyberWeave.Tests;

/// <summary>
/// <c>review verdict</c> with a host severity override: the overridden severity is the one the verdict rules, the
/// fix-level routing and <c>findings.json</c> read. Runs in process against a temporary repository; no model.
/// </summary>
public sealed class ReviewSeverityOverrideVerdictTests : IDisposable
{
    private const string FixRound = "KW-REVIEW-056";

    private readonly TempDirectory _temp = new();
    private readonly string _repo;
    private readonly string _seats;

    public ReviewSeverityOverrideVerdictTests()
    {
        _repo = Path.Combine(_temp.Path, "repo");
        TestGitRepositoryTemplate.CreateReviewGatesCouncilRepository(_repo);
        Directory.CreateDirectory(Path.Combine(_repo, ".kyber-weave"));
        _seats = Path.Combine(_temp.Path, "seats");
        Directory.CreateDirectory(_seats);
        File.WriteAllText(Path.Combine(_repo, "REVIEW.md"), """
            # Review guidance

            ## Severity overrides

            | Id | Finding class | Severity | Applies to | Why |
            |---|---|---|---|---|
            | SO-1 | File or type placed against the documented structure | major | model-placement | Owner's call |
            | SO-2 | Wording of a log message | nitpick | | Style |
            """);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AMinorRaisedToMajorByOverrideBlocksAtOneMajorAndIsFixedAtLevelMajor()
    {
        UsePolicy("    major-count-blocks: 1\n    fix-level: major\n");
        WriteSeat("model-placement", Seat("model-placement/wrong-folder", "minor", "SO-1"));
        string findings = Path.Combine(_temp.Path, "findings.json");

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, "--out", findings, .. NotSeatedExcept("model-placement")]);

        Assert.Equal(1, run.Result);
        Assert.Contains(
            "'model-placement/wrong-folder': severity set by host override SO-1 (minor → major; cited by the seat, which graded it differently).",
            MessagesWithCode(run.Output, SeverityOverrides.AppliedCode));
        Assert.Contains("fix: model-placement/wrong-folder (major) src/Foo.cs:12", MessagesWithCode(run.Output, FixRound));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(findings));
        JsonElement finding = document.RootElement.GetProperty("findings")[0];
        Assert.Equal("major", finding.GetProperty("severity").GetString());
        Assert.Equal("SO-1", finding.GetProperty("override").GetString());
        Assert.Equal("seat", finding.GetProperty("overrideSource").GetString());
        Assert.Equal("minor", finding.GetProperty("seatSeverity").GetString());
    }

    [Fact]
    public void WithoutTheOverrideTheSameMinorIsFollowUpWorkAtLevelMajor()
    {
        UsePolicy("    major-count-blocks: 1\n    fix-level: major\n");
        WriteSeat("model-placement", Seat("model-placement/wrong-folder", "minor", null));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("model-placement")]);

        Assert.Equal(0, run.Result);
        Assert.Contains("follow-up: model-placement/wrong-folder (minor) src/Foo.cs:12", MessagesWithCode(run.Output, FixRound));
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("major")]
    [InlineData("minor")]
    public void AMajorLoweredToNitpickByOverrideIsDroppedBelowLevelNitpick(string level)
    {
        UsePolicy($"    fix-level: {level}\n");
        WriteSeat("supportability", Seat("supportability/log-wording", "major", "SO-2"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("supportability")]);

        Assert.Equal(0, run.Result);
        Assert.Contains(MessagesWithCode(run.Output, "KW-REVIEW-055"), line => line.StartsWith("Dropped as nitpick", StringComparison.Ordinal) && line.Contains("supportability/log-wording", StringComparison.Ordinal));
        Assert.Contains($"fix round: none (fix level {level}); review clean: proceed to archive.", MessagesWithCode(run.Output, FixRound));
    }

    [Fact]
    public void AMajorLoweredToNitpickByOverrideIsFixedAtLevelNitpick()
    {
        UsePolicy("    fix-level: nitpick\n");
        WriteSeat("supportability", Seat("supportability/log-wording", "major", "SO-2"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("supportability")]);

        IReadOnlyList<string> lines = MessagesWithCode(run.Output, FixRound);
        Assert.Contains("fix: supportability/log-wording (nitpick) src/Foo.cs:12", lines);
        Assert.Contains("fix round: 1 item(s) at fix level nitpick; fix these, then run a verifier cycle.", lines);
    }

    [Fact]
    public void AnUnknownCitedIdKeepsTheSeatsSeverityWithAWarning()
    {
        UsePolicy("    fix-level: major\n");
        WriteSeat("model-placement", Seat("model-placement/wrong-folder", "minor", "SO-7"));

        CapturedConsoleExecution<int> run = Verdict(["--seats", _seats, .. NotSeatedExcept("model-placement")]);

        AssertDiagnostic(run.Output, SeverityOverrides.AppliedCode, "Warning", "SO-7");
        Assert.Contains("follow-up: model-placement/wrong-folder (minor) src/Foo.cs:12", MessagesWithCode(run.Output, FixRound));
    }

    private void UsePolicy(string extra) =>
        File.WriteAllText(
            Path.Combine(_repo, ".kyber-weave", "kyber-weave.yml"),
            "ontology:\n  docs-root: docs\nreview:\n  policy:\n    strictness: balanced\n" + extra);

    private CapturedConsoleExecution<int> Verdict(params string[] options)
    {
        List<string> arguments = ["review", "verdict", _repo, .. options, "--format", "json"];
        return ProcessConsoleCapture.Run(() => CreateApp().Run([.. arguments]));
    }

    private static string[] NotSeatedExcept(params string[] seated) =>
        [.. ReviewLensCatalogue.Names
            .Where(name => !seated.Contains(name, StringComparer.Ordinal))
            .SelectMany(name => new[] { "--not-seated", $"{name}=not run in this test" })];

    private static string Seat(string id, string severity, string? cited)
    {
        string lens = id[..id.IndexOf('/', StringComparison.Ordinal)];
        List<string> lines =
        [
            "```yaml",
            $"- id: {id}",
            $"  lens: {lens}",
            $"  severity: {severity}",
            "  confidence: 9",
            "  file: src/Foo.cs",
            "  line: 12",
            "  excerpt: \"if (i < count)\"",
            "  claim: The type sits outside its documented layer.",
            "  evidence: src/Foo.cs",
            "  failure_scenario: A reader looks for it in its layer and does not find it.",
            "  suggestion: Move it.",
        ];
        if (cited is not null)
            lines.Add($"  override: {cited}");
        lines.AddRange(["```", "Discovery: codegraph 2, docs 0, direct reads 0", "REVIEW.md sections checked: [Ground rules]"]);
        return string.Join('\n', lines) + "\n";
    }

    private void WriteSeat(string lens, string content) => File.WriteAllText(Path.Combine(_seats, lens + ".md"), content);

    private static CommandApp CreateApp()
    {
        CommandApp app = new();
        app.Configure(config =>
        {
            config.SetApplicationName("kyber-weave");
            config.AddBranch("review", review =>
                review.AddCommand<ReviewVerdictCommand>("verdict").WithDescription(ReviewVerdictCommand.Description));
        });
        return app;
    }
}
