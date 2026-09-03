using System.Text;
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
    public async Task AntigravityFallbackReusesSharedIdentityAsExactlyOneUnprefixedSkill()
    {
        using SharedIdentitySquadFixture fixture = SharedIdentitySquadFixture.Create();
        SquadRendererRegistry registry = new([new AntigravityRenderer()]);
        SquadRenderRequest request = new(
            fixture.Path,
            [SquadTarget.Antigravity],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Single(
            result.Files,
            file => file.RelativePath == $".agents/skills/{SharedIdentitySquadFixture.Identity}/SKILL.md");
        Assert.DoesNotContain(
            result.Files,
            file => file.RelativePath == $".agents/skills/role-{SharedIdentitySquadFixture.Identity}/SKILL.md");
    }

    [Fact]
    public async Task NativeRendererEmitsAgentAndSuppressesSharedCanonicalSkill()
    {
        using SharedIdentitySquadFixture fixture = SharedIdentitySquadFixture.Create();
        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderRequest request = new(
            fixture.Path,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project);

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
        SquadRendererRegistry registry = new([new SingleProjectionRenderer()]);
        SquadRenderRequest request = new(
            fixture.Path,
            [SquadTarget.Copilot],
            SquadDeploymentScope.Project);

        SquadRenderResult result = await registry.RenderAsync(request);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Single(result.Files);
    }

    private sealed class SingleProjectionRenderer : ISquadRenderer
    {
        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Copilot];

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SquadDeploymentFile file = new(
                $".github/agents/{SharedIdentitySquadFixture.Identity}.agent.md",
                Encoding.UTF8.GetBytes("shared projection"),
                "copilot");
            return Task.FromResult(new SquadRenderResult(true, [file], [], [], []));
        }
    }
}
