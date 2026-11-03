using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using System.Text;
using KyberWeave.Tests.Fakes;
using Xunit;
using YamlDotNet.RepresentationModel;

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
/// <see cref="ShellWithoutWriteContracts"/>. The skill document, the skill loop and degradation
/// record identity come from the shared render pipeline (ADR 0056), so they are checked here once
/// against each renderer's own <see cref="SquadHarnessSpec"/>; a harness suite keeps only what its
/// harness decides.
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

    /// <summary>
    /// Harnesses that emit every canonical skill, a suppressed shared identity included: Warp has no
    /// agent file to carry the body, and Antigravity's Native Both pattern keeps both namespaces
    /// (ADR 0022). Every other harness emits a subagent shared identity once, as its agent file.
    /// </summary>
    private static readonly HashSet<SquadTarget> EmitsEveryCanonicalSkill = [SquadTarget.Warp, SquadTarget.Antigravity];

    public static TheoryData<SquadTarget, SquadDeploymentScope> ShippedTargetsAtEachScope()
    {
        TheoryData<SquadTarget, SquadDeploymentScope> rows = [];
        foreach (SquadTarget target in SquadCommandComposition.CreateRenderers().SelectMany(renderer => renderer.SupportedTargets))
        {
            rows.Add(target, SquadDeploymentScope.Project);
            rows.Add(target, SquadDeploymentScope.Global);
        }

        return rows;
    }

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
    /// Every renderer is on the shared pipeline, so its harness facts are a spec the shared checks
    /// below can read rather than a copy each suite restates.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedTargets))]
    public void EachRendererRunsOnTheSharedPipelineWithASpecForItsOwnTarget(SquadTarget target)
    {
        SquadHarnessRenderer renderer = Assert.IsAssignableFrom<SquadHarnessRenderer>(RendererFor(target));

        Assert.Equal(target, renderer.Spec.Target);
        Assert.Equal(SquadTargetCatalog.GetToken(target), renderer.Spec.Token);
    }

    /// <summary>
    /// The skill document is the pipeline's, not the harness's: each canonical skill a harness does
    /// not suppress ships once, under the harness's skills directory, with its keys in order, its
    /// name, its description lowered by the spec's line-break rule, the spec's license, and its body
    /// verbatim.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedTargetsAtEachScope))]
    public async Task EachRendererEmitsEachCanonicalSkillOnceAsTheSharedSkillDocument(
        SquadTarget target,
        SquadDeploymentScope scope)
    {
        SquadHarnessSpec spec = SpecFor(target);
        SquadSource source = TestSquadCorpus.CanonicalSource;
        HashSet<string> suppressed = EmitsEveryCanonicalSkill.Contains(target)
            ? []
            : SquadRenderedOutput.SuppressedSkillIdentities(source);
        SquadRenderResult result = await TestSquadCorpus.GetDefaultRenderAsync(target, scope);
        string skillsDirectory = spec.ResolveDirectory("skills", scope);

        Assert.Contains(source.Skills, skill => suppressed.Count == 0 || !suppressed.Contains(skill.Name));
        foreach (SquadSkill skill in source.Skills)
        {
            string path = $"{skillsDirectory}/{skill.Name}/SKILL.md";
            if (suppressed.Contains(skill.Name))
            {
                Assert.DoesNotContain(result.Files, file => file.RelativePath == path);
                continue;
            }

            SquadDeploymentFile file = Assert.Single(result.Files, candidate => candidate.RelativePath == path);
            (YamlMappingNode frontmatter, string body) =
                SquadRenderedOutput.SplitFrontmatter(Encoding.UTF8.GetString(file.Content.Span), skill.Name);

            // An ordered mapping, not a sequence: a serializer change must not reorder the keys.
            Assert.Equal(
                spec.SkillLicense is null ? ["name", "description"] : ["name", "description", "license"],
                frontmatter.Children.Keys.OfType<YamlScalarNode>().Select(key => key.Value));
            Assert.Equal(skill.Name, SquadRenderedOutput.RequireScalar(frontmatter, "name", skill.Name));
            Assert.Equal(
                SquadHarnessLowering.SingleLine(skill.Description, spec.DescriptionLineBreaks),
                SquadRenderedOutput.RequireScalar(frontmatter, "description", skill.Name));
            Assert.Equal(
                spec.SkillLicense,
                frontmatter.Children.TryGetValue(new YamlScalarNode("license"), out YamlNode? license)
                    ? ((YamlScalarNode)license).Value
                    : null);
            Assert.Equal(Normalized(skill.InstructionBody), body);
        }
    }

    /// <summary>
    /// What a harness emits is its own, under its own root: every file carries the harness token and,
    /// at project scope, sits under the spec's root; every record names the harness and carries the
    /// body digest of the agent it describes, so a receipt cannot attribute a lowering to the wrong
    /// harness, the wrong revision or the wrong output identity.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedTargetsAtEachScope))]
    public async Task EachRendererEmitsItsFilesAndRecordsUnderItsOwnTarget(SquadTarget target, SquadDeploymentScope scope)
    {
        SquadHarnessSpec spec = SpecFor(target);
        Dictionary<string, SquadAgent> agents = TestSquadCorpus.CanonicalSource.Agents
            .ToDictionary(agent => agent.Name, StringComparer.Ordinal);
        SquadRenderResult result = await TestSquadCorpus.GetDefaultRenderAsync(target, scope);

        Assert.NotEmpty(result.Files);
        Assert.All(result.Files, file =>
        {
            Assert.Equal(spec.Token, file.Target);
            if (scope == SquadDeploymentScope.Project)
            {
                Assert.StartsWith(spec.ProjectRoot + "/", file.RelativePath, StringComparison.Ordinal);
            }
            else
            {
                Assert.False(
                    file.RelativePath.StartsWith(spec.ProjectRoot + "/", StringComparison.Ordinal),
                    $"'{file.RelativePath}' keeps the project-scope root at global scope.");
            }
        });
        Assert.All(result.Degradations, record =>
        {
            Assert.Equal(spec.Token, record.Target);
            Assert.True(agents.TryGetValue(record.CanonicalIdentity, out SquadAgent? agent), record.CanonicalIdentity);
            Assert.Equal(agent.BodyDigest, record.InstructionDigest);

            // A native harness records under the agent's own name; only role-skill lowering may
            // record the role-prefixed skill it emitted instead.
            if (SquadTargetCatalog.GetAgentProjection(target) == SquadAgentProjection.Native)
            {
                Assert.Equal(record.CanonicalIdentity, record.OutputIdentity);
            }
            else
            {
                Assert.Contains(record.OutputIdentity, new[] { record.CanonicalIdentity, $"role-{record.CanonicalIdentity}" });
            }

            Assert.Contains(record.Code, DegradationCodes);
            Assert.NotNull(record.Details);
            if (!IsKnownEmptyCommandCodeRecord(target, record))
            {
                Assert.False(string.IsNullOrWhiteSpace(record.Details), $"'{record.CanonicalIdentity}' {record.Code} has no details.");
            }
        });
    }

    public static TheoryData<SquadTarget, string> ShippedTargetsWithEachUnresolvableProfile()
    {
        TheoryData<SquadTarget, string> rows = [];
        foreach (SquadTarget target in SquadCommandComposition.CreateRenderers().SelectMany(renderer => renderer.SupportedTargets))
        {
            rows.Add(target, "model-profile");
            rows.Add(target, "capability-profile");
            rows.Add(target, "fallback");
        }

        return rows;
    }

    /// <summary>
    /// An agent naming a profile its source does not declare fails the render on every harness,
    /// whether or not that harness reads the profile, so no harness guesses at a grant or a model.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedTargetsWithEachUnresolvableProfile))]
    public async Task EachRendererRefusesAnAgentWhoseProfileDoesNotResolve(SquadTarget target, string key)
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadSource tampered = source with
        {
            Agents =
            [
                .. source.Agents.Select((agent, index) => index != 0
                    ? agent
                    : key switch
                    {
                        "model-profile" => agent with { ModelProfile = "undeclared" },
                        "capability-profile" => agent with { CapabilityProfile = "undeclared" },
                        _ => agent with { Fallback = "undeclared" }
                    })
            ]
        };

        SquadRenderValidationException refused = await Assert.ThrowsAsync<SquadRenderValidationException>(() =>
            RendererFor(target).RenderAsync(
                new SquadRenderRequest(ProductRoot, [target], SquadDeploymentScope.Project, PreloadedSource: tampered)));
        Assert.Contains(key, refused.Message, StringComparison.Ordinal);
    }

    public static TheoryData<SquadTarget, string> ShippedTargetsWithEachUnsafeOutputName()
    {
        TheoryData<SquadTarget, string> rows = [];
        foreach (SquadTarget target in SquadCommandComposition.CreateRenderers().SelectMany(renderer => renderer.SupportedTargets))
        {
            rows.Add(target, "agent-name");
            rows.Add(target, "skill-name");
            rows.Add(target, "resource-path");
        }

        return rows;
    }

    /// <summary>
    /// The pipeline's gate runs inside every renderer, not only in the helper: a source whose agent
    /// name, skill name or resource path carries path syntax fails the render before any file is
    /// built.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedTargetsWithEachUnsafeOutputName))]
    public async Task EachRendererRefusesAnOutputNameThatIsNotAPortablePath(SquadTarget target, string tampering)
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadSource tampered = tampering switch
        {
            "agent-name" => source with
            {
                Agents = [.. source.Agents.Select((agent, index) => index == 0 ? agent with { Name = "nested/agent" } : agent)]
            },
            "skill-name" => source with
            {
                Skills = [.. source.Skills.Select((skill, index) => index == 0 ? skill with { Name = ".." } : skill)]
            },
            _ => source with
            {
                Agents =
                [
                    .. source.Agents.Select((agent, index) => index == 0
                        ? agent with { Resources = [new SquadResource("../outside.md", "escape\n")] }
                        : agent)
                ]
            }
        };

        await Assert.ThrowsAsync<SquadRenderValidationException>(() => RendererFor(target).RenderAsync(
            new SquadRenderRequest(ProductRoot, [target], SquadDeploymentScope.Project, PreloadedSource: tampered)));
    }

    /// <summary>
    /// Under global scope the physical root already is the harness's global directory, so every
    /// harness ships the same tree as at project scope with only the project root dropped.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedTargets))]
    public async Task EachRendererShipsItsProjectTreeAtGlobalScopeWithoutTheProjectRoot(SquadTarget target)
    {
        string root = SpecFor(target).ProjectRoot + "/";
        SquadRenderResult project = await TestSquadCorpus.GetDefaultRenderAsync(target, SquadDeploymentScope.Project);
        SquadRenderResult global = await TestSquadCorpus.GetDefaultRenderAsync(target, SquadDeploymentScope.Global);

        Assert.NotEmpty(project.Files);
        Assert.All(project.Files, file => Assert.StartsWith(root, file.RelativePath, StringComparison.Ordinal));
        Assert.Equal(
            project.Files.Select(file => file.RelativePath[root.Length..]).Order(StringComparer.Ordinal),
            global.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The pipeline's name gate rejects only what would not stay one path segment, so a name that
    /// merely contains dots still renders on every harness that accepted it before the gate existed.
    /// </summary>
    [Theory]
    [InlineData("a..b", true)]
    [InlineData("v1.2", true)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData(" ", false)]
    public void ThePipelineRejectsOnlyCanonicalNamesThatAreNotOnePathSegment(string name, bool accepted)
    {
        Exception? thrown = Record.Exception(() => SquadHarnessRenderContext.ValidatePathSegment(name));

        if (accepted)
        {
            Assert.Null(thrown);
        }
        else
        {
            Assert.IsType<SquadRenderValidationException>(thrown);
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

    /// <summary>The degradation codes a default render may record, whatever the harness.</summary>
    private static readonly string[] DegradationCodes =
    [
        "capability-not-isolable",
        "omitted",
        "permission-not-expressible",
        "resource-links-rewritten",
        "role-skill-fallback",
        "safety-narrowed"
    ];

    /// <summary>
    /// Command Code records <c>permission-not-expressible</c> with empty details for agents it has
    /// nothing to say about. Removing those records changes receipts, so it is a deliberate output
    /// change tracked separately; this is the only empty record the contract tolerates.
    /// </summary>
    private static bool IsKnownEmptyCommandCodeRecord(SquadTarget target, SquadDegradationRecord record) =>
        target == SquadTarget.CommandCode && record.Code == "permission-not-expressible";

    private static SquadHarnessSpec SpecFor(SquadTarget target) =>
        Assert.IsAssignableFrom<SquadHarnessRenderer>(RendererFor(target)).Spec;

    private static string Normalized(string body)
    {
        string normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        return normalized.EndsWith('\n') ? normalized : normalized + "\n";
    }

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
