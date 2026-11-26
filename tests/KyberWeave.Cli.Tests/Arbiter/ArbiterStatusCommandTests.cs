using KyberWeave.Contracts.Repositories;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract: <c>arbiter status</c> reports both global and project scope, brands the
/// project override, and enumerates the per-harness gap tiers.
/// </summary>
public sealed class ArbiterStatusCommandTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Status_ReportsGlobalAndProjectScope_WithProjectOverride()
    {
        _host.Get<IUserHostConfigStore>().WriteGlobalTargets(_host.HomeDirectory, ["warp", "opencode", "pi", "copilot", "commandcode"]);
        WriteProjectConfig(
            """
            arbiter:
              enabled: true
              global:
                targets:
                  - warp
              provider:
                kind: none
            """);

        ArbiterCommandRun execution = _host.Run(() => _host.Command<ArbiterStatusCommand>().Execute(
            null!,
            new ArbiterSettings { Path = _host.WorkingDirectory }));

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("GLOBAL scope", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PROJECT scope", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project overrides global", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Warp", execution.Output, StringComparison.Ordinal);
        Assert.Contains("fallback-only", execution.Output, StringComparison.Ordinal);
        Assert.Contains("Pi", execution.Output, StringComparison.Ordinal);
        Assert.Contains("partial", execution.Output, StringComparison.Ordinal);
        Assert.Contains("OpenCode", execution.Output, StringComparison.Ordinal);
        Assert.Contains("dispatch-unconfirmed", execution.Output, StringComparison.Ordinal);
        AssertHarnessLine(execution.Output, "Pi", "ungated", "kyber-weave arbiter install --project --target pi");
        AssertHarnessLine(execution.Output, "OpenCode", "ungated", "kyber-weave arbiter install --project --target opencode");

        // Command Code enforces through its mod, so the global tier reports the enforced
        // classification rather than the dispatch-unconfirmed one.
        AssertHarnessLine(execution.Output, "CommandCode", "enforced-global");
    }

    /// <summary>
    /// Asserts that one output line names <paramref name="harness"/> and carries every fragment, so a
    /// per-harness gating line is checked as a unit rather than as separate words elsewhere in the output.
    /// </summary>
    private static void AssertHarnessLine(string output, string harness, params string[] fragments)
    {
        IEnumerable<string> named = output.Split('\n')
            .Where(line => Regex.IsMatch(line, $@"\b{harness}\b"));
        bool found = named.Any(line => fragments.All(fragment => line.Contains(fragment, StringComparison.Ordinal)));
        Assert.True(
            found,
            $"Expected a line naming '{harness}' with [{string.Join(", ", fragments)}]. Output:\n{output}");
    }

    private void WriteProjectConfig(string yaml)
    {
        _ = _host.Write(_host.InWork(".kyber-weave", "kyber-weave.yml"), yaml);
    }
}
