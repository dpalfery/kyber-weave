using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// The contract every shipped Squad renderer owes whatever its harness, run once per renderer in
/// <see cref="SquadCommandComposition.CreateRenderers"/>.
/// </summary>
/// <remarks>
/// Each of these used to be copied into every harness suite with only the renderer and target
/// swapped, so changing the contract meant the same edit in a dozen files, and a renderer added
/// later got whichever copies someone remembered. The target checks take their rows from the
/// production list, so a new renderer is held to them the day it ships. Resource projection and the
/// shell-implies-write record depend on the harness, so a new renderer adds its row to
/// <see cref="ResourceProjectionContracts"/> and, when it cannot isolate the shell from writes,
/// <see cref="ShellWithoutWriteContracts"/>. How a harness's files look stays in that harness's own
/// suite.
/// </remarks>
public sealed class SquadRendererSharedContractTests
{
    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    /// <summary>
    /// Where each harness puts the resource-bearing fixture's agent, and how it treats links inside
    /// an owner's resource closure. Native harnesses project them verbatim; discovery-guarded ones
    /// rewrite in-closure <c>.md</c> links so the harness's agent scanner does not load a resource
    /// as an agent; fallback ones lower the agent to a role skill and project beneath it.
    /// </summary>
    private static readonly Dictionary<SquadTarget, Func<ISquadRenderer, Task>> ResourceProjectionContracts = new()
    {
        [SquadTarget.Copilot] = renderer => SquadResourceRenderingContract.AssertNativeProjectionAsync(
            renderer, SquadTarget.Copilot, $".github/agents/{ResourceBearingSquadFixture.AgentName}.agent.md", ".github/agents", ".github/skills"),
        [SquadTarget.Claude] = renderer => SquadResourceRenderingContract.AssertNativeProjectionAsync(
            renderer, SquadTarget.Claude, $".claude/agents/{ResourceBearingSquadFixture.AgentName}.md", ".claude/agents", ".claude/skills"),
        [SquadTarget.Codex] = renderer => SquadResourceRenderingContract.AssertNativeProjectionAsync(
            renderer, SquadTarget.Codex, $".codex/agents/{ResourceBearingSquadFixture.AgentName}.toml", ".codex/agents", ".codex/skills"),
        [SquadTarget.Cursor] = renderer => SquadResourceRenderingContract.AssertNativeProjectionAsync(
            renderer, SquadTarget.Cursor, $".cursor/agents/{ResourceBearingSquadFixture.AgentName}.md", ".cursor/agents", ".cursor/skills"),
        [SquadTarget.Devin] = renderer => SquadResourceRenderingContract.AssertNativeProjectionAsync(
            renderer,
            SquadTarget.Devin,
            $".devin/agents/{ResourceBearingSquadFixture.AgentName}/AGENT.md",
            $".devin/agents/{ResourceBearingSquadFixture.AgentName}",
            ".devin/skills"),
        [SquadTarget.Factory] = renderer => SquadResourceRenderingContract.AssertNativeProjectionAsync(
            renderer, SquadTarget.Factory, $".factory/droids/{ResourceBearingSquadFixture.AgentName}.md", ".factory/droids", ".factory/skills"),
        [SquadTarget.Pi] = renderer => SquadResourceRenderingContract.AssertNativeProjectionAsync(
            renderer, SquadTarget.Pi, $".pi/agents/{ResourceBearingSquadFixture.AgentName}.md", ".pi/agents", ".pi/skills"),
        [SquadTarget.Kilo] = renderer => SquadResourceRenderingContract.AssertDiscoveryGuardedProjectionAsync(
            renderer, SquadTarget.Kilo, $".kilo/agents/{ResourceBearingSquadFixture.AgentName}.md", ".kilo/agents", ".kilo/skills"),
        [SquadTarget.OpenCode] = renderer => SquadResourceRenderingContract.AssertDiscoveryGuardedProjectionAsync(
            renderer, SquadTarget.OpenCode, $".opencode/agents/{ResourceBearingSquadFixture.AgentName}.md", ".opencode/agents", ".opencode/skills"),
        [SquadTarget.Warp] = renderer => SquadResourceRenderingContract.AssertFallbackProjectionAsync(
            renderer, SquadTarget.Warp, ".warp/skills"),
    };

    /// <summary>
    /// The harness tool names that carry shell execution and file writes. A harness that cannot
    /// grant the shell while withholding writes must say so per agent, because the shell can write
    /// anyway. <c>SubagentsOnly</c> marks a harness that lowers its primary agent to a skill, which
    /// carries no tool grant to degrade.
    /// </summary>
    private static readonly Dictionary<SquadTarget, ShellWithoutWriteContract> ShellWithoutWriteContracts = new()
    {
        [SquadTarget.Claude] = new(["Bash", "PowerShell"], ["Edit", "NotebookEdit", "Write"], SubagentsOnly: false),
        [SquadTarget.Factory] = new(["Execute"], ["ApplyPatch", "Create", "Edit"], SubagentsOnly: false),
        [SquadTarget.OpenCode] = new(["bash"], ["edit"], SubagentsOnly: false),
        [SquadTarget.Pi] = new(["bash"], ["edit", "write"], SubagentsOnly: true),
        [SquadTarget.ZCode] = new(["Bash"], ["Edit", "Write"], SubagentsOnly: false),
    };

    public static TheoryData<SquadTarget> ShippedTargets() =>
        new(SquadCommandComposition.CreateRenderers().SelectMany(renderer => renderer.SupportedTargets));

    public static TheoryData<SquadTarget> TargetsWithAResourceProjectionContract() =>
        new(ResourceProjectionContracts.Keys);

    public static TheoryData<SquadTarget> TargetsThatCannotIsolateTheShellFromWrites() =>
        new(ShellWithoutWriteContracts.Keys);

    /// <summary>
    /// Asserted over the renderers themselves, not over the targets they claim. A renderer that
    /// claims no target contributes no row to <see cref="ShippedTargets"/>, and one that copies
    /// another's target leaves its own with none, so either way every per-target check below would
    /// silently skip it.
    /// </summary>
    [Fact]
    public void EveryShippedRendererClaimsExactlyOneTargetNoOtherRendererClaims()
    {
        IReadOnlyList<ISquadRenderer> renderers = SquadCommandComposition.CreateRenderers();

        Assert.All(renderers, renderer => Assert.True(
            renderer.SupportedTargets.Count == 1,
            $"{renderer.GetType().Name} claims {renderer.SupportedTargets.Count} targets; expected exactly one."));

        string[] claimedTwice = renderers
            .SelectMany(renderer => renderer.SupportedTargets, (renderer, target) => (Renderer: renderer, Target: target))
            .GroupBy(claim => claim.Target)
            .Where(group => group.Count() > 1)
            .Select(group => $"{SquadTargetCatalog.GetToken(group.Key)} ({string.Join(", ", group.Select(claim => claim.Renderer.GetType().Name))})")
            .ToArray();
        Assert.True(claimedTwice.Length == 0, $"Targets claimed by more than one renderer: {string.Join("; ", claimedTwice)}.");
    }

    [Theory]
    [MemberData(nameof(ShippedTargets))]
    public async Task EachRendererRefusesARequestForAnotherTarget(SquadTarget target)
    {
        SquadRenderRequest request = new(
            ProductRoot,
            [AnotherTarget(target)],
            SquadDeploymentScope.Project,
            PreloadedSource: TestSquadCorpus.CanonicalSource);

        await Assert.ThrowsAsync<ArgumentException>(() => RendererFor(target).RenderAsync(request));
    }

    /// <summary>
    /// The second render loads the corpus from disk again rather than reusing the cached source, so
    /// a loader that orders anything by enumeration order fails here too, not only a renderer.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedTargets))]
    public async Task EachRendererRendersTheCanonicalCorpusByteForByteTheSameTwice(SquadTarget target)
    {
        SquadRendererRegistry registry = new([RendererFor(target)]);
        SquadRenderRequest fromDisk = new(ProductRoot, [target], SquadDeploymentScope.Project);

        SquadRenderResult first = await registry.RenderAsync(
            fromDisk with { PreloadedSource = TestSquadCorpus.CanonicalSource });
        SquadRenderResult second = await registry.RenderAsync(fromDisk);

        Assert.True(first.Success, string.Join("; ", first.Errors));
        Assert.True(second.Success, string.Join("; ", second.Errors));
        Assert.Equal(first.Files.Select(Fingerprint), second.Files.Select(Fingerprint));
        Assert.Equal(first.Degradations, second.Degradations);
    }

    [Theory]
    [MemberData(nameof(TargetsWithAResourceProjectionContract))]
    public async Task EachRendererProjectsLinkedAgentAndSkillResourcesDeterministically(SquadTarget target)
    {
        await ResourceProjectionContracts[target](RendererFor(target));
    }

    [Theory]
    [MemberData(nameof(TargetsThatCannotIsolateTheShellFromWrites))]
    public async Task EachRendererRecordsCapabilityNotIsolableWhereTheShellImpliesWrite(SquadTarget target)
    {
        ShellWithoutWriteContract contract = ShellWithoutWriteContracts[target];
        string token = SquadTargetCatalog.GetToken(target);
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(target);

        IEnumerable<SquadAgent> degradable = contract.SubagentsOnly
            ? source.Agents.Where(agent => agent.Invocation == SquadInvocation.Subagent)
            : source.Agents;
        Assert.Contains(degradable, agent => GrantsShellWithoutWrite(source, agent));

        foreach (SquadAgent agent in degradable)
        {
            SquadDegradationRecord? record = result.Degradations.FirstOrDefault(d =>
                d.CanonicalIdentity == agent.Name && d.Code == "capability-not-isolable");

            if (!GrantsShellWithoutWrite(source, agent))
            {
                Assert.True(record is null, $"'{agent.Name}' can write, so nothing is lost by granting it the shell.");
                continue;
            }

            Assert.NotNull(record);
            Assert.Equal(token, record.Target);
            Assert.Equal(agent.Name, record.OutputIdentity);
            Assert.Equal(agent.BodyDigest, record.InstructionDigest);
            Assert.NotNull(record.Details);
            foreach (string tool in contract.GrantedShellTools.Concat(contract.WithheldWriteTools))
            {
                Assert.Contains(tool, record.Details, StringComparison.Ordinal);
            }
        }

        if (contract.SubagentsOnly)
        {
            Assert.DoesNotContain(result.Degradations, d =>
                d.Code == "capability-not-isolable" &&
                source.Agents.Any(a => a.Invocation == SquadInvocation.Primary && a.Name == d.CanonicalIdentity));
        }
    }

    /// <summary>
    /// A request naming a target with no renderer fails as a whole, before the renderers that do
    /// exist run, and points at the <c>docs/todo</c> entry for adding the missing one.
    /// </summary>
    [Fact]
    public async Task TheRegistryRefusesTheWholeRequestBeforeAnyRendererRunsWhenATargetHasNoRenderer()
    {
        CountingRenderer claude = new(SquadTarget.Claude);
        SquadRendererRegistry registry = new([claude]);

        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            ProductRoot,
            [SquadTarget.Copilot, SquadTarget.Claude],
            SquadDeploymentScope.Project,
            PreloadedSource: TestSquadCorpus.CanonicalSource));

        Assert.False(result.Success);
        Assert.Empty(result.Files);
        Assert.Equal(0, claude.Calls);
        Assert.Contains(result.Errors, e => e.Contains("copilot", StringComparison.Ordinal));
        Assert.Contains(result.Errors, e => e.Contains("docs/todo", StringComparison.Ordinal));
    }

    private static ISquadRenderer RendererFor(SquadTarget target) =>
        SquadCommandComposition.CreateRenderers().Single(renderer => renderer.SupportedTargets.Contains(target));

    private static SquadTarget AnotherTarget(SquadTarget target) =>
        target == SquadTarget.Copilot ? SquadTarget.Claude : SquadTarget.Copilot;

    private static (string Target, string RelativePath, string Content) Fingerprint(SquadDeploymentFile file) =>
        (file.Target, file.RelativePath, Convert.ToHexString(file.Content.Span));

    private static bool GrantsShellWithoutWrite(SquadSource source, SquadAgent agent)
    {
        SquadCapabilityProfile profile = source.CapabilityProfiles.Profiles[agent.CapabilityProfile];
        return IsAllowed(profile, "process.execute") && !IsAllowed(profile, "filesystem.write");
    }

    private static bool IsAllowed(SquadCapabilityProfile profile, string capability) =>
        profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
        decision == SquadPermissionDecision.Allow;

    private sealed record ShellWithoutWriteContract(
        string[] GrantedShellTools,
        string[] WithheldWriteTools,
        bool SubagentsOnly);

    private sealed class CountingRenderer(SquadTarget target) : ISquadRenderer
    {
        public int Calls { get; private set; }

        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [target];

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }
    }
}
