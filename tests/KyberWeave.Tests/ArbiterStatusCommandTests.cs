using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract: <c>arbiter status</c> reports both global and project scope, brands the
/// project override, and enumerates the per-harness gap tiers.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterStatusCommandTests : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly TempDirectory _repo = new();
    private readonly Func<string> _savedHome;

    public ArbiterStatusCommandTests()
    {
        _savedHome = ArbiterCommandComposition.HomeDirectory;
        ArbiterCommandComposition.HomeDirectory = () => _home.Path;
    }

    public void Dispose()
    {
        ArbiterCommandComposition.HomeDirectory = _savedHome;
        _home.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public void Status_ReportsGlobalAndProjectScope_WithProjectOverride()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["warp", "opencode", "pi", "copilot"]);
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

        CommandExecution execution = Capture(() => new ArbiterStatusCommand().Execute(
            null!,
            new ArbiterSettings { Path = _repo.Path }));

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
        string dir = Path.Combine(_repo.Path, ".kyber-weave");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "kyber-weave.yml"), yaml);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
