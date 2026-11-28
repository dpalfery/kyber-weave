using KyberWeave.Contracts.Repositories;
using System.Text.RegularExpressions;
using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract: <c>arbiter doctor</c> reports both scope sections, falls back to the global
/// mapping, and warns when a project enables Arbiter without any project hook installation.
/// </summary>
public sealed class ArbiterDoctorCommandTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Doctor_ReportsScopeSummary_AndWarnsWhenProjectEnabledWithoutHooks()
    {
        _host.Get<IUserHostConfigStore>().WriteGlobalTargets(_host.HomeDirectory, ["warp", "opencode", "pi", "commandcode"]);
        WriteProjectConfig(
            """
            arbiter:
              enabled: true
            """);

        ArbiterCommandRun execution = await _host.RunAsync(token => _host.Command<ArbiterDoctorCommand>().ExecuteAsync(
            null!,
            new ArbiterSettings { Path = _host.WorkingDirectory },
            token));

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

        // Command Code enforces through its mod, so the global tier no longer holds the
        // dispatch-unconfirmed classification and the ungated project row names its fix.
        AssertHarnessLine(execution.Output, "CommandCode", "enforced-global");
        AssertHarnessLine(execution.Output, "CommandCode", "ungated", "kyber-weave arbiter install --project --target commandcode");
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

    /// <summary>The global model-targets file for Claude, under Claude's own global root.</summary>
    private string GlobalModelTargetsPath =>
        Path.Combine(_host.HomeDirectory, ".claude", ".kyber-weave", "arbiter", "model-targets.claude.json");

    /// <summary>The global Squad lock, which the targets file must be at least as new as.</summary>
    private string GlobalLockPath =>
        Path.Combine(_host.HomeDirectory, ".kyber-weave", "squad", "global", "squad.lock.yml");

    /// <summary>
    /// Targeting is on and Claude is hooked, but the targets file is absent: a model override
    /// would be dropped without anyone seeing it, so the doctor warns and names the fix.
    /// </summary>
    [Fact]
    public async Task Doctor_WarnsModelMissingWhenTargetingIsOnAndTheHookedTargetHasNoTargetsFile()
    {
        _host.Get<IUserHostConfigStore>().WriteGlobalTargets(_host.HomeDirectory, ["claude"]);
        WriteProjectConfig(ShadowModelTargetingYaml);

        ArbiterCommandRun execution = await RunDoctorAsync();

        Assert.Contains("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
        Assert.Contains("Run kyber-weave squad update", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>A targets file that does not parse is the same warning as a missing one.</summary>
    [Fact]
    public async Task Doctor_WarnsModelUnreadableWhenTheTargetsFileDoesNotParse()
    {
        _host.Get<IUserHostConfigStore>().WriteGlobalTargets(_host.HomeDirectory, ["claude"]);
        WriteProjectConfig(ShadowModelTargetingYaml);
        WriteFile(GlobalModelTargetsPath, "{not json");

        ArbiterCommandRun execution = await RunDoctorAsync();

        Assert.Contains("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
        Assert.Contains("Run kyber-weave squad update", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table with no recorded digest, such as one no deployment owns, falls back to write time: older
    /// than the Squad lock means it was written before the last squad update.
    /// </summary>
    [Fact]
    public async Task Doctor_WarnsModelStaleWhenTheTargetsFileIsOlderThanTheSquadLock()
    {
        _host.Get<IUserHostConfigStore>().WriteGlobalTargets(_host.HomeDirectory, ["claude"]);
        WriteProjectConfig(ShadowModelTargetingYaml);
        WriteFile(GlobalModelTargetsPath, ModelTargetsJson);
        _host.Clock.Advance(TimeSpan.FromHours(2));
        WriteFile(GlobalLockPath, "schema: kyber-squad.lock/v2\n");

        ArbiterCommandRun execution = await RunDoctorAsync();

        Assert.Contains("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
        Assert.Contains("Run kyber-weave squad update", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>The negative for the stale case: a targets file newer than the lock is not warned.</summary>
    [Fact]
    public async Task Doctor_DoesNotWarnModelWhenTheTargetsFileIsCurrent()
    {
        _host.Get<IUserHostConfigStore>().WriteGlobalTargets(_host.HomeDirectory, ["claude"]);
        WriteProjectConfig(ShadowModelTargetingYaml);
        WriteFile(GlobalLockPath, "schema: kyber-squad.lock/v2\n");
        _host.Clock.Advance(TimeSpan.FromHours(2));
        WriteFile(GlobalModelTargetsPath, ModelTargetsJson);

        ArbiterCommandRun execution = await RunDoctorAsync();

        Assert.DoesNotContain("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>Targeting off means no model is routed, so a missing targets file is not a warning.</summary>
    [Fact]
    public async Task Doctor_DoesNotWarnModelWhenTargetingIsOff()
    {
        _host.Get<IUserHostConfigStore>().WriteGlobalTargets(_host.HomeDirectory, ["claude"]);
        WriteProjectConfig(
            """
            arbiter:
              enabled: true
              model-targeting:
                mode: off
            """);

        ArbiterCommandRun execution = await RunDoctorAsync();

        Assert.DoesNotContain("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
    }

    /// <summary>With no hooked target installed there is no override to honour, so no warning.</summary>
    [Fact]
    public async Task Doctor_DoesNotWarnModelWhenNoHookedTargetIsInstalled()
    {
        _host.Get<IUserHostConfigStore>().WriteGlobalTargets(_host.HomeDirectory, ["warp"]);
        WriteProjectConfig(ShadowModelTargetingYaml);

        ArbiterCommandRun execution = await RunDoctorAsync();

        Assert.DoesNotContain("KW-ARB-MODEL-001", execution.Output, StringComparison.Ordinal);
    }

    private Task<ArbiterCommandRun> RunDoctorAsync() =>
        _host.RunAsync(token => _host.Command<ArbiterDoctorCommand>().ExecuteAsync(
            null!,
            new ArbiterSettings { Path = _host.WorkingDirectory },
            token));

    private void WriteFile(string path, string content) => _ = _host.Write(path, content);

    private void WriteProjectConfig(string yaml)
    {
        _ = _host.Write(_host.InWork(".kyber-weave", "kyber-weave.yml"), yaml);
    }

}
