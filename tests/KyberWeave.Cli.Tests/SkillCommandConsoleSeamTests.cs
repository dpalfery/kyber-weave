using KyberWeave.Cli.Commands.Skills;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Routing;
using KyberWeave.Tests.Fakes;
using Spectre.Console.Testing;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The skill commands print markup and tables on the Spectre console they were given and their JSON documents on the
/// standard-output console they were given, so a unit test reads each stream without a terminal and tells them apart.
/// </summary>
public sealed class SkillCommandConsoleSeamTests : IDisposable
{
    private readonly TestConsole _ansi = new();
    private readonly FakeConsole _standard = new();
    private readonly ScriptedSkills _skills = new();

    public SkillCommandConsoleSeamTests() => _ansi.Profile.Width = 200;

    public void Dispose() => _ansi.Dispose();

    private static readonly InMemoryGovernanceReaders Governance = new();

    private static Skill SkillNamed(string name, string description) =>
        Governance.Skills.Read(
            $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\nBody instructions.",
            $"/kw/skills/{name}/SKILL.md",
            $"/kw/skills/{name}");

    private static SkillSet OneSkill() =>
        new([SkillNamed("sql-generator", "Use when generating SQL queries for a Postgres database. Do NOT use for migrations.")]);

    // ------------------------------------------------------------------------------------ catalog

    [Fact]
    public void Catalog_ASetWithNoSkills_SaysSoOnTheAnsiConsoleAndExitsZero()
    {
        _skills.Set = new SkillSet([]);

        int exit = new CatalogCommand(_skills, _ansi, _standard)
            .Execute(null!, new CatalogSettings { Path = "/kw/none" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("No skills found under '/kw/none'.", _ansi.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _standard.Output);
    }

    [Fact]
    public void Catalog_ATable_GoesToTheAnsiConsole()
    {
        _skills.Set = OneSkill();

        int exit = new CatalogCommand(_skills, _ansi, _standard)
            .Execute(null!, new CatalogSettings(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("sql-generator", _ansi.Output, StringComparison.Ordinal);
        Assert.Contains("1 skill(s) catalogued.", _ansi.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _standard.Output);
    }

    [Fact]
    public void Catalog_Json_GoesToTheStandardOutputOnly()
    {
        _skills.Set = OneSkill();

        int exit = new CatalogCommand(_skills, _ansi, _standard)
            .Execute(null!, new CatalogSettings { Json = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("\"name\": \"sql-generator\"", _standard.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _ansi.Output);
    }

    // ------------------------------------------------------------------------------------ lint

    [Fact]
    public void Lint_ExplainPrintsEachSkillsScoreAndTheReportOnTheAnsiConsole()
    {
        _skills.Set = OneSkill();
        DiagnosticReport report = new();
        report.Add(new Diagnostic("KW-TEST-9", Severity.Warning, "vague", "sql-generator"));
        _skills.LintReport = report;

        int exit = new LintCommand(_skills, _ansi)
            .Execute(null!, new LintSettings { Explain = true, Format = "table" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("sql-generator", _ansi.Output, StringComparison.Ordinal);
        Assert.Contains("routing score", _ansi.Output, StringComparison.Ordinal);
        Assert.Contains("KW-TEST-9", _ansi.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_AnErrorFinding_ExitsOne()
    {
        DiagnosticReport report = new();
        report.Add(new Diagnostic("KW-TEST-9", Severity.Error, "collision", "sql-generator"));
        _skills.LintReport = report;

        int exit = new LintCommand(_skills, _ansi)
            .Execute(null!, new LintSettings { Format = "json" }, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("KW-TEST-9", _ansi.Output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------ new

    [Theory]
    [InlineData(SkillScaffoldStatus.InvalidName, 2, "Invalid name 'Bad Name'.")]
    [InlineData(SkillScaffoldStatus.AlreadyExists, 2, "Directory already exists: /kw/out/Bad Name")]
    [InlineData(SkillScaffoldStatus.Created, 0, "Created skill Bad Name at /kw/out/Bad Name")]
    public void New_SaysWhatHappenedOnTheAnsiConsole(SkillScaffoldStatus status, int expectedExit, string expected)
    {
        _skills.Scaffolded = new SkillScaffoldResult(status, "/kw/out/Bad Name");

        int exit = new NewCommand(_skills, _ansi)
            .Execute(null!, new NewSettings { Name = "Bad Name", Output = "/kw/out" }, CancellationToken.None);

        Assert.Equal(expectedExit, exit);
        Assert.Contains(expected, _ansi.Output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------ pack

    [Fact]
    public void Pack_NoSkillFile_IsRefusedOnTheAnsiConsole()
    {
        _skills.Packed = new SkillPackResult(SkillPackStatus.NoSkillFile, "", "", 0, new DiagnosticReport());

        int exit = new PackCommand(_skills, _ansi)
            .Execute(null!, new PackSettings { Path = "/kw/skills/x" }, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("No SKILL.md in '/kw/skills/x'.", _ansi.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_ValidationFailure_ListsEachErrorCodeFirst()
    {
        DiagnosticReport report = new();
        report.Add(new Diagnostic("KW-TEST-5", Severity.Error, "bad front matter", "x"));
        report.Add(new Diagnostic("KW-TEST-6", Severity.Info, "just a note", "x"));
        _skills.Packed = new SkillPackResult(SkillPackStatus.ValidationFailed, "x", "", 0, report);

        int exit = new PackCommand(_skills, _ansi)
            .Execute(null!, new PackSettings { Path = "/kw/skills/x" }, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("Refusing to pack", _ansi.Output, StringComparison.Ordinal);
        Assert.Contains("KW-TEST-5 bad front matter", _ansi.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("KW-TEST-6", _ansi.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_ASuccess_NamesTheArchive()
    {
        _skills.Packed = new SkillPackResult(SkillPackStatus.Packed, "x", "/kw/x.zip", 3, new DiagnosticReport());

        int exit = new PackCommand(_skills, _ansi)
            .Execute(null!, new PackSettings { Path = "/kw/skills/x" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Packed x", _ansi.Output, StringComparison.Ordinal);
        Assert.Contains("/kw/x.zip (3 bundled file(s))", _ansi.Output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------ route

    [Fact]
    public void Route_NoSkills_IsRefusedOnTheAnsiConsole()
    {
        _skills.Set = new SkillSet([]);

        int exit = new RouteCommand(_skills, _ansi, _standard)
            .Execute(null!, new RouteSettings { SkillsPath = "/kw/none", Prompt = "x" }, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("No skills found under '/kw/none'.", _ansi.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_NoPromptAndNoEvalFile_IsAUsageError()
    {
        _skills.Set = OneSkill();

        int exit = new RouteCommand(_skills, _ansi, _standard)
            .Execute(null!, new RouteSettings(), CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("Provide a prompt, or use --eval <file>.", _ansi.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_APrompt_PrintsTheRankingOnTheAnsiConsole()
    {
        _skills.Set = OneSkill();

        int exit = new RouteCommand(_skills, _ansi, _standard)
            .Execute(null!, new RouteSettings { Prompt = "generate sql for postgres" }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Prompt: \"generate sql for postgres\"", _ansi.Output, StringComparison.Ordinal);
        Assert.Contains("sql-generator", _ansi.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _standard.Output);
    }

    [Fact]
    public void Route_APromptAsJson_GoesToTheStandardOutputOnly()
    {
        _skills.Set = OneSkill();

        int exit = new RouteCommand(_skills, _ansi, _standard)
            .Execute(null!, new RouteSettings { Prompt = "generate sql for postgres", Json = true }, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("\"prompt\": \"generate sql for postgres\"", _standard.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _ansi.Output);
    }

    [Fact]
    public void Route_AMissingEvalFile_IsRefusedOnTheAnsiConsole()
    {
        _skills.Set = OneSkill();
        _skills.Eval = null;

        int exit = new RouteCommand(_skills, _ansi, _standard)
            .Execute(null!, new RouteSettings { EvalFile = "/kw/eval.yml" }, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("Eval file not found: /kw/eval.yml", _ansi.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_AnEvalFileThatDoesNotParse_PrintsTheParseMessage()
    {
        _skills.Set = OneSkill();
        _skills.EvalFailure = new RoutingEvalParseException("cases: expected a list");

        int exit = new RouteCommand(_skills, _ansi, _standard)
            .Execute(null!, new RouteSettings { EvalFile = "/kw/eval.yml" }, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("cases: expected a list", _ansi.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Route_AnEvalRun_PrintsTheAccuracyOnTheAnsiConsoleAndItsJsonOnTheStandardOutput()
    {
        _skills.Set = OneSkill();
        _skills.Eval = new RoutingEvalSummary([]);

        int table = new RouteCommand(_skills, _ansi, _standard)
            .Execute(null!, new RouteSettings { EvalFile = "/kw/eval.yml" }, CancellationToken.None);

        TestConsole jsonAnsi = new();
        FakeConsole jsonStandard = new();
        int json = new RouteCommand(_skills, jsonAnsi, jsonStandard)
            .Execute(null!, new RouteSettings { EvalFile = "/kw/eval.yml", Json = true }, CancellationToken.None);

        Assert.Equal(1, table);
        Assert.Contains("Routing accuracy:", _ansi.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, _standard.Output);
        Assert.Equal(1, json);
        Assert.Contains("\"accuracy\":", jsonStandard.Output, StringComparison.Ordinal);
        Assert.Equal(string.Empty, jsonAnsi.Output);
    }

    // ------------------------------------------------------------------------------------ fake

    private sealed class ScriptedSkills : ISkillGovernanceService
    {
        public SkillSet Set { get; set; } = new([]);

        public DiagnosticReport LintReport { get; set; } = new();

        public RoutingEvalSummary? Eval { get; set; }

        public Exception? EvalFailure { get; set; }

        public SkillPackResult? Packed { get; set; }

        public SkillScaffoldResult? Scaffolded { get; set; }

        public DiagnosticReport Validate(string path) => new();

        public SkillLintResult Lint(string path, int minDescriptionScore) => new(LintReport, Set);

        public DiagnosticReport Scan(string path) => new();

        public SkillSet LoadSet(string path) => Set;

        public RoutingEvalSummary? Evaluate(string evalFile, IRoutingStrategy strategy, SkillSet skills) =>
            EvalFailure is null ? Eval : throw EvalFailure;

        public SkillPackResult Pack(string skillDirectory, string? output, bool skipValidation) => Packed!;

        public SkillScaffoldResult Scaffold(
            string name, string outputDirectory, string templateName, bool includeLicense, bool includeMetadata) =>
            Scaffolded!;
    }
}
