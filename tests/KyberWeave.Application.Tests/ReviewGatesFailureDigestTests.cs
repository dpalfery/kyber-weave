using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// What a failed repository gate records in its report: the test totals, each failing test with the first line of its
/// error, or the compile errors, read from the command's whole output. The same string is the printed summary and the
/// <c>summary</c> in <c>gates.json</c>, so a reader can tell which test failed instead of reading the last log lines.
/// </summary>
public sealed class ReviewGatesFailureDigestTests : IDisposable
{
    private readonly InMemoryReviewServices _services = new();

    public void Dispose() => _services.Dispose();

    private GateReport Run(string program, string output, string error = "", int exitCode = 1)
    {
        _services.Processes.Script(program, new ProcessRunResult(exitCode, string.Empty, TimedOut: false)
        {
            StandardOutput = output,
            StandardError = error,
        });

        return _services.Gates.Run(
            new ReviewConfig { Gates = [new ReviewGate("tests", [program])] },
            _services.Root);
    }

    private static string VsTestFailure(string name, string assertion) => $"""
          Failed {name} [12 ms]
          Error Message:
           {assertion}
          Expected: 2
          Actual:   3
          Stack Trace:
             at {name}() in /repo/tests/OrdersTests.cs:line 40
        """;

    [Fact]
    public void AFailingDotnetTestGateNamesEachFailingTestAndItsErrorInTheReport()
    {
        string output = string.Join('\n',
            VsTestFailure("Acme.Tests.OrdersTests.Total_WhenTwoLines_IsTheirSum", "Assert.Equal() Failure: Values differ"),
            VsTestFailure("Acme.Tests.OrdersTests.Cancel_WhenShipped_Throws", "Assert.Throws() Failure: No exception was thrown"),
            "Failed!  - Failed:     2, Passed:     7, Skipped:     0, Total:     9, Duration: 1 s - Acme.Tests.dll (net10.0)");

        // The test host's logging fills standard error, which is what the summary used to show on its own.
        GateReport report = Run("dotnet-test", output, error: "info: SignalR[0]\n      Hub connection established.");

        GateResult gate = Assert.Single(report.Gates);
        Assert.Contains("FAIL — tests: total=9 failed=2 passed=7", gate.Summary, StringComparison.Ordinal);
        Assert.Contains("Acme.Tests.OrdersTests.Total_WhenTwoLines_IsTheirSum — Assert.Equal() Failure: Values differ", gate.Summary, StringComparison.Ordinal);
        Assert.Contains("Acme.Tests.OrdersTests.Cancel_WhenShipped_Throws — Assert.Throws() Failure: No exception was thrown", gate.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("SignalR", gate.Summary, StringComparison.Ordinal);

        GateResult reread = Assert.Single(ReviewJson.ReadGates(ReviewJson.Write(report)).Gates);
        Assert.Equal(gate.Summary, reread.Summary);
    }

    [Fact]
    public void AFailingScriptTestGateNamesEachFailingTest()
    {
        string output = string.Join('\n',
            " FAIL  tests/cart.test.ts > cart > adds an item",
            "AssertionError: expected 2 to be 3 // Object.is equality",
            " FAIL  tests/cart.test.ts > cart > removes an item",
            "Error: expected element to be visible",
            "Tests:       2 failed, 7 passed, 9 total");

        GateResult gate = Assert.Single(Run("ts-test", output).Gates);

        Assert.Contains("FAIL — tests: total=9 failed=2 passed=7", gate.Summary, StringComparison.Ordinal);
        Assert.Contains("tests/cart.test.ts > cart > adds an item — AssertionError: expected 2 to be 3 // Object.is equality", gate.Summary, StringComparison.Ordinal);
        Assert.Contains("tests/cart.test.ts > cart > removes an item — Error: expected element to be visible", gate.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailingBuildGateListsTheCompileErrors()
    {
        string output = string.Join('\n',
            "/repo/tests/Acme.Tests/OrdersTests.cs(12,17): error CS0246: The type or namespace name 'OrderTotals' could not be found [/repo/tests/Acme.Tests/Acme.Tests.csproj]",
            "/repo/tests/Acme.Tests/OrdersTests.cs(20,9): error CS0103: The name 'Cancel' does not exist in the current context [/repo/tests/Acme.Tests/Acme.Tests.csproj]",
            "Build FAILED.");

        GateResult gate = Assert.Single(Run("dotnet-build", output).Gates);

        Assert.Contains("FAIL — the build failed with 2 compile errors, so no test ran", gate.Summary, StringComparison.Ordinal);
        Assert.Contains("CS0246 The type or namespace name 'OrderTotals' could not be found", gate.Summary, StringComparison.Ordinal);
        Assert.Contains("CS0103 The name 'Cancel' does not exist in the current context", gate.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void APassingGateKeepsItsSummary()
    {
        GateResult gate = Assert.Single(
            Run("dotnet-test", "Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41, Duration: 2 s", exitCode: 0).Gates);

        Assert.Equal("passed", gate.Summary);
    }
}
