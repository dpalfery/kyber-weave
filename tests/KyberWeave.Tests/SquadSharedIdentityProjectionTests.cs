using System.Text;
using KyberWeave.Tests.Fakes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins shared-identity projection with a synthetic non-empty profile so the contract
/// cannot become vacuous when the shipped product corpus declares no shared identities.
/// </summary>
public sealed class SquadSharedIdentityProjectionTests
{
    [Fact]
    public async Task AntigravitySuppressesRedundantSharedIdentitySkillProjection()
    {
        using SharedIdentitySquadFixture fixture = SharedIdentitySquadFixture.Create();
        SquadRendererRegistry registry = SquadTestRegistry.Create([new AntigravityRenderer()]);
        SquadRenderRequest request = new SquadRenderRequest(
            fixture.Path,
            [SquadTarget.Antigravity],
            SquadDeploymentScope.Project).Prepared();

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));

        // Native behavior: shared identity agent emits native agent.md
        Assert.Single(
            result.Files,
            file => file.RelativePath == $".agents/agents/{SharedIdentitySquadFixture.Identity}/agent.md");

        // Canonical skill still emitted (native both pattern)
        Assert.Single(
            result.Files,
            file => file.RelativePath == $".agents/skills/{SharedIdentitySquadFixture.Identity}/SKILL.md");

        // No role-prefixed collision files in native mode (no collision prefix needed with dual namespaces)
        Assert.DoesNotContain(
            result.Files,
            file => file.RelativePath == $".agents/skills/role-{SharedIdentitySquadFixture.Identity}/SKILL.md");
    }

    [Fact]
    public async Task NativeRendererEmitsAgentAndSuppressesSharedCanonicalSkill()
    {
        using SharedIdentitySquadFixture fixture = SharedIdentitySquadFixture.Create();
        SquadRendererRegistry registry = SquadTestRegistry.Create([new CopilotRenderer()]);
        SquadRenderRequest request = new SquadRenderRequest(
            fixture.Path,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project).Prepared();

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Contains(
            result.Files,
            file => file.RelativePath == $".github/agents/{SharedIdentitySquadFixture.Identity}.agent.md");
        Assert.DoesNotContain(
            result.Files,
            file => file.RelativePath == $".github/skills/{SharedIdentitySquadFixture.Identity}/SKILL.md");
    }

    [Fact]
    public async Task RegistryAcceptsExactlyOneSharedIdentityProjection()
    {
        using SharedIdentitySquadFixture fixture = SharedIdentitySquadFixture.Create();
        SquadDeploymentFile projection = new(
            $".github/agents/{SharedIdentitySquadFixture.Identity}.agent.md",
            Encoding.UTF8.GetBytes("shared projection"),
            "copilot");
        SquadRendererRegistry registry = SquadTestRegistry.Create(
            [FakeSquadRenderer.Emitting([projection], SquadTarget.Copilot)]);
        SquadRenderRequest request = new SquadRenderRequest(
            fixture.Path,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project).Prepared();

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Single(result.Files);
    }
}
