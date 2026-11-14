using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The short form of a task gate's run: a worker reads pass or fail, the totals and each failing test with its first
/// error line, never the raw output, which a harness spills to a file once it passes about 20 KB.
/// </summary>
public sealed class TaskTestDigestTests
{
    private const string Log = ".agent_scratchpad/t1/logs/tests.log";

    private static string VsTestFailure(string name, string assertion) => $"""
          Failed {name} [12 ms]
          Error Message:
           {assertion}
        Expected: 2
        Actual:   3
          Stack Trace:
             at {name}() in /repo/tests/OrdersTests.cs:line 40
        """;

    private static string VsTestSummary(int failed, int passed) =>
        $"Failed!  - Failed:    {failed}, Passed:    {passed}, Skipped:     0, Total:    {failed + passed}, Duration: 1 s - Acme.Tests.dll (net10.0)";

    [Fact]
    public void AFailingRunNamesEachFailingTestWithTheFirstLineOfItsError()
    {
        string output = string.Join('\n',
            "[xUnit.net 00:00:00.41]     Acme.Tests.OrdersTests.Total_WhenTwoLines_IsTheirSum [FAIL]",
            VsTestFailure("Acme.Tests.OrdersTests.Total_WhenTwoLines_IsTheirSum", "Assert.Equal() Failure: Values differ"),
            VsTestFailure("Acme.Tests.OrdersTests.Cancel_WhenShipped_Throws", "Assert.Throws() Failure: No exception was thrown"),
            VsTestSummary(2, 7));

        IReadOnlyList<string> lines = TaskTestDigest.Lines("tests", passed: false, output, Log);

        Assert.Equal(
            [
                "tests: FAIL — tests: total=9 failed=2 passed=7",
                "  Acme.Tests.OrdersTests.Total_WhenTwoLines_IsTheirSum — Assert.Equal() Failure: Values differ",
                "  Acme.Tests.OrdersTests.Cancel_WhenShipped_Throws — Assert.Throws() Failure: No exception was thrown",
                "  full output: " + Log,
            ],
            lines);
    }

    [Fact]
    public void TheTestingPlatformsAssemblyLineUnderAFailedTestIsSkippedForItsAssertion()
    {
        string output = string.Join('\n',
            "failed Acme.Tests.OrdersTests.Total_IsTheSum (0ms)",
            "  from /repo/artifacts/bin/Acme.Tests/release/Acme.Tests.dll (net10.0|arm64)",
            "  Assert.Equal() Failure: Collections differ at index 0",
            "    at Acme.Tests.OrdersTests.Total_IsTheSum() in /repo/tests/OrdersTests.cs:51",
            "Test run summary: Failed!",
            "  total: 5",
            "  failed: 1",
            "  succeeded: 4");

        IReadOnlyList<string> lines = TaskTestDigest.Lines("tests", passed: false, output, null);

        Assert.Equal(
            ["tests: FAIL — tests: total=5 failed=1 passed=4", "  Acme.Tests.OrdersTests.Total_IsTheSum — Assert.Equal() Failure: Collections differ at index 0"],
            lines);
    }

    [Fact]
    public void APassingRunIsOneLineWithItsTotals()
    {
        IReadOnlyList<string> lines = TaskTestDigest.Lines(
            "tests", passed: true, "Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41, Duration: 2 s", Log);

        Assert.Equal(["tests: PASS — tests: total=41 failed=0 passed=41", "  full output: " + Log], lines);
    }

    [Fact]
    public void ACompileFailureIsAFailingRunThatListsTheCompilerErrors()
    {
        string output = string.Join('\n',
            "/repo/tests/Acme.Tests/OrdersTests.cs(12,17): error CS0246: The type or namespace name 'OrderTotals' could not be found [/repo/tests/Acme.Tests/Acme.Tests.csproj]",
            "/repo/tests/Acme.Tests/OrdersTests.cs(20,9): error CS0103: The name 'Cancel' does not exist in the current context [/repo/tests/Acme.Tests/Acme.Tests.csproj]",
            "Build FAILED.");

        IReadOnlyList<string> lines = TaskTestDigest.Lines("dotnet-build", passed: false, output, null);

        Assert.Equal("dotnet-build: FAIL — the build failed with 2 compile errors, so no test ran", lines[0]);
        Assert.Contains("  /repo/tests/Acme.Tests/OrdersTests.cs: CS0246 The type or namespace name 'OrderTotals' could not be found", lines);
        Assert.Equal(3, lines.Count);
    }

    [Fact]
    public void TheOutputIsBoundedHoweverManyTestsFailAndHoweverLongTheirErrorsAre()
    {
        string longAssertion = "Assert.Equal() Failure: " + new string('x', 5000);
        string output = string.Join('\n', Enumerable.Range(0, 400)
            .Select(index => VsTestFailure($"Acme.Tests.OrdersTests.Case{index}", longAssertion))
            .Append(VsTestSummary(400, 0)));

        IReadOnlyList<string> lines = TaskTestDigest.Lines("tests", passed: false, output, Log);

        Assert.True(output.Length > 2_000_000);
        Assert.Equal(TaskTestDigest.MaxItems + 3, lines.Count);
        Assert.All(lines, line => Assert.True(line.Length <= TaskTestDigest.MaxLineLength, line));
        Assert.Contains("  and 390 more", lines);
        Assert.True(string.Join('\n', lines).Length < 4_000);
    }

    [Fact]
    public void AFailureWithNoTestSummaryOrCompileErrorShowsItsLastLines()
    {
        IReadOnlyList<string> lines = TaskTestDigest.Lines("format-style", passed: false, "one\ntwo\nthree\nfour\n", null);

        Assert.Equal(["format-style: FAIL — no test summary or compile error in its output; its last lines:", "  two", "  three", "  four"], lines);
    }
}
