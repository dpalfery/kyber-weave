using KyberWeave.Core.Arbiter.Facts;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The seam between <c>review gates --red</c> and <c>KW-ARB-MODE-002</c>: a real failing run, then a real passing run,
/// written to two reports in one task folder, must read as a failing run first. The Arbiter recognises the failing-run
/// report by the summary <c>review gates --red</c> gives an accepted gate, so a reworded summary fails here.
/// </summary>
public sealed class ReviewGatesFailingRunProofTests : IDisposable
{
    private const string TestFile = "tests/Widget.Tests/WidgetTests.cs";
    private const string ProductionFile = "tests/Widget.Tests/WidgetRepository.cs";

    private readonly string _root = Directory.CreateTempSubdirectory("kw-failing-run-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void TheRedRunThenThePassingRunReadAsAFailingRunFirst()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".kyber-weave"));
        Directory.CreateDirectory(Path.Combine(_root, "tests/Widget.Tests"));
        File.WriteAllText(
            Path.Combine(_root, "tests/Widget.Tests/Widget.Tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(_root, ".kyber-weave/kyber-weave.yml"),
            """
            review:
              task-gates:
                csharp-dev:
                  - id: dotnet-build
                    include: ["**/*.cs"]
                    run: [dotnet, build, tests/Widget.Tests/Widget.Tests.csproj, -nologo, -nr:false, -p:UseSharedCompilation=false]
            """);
        File.WriteAllText(
            Path.Combine(_root, TestFile),
            "namespace Widget.Tests; public class WidgetTests { public void T() { var r = new WidgetRepository(); r.Load(); } }");
        string folder = Path.Combine(_root, ".agent_scratchpad", "T1");
        string red = Path.Combine(folder, "task-gate-red.json");
        string pass = Path.Combine(folder, "task-gate.json");

        (int redExit, string redOutput) = Run(["--paths", TestFile], "--red", "--out", red);
        Assert.True(redExit == 0, redOutput);

        File.WriteAllText(
            Path.Combine(_root, ProductionFile),
            "namespace Widget.Tests; public class WidgetRepository { public void Load() { } }");
        (int passExit, string passOutput) = Run(["--paths", TestFile, "--paths", ProductionFile], "--out", pass);
        Assert.True(passExit == 0, passOutput);
        File.SetLastWriteTimeUtc(pass, File.GetLastWriteTimeUtc(red).AddSeconds(1));

        string digest = $"TASK_GATE: PASS — agent=csharp-dev gates=1 failed=none skipped=0 report={pass} runs=2";
        Assert.True(GateReportFacts.FailingRunFirst(digest, _root));
    }

    private (int ExitCode, string Output) Run(string[] paths, params string[] options)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(
            () => CommandAppFixture.ReviewGates().Run(["review", "gates", _root, "--agent", "csharp-dev", .. paths, "--task", "T1", .. options]));
        return (execution.Result, execution.Output);
    }
}
