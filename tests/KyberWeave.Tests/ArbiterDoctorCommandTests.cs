using System.Text.RegularExpressions;
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
        Assert.Contains("arbiter.enabled: true", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project overrides global", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no project hooks installed", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ungated by design", execution.Output, StringComparison.OrdinalIgnoreCase);
        AssertHarnessLine(execution.Output, "OpenCode", "ungated", "kyber-weave arbiter install --project --target opencode");
        AssertHarnessLine(execution.Output, "Pi", "ungated", "kyber-weave arbiter install --project --target pi");
        AssertHarnessLine(execution.Output, "Warp", "fallback-only");
        Assert.Contains("Warp", execution.Output, StringComparison.Ordinal);
        Assert.Contains("fallback-only", execution.Output, StringComparison.Ordinal);
        Assert.Contains("OpenCode", execution.Output, StringComparison.Ordinal);
        Assert.Contains("dispatch-unconfirmed", execution.Output, StringComparison.Ordinal);
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

    private const string ShadowModelTargetingYaml =
        "arbiter:\n" +
        "  enabled: true\n" +
        "  model-targeting:\n" +
        "    mode: shadow\n";

    private const string ModelTargetsJson =
        "{\"schema\":\"kyber-arbiter.model-targets/v1\",\"harness\":\"claude\"," +
        "\"preset\":{\"name\":\"work\",\"digest\":\"d\"},\"bundle-digest\":\"b\"," +
        "\"profiles\":{\"fast\":\"claude-haiku-5-5\"},\"agents\":{}}";

    /// <summary>The global model-targets file for Claude, where the global scope writes it.</summary>
    private string GlobalModelTargetsPath =>
        Path.Combine(_home.Path, ".kyber-weave", "arbiter", "model-targets.claude.json");

    /// <summary>The global Squad lock, which the targets file must be at least as new as.</summary>
    private string GlobalLockPath =>
        Path.Combine(_home.Path, ".kyber-weave", "squad", "global", "squad.lock.yml");

    /// <summary>
    /// Targeting is on and Claude is hooked, but the targets file is absent: a model override
    /// would be dropped without anyone seeing it, so the doctor warns and names the fix.
    /// </summary>
    [Fact]
    public void Doctor_WarnsModelMissingWhenTargetingIsOnAndTheHookedTargetHasNoTargetsFile()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude"]);
        WriteProjectConfig(ShadowModelTargetingYaml);

        CommandExecution execution = RunDoctor();

        Assert.Contains("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
        Assert.Contains("Run kyber-weave squad update", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>A targets file that does not parse is the same warning as a missing one.</summary>
    [Fact]
    public void Doctor_WarnsModelUnreadableWhenTheTargetsFileDoesNotParse()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude"]);
        WriteProjectConfig(ShadowModelTargetingYaml);
        WriteFile(GlobalModelTargetsPath, "{not json");

        CommandExecution execution = RunDoctor();

        Assert.Contains("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
        Assert.Contains("Run kyber-weave squad update", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A targets file older than the Squad lock was written before the last squad update, so it
    /// may name models the lock no longer does.
    /// </summary>
    [Fact]
    public void Doctor_WarnsModelStaleWhenTheTargetsFileIsOlderThanTheSquadLock()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude"]);
        WriteProjectConfig(ShadowModelTargetingYaml);
        WriteFile(GlobalModelTargetsPath, ModelTargetsJson);
        WriteFile(GlobalLockPath, "schema: kyber-squad.lock/v2\n");
        File.SetLastWriteTimeUtc(GlobalModelTargetsPath, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(GlobalLockPath, DateTime.UtcNow);

        CommandExecution execution = RunDoctor();

        Assert.Contains("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
        Assert.Contains("Run kyber-weave squad update", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>The negative for the stale case: a targets file newer than the lock is not warned.</summary>
    [Fact]
    public void Doctor_DoesNotWarnModelWhenTheTargetsFileIsCurrent()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude"]);
        WriteProjectConfig(ShadowModelTargetingYaml);
        WriteFile(GlobalModelTargetsPath, ModelTargetsJson);
        WriteFile(GlobalLockPath, "schema: kyber-squad.lock/v2\n");
        File.SetLastWriteTimeUtc(GlobalLockPath, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(GlobalModelTargetsPath, DateTime.UtcNow);

        CommandExecution execution = RunDoctor();

        Assert.DoesNotContain("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>Targeting off means no model is routed, so a missing targets file is not a warning.</summary>
    [Fact]
    public void Doctor_DoesNotWarnModelWhenTargetingIsOff()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["claude"]);
        WriteProjectConfig(
            """
            arbiter:
              enabled: true
              model-targeting:
                mode: off
            """);

        CommandExecution execution = RunDoctor();

        Assert.DoesNotContain("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>With no hooked target installed there is no override to honour, so no warning.</summary>
    [Fact]
    public void Doctor_DoesNotWarnModelWhenNoHookedTargetIsInstalled()
    {
        UserHostConfig.WriteGlobalTargets(_home.Path, ["warp"]);
        WriteProjectConfig(ShadowModelTargetingYaml);

        CommandExecution execution = RunDoctor();

        Assert.DoesNotContain("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
    }

    private CommandExecution RunDoctor() =>
        Capture(() => new ArbiterDoctorCommand().Execute(
            null!,
            new ArbiterSettings { Path = _repo.Path }));

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
