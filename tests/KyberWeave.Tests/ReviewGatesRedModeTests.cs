using System.Runtime.CompilerServices;
using System.Text.Json;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The RED judgment for test-authoring task gates. The classifier cases read fake compiler and test-runner output
/// (the exact shapes Roslyn, MSBuild, the .NET test runners and tsc print); the command cases build a real tiny C#
/// project with <c>dotnet build</c>, so the missing-symbol codes are the compiler's own, not ones written by hand.
/// </summary>
public sealed class ReviewGatesRedModeTests : IDisposable
{
    private const string TestFile = "tests/Widget.Tests/WidgetTests.cs";
    private readonly TempDirectory _temp = new();

    private static RedOutcome Judge(string output, int exitCode = 1, params string[] ownFiles) =>
        RedClassifier.Evaluate([new RedGateObservation("dotnet-build", "dotnet build tests/Widget.Tests", exitCode, output)], ownFiles.Length > 0 ? ownFiles : [TestFile]);

    private static string CSharpError(string file, string code, string message) =>
        $"/work/repo/{file}(12,17): error {code}: {message} [/work/repo/tests/Widget.Tests/Widget.Tests.csproj]";

    public void Dispose() => _temp.Dispose();

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
        RedGateObservation gate = new("dotnet-build", "dotnet build", 1, CSharpError("tests/Widget.Tests/SiblingTests.cs", "CS0246", "The type or namespace name 'WidgetRepository' could not be found"));

        Assert.Equal(["WidgetRepository"], RedClassifier.SiblingSymbols(gate, ["src/Widget/Panel.cs"]));
        Assert.Null(RedClassifier.SiblingSymbols(gate, ["tests/Widget.Tests/SiblingTests.cs"]));
        Assert.Null(RedClassifier.SiblingSymbols(gate with { Output = gate.Output + "\n" + CSharpError("tests/Widget.Tests/SiblingTests.cs", "CS1002", "; expected") }, ["src/Widget/Panel.cs"]));
        Assert.Null(RedClassifier.SiblingSymbols(gate with { ExitCode = 0 }, ["src/Widget/Panel.cs"]));
    }

    // ---- the first live run's reports (fixtures), and the runner patterns -----------------------------

    private static string Fixture(string name, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "red-gate-reports", name));

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
        string root = Path.Combine(_temp.Path, "zero-root");
        Directory.CreateDirectory(Path.Combine(root, "tests/Other.IntegrationTests"));
        File.WriteAllText(Path.Combine(root, "tests/Other.IntegrationTests/Other.IntegrationTests.csproj"), "<Project />");
        const string file = "tests/Other.IntegrationTests/JobTests.cs";
        File.WriteAllText(Path.Combine(root, file), "// test");

        RedOutcome outcome = RedClassifier.Evaluate(
            [new RedGateObservation("tests", "dotnet test --filter FullyQualifiedName~JobTests", 8, GateSummary("t1-task-gate.json", "tests"))],
            [file],
            root);

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

    // ---- the command, against a real C# project --------------------------------------------------

    private string Project(string testSource, string? siblingSource = null, bool withTestGate = false)
    {
        string root = Path.Combine(_temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(root, ".kyber-weave"));
        Directory.CreateDirectory(Path.Combine(root, "tests/Widget.Tests"));
        File.WriteAllText(Path.Combine(root, "tests/Widget.Tests/Widget.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, TestFile), testSource);
        if (siblingSource is not null)
        {
            File.WriteAllText(Path.Combine(root, "tests/Widget.Tests/SiblingTests.cs"), siblingSource);
        }

        File.WriteAllText(Path.Combine(root, ".kyber-weave/kyber-weave.yml"), """
            review:
              task-gates:
                test-dev:
                  - id: dotnet-build
                    include: ["**/*.cs"]
                    run: [dotnet, build, tests/Widget.Tests/Widget.Tests.csproj, -nologo, -nr:false, -p:UseSharedCompilation=false]
            """ + (withTestGate ? """

                  - id: tests
                    include: ["**/*.cs"]
                    run: [dotnet, test, tests/Widget.Tests/Widget.Tests.csproj, --no-build]
            """ : string.Empty));
        return root;
    }

    private static (int ExitCode, string Output) Run(string root, params string[] options) => RunAs("test-dev", root, options);

    private static (int ExitCode, string Output) RunAs(string agent, string root, params string[] options)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(["review", "gates", root, "--agent", agent, "--paths", TestFile, .. options]));
        return (execution.Result, execution.Output);
    }

    [Theory]
    [InlineData("csharp-dev")]
    [InlineData("dal-dev")]
    [InlineData("react-dev")]
    public void RedModeWorksForAnyAgentsTaskGatesNotOnlyTestDev(string agent)
    {
        string root = Project("namespace Widget.Tests; public class WidgetTests { public void T() { var r = new WidgetRepository(); r.Load(); } }");
        string config = Path.Combine(root, ".kyber-weave/kyber-weave.yml");
        File.WriteAllText(config, File.ReadAllText(config).Replace("  test-dev:", $"  {agent}:", StringComparison.Ordinal));

        (int exit, string output) = RunAs(agent, root, "--red");

        Assert.True(exit == 0, output);
        Assert.Contains("RED_EVIDENCE: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RedModePassesARealBuildThatFailsOnlyOnMissingSymbolsAndPrintsTheLineToPaste()
    {
        string root = Project("namespace Widget.Tests; public class WidgetTests { public void T() { var r = new WidgetRepository(); r.Load(); } }");

        (int exit, string output) = Run(root, "--red");

        Assert.True(exit == 0, output);
        Assert.Contains("RED_EVIDENCE: ", output, StringComparison.Ordinal);
        Assert.Contains("compile: missing WidgetRepository", output, StringComparison.Ordinal);
        Assert.Contains("KW-REVIEW-051", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuildAcceptedAsRedSkipsTheLaterTestGateAndTheReportCorroboratesAPassClaim()
    {
        string root = Project("namespace Widget.Tests; public class WidgetTests { public void T() { var r = new WidgetRepository(); } }", withTestGate: true);
        string report = Path.Combine(root, "gates.json");

        (int exit, string output) = Run(root, "--red", "--out", report);

        Assert.True(exit == 0, output);
        Assert.Contains("TASK_GATE: PASS", output, StringComparison.Ordinal);
        Assert.Contains("skipped=1", output, StringComparison.Ordinal);
        Assert.Contains("RED_EVIDENCE: ", output, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(report));
        JsonElement tests = json.RootElement.GetProperty("gates").EnumerateArray().Single(gate => gate.GetProperty("id").GetString() == "tests");
        Assert.Contains("not run — RED build (missing symbols WidgetRepository)", tests.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.True(tests.GetProperty("notApplicable").GetBoolean());
        // KW-ARB-GATE-REPORT-001 reads the claim against this report: it must corroborate, not disagree.
        Assert.True(GateReportFacts.Agrees($"TASK_GATE: PASS — agent=test-dev gates=2 failed=none skipped=1 report={report} runs=1", root));
    }

    [Fact]
    public void ARejectedRedRunPrintsTheRejectionLineAndFailsTheTaskGateStatus()
    {
        string root = Project("namespace Widget.Tests; public class WidgetTests { public void T() { var r = new WidgetRepository() } }");

        (int exit, string output) = Run(root, "--red");

        Assert.Equal(1, exit);
        Assert.Contains("TASK_GATE: FAIL", output, StringComparison.Ordinal);
        Assert.Contains("RED_REJECTED: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RedModeRefusesARealBuildThatBreaksForAnyOtherReason()
    {
        string root = Project("namespace Widget.Tests; public class WidgetTests { public void T() { var r = new WidgetRepository() } }");

        (int exit, string output) = Run(root, "--red");

        Assert.Equal(1, exit);
        Assert.Contains("KW-REVIEW-052", output, StringComparison.Ordinal);
        Assert.Contains("CS1002", output, StringComparison.Ordinal);
        Assert.DoesNotContain("RED_EVIDENCE: ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RedModeRefusesABuildThatSucceedsBecauseNoTestFails()
    {
        string root = Project("namespace Widget.Tests; public class WidgetTests { public void T() { } }");

        (int exit, string output) = Run(root, "--red");

        Assert.Equal(1, exit);
        Assert.Contains("KW-REVIEW-052", output, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutRedASiblingTestsMissingSymbolsAreNamedInTheWordsToReport()
    {
        string root = Project(
            "namespace Widget.Tests; public class WidgetTests { }",
            "namespace Widget.Tests; public class SiblingTests { public void T() { var r = new WidgetRepository(); } }");

        (int exit, string output) = Run(root);

        Assert.Equal(1, exit);
        Assert.Contains("KW-REVIEW-053", output, StringComparison.Ordinal);
        Assert.Contains("NOT VERIFIED: dotnet build", output, StringComparison.Ordinal);
        Assert.Contains("sibling RED tests reference symbols no task has created yet: WidgetRepository", output, StringComparison.Ordinal);
    }

    [Fact]
    public void APassingRunPrintsTheDigestWhoseTaskGateLineTheReportCorroborates()
    {
        string root = Project("namespace Widget.Tests; public class WidgetTests { public void T() { } }");
        string report = Path.Combine(root, "gates.json");

        (int exit, string output) = Run(root, "--out", report);

        Assert.True(exit == 0, output);
        Assert.Contains("STATUS: PASS", output, StringComparison.Ordinal);
        Assert.Contains($"FILES: {TestFile}", output, StringComparison.Ordinal);
        string claim = output.Split('\n').Select(line => line.Trim()).Single(line => line.StartsWith("TASK_GATE: ", StringComparison.Ordinal));
        // The worker fills in the run count; the rest of the line is the report's own and must corroborate.
        Assert.True(GateReportFacts.Agrees(claim.Replace("runs=<k>", "runs=2", StringComparison.Ordinal), root), claim);
    }

    [Fact]
    public void RedRequiresAnAgent()
    {
        string root = Project("namespace Widget.Tests; public class WidgetTests { }");
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(["review", "gates", root, "--red"]));

        Assert.Equal(2, execution.Result);
    }
}
