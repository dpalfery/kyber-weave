using System.Security;
using System.Text.Json;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The RED judgment for test-authoring task gates. The classifier cases read fake compiler and test-runner output
/// (the exact shapes Roslyn, MSBuild, the .NET test runners and tsc print). The command cases that build a real tiny
/// C# project with <c>dotnet build</c> live in the integration project, so the missing-symbol codes there are the
/// compiler's own.
/// </summary>
public sealed class ReviewGatesRedModeTests : IDisposable
{
    private readonly InMemoryReviewServices _services = new();

    private const string TestFile = "tests/Widget.Tests/WidgetTests.cs";

    public void Dispose() => _services.Dispose();

    private static RedOutcome Judge(string output, int exitCode = 1, params string[] ownFiles) =>
        RedClassifier.Evaluate(
            [new RedGateObservation("dotnet-build", "dotnet build tests/Widget.Tests", exitCode, output, RepositoryRoot: "/work/repo")],
            ownFiles.Length > 0 ? ownFiles : [TestFile]);

    private static string CSharpError(string file, string code, string message) =>
        $"/work/repo/{file}(12,17): error {code}: {message} [/work/repo/tests/Widget.Tests/Widget.Tests.csproj]";

    [Fact]
    public void ABuildThatFailsOnlyOnMissingSymbolsInTheTasksTestFilesIsValidRed()
    {
        string output = string.Join('\n',
            CSharpError(TestFile, "CS0246", "The type or namespace name 'WidgetRepository' could not be found (are you missing a using directive or an assembly reference?)"),
            CSharpError(TestFile, "CS0103", "The name 'Frobnicate' does not exist in the current context"),
            CSharpError(TestFile, "CS1061", "'Widget' does not contain a definition for 'Colour' and no accessible extension method 'Colour' accepting a first argument of type 'Widget' could be found"),
            CSharpError(TestFile, "CS0117", "'WidgetDto' does not contain a definition for 'Id'"),
            CSharpError(TestFile, "CS0234", "The type or namespace name 'Dtos' does not exist in the namespace 'Widget' (are you missing an assembly reference?)"),
            "Build FAILED.");

        RedOutcome outcome = Judge(output);

        Assert.True(outcome.Passed, outcome.Reason);
        Assert.Equal(["WidgetRepository", "Frobnicate", "Widget.Colour", "WidgetDto.Id", "Dtos"], outcome.MissingSymbols);
        Assert.Equal(
            "RED_EVIDENCE: dotnet build tests/Widget.Tests — compile: missing WidgetRepository, Frobnicate, Widget.Colour, WidgetDto.Id, Dtos",
            outcome.EvidenceLine);
    }

    [Fact]
    public void AnUnusedUsingErrorBesideAMissingSymbolInTheTasksFileDoesNotDisqualifyTheRun()
    {
        string output = string.Join('\n',
            CSharpError(TestFile, "CS0246", "The type or namespace name 'WidgetRepository' could not be found"),
            CSharpError(TestFile, "IDE0005", "Using directive is unnecessary. (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/style-rules/ide0005)"),
            "Build FAILED.");

        RedOutcome outcome = Judge(output);

        Assert.True(outcome.Passed, outcome.Reason);
        Assert.Equal(["WidgetRepository"], outcome.MissingSymbols);
    }

    [Fact]
    public void AnUnusedUsingErrorWithNoMissingSymbolIsRefusedAndNamesItsFile()
    {
        RedOutcome outcome = Judge(CSharpError(TestFile, "IDE0005", "Using directive is unnecessary.") + "\nBuild FAILED.");

        Assert.False(outcome.Passed);
        Assert.Contains("IDE0005 in /work/repo/" + TestFile, outcome.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("no source file", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildErrorThatIsNotACompilerErrorIsQuotedInTheReason()
    {
        string output = "/work/repo/Directory.Build.props(4,3): error MSB4057: The target \"Frobnicate\" does not exist in the project.\nBuild FAILED.";

        RedOutcome outcome = Judge(output);

        Assert.False(outcome.Passed);
        Assert.Contains("MSB4057", outcome.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tests", "bash 5-Test/run-task-tests.sh 'FullyQualifiedName~X' out", true)]
    [InlineData("ts-test", "node run.js", true)]
    [InlineData("dotnet-build", "dotnet build App.sln", false)]
    [InlineData("build", "dotnet test App.sln", true)]
    public void AGateIsATestGateByItsCommandOrByTheHostsNameForIt(string id, string command, bool expected) =>
        Assert.Equal(expected, RedClassifier.IsTestGate(id, command));

    [Fact]
    public void ADuplicatedErrorFromTwoTargetFrameworksIsListedOnce()
    {
        string line = CSharpError(TestFile, "CS0246", "The type or namespace name 'WidgetRepository' could not be found");

        RedOutcome outcome = Judge(line + "\n" + line);

        Assert.Equal(["WidgetRepository"], outcome.MissingSymbols);
    }

    [Theory]
    [InlineData("CS1002", "; expected")]
    [InlineData("CS1503", "Argument 1: cannot convert from 'string' to 'int'")]
    [InlineData("CS7036", "There is no argument given that corresponds to the required parameter 'id' of 'Widget.Widget(int)'")]
    [InlineData("CS0029", "Cannot implicitly convert type 'string' to 'int'")]
    public void AnyOtherCompilerErrorBesideAMissingSymbolIsNotRed(string code, string message)
    {
        string output = CSharpError(TestFile, "CS0246", "The type or namespace name 'WidgetRepository' could not be found") + "\n" + CSharpError(TestFile, code, message);

        RedOutcome outcome = Judge(output);

        Assert.False(outcome.Passed);
        Assert.Contains(code, outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(outcome.EvidenceLine);
    }

    [Fact]
    public void AMissingSymbolInAFileTheTaskDidNotChangeIsNotRed()
    {
        RedOutcome outcome = Judge(CSharpError("src/Widget/Other.cs", "CS0246", "The type or namespace name 'Gone' could not be found"));

        Assert.False(outcome.Passed);
        Assert.Contains("did not change", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildErrorThatNamesNoSourceFileIsNotRed()
    {
        RedOutcome outcome = Judge("MSBUILD : error MSB1009: Project file does not exist.");

        Assert.False(outcome.Passed);
    }

    [Fact]
    public void TestsThatRanAndFailedAreValidRedAndNameTheFailingTestsAndTheAssertion()
    {
        const string output = """
            failed Widget.Tests.WidgetTests.Render_PlacesPanelLeftOfChart (70ms)
              Assert.Equal() Failure: Values differ
              Expected: 2
              Actual:   1

            Test run summary: Failed!
              total: 4
              failed: 1
              succeeded: 3
              skipped: 0
            """;

        RedOutcome outcome = Judge(output);

        Assert.True(outcome.Passed, outcome.Reason);
        Assert.Equal(["Widget.Tests.WidgetTests.Render_PlacesPanelLeftOfChart"], outcome.FailingTests);
        Assert.Equal(
            "RED_EVIDENCE: dotnet build tests/Widget.Tests — Widget.Tests.WidgetTests.Render_PlacesPanelLeftOfChart — Assert.Equal() Failure: Values differ",
            outcome.EvidenceLine);
    }

    [Fact]
    public void TheOlderRunnerSummaryIsReadToo()
    {
        const string output = "  Failed Widget.Tests.WidgetTests.Throws [3 ms]\n  Assert.Throws() Failure\nFailed!  - Failed:     1, Passed:     2, Skipped:     0, Total:     3, Duration: 12 ms";

        Assert.True(Judge(output).Passed);
    }

    [Fact]
    public void TestsThatAllPassAreNotRed()
    {
        RedOutcome outcome = Judge("Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 10 ms", exitCode: 0);

        Assert.False(outcome.Passed);
        Assert.Contains("every test passed", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RunsThatShowNothingFailingAreNotRed()
    {
        Assert.False(Judge("formatted 3 files", exitCode: 0).Passed);
        Assert.False(Judge("something went wrong", exitCode: 3).Passed);
    }

    [Fact]
    public void TypeScriptMissingSymbolsAreReadFromTscOutput()
    {
        const string output = """
            tests/widget.test.ts(4,10): error TS2304: Cannot find name 'WidgetPanel'.
            tests/widget.test.ts(9,22): error TS2339: Property 'colour' does not exist on type 'Widget'.
            tests/widget.test.ts(1,25): error TS2307: Cannot find module '../src/widget' or its corresponding type declarations.
            """;

        RedOutcome outcome = Judge(output, ownFiles: "tests/widget.test.ts");

        Assert.True(outcome.Passed, outcome.Reason);
        Assert.Equal(["WidgetPanel", "Widget.colour", "../src/widget"], outcome.MissingSymbols);
    }

    [Fact]
    public void ATypeScriptTypeMismatchIsNotRed()
    {
        Assert.False(Judge("tests/widget.test.ts(4,10): error TS2322: Type 'string' is not assignable to type 'number'.", 1, "tests/widget.test.ts").Passed);
    }

    [Fact]
    public void SiblingSymbolsAreReportedOnlyWhenEveryErrorIsAMissingSymbolOutsideTheTasksFiles()
    {
        RedGateObservation gate = new(
            "dotnet-build",
            "dotnet build",
            1,
            CSharpError("tests/Widget.Tests/SiblingTests.cs", "CS0246", "The type or namespace name 'WidgetRepository' could not be found"),
            RepositoryRoot: "/work/repo");

        Assert.Equal(["WidgetRepository"], RedClassifier.SiblingSymbols(gate, ["src/Widget/Panel.cs"]));
        Assert.Null(RedClassifier.SiblingSymbols(gate, ["tests/Widget.Tests/SiblingTests.cs"]));
        Assert.Null(RedClassifier.SiblingSymbols(gate with { Output = gate.Output + "\n" + CSharpError("tests/Widget.Tests/SiblingTests.cs", "CS1002", "; expected") }, ["src/Widget/Panel.cs"]));
        Assert.Null(RedClassifier.SiblingSymbols(gate with { ExitCode = 0 }, ["src/Widget/Panel.cs"]));
    }

    // ---- the first live run's reports (fixtures), and the runner patterns -----------------------------

    private static string Fixture(string name)
    {
        using Stream stream = typeof(ReviewGatesRedModeTests).Assembly.GetManifestResourceStream("red-gate-reports/" + name)
            ?? throw new FileNotFoundException("No embedded fixture named " + name);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string GateSummary(string fixture, string gateId)
    {
        using JsonDocument report = JsonDocument.Parse(Fixture(fixture));
        return report.RootElement.GetProperty("gates").EnumerateArray()
            .Single(gate => gate.GetProperty("id").GetString() == gateId).GetProperty("summary").GetString()!;
    }

    [Fact]
    public void ARunnerCrashWithNoFailingTestIsNamedAsInfrastructureNotRed()
    {
        // t2 of the live run: dotnet test on a project that could not build.
        RedOutcome outcome = Judge(GateSummary("t2-task-gate.json", "tests"), 1);

        Assert.False(outcome.Passed);
        Assert.Contains("infrastructure failure, not RED", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroTestsRanIsNamedWithTheFilterAndTheProjectTheHostGatesDoNotCover()
    {
        // t1 of the live run: exit code 8, the filter matched nothing because the class is in another test project.
        string root = Path.Combine(_services.Root, "zero-root");
        _services.Files.CreateDirectory(Path.Combine(root, "tests/Other.IntegrationTests"));
        _services.Files.WriteAllText(Path.Combine(root, "tests/Other.IntegrationTests/Other.IntegrationTests.csproj"), "<Project />");
        const string file = "tests/Other.IntegrationTests/JobTests.cs";
        _services.Files.WriteAllText(Path.Combine(root, file), "// test");

        RedOutcome outcome = RedClassifier.Evaluate(
            [new RedGateObservation("tests", "dotnet test --filter FullyQualifiedName~JobTests", 8, GateSummary("t1-task-gate.json", "tests"))],
            [file],
            projectOf => _services.ProjectFiles.NearestProjectFile(root, projectOf));

        Assert.False(outcome.Passed);
        Assert.Contains("zero tests", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName~JobTests", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("does not cover tests/Other.IntegrationTests/Other.IntegrationTests.csproj", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaywrightTestsThatRanAndFailedAreRedAndAServerThatDidNotStartIsNot()
    {
        const string failed = """
            Running 3 tests using 1 worker

              1) [chromium] › 1-Presentation/admin-dashboard/tests/job-status-cards.spec.ts:7:5 › shows four status cards ───

                Error: expect(locator).toHaveCount(expected)

                Expected: 4
                Received: 0

              1 failed
              2 passed (6.1s)
            """;
        RedOutcome red = Judge(failed, 1);
        Assert.True(red.Passed, red.Reason);
        Assert.Contains("shows four status cards", red.EvidenceLine, StringComparison.Ordinal);
        Assert.Contains("expect(locator).toHaveCount", red.EvidenceLine, StringComparison.Ordinal);

        RedOutcome infrastructure = Judge("Error: Timed out waiting 60000ms from config.webServer.", 1);
        Assert.False(infrastructure.Passed);
        Assert.Contains("web server", infrastructure.Reason, StringComparison.Ordinal);

        RedOutcome none = Judge("Error: No tests found", 1);
        Assert.False(none.Passed);
        Assert.Contains("zero tests", none.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pytest tests/test_widget.py", "tests/test_widget.py::test_panel FAILED\nFAILED tests/test_widget.py::test_panel - AssertionError: assert 1 == 2\n=========== 1 failed, 2 passed in 0.12s ===========", "tests/test_widget.py::test_panel")]
    [InlineData("cargo test widget", "test widget::panel ... FAILED\ntest result: FAILED. 2 passed; 1 failed; 0 ignored; 0 measured; 0 filtered out", "widget::panel")]
    [InlineData("go test ./widget", "--- FAIL: TestPanel (0.00s)\n    panel_test.go:12: got 1, want 2\nFAIL\tgithub.com/x/widget\t0.004s", "TestPanel")]
    [InlineData("npx vitest run widget", "FAIL  tests/widget.test.ts > panel renders\nAssertionError: expected 1 to be 2\n Tests  1 failed | 2 passed (3)", "tests/widget.test.ts > panel renders")]
    public void EachRunnersFailingSummaryIsRedAndNamesTheTest(string command, string output, string testName)
    {
        RedOutcome outcome = RedClassifier.Evaluate([new RedGateObservation("tests", command, 1, output.Replace("\\n", "\n", StringComparison.Ordinal))], [TestFile]);

        Assert.True(outcome.Passed, outcome.Reason);
        Assert.Contains(testName, outcome.EvidenceLine, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pytest tests", "============ no tests ran in 0.01s ============", 5)]
    [InlineData("cargo test nothing", "running 0 tests\ntest result: ok. 0 passed; 0 failed", 0)]
    [InlineData("go test ./none", "?   \tgithub.com/x/none\t[no test files]", 0)]
    public void EachRunnersZeroTestsOutputIsNotRed(string command, string output, int exit)
    {
        RedOutcome outcome = RedClassifier.Evaluate([new RedGateObservation("tests", command, exit == 0 ? 1 : exit, output.Replace("\\n", "\n", StringComparison.Ordinal))], [TestFile]);

        Assert.False(outcome.Passed);
        Assert.Contains("zero tests", outcome.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet test tests/Widget.Tests", true)]
    [InlineData("npx playwright test --project=chromium", true)]
    [InlineData("npm run test", true)]
    [InlineData("pytest -q", true)]
    [InlineData("dotnet build tests/Widget.Tests", false)]
    [InlineData("dotnet format whitespace --verify-no-changes", false)]
    public void TheTestCommandMarkersSeparateTestGatesFromBuildGates(string command, bool isTests) =>
        Assert.Equal(isTests, RedClassifier.IsTestCommand(command));

    private static PlannedTaskGate Planned(string id, params string[] run) => new(id, true, 60, [], run);

    private static (RedOutcome Outcome, DiagnosticReport Report) Apply(
        IReadOnlyList<(string Id, string Command, int Exit, string Output)> gates, params string[] changed)
    {
        GateReport ran = new(
            GateReport.CurrentSchema,
            [.. gates.Select(gate => new GateResult(gate.Id, true, gate.Exit, "ran"))]);
        DiagnosticReport report = new();
        (_, RedOutcome outcome) = ReviewGateReporting.ApplyRed(
            ran,
            [.. gates.Select(gate => Planned(gate.Id, gate.Command.Split(' ')))],
            gates.ToDictionary(gate => gate.Id, gate => gate.Output, StringComparer.Ordinal),
            changed,
            _ => null,
            report);
        return (outcome, report);
    }

    [Fact]
    public void GatesThatRunNoTestsAndAllPassAreRedNotApplicableAndNameTheGates()
    {
        (RedOutcome outcome, DiagnosticReport report) = Apply(
            [("dash-lint", "npm run --prefix dash lint", 0, "ok"), ("dash-typecheck", "npm run --prefix dash typecheck", 0, "ok")],
            "dash/tests/foo.test.ts");

        Assert.True(outcome.Passed, outcome.Reason);
        Assert.Contains("dash-lint", outcome.EvidenceLine, StringComparison.Ordinal);
        Assert.Contains("dash-typecheck", outcome.EvidenceLine, StringComparison.Ordinal);
        Assert.Contains(report.Items, item => item.Code == ReviewGateReporting.RedAccepted);
        Assert.DoesNotContain(report.Items, item => item.Code == ReviewGateReporting.RedRejected);
    }

    [Fact]
    public void ARelativeCompilerPathMatchesTheTasksOwnFileAndIsCompileRed()
    {
        (RedOutcome outcome, DiagnosticReport report) = Apply(
            [("dash-lint", "npm run --prefix dash lint", 0, "ok"),
             ("dash-typecheck", "npm run --prefix dash typecheck", 2, "tests/foo.test.ts(3,10): error TS2305: Module '\"../src/x\"' has no exported member 'Gadget'.")],
            "dash/tests/foo.test.ts");

        Assert.True(outcome.Passed, outcome.Reason);
        Assert.Contains("Gadget", outcome.MissingSymbols);
        Assert.Contains(report.Items, item => item.Code == ReviewGateReporting.RedAccepted);
    }

    [Fact]
    public void ARelativeCompilerPathInAFileTheTaskDidNotChangeIsRejected()
    {
        (RedOutcome outcome, DiagnosticReport report) = Apply(
            [("dash-typecheck", "npm run --prefix dash typecheck", 2, "tests/foo.test.ts(3,10): error TS2305: Module '\"../src/x\"' has no exported member 'Gadget'.")],
            "dash/tests/other.test.ts");

        Assert.False(outcome.Passed);
        Assert.Contains(report.Items, item => item.Code == ReviewGateReporting.RedRejected);
    }

    [Fact]
    public void ATestGateThatPassesIsStillRejectedWhateverElseIsPlanned()
    {
        (RedOutcome outcome, DiagnosticReport report) = Apply(
            [("dash-lint", "npm run --prefix dash lint", 0, "ok"),
             ("unit-tests", "dotnet test tests/Widget.Tests", 0, "Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 10 ms")],
            TestFile);

        Assert.False(outcome.Passed);
        Assert.Contains(report.Items, item => item.Code == ReviewGateReporting.RedRejected);
    }

    [Theory]
    [InlineData("dash/xtests/foo.test.ts", null, false)]
    [InlineData("dash/tests/foo.test.ts", null, false)]
    [InlineData("tests/foo.test.ts", null, true)]
    [InlineData("dash/xtests/foo.test.ts", "dash", false)]
    [InlineData("dash/tests/foo.test.ts", "dash", true)]
    [InlineData("tests/foo.test.ts", "dash", false)]
    public void ARelativePathMatchesOnASegmentBoundaryOnly(string own, string? workingDirectory, bool expected) =>
        Assert.Equal(expected, RedClassifier.IsOwnFile("tests/foo.test.ts", [own], workingDirectory));

    [Theory]
    [InlineData("npx tsc --noEmit", "tests/foo.test.ts", "dash/tests/foo.test.ts")]
    [InlineData("npm run --prefix dash/tray/ui typecheck", "src/App.tsx", "dash/web/src/App.tsx")]
    [InlineData("npm run --prefix dash typecheck", "tests/foo.test.ts", "tests/foo.test.ts")]
    public void ASiblingRelativeCompilerPathWithoutAMatchingGateDirectoryIsRejected(
        string command, string reportedFile, string ownFile)
    {
        (RedOutcome outcome, DiagnosticReport report) = Apply(
            [("typecheck", command, 2, $"{reportedFile}(3,10): error TS2305: Module '\"../src/x\"' has no exported member 'Gadget'.")],
            ownFile);

        Assert.False(outcome.Passed);
        Assert.Contains(report.Items, item => item.Code == ReviewGateReporting.RedRejected);
    }

    [Fact]
    public void ACompilerPathRelativeToPlannedWorkingDirectoryIsCompileRed()
    {
        PlannedTaskGate planned = new("dash-typecheck", true, 60, [], ["npm", "run", "typecheck"], WorkingDirectory: "dash");
        GateReport ran = new(GateReport.CurrentSchema, [new GateResult("dash-typecheck", true, 2, "ran")]);
        DiagnosticReport report = new();

        (_, RedOutcome outcome) = ReviewGateReporting.ApplyRed(
            ran,
            [planned],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["dash-typecheck"] = "tests/foo.test.ts(3,10): error TS2305: Module '\"../src/x\"' has no exported member 'Gadget'.",
            },
            ["dash/tests/foo.test.ts"],
            _ => null,
            report);

        Assert.True(outcome.Passed, outcome.Reason);
        Assert.Contains("Gadget", outcome.MissingSymbols);
    }

    [Theory]
    [InlineData("/tmp/other/dash/tests/foo.test.ts", null, false)]
    [InlineData("/dash/tests/foo.test.ts", null, false)]
    [InlineData("/tmp/other/dash/tests/foo.test.ts/", null, false)]
    [InlineData("/tmp/other/dash/tests/foo.test.ts", "/work/repo", false)]
    [InlineData("/work/repo-sibling/dash/tests/foo.test.ts", "/work/repo", false)]
    [InlineData("/work/repo/other/dash/tests/foo.test.ts", "/work/repo", false)]
    [InlineData("/work/repo/dash/../../other/dash/tests/foo.test.ts", "/work/repo", false)]
    [InlineData("/work/repo/dash/tests/foo.test.ts", "/work/repo", true)]
    [InlineData("/work/repo/dash/tests/../tests/foo.test.ts", "/work/repo/", true)]
    [InlineData("/work/repo/dash/tests/foo.test.ts", "work/repo", false)]
    [InlineData(@"C:\other\dash\tests\foo.test.ts", @"C:\work\repo", false)]
    [InlineData(@"D:\work\repo\dash\tests\foo.test.ts", @"C:\work\repo", false)]
    [InlineData("C:dash/tests/foo.test.ts", @"C:\work\repo", false)]
    [InlineData(@"C:\work\repo\dash\tests\foo.test.ts", @"C:\work\repo", true)]
    [InlineData(@"\\server\share\other\dash\tests\foo.test.ts", @"\\server\share\repo", false)]
    [InlineData(@"\\server\share\repo\dash\tests\foo.test.ts", @"\\server\share\repo", true)]
    public void ARootedCompilerPathResolvesOnlyInsideTheGivenRepositoryRoot(
        string reportedFile, string? repositoryRoot, bool expected) =>
        Assert.Equal(expected, RedClassifier.IsOwnFile(reportedFile, ["dash/tests/foo.test.ts"], repositoryRoot: repositoryRoot));

    [Fact]
    public void ARootedCompilerPathWithoutARepositoryRootMatchesOnlyAnIdenticalRootedOwnPath()
    {
        Assert.True(RedClassifier.IsOwnFile("/work/repo/dash/tests/foo.test.ts", ["/work/repo/dash/tests/foo.test.ts"]));
        Assert.False(RedClassifier.IsOwnFile("/tmp/other/dash/tests/foo.test.ts", ["/work/repo/dash/tests/foo.test.ts"]));
    }

    [Theory]
    [InlineData("/real/repo/dash/tests/foo.test.ts", "/alias/repo", "/real/repo", true)]
    [InlineData("/alias/repo/dash/tests/foo.test.ts", "/alias/repo", "/real/repo", true)]
    [InlineData("/other/repo/dash/tests/foo.test.ts", "/alias/repo", "/real/repo", false)]
    public void PublicIsOwnFileResolvesBothLexicalAndCanonicalRepositoryRoots(
        string reportedFile, string repositoryRoot, string canonicalRepositoryRoot, bool expected) =>
        Assert.Equal(
            expected,
            RedClassifier.IsOwnFile(
                reportedFile,
                ["dash/tests/foo.test.ts"],
                repositoryRoot: repositoryRoot,
                canonicalRepositoryRoot: canonicalRepositoryRoot));

    [Theory]
    [InlineData("/work/repo/dash/tests/foo.test.ts", "/work/repo/dash/tests/foo.test.ts", null, "/work/repo", null, true)]
    [InlineData("/real/repo/dash/tests/foo.test.ts", "/alias/repo/dash/tests/foo.test.ts", null, "/alias/repo", "/real/repo", true)]
    [InlineData("tests/foo.test.ts", "/work/repo/dash/tests/foo.test.ts", "dash", "/work/repo", null, true)]
    [InlineData("/tmp/other/dash/tests/foo.test.ts", "/tmp/other/dash/tests/foo.test.ts", null, "/work/repo", null, true)]
    [InlineData("/work/repo/work/repo/dash/tests/foo.test.ts", "/work/repo/dash/tests/foo.test.ts", null, "/work/repo", null, false)]
    [InlineData("/tmp/other/dash/tests/foo.test.ts", "/work/repo/dash/tests/foo.test.ts", null, "/work/repo", null, false)]
    [InlineData("/work/repo/dash/tests/foo.test.ts", "/dash/tests/foo.test.ts", null, "/work/repo", null, false)]
    public void ARootedOwnPathMatchesAcrossBoundAndCanonicalRootsAndExactRootedPaths(
        string reportedFile,
        string ownFile,
        string? workingDirectory,
        string? repositoryRoot,
        string? canonicalRepositoryRoot,
        bool expected) =>
        Assert.Equal(
            expected,
            RedClassifier.IsOwnFile(
                reportedFile,
                [ownFile],
                workingDirectory,
                repositoryRoot,
                canonicalRepositoryRoot));

    [Theory]
    [InlineData("/WORK/repo/dash/tests/foo.test.ts", "dash/tests/foo.test.ts", null, "/work/repo", false, false)]
    [InlineData("/WORK/repo/dash/tests/foo.test.ts", "dash/tests/foo.test.ts", null, "/work/repo", true, true)]
    [InlineData("/work/repo/dash/tests/Foo.test.ts", "dash/tests/foo.test.ts", null, "/work/repo", false, false)]
    [InlineData("/work/repo/dash/tests/Foo.test.ts", "dash/tests/foo.test.ts", null, "/work/repo", true, true)]
    [InlineData("tests/Foo.test.ts", "dash/tests/foo.test.ts", "dash", null, false, false)]
    [InlineData("tests/Foo.test.ts", "dash/tests/foo.test.ts", "dash", null, true, true)]
    [InlineData("/WORK/repo/dash/tests/foo.test.ts", "/work/repo/dash/tests/foo.test.ts", null, null, false, false)]
    [InlineData("/WORK/repo/dash/tests/foo.test.ts", "/work/repo/dash/tests/foo.test.ts", null, null, true, true)]
    public void ARootedOrRelativePathFollowsTheVolumesCaseRule(
        string reportedFile,
        string ownFile,
        string? workingDirectory,
        string? repositoryRoot,
        bool isFileSystemCaseInsensitive,
        bool expected) =>
        Assert.Equal(
            expected,
            RedClassifier.IsOwnFile(
                reportedFile,
                [ownFile],
                workingDirectory,
                repositoryRoot,
                isFileSystemCaseInsensitive: isFileSystemCaseInsensitive));

    [Theory]
    [InlineData(null)]
    [InlineData("/work/repo")]
    public void EvaluateRejectsASiblingAbsoluteCompilerPathWithOrWithoutARepositoryRoot(string? repositoryRoot)
    {
        RedGateObservation gate = new(
            "dash-typecheck",
            "npm run --prefix dash typecheck",
            2,
            "/tmp/other/dash/tests/foo.test.ts(3,10): error TS2305: Module '\"../src/x\"' has no exported member 'Gadget'.",
            WorkingDirectory: "dash",
            RepositoryRoot: repositoryRoot);

        RedOutcome outcome = RedClassifier.Evaluate([gate], ["dash/tests/foo.test.ts"]);

        Assert.False(outcome.Passed);
        Assert.Contains("did not change", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewGatesRedRunRejectsSiblingAbsoluteCompilerPathAndAcceptsRootedAndCanonicalOwnPaths()
    {
        string physicalParent = Path.Combine(_services.Files.Root, "physical");
        string repo = Path.Combine(physicalParent, "repo");
        string aliasParent = Path.Combine(_services.Files.Root, "alias");
        string aliasedRepo = Path.Combine(aliasParent, "repo");
        _services.Files.CreateDirectory(Path.Combine(repo, ".kyber-weave"));
        _services.Files.CreateDirectory(Path.Combine(repo, "tests", "Widget.Tests"));
        _services.Files.WriteAllText(Path.Combine(repo, TestFile), "namespace Widget.Tests; public class WidgetTests { }\n");
        _services.Files.WriteAllText(
            Path.Combine(repo, ".kyber-weave", "kyber-weave.yml"),
            """
            review:
              task-gates:
                csharp-dev:
                  - id: dotnet-build
                    include: ["**/*.cs"]
                    run: [dotnet, build]
            """);
        _services.Files.CreateDirectoryLink(aliasParent, physicalParent);

        string siblingPath = Path.Combine(_services.Files.Root, "other", TestFile).Replace('\\', '/');
        CapturedConsoleExecution<int> rejected = RunBuildGate(aliasedRepo, siblingPath, "--red");

        Assert.Equal(1, rejected.Result);
        Assert.Contains("RED_REJECTED:", rejected.Output, StringComparison.Ordinal);
        Assert.Contains("did not change", rejected.Output, StringComparison.Ordinal);

        string canonicalOwnPath = Path.Combine(repo, TestFile).Replace('\\', '/');
        CapturedConsoleExecution<int> acceptedCanonical = RunBuildGate(aliasedRepo, canonicalOwnPath, "--red");

        Assert.Equal(0, acceptedCanonical.Result);
        Assert.Contains("RED_EVIDENCE:", acceptedCanonical.Output, StringComparison.Ordinal);

        string lexicalOwnPath = Path.Combine(aliasedRepo, TestFile).Replace('\\', '/');
        CapturedConsoleExecution<int> acceptedLexical = RunBuildGate(aliasedRepo, lexicalOwnPath, "--red");

        Assert.Equal(0, acceptedLexical.Result);
        Assert.Contains("RED_EVIDENCE:", acceptedLexical.Output, StringComparison.Ordinal);

        CapturedConsoleExecution<int> siblingNote = RunBuildGate(aliasedRepo, siblingPath);
        Assert.Contains("sibling RED tests reference symbols no task has created yet: WidgetRepository", siblingNote.Output, StringComparison.Ordinal);

        CapturedConsoleExecution<int> ownNonRed = RunBuildGate(aliasedRepo, canonicalOwnPath);
        Assert.DoesNotContain("sibling RED tests reference", ownNonRed.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockedDownRootCanonicalProbeThrowingSecurityExceptionFallsBackToSingleRootRedMatching()
    {
        string lockedParent = Path.Combine(_services.Files.Root, "locked");
        string repo = Path.Combine(lockedParent, "repo");
        _services.Files.CreateDirectory(Path.Combine(repo, ".kyber-weave"));
        _services.Files.CreateDirectory(Path.Combine(repo, "tests", "Widget.Tests"));
        _services.Files.WriteAllText(Path.Combine(repo, TestFile), "namespace Widget.Tests; public class WidgetTests { }\n");
        _services.Files.WriteAllText(
            Path.Combine(repo, ".kyber-weave", "kyber-weave.yml"),
            """
            review:
              task-gates:
                csharp-dev:
                  - id: dotnet-build
                    include: ["**/*.cs"]
                    run: [dotnet, build]
            """);

        // A locked-down parent raises SecurityException when Canonicalize enumerates it; --red must fall back to lexical matching.
        _services.Files.FailWhen(
            op => op.Name == nameof(IFileSystem.EnumerateEntries)
                  && string.Equals(op.Path, lockedParent, StringComparison.Ordinal),
            new SecurityException("Access to locked directory denied."));

        string lexicalOwnPath = Path.Combine(repo, TestFile).Replace('\\', '/');
        CapturedConsoleExecution<int> result = RunBuildGate(repo, lexicalOwnPath, "--red");

        Assert.Equal(0, result.Result);
        Assert.Contains("RED_EVIDENCE:", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 1, "RED_REJECTED:")]
    [InlineData(true, 0, "RED_EVIDENCE:")]
    public void ReviewGatesRedRunHonorsEnvironmentCaseSensitivityOnRootedCompilerPaths(
        bool isFileSystemCaseInsensitive,
        int expectedExit,
        string expectedMarker)
    {
        InMemoryFileSystem files = new(caseSensitive: !isFileSystemCaseInsensitive);
        string repo = Path.Combine(files.Root, "work", "repo");
        string siblingCaseDir = Path.Combine(files.Root, "work", "REPO");
        files.CreateDirectory(Path.Combine(repo, ".kyber-weave"));
        files.CreateDirectory(Path.Combine(repo, "tests", "Widget.Tests"));
        files.WriteAllText(Path.Combine(repo, TestFile), "namespace Widget.Tests; public class WidgetTests { }\n");
        files.WriteAllText(
            Path.Combine(repo, ".kyber-weave", "kyber-weave.yml"),
            """
            review:
              task-gates:
                csharp-dev:
                  - id: dotnet-build
                    include: ["**/*.cs"]
                    run: [dotnet, build]
                  - id: tests
                    include: ["**/*.cs"]
                    run: [dotnet, test]
            """);

        string reportedFile = Path.Combine(siblingCaseDir, TestFile).Replace('\\', '/');
        CapturedConsoleExecution<int> RunCase(params string[] extraArgs)
        {
            using InMemoryReviewServices services = new(files);
            services.Processes.Script(
                "dotnet",
                request => request.Arguments.Contains("build", StringComparer.Ordinal)
                    ? new ProcessRunResult(1, string.Empty, TimedOut: false)
                    {
                        StandardOutput = $"{reportedFile}(12,17): error CS0246: The type or namespace name 'WidgetRepository' could not be found\nBuild FAILED.",
                    }
                    : new ProcessRunResult(0, string.Empty, TimedOut: false)
                    {
                        StandardOutput = "Passed!  - Failed:     0, Passed:    1, Skipped:     0, Total:    1, Duration: 10 ms",
                    });

            CommandApp app = CommandAppFixture.ReviewGates(
                collection =>
                {
                    services.Register(collection);
                    collection.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(
                        currentDirectory: services.Root,
                        homeDirectory: Path.Combine(files.Root, "home"),
                        tempDirectory: Path.Combine(files.Root, "temp"),
                        isFileSystemCaseInsensitive: isFileSystemCaseInsensitive));
                },
                services.Ansi);

            return services.Run(
                app,
                ["review", "gates", repo, "--agent", "csharp-dev", "--paths", TestFile, "--task", "t1", .. extraArgs]);
        }

        CapturedConsoleExecution<int> result = RunCase("--red");

        Assert.Equal(expectedExit, result.Result);
        Assert.Contains(expectedMarker, result.Output, StringComparison.Ordinal);
        if (!isFileSystemCaseInsensitive)
        {
            Assert.Contains("did not change", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("not run — RED build", result.Output, StringComparison.Ordinal);
        }

        CapturedConsoleExecution<int> nonRed = RunCase();
        if (isFileSystemCaseInsensitive)
        {
            Assert.DoesNotContain("sibling RED tests reference", nonRed.Output, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("sibling RED tests reference symbols no task has created yet: WidgetRepository", nonRed.Output, StringComparison.Ordinal);
        }
    }

    private CapturedConsoleExecution<int> RunBuildGate(string repoRoot, string reportedFile, params string[] extraOptions)
    {
        using InMemoryReviewServices services = new(_services.Files);
        services.Processes.Script(
            "dotnet",
            new KyberWeave.Contracts.Repositories.ProcessRunResult(1, string.Empty, TimedOut: false)
            {
                StandardOutput = $"{reportedFile}(12,17): error CS0246: The type or namespace name 'WidgetRepository' could not be found\nBuild FAILED.",
            });
        return services.Run(
            services.ReviewGatesApp(),
            ["review", "gates", repoRoot, "--agent", "csharp-dev", "--paths", TestFile, "--task", "t1", .. extraOptions]);
    }
}
