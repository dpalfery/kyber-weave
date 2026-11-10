using System.Text;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// The facts step-1 questions read on the hook route: a lens's diff summary and a
/// refutation's finding and surrounding code. A null here means the model answers blind,
/// so each test runs the real classifier, fact builder and <c>HookGitFacts.Enrich</c>
/// against a real repository from <see cref="RunStartProbe" />.
/// </summary>
public sealed class ArbiterStepOneFactsTests : IDisposable
{
    private const string FindingId = "security/key-in-argv";

    private readonly RunStartProbe _probe = RunStartProbe.Create();
    private readonly TempDirectory _home = new();

    public void Dispose()
    {
        _probe.Dispose();
        _home.Dispose();
    }

    [Fact]
    public void DiffSummaryListsEachChangedPathWithItsHunkHeaders()
    {
        _probe.Git("switch", "-c", "feature/summary", "origin/main");
        Write("src/Tracked.cs", "  one\n  two\n");
        _probe.Git("add", "-A");
        _probe.Git("commit", "-m", "feat: tracked");
        Write("src/Tracked.cs", "  one\n  two\n  three\n");
        Write("src/Untracked.cs", "  new\n");

        ArbiterFactSet facts = Enrich(Lens());

        Assert.True(facts.TryGet("review.diff-summary", out ArbiterFact? summary), "a lens spawn must get a diff summary.");
        Assert.Equal("src/Tracked.cs\n@@ -0,0 +1,3 @@\nsrc/Untracked.cs", summary!.Value);
        Assert.Equal(ArbiterFactLabel.Derived, summary.Label);
    }

    [Fact]
    public void DiffSummaryStaysAbsentWhenNothingChanged()
    {
        Assert.False(Enrich(Lens()).IsPresent("review.diff-summary"));
    }

    /// <summary>A body line can look like a file header, and must not move later hunks to another file.</summary>
    [Fact]
    public void DiffSummaryKeepsHunksOnTheirFileWhenABodyLineLooksLikeAFileHeader()
    {
        Write("query.sql", "-- one\n  keep\n  keep\n  keep\n-- two\n");
        _probe.Git("add", "-A");
        _probe.Git("commit", "-m", "chore: base");
        string baseCommit = _probe.GitOutput("rev-parse", "HEAD").Trim();
        Write("query.sql", "++ one\n  keep\n  keep\n  keep\n++ two\n");

        string? summary = GitFacts.GetDiffSummary(_probe.Root, baseCommit, ["query.sql"]);

        Assert.Equal("query.sql\n@@ -1 +1 @@\n@@ -5 +5 @@", summary);
    }

    [Fact]
    public void DiffSummaryNamesADeletedFileByItsOldPath()
    {
        Write("gone.txt", "  only\n");
        _probe.Git("add", "-A");
        _probe.Git("commit", "-m", "chore: base");
        string baseCommit = _probe.GitOutput("rev-parse", "HEAD").Trim();
        _probe.Git("rm", "gone.txt");

        string? summary = GitFacts.GetDiffSummary(_probe.Root, baseCommit, ["gone.txt"]);

        Assert.Equal("gone.txt\n@@ -1 +0,0 @@", summary);
    }

    [Fact]
    public void DiffSummaryStaysAbsentWhenTheBaseCannotBeRead()
    {
        Assert.Null(GitFacts.GetDiffSummary(_probe.Root, "no-such-ref", ["a.cs"]));
    }

    [Fact]
    public void RefutationCarriesTheFindingItNames()
    {
        ArbiterFactSet facts = Enrich(Refute("src/Key.cs", "30"));

        Assert.Equal(FindingId, Value(facts, "finding.id"));
        Assert.Equal("src/Key.cs", Value(facts, "finding.file"));
        Assert.Equal(30, Value(facts, "finding.line"));
        Assert.Equal("var key = args[0];\n\nUse(key);", Value(facts, "finding.excerpt"));
        Assert.Equal("The key leaks: argv is world readable.", Value(facts, "finding.claim"));
    }

    [Fact]
    public void RefutationIgnoresAFindingWithAnotherId()
    {
        string prompt = Refute("src/Key.cs", "30").Replace($"id: {FindingId}", "id: security/other", StringComparison.Ordinal);

        ArbiterFactSet facts = Enrich(prompt);

        Assert.False(facts.IsPresent("finding.file"));
        Assert.False(facts.IsPresent("finding.claim"));
        Assert.False(facts.IsPresent("file.surroundings"));
    }

    [Fact]
    public void SurroundingsAreTwentyLinesEitherSideOfTheFinding()
    {
        WriteNumberedLines("src/Key.cs", 60);

        object? surroundings = Value(Enrich(Refute("src/Key.cs", "30")), "file.surroundings");

        Assert.Equal(NumberedLines(10, 50), surroundings);
    }

    [Fact]
    public void SurroundingsStopAtTheStartOfTheFile()
    {
        WriteNumberedLines("src/Key.cs", 60);

        Assert.Equal(NumberedLines(1, 23), Value(Enrich(Refute("src/Key.cs", "3")), "file.surroundings"));
    }

    [Theory]
    [InlineData("../outside.cs", "1")]
    [InlineData("src/Missing.cs", "1")]
    [InlineData("src/Key.cs", "61")]
    [InlineData("src/Key.cs", "0")]
    [InlineData("src/Key.cs", "2147483647")]
    public void SurroundingsStayAbsentWhenTheFindingCannotBeLocated(string file, string line)
    {
        WriteNumberedLines("src/Key.cs", 60);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_probe.Root)!, "outside.cs"), "secret\n", Encoding.UTF8);

        Assert.False(Enrich(Refute(file, line)).IsPresent("file.surroundings"));
    }

    private static string Lens() => "KYBER-ARBITER: true\nLENS: correctness\n\nApply the lens to the change.";

    private static string Refute(string file, string line) =>
        $"""
        KYBER-ARBITER: true
        REFUTE: {FindingId}

        Argue this finding is wrong.

        - id: {FindingId}
          lens: security
          severity: major
          confidence: 8
          file: {file}
          line: {line}
          excerpt: |
            var key = args[0];

            Use(key);

          claim: The key leaks: argv is world readable.
          evidence: Read the call site.
          failure_scenario: Another user lists the process.
          suggestion: Read the key from stdin.
        - id: security/next
          file: src/Other.cs
          line: 1
        """;

    private static object? Value(ArbiterFactSet facts, string name)
    {
        Assert.True(facts.TryGet(name, out ArbiterFact? fact), $"'{name}' must be declared.");
        return fact!.Value;
    }

    private static string NumberedLines(int first, int last) =>
        string.Join('\n', Enumerable.Range(first, last - first + 1).Select(number => $"line {number}"));

    private void WriteNumberedLines(string relative, int count) =>
        Write(relative, NumberedLines(1, count) + "\n");

    private void Write(string relative, string text)
    {
        string path = Path.Combine(_probe.Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private ArbiterFactSet Enrich(string prompt)
    {
        ArbiterEvent ev = new()
        {
            Harness = "claude",
            Phase = "pre",
            Target = "review-lens",
            Prompt = prompt,
            HarnessCaller = "code-reviewer",
            Cwd = _probe.Root,
            IsDispatch = true,
        };

        TriggerClassification classification = TriggerClassifier.ClassifySingle(ev, "review-lens", prompt);
        ArbiterFactSet facts = TriggerFactBuilder.Build(classification, prompt, KyberWeaveConfig.ProductDefaults);
        return new HookGitFacts(_probe.Root, _home.Path).Enrich(facts, classification, ev);
    }
}
