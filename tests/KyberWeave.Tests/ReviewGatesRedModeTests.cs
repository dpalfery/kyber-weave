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

    // ---- the command, against a real C# project --------------------------------------------------

    private string Project(string testSource, string? siblingSource = null)
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
            """);
        return root;
    }

    private static (int ExitCode, string Output) Run(string root, params string[] options)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(["review", "gates", root, "--agent", "test-dev", "--paths", TestFile, .. options]));
        return (execution.Result, execution.Output);
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
    public void RedRequiresAnAgent()
    {
        string root = Project("namespace Widget.Tests; public class WidgetTests { }");
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(["review", "gates", root, "--red"]));

        Assert.Equal(2, execution.Result);
    }
}
