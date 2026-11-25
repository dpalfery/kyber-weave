using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Review;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Fakes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The command cases of RED mode: each builds a real tiny C# project and runs <c>review gates --red</c> over it with
/// <c>dotnet build</c>, so the missing-symbol codes are the compiler's own, not ones written by hand. The judgment
/// of compiler and runner output is covered over fake output in the unit project.
/// </summary>
public sealed class ReviewGatesRedModeCommandTests : IDisposable
{
    private const string TestFile = "tests/Widget.Tests/WidgetTests.cs";
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

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
        Assert.True(RealGateReportFacts.Create().Agrees($"TASK_GATE: PASS — agent=test-dev gates=2 failed=none skipped=1 report={report} runs=1", root));
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
        string root = Project("namespace Widget.Tests; public class WidgetTests { public void T() { } }", withTestGate: true);

        (int exit, string output) = Run(root, "--red");

        Assert.Equal(1, exit);
        Assert.Contains("KW-REVIEW-052", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RedModeAcceptsAPassingBuildAsNotApplicableWhenNoSelectedGateRunsTests()
    {
        string root = Project("namespace Widget.Tests; public class WidgetTests { public void T() { } }");

        (int exit, string output) = Run(root, "--red");

        Assert.Equal(0, exit);
        Assert.Contains("KW-REVIEW-051", output, StringComparison.Ordinal);
        Assert.Contains("not applicable", output, StringComparison.Ordinal);
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
        // The gate prints the real run count since the count was added; the line is the report's own and must corroborate as printed.
        Assert.DoesNotContain("runs=<k>", claim, StringComparison.Ordinal);
        Assert.True(RealGateReportFacts.Create().Agrees(claim, root), claim);
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
