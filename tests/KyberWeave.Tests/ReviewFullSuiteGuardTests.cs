using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pure unit tests for the full-suite admission rule: which review gates a caller may run, and what a refusal says.
/// </summary>
/// <remarks>
/// <para>
/// Only the review council and CI run the full product suite. A review gate marked <c>full-suite: true</c> is refused
/// when the caller passes no <c>--council</c> and stdin is not an interactive terminal. The refusal is reported as a
/// blocking failure whose text names the targeted alternatives. Every other gate is admitted, as is every gate when the
/// caller is the council or a human at a terminal.
/// </para>
/// <para>
/// <b>Chosen API (the contract csharp-dev implements exactly):</b>
/// </para>
/// <code>
/// namespace KyberWeave.Core.Configuration;
///
/// // In ReviewGate (ReviewConfig.cs): a new init property, default false, read from YAML key "full-suite".
/// //     public bool FullSuite { get; init; }
///
/// public static class FullSuiteGate
/// {
///     public static FullSuiteSelection Select(
///         IEnumerable&lt;ReviewGate&gt; gates, bool council, bool interactive);
/// }
///
/// public sealed record FullSuiteSelection(
///     IReadOnlyList&lt;ReviewGate&gt; Admitted,
///     IReadOnlyList&lt;FullSuiteRefusal&gt; Refused);
///
/// public sealed record FullSuiteRefusal(string GateId, string Message);
/// </code>
/// <para>
/// A refusal's <c>Message</c> must contain <c>kyber-weave review gates . --agent &lt;name&gt;</c> and
/// <c>scripts/fast-loop.sh --class 'KyberWeave.Tests.&lt;Area&gt;*'</c>. The loader reads <c>full-suite</c> through
/// <c>ReviewConfigLoader.Merge</c>, which is internal and visible to this assembly.
/// </para>
/// <para>
/// RED for this file is a compile failure on the names above, which do not exist yet. That build error is the
/// intended missing behaviour, recorded as the RED evidence.
/// </para>
/// </remarks>
public sealed class ReviewFullSuiteGuardTests
{
    private const string AgentAlternative = "kyber-weave review gates . --agent";
    private const string ClassAlternative = "scripts/fast-loop.sh --class";

    /// <summary>
    /// A caller that is neither the council nor an interactive terminal is refused every full-suite gate, and each refusal
    /// names both targeted alternatives.
    /// </summary>
    /// <remarks>
    /// A refusal with no alternative leaves the dev agent with nothing to run. Naming the two alternatives lets it run the
    /// same review gates scoped to its task, or the one area it changed.
    /// </remarks>
    [Fact]
    public void ANonCouncilNonInteractiveRunIsRefusedFromEveryFullSuiteGateWithTheTargetedAlternative()
    {
        ReviewGate[] gates =
        [
            new ReviewGate("test", ["scripts/proc-guard.sh", "run", "--", "dotnet", "test"]) { FullSuite = true },
            new ReviewGate("ts-test", ["scripts/proc-guard.sh", "run", "--", "npm", "run", "test"]) { FullSuite = true },
            new ReviewGate("cargo-test", ["cargo", "test"]) { FullSuite = true }
        ];

        FullSuiteSelection selection = FullSuiteGate.Select(gates, council: false, interactive: false);

        Assert.Equal(["test", "ts-test", "cargo-test"], selection.Refused.Select(refusal => refusal.GateId));
        Assert.All(selection.Refused, refusal =>
        {
            Assert.Contains(AgentAlternative, refusal.Message, StringComparison.Ordinal);
            Assert.Contains(ClassAlternative, refusal.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A caller that is neither the council nor an interactive terminal still runs every gate that is not marked full-suite.
    /// </summary>
    /// <remarks>
    /// The refusal is narrow. A build, a format check or a targeted gate is never blocked by it, so a dev agent keeps every
    /// check it needs.
    /// </remarks>
    [Fact]
    public void ANonCouncilNonInteractiveRunStillAdmitsEveryGateThatIsNotFullSuite()
    {
        ReviewGate[] gates =
        [
            new ReviewGate("format-whitespace", ["dotnet", "format"]),
            new ReviewGate("build", ["dotnet", "build"]),
            new ReviewGate("test", ["dotnet", "test"]) { FullSuite = true },
            new ReviewGate("ts-lint", ["npm", "run", "lint"])
        ];

        FullSuiteSelection selection = FullSuiteGate.Select(gates, council: false, interactive: false);

        Assert.Equal(["format-whitespace", "build", "ts-lint"], selection.Admitted.Select(gate => gate.Id));
    }

    /// <summary>
    /// With <c>--council</c> or an interactive terminal, nothing is refused: every gate, full-suite included, is admitted.
    /// </summary>
    /// <remarks>
    /// The council and CI pass <c>--council</c>. A human at a terminal is the operator and may run anything. Refusing either
    /// would stop the final review from running the full suite it cites.
    /// </remarks>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ACouncilOrInteractiveRunRefusesNothing(bool council, bool interactive)
    {
        ReviewGate[] gates =
        [
            new ReviewGate("build", ["dotnet", "build"]),
            new ReviewGate("test", ["dotnet", "test"]) { FullSuite = true },
            new ReviewGate("cargo-test", ["cargo", "test"]) { FullSuite = true }
        ];

        FullSuiteSelection selection = FullSuiteGate.Select(gates, council, interactive);

        Assert.Empty(selection.Refused);
        Assert.Equal(["build", "test", "cargo-test"], selection.Admitted.Select(gate => gate.Id));
    }

    /// <summary>
    /// A host gate declaring <c>full-suite: true</c> loads with <see cref="ReviewGate.FullSuite"/> set, and a gate that omits
    /// the key loads with it false.
    /// </summary>
    /// <remarks>
    /// The key is the only thing that marks a gate full-suite. If the loader dropped it, the refusal would silently never
    /// apply, and the full suite would run from any dev agent through the review gate.
    /// </remarks>
    [Fact]
    public void TheLoaderReadsFullSuiteTrueAndDefaultsTheKeyToFalse()
    {
        const string yaml = """
            review:
              gates:
                - id: test
                  run: [dotnet, test, tests/KyberWeave.Tests/KyberWeave.Tests.csproj]
                  full-suite: true
                - id: build
                  run: [dotnet, build, KyberWeave.sln]
            """;

        ReviewConfig config = ReviewConfigLoader.Merge(
            ReviewConfig.ProductDefaults,
            KyberWeaveYamlParser.Parse(yaml).Review);

        Assert.True(config.Gates.Single(gate => gate.Id == "test").FullSuite);
        Assert.False(config.Gates.Single(gate => gate.Id == "build").FullSuite);
    }
}
