using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// Gate <c>applies-when</c>: a gate declares the paths it applies to, and a gate that
/// does not apply is reported as not applicable rather than executed or omitted. The
/// verdict engine counts such a gate as neither passed nor failed, and older
/// <c>review-gates/v1</c> reports still read.
/// </summary>
public sealed class ReviewGateApplicabilityTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 8, 20);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static ReviewGate DashGate(string[] run, bool blocking = true) =>
        new("dash-gate", run, blocking, 900, new ReviewGateAppliesWhen(["dash/**"]));

    private static ReviewConfig Config(params ReviewGate[] gates) => new() { Gates = gates };

    private static ReviewConfig PolicyConfig() =>
        new() { Policy = new ReviewPolicy { AlwaysHuman = ["**/auth/**"] } };

    [Fact]
    public void AppliesWhenPathsAreParsedFromYaml()
    {
        ReviewConfig config = KyberWeaveConfigLoader.LoadFromYaml("""
            review:
              gates:
                - id: ts-typecheck
                  run: [npm, run, --prefix, dash, typecheck]
                  applies-when:
                    paths: ["dash/**"]
            """).Review;

        ReviewGate gate = Assert.Single(config.Gates);
        Assert.Equal(["dash/**"], gate.AppliesWhen!.Paths);
    }

    [Fact]
    public void AppliesWhenWithNoPathsIsRejected()
    {
        YamlException ex = Assert.ThrowsAny<YamlException>(() => KyberWeaveConfigLoader.LoadFromYaml(
            "review:\n  gates:\n    - id: a\n      run: [dotnet]\n      applies-when:\n        paths: []\n"));

        Assert.Contains("applies-when", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutChangedPathsEveryGateRunsAndTheBaseIsAbsent()
    {
        GateReport report = GateRunner.Run(
            Config(DashGate(["dotnet", "--version"]), new ReviewGate("build", ["dotnet", "--version"])),
            _temp.Path);

        Assert.All(report.Gates, gate => Assert.Null(gate.NotApplicableReason));
        Assert.All(report.Gates, gate => Assert.True(gate.Passed));
        Assert.Null(report.Base);
    }

    [Fact]
    public void ANonMatchingGateIsNotExecutedAndReportsItsReason()
    {
        GateReport report = GateRunner.Run(
            Config(DashGate(["definitely-missing-gate-binary-xyz", "--version"])),
            _temp.Path,
            changedPaths: ["src/Foo.cs"],
            baseRef: "main");

        GateResult gate = Assert.Single(report.Gates);
        Assert.Equal("main", report.Base);
        Assert.NotNull(gate.NotApplicableReason);
        Assert.Contains("dash/**", gate.NotApplicableReason, StringComparison.Ordinal);

        // A run would have failed to start the missing binary; a skip never starts it.
        Assert.DoesNotContain("could not start", gate.Summary, StringComparison.Ordinal);
        Assert.False(gate.Passed);
        Assert.Equal("KW-REVIEW-026", ReviewGateOutcome.NotApplicable);
    }

    [Fact]
    public void AMatchingGateRuns()
    {
        GateReport report = GateRunner.Run(
            Config(DashGate(["dotnet", "--version"])),
            _temp.Path,
            changedPaths: ["dash/src/x.ts"],
            baseRef: "main");

        GateResult gate = Assert.Single(report.Gates);
        Assert.Null(gate.NotApplicableReason);
        Assert.True(gate.Passed);
        Assert.Equal("main", report.Base);
    }

    [Fact]
    public void TheVerdictIgnoresNotApplicableGates()
    {
        ReviewConfig config = PolicyConfig();
        ReviewScope scope = new(["src/Foo.cs"], 10);
        GateResult notApplicable = new("dash-gate", true, 1, "failed", 0, "no changed path matches applies-when");

        ReviewOutcome withNotApplicable =
            VerdictEngine.Evaluate(scope, [], [notApplicable], config, Today);
        ReviewOutcome without =
            VerdictEngine.Evaluate(scope, [], [], config, Today);

        // Even a non-zero exit code on a not-applicable gate changes nothing.
        Assert.Equal(ReviewVerdict.Approve, withNotApplicable.Verdict);
        Assert.Equal(without.Verdict, withNotApplicable.Verdict);
        Assert.Equal(without.Risk, withNotApplicable.Risk);
        Assert.DoesNotContain(
            withNotApplicable.Diagnostics, d => d.Code == VerdictEngine.BlockingGateFailed);
    }

    [Fact]
    public void GateReportsRoundTripTheBaseAndTheReason()
    {
        GateReport report = GateRunner.Run(
            Config(new ReviewGate("build", ["dotnet", "--version"])),
            _temp.Path,
            baseRef: "main");

        GateReport reread = ReviewJson.ReadGates(ReviewJson.Write(report));

        Assert.Equal("main", reread.Base);
    }

    [Fact]
    public void GateReportsWrittenBeforeTheBaseStillRead()
    {
        GateReport old = ReviewJson.ReadGates(
            """{"schema":"kyber-weave.review-gates/v1","gates":[{"id":"build","blocking":true,"exitCode":0,"summary":"passed","durationMilliseconds":5}]}""");

        GateResult gate = Assert.Single(old.Gates);
        Assert.Null(old.Base);
        Assert.Null(gate.NotApplicableReason);
        Assert.True(gate.Passed);
    }
}
