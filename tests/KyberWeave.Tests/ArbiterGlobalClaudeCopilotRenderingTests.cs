using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the global-scope Arbiter contract for Claude and Copilot: Claude injects an owned
/// settings block under the resolved user root, and Copilot emits one global hook file with a
/// global-scoped command line instead of project-scope hooks.
/// </summary>
public sealed class ArbiterGlobalClaudeCopilotRenderingTests : IDisposable
{
    private const int HookTimeoutSeconds = 5;

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_EnabledArbiterUnderGlobalScope_ClaudeAddsOwnedSettingsBlock()
    {
        string home = Path.Combine(_fixture.Path, "home");
        string claudeRoot = Path.Combine(home, "custom-claude");
        Directory.CreateDirectory(claudeRoot);
        SquadGlobalRoots globalRoots = new(
            name => string.Equals(name, "CLAUDE_CONFIG_DIR", StringComparison.Ordinal) ? claudeRoot : null,
            home);

        SquadRenderResult result = await new ClaudeRenderer().RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Global,
            GlobalRoots: globalRoots,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds)));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.NotNull(result.Blocks);
        Assert.Contains(
            result.Blocks!,
            block => block.Target == "claude" &&
                    block.RelativePath == "settings.json" &&
                    block.Entries.Count > 0);
    }

    [Fact]
    public async Task RenderAsync_EnabledArbiterUnderGlobalScope_CopilotUsesOneGlobalHookFileWithGlobalScopeFlags()
    {
        string home = Path.Combine(_fixture.Path, "home");
        string copilotHome = Path.Combine(home, "custom-copilot");
        Directory.CreateDirectory(copilotHome);
        SquadGlobalRoots globalRoots = new(
            name => string.Equals(name, "COPILOT_HOME", StringComparison.Ordinal) ? copilotHome : null,
            home);

        SquadRenderResult result = await new CopilotRenderer().RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Global,
            GlobalRoots: globalRoots,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds)));

        Assert.True(result.Success, string.Join("; ", result.Errors));

        SquadDeploymentFile hookFile = Assert.Single(
            result.Files,
            file => file.Target == "copilot" && file.RelativePath == "hooks/kyber-arbiter.json");

        using JsonDocument document = JsonDocument.Parse(hookFile.Content);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        JsonElement hooks = root.GetProperty("hooks");
        Assert.True(hooks.TryGetProperty("preToolUse", out JsonElement preToolUse));
        JsonElement entry = Assert.Single(preToolUse.EnumerateArray());
        Assert.Equal("command", entry.GetProperty("type").GetString());
        Assert.Equal(
            "kyber-weave-arbiter hook --harness copilot-cli --scope global",
            entry.GetProperty("bash").GetString());
        Assert.Equal(
            "kyber-weave-arbiter hook --harness copilot-cli --scope global",
            entry.GetProperty("powershell").GetString());
        Assert.Equal(HookTimeoutSeconds, entry.GetProperty("timeoutSec").GetInt32());

        Assert.Contains(
            ArbiterHookWiring.TargetDegradations([SquadTarget.Copilot], SquadDeploymentScope.Global),
            record => record.Code == "arbiter-not-enforced" &&
                      record.Details != null &&
                      record.Details.Contains("enforced-global", StringComparison.Ordinal));
    }
}
