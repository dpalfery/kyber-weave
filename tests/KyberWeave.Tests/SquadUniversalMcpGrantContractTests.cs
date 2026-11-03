using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Guards that the universal <c>kyber-weave</c> grant has one meaning across every target, so the
/// per-renderer copies of the entitlement rule cannot drift apart.
/// </summary>
/// <remarks>
/// The rule lives in six renderers because each harness expresses a tool grant differently: a
/// server whitelist, a wildcard, an enumerated name, a permission key, a source-declared tool
/// list. Six copies of a rule is six chances for one of them to keep an exclusion the other five
/// dropped, and no per-renderer suite can see that — each compares its renderer to its own
/// restatement. This suite compares every renderer to one source-computed projection
/// (<see cref="SquadMcpGrantProjection"/>) and to each other.
///
/// Two entitlements are asserted separately because they are different rules: <c>kyber-weave</c>
/// reaches every agent whose profile resolves and allows <c>filesystem.read</c>, while
/// <c>codegraph</c> and <c>context7</c> keep the pure-orchestrator carve-out.
/// </remarks>
public sealed class SquadUniversalMcpGrantContractTests
{
    /// <summary>
    /// Every target that renders a surface through which an MCP grant can be read. Copilot is
    /// included even though its grant is declared in source rather than derived by a renderer:
    /// the entitlement is the same rule, and a target whose declaration stops matching it is
    /// exactly the drift this guard exists to catch.
    /// </summary>
    /// <remarks>
    /// Ten targets, five grant vehicles. Claude and Copilot name wildcards and per-server tool
    /// entries, ZCode, Devin and Command Code enumerate qualified tool names, Factory whitelists
    /// declared servers, OpenCode, Kilo and Pi gate a permission key or an adapter overlay, and
    /// Antigravity writes a server launch definition. The vehicles differ; the entitlement may
    /// not, which is what this list makes testable.
    /// </remarks>
    private static readonly SquadTarget[] GrantingTargets =
    [
        SquadTarget.Claude,
        SquadTarget.ZCode,
        SquadTarget.Devin,
        SquadTarget.Factory,
        SquadTarget.OpenCode,
        SquadTarget.CommandCode,
        SquadTarget.Copilot,
        SquadTarget.Kilo,
        SquadTarget.Antigravity,
        SquadTarget.Pi
    ];

    /// <summary>
    /// The targets whose grant surface names <c>codegraph</c> and <c>context7</c> at all, and so
    /// can be read for whether the carve-out survived.
    /// </summary>
    /// <remarks>
    /// Factory whitelists only the servers the squad's own <c>mcp.json</c> declares, OpenCode,
    /// Kilo and Pi pin a single <c>kyber-weave</c> key or overlay, Antigravity writes the one
    /// declared server, and Command Code names the one server the squad declares. None of them
    /// could grant the other two before this change either, so their silence is not a carve-out
    /// to compare — it is the absence of the mechanism.
    /// </remarks>
    private static readonly SquadTarget[] OrchestratorExcludedServerTargets =
    [
        SquadTarget.Claude,
        SquadTarget.ZCode,
        SquadTarget.Devin,
        SquadTarget.Copilot
    ];

    /// <summary>
    /// The skill path a target lowers a primary agent to, for the targets that do so. A skill
    /// carries no tool list, no permission map and no <c>extensions</c>, so a target in this map
    /// has no surface on which a grant for a primary agent could be expressed.
    /// </summary>
    private static readonly IReadOnlyDictionary<SquadTarget, string> PrimarySkillPathFormats =
        new Dictionary<SquadTarget, string>
        {
            [SquadTarget.Devin] = ".devin/skills/{0}/SKILL.md",
            [SquadTarget.Pi] = ".pi/skills/{0}/SKILL.md"
        };

    /// <summary>
    /// Every target grants <c>kyber-weave</c> to exactly the agents the source entitles, and the
    /// granted set is the same on every target that has somewhere to express it.
    /// </summary>
    /// <remarks>
    /// The set is computed from the capability profiles, never pinned as a roster of names, so
    /// adding an agent or narrowing a profile moves the expectation without editing the test — and
    /// a renderer that keeps the orchestrator exclusion fails rather than agreeing with itself.
    ///
    /// "Every target that has somewhere to express it" is not a loophole. Devin and Pi lower a
    /// primary agent to a skill, and neither harness lets a skill carry a grant vehicle, so the
    /// orchestrator has no surface on those two to widen or to withhold. Parity is therefore
    /// asserted over the intersection of what each target can express, and the orchestrator's
    /// absence from that intersection is pinned separately as an exact, named list.
    /// </remarks>
    [Fact]
    public async Task EveryTargetGrantsKyberWeaveToExactlyTheAgentsTheSourceEntitles()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent conductor = Assert.Single(source.Agents, agent => agent.Name == "conductor");
        IReadOnlySet<string> entitled = SquadMcpGrantProjection.KyberWeaveEntitledAgents(
            source,
            SquadTarget.Factory);
        Assert.Contains(conductor.Name, entitled, StringComparer.Ordinal);

        Dictionary<SquadTarget, IReadOnlySet<string>> grantedByTarget = [];
        Dictionary<SquadTarget, IReadOnlySet<string>> surfaceByTarget = [];
        HashSet<string> commonSurface = [];

        foreach (SquadTarget target in GrantingTargets)
        {
            SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(target);
            Assert.True(result.Success, string.Join("; ", result.Errors));

            IReadOnlySet<string> entitledOnTarget =
                SquadMcpGrantProjection.KyberWeaveEntitledAgents(source, target);
            Assert.Equal(
                entitled.Order(StringComparer.Ordinal),
                entitledOnTarget.Order(StringComparer.Ordinal));

            HashSet<string> surface = new(StringComparer.Ordinal);
            HashSet<string> granted = new(StringComparer.Ordinal);
            foreach (SquadAgent agent in source.Agents)
            {
                bool? grant = KyberWeaveGrant(target, result, agent.Name);
                if (grant is null)
                {
                    continue;
                }

                surface.Add(agent.Name);
                if (grant.Value)
                {
                    granted.Add(agent.Name);
                }

                bool expected = entitledOnTarget.Contains(agent.Name);
                Assert.True(
                    grant.Value == expected,
                    $"Target '{SquadTargetCatalog.GetToken(target)}' renders agent "
                    + $"'{agent.Name}' (capability profile "
                    + $"'{SquadMcpGrantProjection.EffectiveProfileName(agent, target)}') with "
                    + $"kyber-weave {(grant.Value ? "granted" : "withheld")}, but the source "
                    + $"entitlement requires it {(expected ? "granted" : "withheld")}.");
            }

            Assert.NotEmpty(surface);

            // A copy, not the set itself: commonSurface is narrowed in place below, and binding it
            // to the first target's set would narrow that target's recorded surface too — which
            // reads back as a parity gap on the target whose data was mutated.
            if (commonSurface.Count == 0)
            {
                commonSurface = new HashSet<string>(surface, StringComparer.Ordinal);
            }
            else
            {
                commonSurface.IntersectWith(surface);
            }

            grantedByTarget[target] = granted;
            surfaceByTarget[target] = surface;
        }

        // Same answer everywhere, on the agents every target renders a surface for. This is the
        // assertion that fails when one renderer keeps the exclusion the others dropped.
        Assert.NotEmpty(commonSurface);

        foreach ((SquadTarget target, IReadOnlySet<string> granted) in grantedByTarget)
        {
            Assert.Equal(
                commonSurface.Order(StringComparer.Ordinal),
                granted.Intersect(commonSurface, StringComparer.Ordinal).Order(StringComparer.Ordinal));

            // Nothing outside the entitlement is granted on any target or any surface.
            Assert.Empty(granted.Except(entitled, StringComparer.Ordinal));
        }

        // The orchestrator is granted on every target that has a surface for it, and the targets
        // without one are named. Pinning the absent cells by identity is what stops this exception
        // widening: a third target that quietly stopped emitting a principal for the orchestrator
        // would change this list rather than slip through the intersection.
        string[] targetsWithoutConductorSurface =
        [
            .. surfaceByTarget
                .Where(pair => !pair.Value.Contains(conductor.Name))
                .Select(pair => SquadTargetCatalog.GetToken(pair.Key))
                .Order(StringComparer.Ordinal)
        ];
        Assert.Equal(["devin", "pi"], targetsWithoutConductorSurface);

        foreach ((SquadTarget target, IReadOnlySet<string> surface) in surfaceByTarget)
        {
            if (!surface.Contains(conductor.Name))
            {
                continue;
            }

            Assert.Contains(conductor.Name, grantedByTarget[target], StringComparer.Ordinal);
        }

        // Each absence is structural rather than a withholding, and the structure is asserted so
        // the exception cannot be satisfied by a target that merely stopped rendering the agent:
        // a primary agent lowers to a skill there, and the skill carries no grant vehicle.
        Assert.Equal(
            targetsWithoutConductorSurface,
            PrimarySkillPathFormats.Keys
                .Select(SquadTargetCatalog.GetToken)
                .Order(StringComparer.Ordinal));

        foreach ((SquadTarget target, string skillPathFormat) in PrimarySkillPathFormats)
        {
            SquadRenderResult rendered = await TestSquadCorpus.GetProjectRenderAsync(target);
            Assert.True(rendered.Success, string.Join("; ", rendered.Errors));

            // The skill exists and is the conductor's only projection, so the absence of a grant
            // is an absence of surface rather than a renderer that dropped the agent.
            string skillPath = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                skillPathFormat,
                conductor.Name);
            Assert.Contains(rendered.Files, file => file.RelativePath == skillPath);
            Assert.Null(RenderedGrantTokens(target, rendered, conductor.Name));
        }
    }

    /// <summary>
    /// <c>codegraph</c> and <c>context7</c> are withheld from exactly the pure-orchestrator subset
    /// on every target that names them.
    /// </summary>
    [Fact]
    public async Task EveryTargetWithholdsTheOrchestratorExcludedServersFromExactlyThatSubset()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent conductor = Assert.Single(source.Agents, agent => agent.Name == "conductor");
        string[] pureOrchestrators =
        [
            .. source.Agents
                .Where(agent => string.Equals(
                    SquadMcpGrantProjection.EffectiveProfileName(agent, SquadTarget.Claude),
                    SquadMcpGrantProjection.PureOrchestratorProfile,
                    StringComparison.Ordinal))
                .Select(agent => agent.Name)
        ];
        Assert.Equal([conductor.Name], pureOrchestrators);

        IReadOnlyList<string> excludedTools = SquadMcpGrantProjection.OrchestratorExcludedToolNames(source);
        Assert.NotEmpty(excludedTools);

        foreach (SquadTarget target in OrchestratorExcludedServerTargets)
        {
            SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(target);
            Assert.True(result.Success, string.Join("; ", result.Errors));

            foreach (SquadAgent agent in source.Agents)
            {
                IReadOnlyList<string>? tokens = RenderedGrantTokens(target, result, agent.Name);
                if (tokens is null)
                {
                    continue;
                }

                bool expected =
                    SquadMcpGrantProjection.GrantsOrchestratorExcludedServers(source, agent, target);
                bool actual = GrantsOrchestratorExcludedServers(target, tokens, excludedTools);

                Assert.True(
                    expected == actual,
                    $"Target '{SquadTargetCatalog.GetToken(target)}' renders agent '{agent.Name}' "
                    + $"with the orchestrator-excluded servers {(actual ? "granted" : "withheld")}, "
                    + $"but the source rule requires them {(expected ? "granted" : "withheld")}.");
            }
        }
    }

    /// <summary>
    /// The three new grant vehicles each spell the server name one character away from a value
    /// that would parse cleanly and resolve nothing, so the wrong spelling is asserted absent
    /// beside the right one.
    /// </summary>
    /// <remarks>
    /// This is the test a typo has to get past. Kilo sanitizes a server name only for characters
    /// outside <c>[a-zA-Z0-9_-]</c>, so the hyphen survives and <c>kyber-weave_*</c> is the
    /// wildcard its matcher resolves — <c>kyber_weave_*</c> is accepted without complaint and
    /// matches nothing. Pi's MCP adapter inverts that: its deferred surface maps the hyphen to an
    /// underscore, so <c>mcp__kyber_weave__</c> resolves there and the hyphenated form does not.
    /// The two spellings are the same server name, and a single test that assumed one form would
    /// pass on one target while asserting nothing on the other.
    ///
    /// Antigravity's entry is asserted key-for-key and value-for-value against the squad's own
    /// <c>mcp.json</c>, because the alternative spelling — a sequence whose items each map the
    /// server name to its definition — lists and runs under <c>agy</c> but is never called.
    /// </remarks>
    [Fact]
    public async Task TheNewGrantVehiclesSpellTheServerNameTheWayTheirHarnessResolves()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadAgent conductor = Assert.Single(source.Agents, agent => agent.Name == "conductor");

        // The two spellings are genuinely different strings, so neither assertion can be
        // satisfied by the other.
        Assert.NotEqual(
            SquadMcpGrantProjection.KiloMcpPermissionKey,
            SquadMcpGrantProjection.KiloMcpPermissionKeyMisspelled);
        Assert.Contains(
            SquadMcpGrantProjection.KyberWeaveServer,
            SquadMcpGrantProjection.KiloMcpPermissionKey,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            SquadMcpGrantProjection.KyberWeaveServer,
            SquadMcpGrantProjection.PiMcpToolEntryPrefix,
            StringComparison.Ordinal);

        // Kilo: the hyphenated wildcard, and never the underscored one.
        SquadRenderResult kilo = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Kilo);
        Assert.True(kilo.Success, string.Join("; ", kilo.Errors));
        Assert.Contains(
            conductor.Name,
            SquadMcpGrantProjection.KyberWeaveEntitledAgents(source, SquadTarget.Kilo),
            StringComparer.Ordinal);

        string kiloConductor = RenderedDocument(kilo, $".kilo/agents/{conductor.Name}.md");
        Assert.Contains(
            SquadMcpGrantProjection.KiloMcpPermissionKey,
            kiloConductor,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            SquadMcpGrantProjection.KiloMcpPermissionKeyMisspelled,
            kiloConductor,
            StringComparison.Ordinal);

        // Pi: the underscored namespace, and never the hyphenated one.
        SquadRenderResult pi = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Pi);
        Assert.True(pi.Success, string.Join("; ", pi.Errors));

        IReadOnlyList<string> piOverlay = SquadMcpGrantProjection.PiMcpOverlayToolEntries(source);
        Assert.NotEmpty(piOverlay);
        Assert.Equal(SquadMcpGrantProjection.PiMcpProxyToolEntry, piOverlay[0]);

        // A representative entitled subagent: every subagent in the shipped squad is entitled, so
        // any one of them exercises the overlay. Asserting a singleton would fail the moment a
        // second profile is narrowed, which is not the property under test.
        SquadAgent piSubagent = source.Agents.First(
            agent => agent.Invocation == SquadInvocation.Subagent &&
                     SquadMcpGrantProjection.GrantsKyberWeave(source, agent, SquadTarget.Pi));
        string piDocument = RenderedDocument(pi, $".pi/agents/{piSubagent.Name}.md");
        Assert.Contains(
            SquadMcpGrantProjection.PiMcpToolEntryPrefix,
            piDocument,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"ext:{SquadMcpGrantProjection.PiMcpAdapterExtension}/mcp__{SquadMcpGrantProjection.KyberWeaveServer}__",
            piDocument,
            StringComparison.Ordinal);
        Assert.Contains(
            SquadMcpGrantProjection.PiMcpAdapterExtension,
            piDocument,
            StringComparison.Ordinal);

        Assert.All(
            piOverlay,
            entry => Assert.Contains(entry, piDocument, StringComparison.Ordinal));

        // Antigravity: exactly the three keys, carrying the declared launch definition.
        SquadDeclaredMcpServer declaration = SquadMcpGrantProjection.DeclaredServerLaunch(
            source,
            SquadMcpGrantProjection.KyberWeaveServer)
            ?? throw new InvalidOperationException(
                "The canonical source declares no usable kyber-weave launch definition, so the "
                + "Antigravity entry cannot be checked against it.");

        SquadRenderResult antigravity = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Antigravity);
        Assert.True(antigravity.Success, string.Join("; ", antigravity.Errors));

        YamlMappingNode antigravityFrontmatter = Frontmatter(Assert.Single(
            antigravity.Files,
            file => file.RelativePath == $".agents/agents/{conductor.Name}/agent.md"));
        Assert.True(
            antigravityFrontmatter.Children.TryGetValue(
                new YamlScalarNode("mcpServers"), out YamlNode? mcpServersNode),
            "The conductor renders no 'mcpServers' key on Antigravity, so the entry shape cannot "
            + "be checked.");
        YamlMappingNode entry = Assert.IsType<YamlMappingNode>(
            Assert.Single(Assert.IsType<YamlSequenceNode>(mcpServersNode).Children));

        Assert.Equal(
            ["name", "command", "args"],
            entry.Children.Keys
                .OfType<YamlScalarNode>()
                .Select(key => key.Value ?? string.Empty));
        Assert.Equal(declaration.Name, Scalar(entry, "name"));
        Assert.Equal(declaration.Command, Scalar(entry, "command"));
        IReadOnlyList<string> args = Sequence(entry, "args")
            ?? throw new InvalidOperationException(
                "The rendered Antigravity entry declares no 'args' sequence.");
        Assert.Equal(declaration.Args, args);
    }

    /// <summary>
    /// A corpus whose profile denies <c>filesystem.read</c> grants no <c>kyber-weave</c> on any
    /// target and keeps the withholding record it already had, so the widening cannot become a
    /// fail-open.
    /// </summary>
    /// <remarks>
    /// The read-denied agent's profile is the only variable: every other agent in the fixture
    /// stays entitled, so a renderer that granted everyone — or read the entitlement off the wrong
    /// profile — fails here rather than only in the Factory suite.
    /// </remarks>
    [Fact]
    public async Task AProfileThatDeniesFileReadGrantsNoMcpOnAnyTarget()
    {
        using McpDeclarationCorpusFixture fixture =
            McpDeclarationCorpusFixture.WithFileReadDeniedAgent("review-lens");
        SquadSource source = SquadSourceLoader.Load(fixture.ProductRoot);
        SquadAgent denied = Assert.Single(source.Agents, agent => agent.Name == "review-lens");

        foreach (SquadTarget target in GrantingTargets)
        {
            Assert.False(
                SquadMcpGrantProjection.GrantsKyberWeave(source, denied, target),
                $"The fixture's read-denied agent is still entitled on "
                + $"'{SquadTargetCatalog.GetToken(target)}', so the fail-closed branch is not "
                + "exercised.");

            SquadRenderResult result = await RenderAsync(fixture.ProductRoot, target);
            bool? grant = KyberWeaveGrant(target, result, denied.Name);
            Assert.True(
                grant.HasValue,
                $"Target '{SquadTargetCatalog.GetToken(target)}' renders no grant surface for "
                + $"'{denied.Name}', so nothing about the fail-closed grant was exercised.");
            Assert.False(
                grant.Value,
                $"Target '{SquadTargetCatalog.GetToken(target)}' granted kyber-weave to the "
                + "read-denied agent.");

            // Where the target states a withholding, it still states it: a fail-closed target has
            // to say why, or the absence reads as an oversight. Four targets state nothing and are
            // excluded on purpose. Claude withholds by omission — the wildcards simply are not in
            // the subagent's tools list, which the assertion above already covers. OpenCode and
            // Kilo pin an explicit `deny` or no key at all, and Kilo's renderer documents that it
            // names no denied server because the entitlement decides only what to grant. Pi's
            // record speaks about the primary-agent projection rather than about MCP reach.
            if (target is SquadTarget.ZCode or SquadTarget.Devin or SquadTarget.CommandCode
                or SquadTarget.Factory or SquadTarget.Antigravity)
            {
                SquadDegradationRecord record = Assert.Single(
                    result.Degradations,
                    d => string.Equals(d.CanonicalIdentity, denied.Name, StringComparison.Ordinal) &&
                         string.Equals(d.Code, "permission-not-expressible", StringComparison.Ordinal));
                Assert.NotNull(record.Details);
                Assert.Contains("MCP", record.Details, StringComparison.Ordinal);

                // Antigravity names the server itself, so its record can be checked for the name
                // rather than only for the mechanism. The other four state the rule that
                // withheld it; asserting the server name there would fail on wording the renderer
                // is not required to use.
                if (target == SquadTarget.Antigravity)
                {
                    Assert.Contains(
                        SquadMcpGrantProjection.KyberWeaveServer,
                        record.Details,
                        StringComparison.Ordinal);
                }
            }
        }
    }

    /// <summary>
    /// Whether one target positively grants <c>kyber-weave</c> to one agent, or
    /// <see langword="null"/> when the target renders no surface that could carry the grant.
    /// </summary>
    /// <remarks>
    /// Each target answers in its own vocabulary: a server whitelist on Factory, a wildcard on
    /// Claude, qualified tool names on ZCode, Devin and Command Code, a permission key on OpenCode
    /// and Kilo, an adapter overlay on Pi, a server launch entry on Antigravity, and the agent's
    /// own declared entry on Copilot. Reading each on its own terms is what lets the same question
    /// be asked of all ten.
    /// </remarks>
    private static bool? KyberWeaveGrant(SquadTarget target, SquadRenderResult result, string agentName)
    {
        IReadOnlyList<string>? tokens = RenderedGrantTokens(target, result, agentName);
        if (tokens is null)
        {
            return null;
        }

        string server = SquadMcpGrantProjection.KyberWeaveServer;
        return target switch
        {
            SquadTarget.Claude => tokens.Contains($"mcp__{server}__*", StringComparer.Ordinal),
            SquadTarget.Copilot => tokens.Contains($"{server}/*", StringComparer.Ordinal),
            SquadTarget.OpenCode => tokens.Contains($"{server}_*", StringComparer.Ordinal),

            // Kilo keeps the server name's hyphen, so the wildcard is hyphenated. The misspelling
            // is not a grant either, which is the whole point of naming both spellings.
            SquadTarget.Kilo => tokens.Contains(
                SquadMcpGrantProjection.KiloMcpPermissionKey,
                StringComparer.Ordinal),

            // pi-mcp-adapter underscores the same hyphen. The proxy entry alone is not a grant:
            // it loads the adapter and names no server, so the tool entries are what decide.
            SquadTarget.Pi => tokens.Any(token => token.StartsWith(
                SquadMcpGrantProjection.PiMcpToolEntryPrefix,
                StringComparison.Ordinal)),

            // The tokens are the entry names of the rendered mcpServers sequence.
            SquadTarget.Antigravity => tokens.Contains(server, StringComparer.Ordinal),
            SquadTarget.Factory => tokens.Contains(server, StringComparer.Ordinal),
            _ => tokens.Any(token => token.StartsWith($"mcp__{server}__", StringComparison.Ordinal))
        };
    }

    /// <summary>
    /// Whether the surface grants both orchestrator-excluded servers, in whichever form the
    /// target names them.
    /// </summary>
    private static bool GrantsOrchestratorExcludedServers(
        SquadTarget target,
        IReadOnlyList<string> tokens,
        IReadOnlyList<string> qualifiedExcludedTools)
    {
        if (target != SquadTarget.Claude && target != SquadTarget.Copilot)
        {
            return qualifiedExcludedTools.All(token => tokens.Contains(token, StringComparer.Ordinal));
        }

        if (target == SquadTarget.Claude)
        {
            return tokens.Contains("mcp__codegraph__*", StringComparer.Ordinal) &&
                   tokens.Contains("mcp__context7__*", StringComparer.Ordinal);
        }

        // Copilot's catalog spells the code graph as one wildcard and context7 as its two tools,
        // which is the vocabulary its own suite pins.
        return tokens.Contains("codegraph/*", StringComparer.Ordinal) &&
               tokens.Any(token => token.StartsWith("context7/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The tool, server, or permission entries a rendered principal carries, or
    /// <see langword="null"/> when the target emits no principal for that agent.
    /// </summary>
    private static IReadOnlyList<string>? RenderedGrantTokens(
        SquadTarget target,
        SquadRenderResult result,
        string agentName)
    {
        SquadDeploymentFile? principal = target switch
        {
            SquadTarget.Claude => Find(result, $".claude/agents/{agentName}.md"),
            SquadTarget.ZCode => Find(result, $".zcode/agents/{agentName}.md")
                                 ?? Find(result, $".zcode/commands/{agentName}.md"),
            SquadTarget.Devin => Find(result, $".devin/agents/{agentName}/AGENT.md"),
            SquadTarget.Factory => Find(result, $".factory/droids/{agentName}.md"),
            SquadTarget.OpenCode => Find(result, $".opencode/agents/{agentName}.md"),
            SquadTarget.CommandCode => Find(result, $".commandcode/agents/{agentName}.md"),
            SquadTarget.Copilot => Find(result, $".github/agents/{agentName}.agent.md"),
            SquadTarget.Kilo => Find(result, $".kilo/agents/{agentName}.md"),
            SquadTarget.Antigravity => Find(result, $".agents/agents/{agentName}/agent.md"),
            SquadTarget.Pi => Find(result, $".pi/agents/{agentName}.md"),
            _ => throw new InvalidOperationException(
                $"Target '{SquadTargetCatalog.GetToken(target)}' has no MCP grant surface to read.")
        };

        if (principal is null)
        {
            return null;
        }

        YamlMappingNode frontmatter = Frontmatter(principal);

        return target switch
        {
            SquadTarget.Factory => Sequence(frontmatter, "mcpServers") ?? [],

            // Antigravity writes a sequence of {name, command, args} entries, so the entry names
            // are the token: an absent key is no grant, not a missing surface.
            SquadTarget.Antigravity => McpServerEntryNames(frontmatter),

            // OpenCode and Kilo both gate MCP through a permission map, so one reader serves both.
            SquadTarget.OpenCode or SquadTarget.Kilo => AllowedPermissionKeys(frontmatter),

            // Pi writes tools as a comma-joined scalar rather than a sequence.
            SquadTarget.Pi => PiToolEntries(frontmatter),
            SquadTarget.ZCode => Sequence(frontmatter, "tools") ?? Sequence(frontmatter, "allowed-tools") ?? [],
            _ => Sequence(frontmatter, "tools") ?? Sequence(frontmatter, "allowed-tools") ?? []
        };
    }

    /// <summary>
    /// The <c>name</c> each rendered Antigravity <c>mcpServers</c> entry declares, or nothing when
    /// the agent carries no such key.
    /// </summary>
    private static IReadOnlyList<string> McpServerEntryNames(YamlMappingNode frontmatter)
    {
        if (!frontmatter.Children.TryGetValue(new YamlScalarNode("mcpServers"), out YamlNode? node) ||
            node is not YamlSequenceNode entries)
        {
            return [];
        }

        List<string> names = [];
        foreach (YamlNode child in entries.Children)
        {
            YamlMappingNode entry = Assert.IsType<YamlMappingNode>(child);
            if (entry.Children.TryGetValue(new YamlScalarNode("name"), out YamlNode? name) &&
                name is YamlScalarNode scalar)
            {
                names.Add(scalar.Value
                          ?? throw new InvalidOperationException("Rendered mcpServers entry name is null."));
            }
        }

        return names;
    }

    /// <summary>
    /// The <c>tools</c> entries a rendered Pi agent declares. Pi writes that key as one
    /// comma-joined string rather than a sequence, and <c>none</c> for an empty list, so the
    /// reader has to split it rather than cast it.
    /// </summary>
    private static IReadOnlyList<string> PiToolEntries(YamlMappingNode frontmatter)
    {
        if (!frontmatter.Children.TryGetValue(new YamlScalarNode("tools"), out YamlNode? node) ||
            node is not YamlScalarNode { Value: string value } ||
            string.Equals(value, "none", StringComparison.Ordinal))
        {
            return [];
        }

        return
        [
            .. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ];
    }

    /// <summary>
    /// The permission keys a rendered OpenCode agent allows. Read as allowed keys rather than raw
    /// keys so the question — "does this document grant the server" — is the one the other targets
    /// answer, and an explicit <c>deny</c> stays a withholding.
    /// </summary>
    private static IReadOnlyList<string> AllowedPermissionKeys(YamlMappingNode frontmatter)
    {
        if (!frontmatter.Children.TryGetValue(new YamlScalarNode("permission"), out YamlNode? node) ||
            node is not YamlMappingNode permission)
        {
            return [];
        }

        return
        [
            .. permission.Children
                .Where(pair => pair.Value is YamlScalarNode { Value: "allow" })
                .Select(pair => Assert.IsType<YamlScalarNode>(pair.Key).Value
                                ?? throw new InvalidOperationException("Rendered permission key is null."))
        ];
    }

    private static SquadDeploymentFile? Find(SquadRenderResult result, string relativePath)
    {
        return result.Files.FirstOrDefault(file =>
            string.Equals(file.RelativePath, relativePath, StringComparison.Ordinal));
    }

    private static YamlMappingNode Frontmatter(SquadDeploymentFile file)
    {
        const string delimiter = "---\n";
        string text = Encoding.UTF8.GetString(file.Content.Span);
        Assert.StartsWith(delimiter, text, StringComparison.Ordinal);
        int end = text.IndexOf("\n---\n", delimiter.Length, StringComparison.Ordinal);
        Assert.True(end > 0, $"'{file.RelativePath}' is missing a closing frontmatter delimiter.");

        YamlStream stream = new();
        stream.Load(new StringReader(text[delimiter.Length..(end + 1)]));
        return Assert.IsType<YamlMappingNode>(stream.Documents[0].RootNode);
    }

    private static IReadOnlyList<string>? Sequence(YamlMappingNode frontmatter, string key)
    {
        if (!frontmatter.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node))
        {
            return null;
        }

        YamlSequenceNode sequence = Assert.IsType<YamlSequenceNode>(node);
        return
        [
            .. sequence.Children.Select(child => Assert.IsType<YamlScalarNode>(child).Value
                                                 ?? throw new InvalidOperationException(
                                                     $"Frontmatter key '{key}' has a null entry."))
        ];
    }

    /// <summary>The scalar value of one key on a rendered mapping, or a failure naming the key.</summary>
    private static string Scalar(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? value) ||
            value is not YamlScalarNode { Value: string scalar })
        {
            throw new InvalidOperationException(
                $"Rendered mapping is missing scalar key '{key}'. Present keys: "
                + string.Join(", ", node.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value)));
        }

        return scalar;
    }

    /// <summary>
    /// The whole rendered document at one path — frontmatter and body — for assertions that a
    /// spelling appears anywhere in the file a harness reads.
    /// </summary>
    private static string RenderedDocument(SquadRenderResult result, string relativePath)
    {
        SquadDeploymentFile file = Assert.Single(result.Files, f => f.RelativePath == relativePath);
        return Encoding.UTF8.GetString(file.Content.Span);
    }

    private static async Task<SquadRenderResult> RenderAsync(string sourceDirectory, SquadTarget target)
    {
        ISquadRenderer renderer = SquadCommandComposition.ResolveRenderer();
        SquadRenderResult result = await renderer.RenderAsync(new SquadRenderRequest(
            sourceDirectory,
            [target],
            SquadDeploymentScope.Project));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }
}
