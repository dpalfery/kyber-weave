using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Contract: <c>arbiter doctor</c> reports both scope sections, falls back to the global
/// mapping, and warns when a project enables Arbiter without any project hook installation.
/// </summary>
[Collection(ArbiterCommandCompositionCollection.Name)]
public sealed class ArbiterDoctorCommandTests : IDisposable
{
    private readonly TempDirectory _home = new();
    private readonly TempDirectory _repo = new();
    private readonly Func<string> _savedHome;

    public ArbiterDoctorCommandTests()
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
    public void Doctor_ReportsScopeSummary_AndWarnsWhenProjectEnabledWithoutHooks()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["warp", "opencode", "pi"]);
        WriteProjectConfig(
            """
            arbiter:
              enabled: true
            """);

        CommandExecution execution = Capture(() => new ArbiterDoctorCommand().Execute(
            null!,
            new ArbiterSettings { Path = _repo.Path }));

        Assert.Contains("GLOBAL scope", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PROJECT scope", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project overrides global", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("arbiter.enabled: true", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no project hooks installed", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ungated by design", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Warp", execution.Output, StringComparison.Ordinal);
        Assert.Contains("fallback-only", execution.Output, StringComparison.Ordinal);
        Assert.Contains("OpenCode", execution.Output, StringComparison.Ordinal);
        Assert.Contains("dispatch-unconfirmed", execution.Output, StringComparison.Ordinal);
    }

    private void WriteProjectConfig(string yaml)
    {
        string dir = Path.Combine(_repo.Path, ".kyber-weave");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "kyber-weave.yml"), yaml);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        using StringWriter writer = new();
        TextWriter saved = Console.Out;
        Console.SetOut(writer);
        try
        {
            int result = execute();
            return new CommandExecution(result, writer.ToString());
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
