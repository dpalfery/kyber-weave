using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Command Code's native custom-agent and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Contract verified against the published <c>command-code</c> npm package, v1.84.0, read
/// 2026-10-17 (the CLI's own bundled agent and skill loaders, not its marketing pages):
/// custom agents are a single Markdown file with YAML frontmatter at
/// <c>.commandcode/agents/&lt;name&gt;.md</c> (personal <c>~/.commandcode/agents/</c>; project
/// wins a name clash). The agents directory is read flat, not recursively, so a resource
/// closure projected beside an agent never registers as an agent. Skills are
/// <c>.commandcode/skills/&lt;name&gt;/SKILL.md</c> (personal <c>~/.commandcode/skills/</c>).
/// Command Code also reads <c>.agents/skills/</c>; Squad does not write that compatibility tree.
/// </para>
/// <para>
/// Agent frontmatter keys are <c>name</c>, <c>description</c>, <c>tools</c>,
/// <c>disallowedTools</c>, <c>model</c>, <c>reasoningEffort</c>, <c>maxTurns</c>,
/// <c>permissionMode</c>, <c>background</c>, and <c>showOutput</c>; the frontmatter is parsed as
/// real YAML. <c>tools</c> is an allow-list of snake_case tool names, and unlike Factory or
/// Claude an omitted <c>tools</c> grants <em>no</em> tools. This renderer still always emits
/// it, as a flow sequence, so an empty grant is the explicit <c>tools: []</c>. Only
/// <c>allow</c> grants; <c>ask</c> withholds and records <c>safety-narrowed</c>. Reserved
/// names are <c>explore</c>, <c>plan</c>, <c>review</c>, and <c>general</c>; no canonical
/// agent uses one.
/// </para>
/// <para>
/// A subagent is never given the <c>agent</c> or <c>agent_output</c> tools, so
/// <c>delegate</c> is not expressible.
/// </para>
/// <para>
/// <b>MCP is granted by fully qualified tool name, on the file-read entitlement.</b> Command Code
/// gives a subagent no MCP tool it does not name in its own <c>tools</c> list, so an unnamed
/// server is a server the agent cannot call. This renderer names two entitlement classes, in this
/// order, and no others. The <c>kw-docs</c> roster's <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>
/// entries reach every agent whose capability profile resolves and allows
/// <c>filesystem.read</c> — the pure orchestrator included, because that server carries
/// <c>docs_get_coding_standard_for_technology</c> (ADR 0050). The code-graph roster's entries
/// reach the agents <see cref="CapabilityDegradations.GrantsOrchestratorExcludedMcpServers"/>
/// entitles, which is the same read entitlement minus the pure orchestrator: a role that only
/// routes work has no use for a code graph, and a developer that does has been told to query one
/// by its own instructions. A library-lookup server keeps that cut-out too and is not named here,
/// because widening the carve-out for the code graph is not a widening for library lookups. Both
/// rosters come from the toolchain rather than from a list in this file, so a tool added to
/// <c>toolchain.yml</c> is granted without a renderer edit and a roster that declares nothing
/// grants nothing. Where the grant is empty — a profile that denies the read, or an unresolvable
/// one — the agent names no MCP tool and its
/// <c>permission-not-expressible</c> record states that reason rather than claiming in general
/// terms that Squad names nothing.
/// </para>
/// <para>
/// Profile-declared shared identities suppress their skill projections per the native
/// single-projection rule. Under <see cref="SquadDeploymentScope.Global"/> the physical root is
/// <c>~/.commandcode</c>, so relative paths strip the <c>.commandcode/</c> prefix.
/// </para>
/// <para>
/// <strong>Primary-agent entry point:</strong> Command Code dispatches custom agents only as
/// subagents through its task tool and offers no way to start a session as one, so a primary
/// agent whose fallback profile declares <c>no-primary-agent: skill</c> renders as both an
/// agent at <c>.commandcode/agents/&lt;name&gt;.md</c> (kept for dispatch) and an entry-point
/// skill at <c>.commandcode/skills/&lt;name&gt;/SKILL.md</c>, invoked in the main session as
/// <c>/&lt;name&gt;</c>. This is the same shape as <see cref="FactoryRenderer"/>.
/// </para>
/// </remarks>
public sealed class CommandCodeRenderer : SquadHarnessRenderer
{
    /// <summary>
    /// Lowers the semantic capability vocabulary onto Command Code's built-in tool names,
    /// verified against the <c>command-code</c> 1.84.0 tool catalog on 2026-10-17.
    /// <c>network.publish</c> is absent: no built-in tool expresses it.
    /// <c>delegate</c> is absent: subagents never receive the <c>agent</c> tool.
    /// </summary>
    /// <remarks>
    /// Emission order, fixed so a rendered agent file is byte-stable regardless of how the
    /// profile's permissions enumerate.
    /// </remarks>
    private static readonly SquadToolMap Tools = new(
        [
            ("filesystem.read", ["read_file"]),
            ("filesystem.search", ["read_directory", "grep", "glob"]),
            ("filesystem.write", ["write_file", "edit_file"]),
            ("process.execute", ["shell_command", "shell_output"]),
            ("network.read", ["web_search", "web_fetch"])
        ],
        [
            "read_file",
            "read_directory",
            "grep",
            "glob",
            "write_file",
            "edit_file",
            "shell_command",
            "shell_output",
            "web_search",
            "web_fetch"
        ]);

    private static readonly SquadYamlEmitter Yaml = new(new SerializerBuilder()
        .WithTypeConverter(new CommandCodeYamlFlowSequenceConverter())
        .Build());

    private static readonly string[] AllowedReasoningEfforts = ["off", "low", "medium", "high", "xhigh", "max"];

    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.CommandCode,
        "Command Code",
        ".commandcode",
        "agents",
        SquadModelPolicy.InheritOrBlankOmitted,
        SkillLicense: null);

    public CommandCodeRenderer()
        : base(HarnessSpec)
    {
    }

    private protected override void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent)
    {
        McpPlan mcp = context.GetOrCreateState(McpPlan.Create);

        context.AddPrincipal(RenderAgentFile(context, agent, mcp), agent.Resources);
        context.AddEntryPointSkillProjection(
            agent,
            "an agent",
            () => context.RenderSkillDocument(agent.Name, agent.Description, agent.InstructionBody, Yaml),
            (fallbackProfile, path, reused) => BuildPrimaryAgentDegradationRecords(
                agent,
                fallbackProfile,
                context.CapabilityProfiles,
                mcp,
                path,
                reused),
            () => BuildDegradationRecords(agent, context.CapabilityProfiles, mcp));
    }

    private protected override void RenderCanonicalSkills(SquadHarnessRenderContext context) =>
        context.EmitCanonicalSkills(skill =>
            context.RenderSkillDocument(skill.Name, skill.Description, skill.InstructionBody, Yaml));

    private static SquadDeploymentFile RenderAgentFile(
        SquadHarnessRenderContext context,
        SquadAgent agent,
        McpPlan mcp)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = agent.Description
        };

        string? model = context.ResolveModel(agent);
        if (model is not null)
        {
            (string bareModel, string? reasoningEffort) = ParseReasoningEffortSuffix(model);
            frontmatter["model"] = bareModel;
            if (reasoningEffort is not null)
            {
                frontmatter["reasoningEffort"] = reasoningEffort;
            }
        }

        // Always emit tools. Omitting it already grants nothing, but an explicit list keeps
        // the grant reviewable in the file, and an empty grant is the visible tools: [].
        frontmatter["tools"] = new CommandCodeYamlFlowSequence(
            ResolveTools(agent, context.CapabilityProfiles, mcp));

        return context.CreateFile(
            $"{context.AgentsDirectory}/{agent.Name}.md",
            Yaml.Compose(frontmatter, agent.InstructionBody));
    }

    /// <summary>
    /// Splits an optional trailing <c>[reasoningEffort=&lt;level&gt;]</c> suffix off a model value.
    /// Command Code's agent frontmatter accepts <c>off | low | medium | high | xhigh | max</c>
    /// (<c>command-code</c> 1.84.0), so any other level throws <see cref="SquadRenderValidationException"/>.
    /// </summary>
    internal static (string BareModel, string? ReasoningEffort) ParseReasoningEffortSuffix(string modelValue) =>
        SquadHarnessLowering.ParseReasoningEffortSuffix(modelValue, AllowedReasoningEfforts, HarnessSpec.DisplayName);

    /// <summary>
    /// Lowers a capability profile onto Command Code's tool allow-list. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants: <c>ask</c> and <c>deny</c> both
    /// withhold, which keeps the lowering non-broadening by construction.
    /// </summary>
    /// <remarks>
    /// Command Code has no per-server permission key, only the agent's own <c>tools</c> list, so
    /// the MCP entitlement is written out name by name — which is how the target consumes the
    /// roster, not how the roster is derived. Which names the roster holds, and which of them a
    /// profile is entitled to, are the shared
    /// <see cref="CapabilityDegradations.CodeGraphMcpToolNames"/>,
    /// <see cref="CapabilityDegradations.UniversalMcpToolNames"/> and
    /// <see cref="CapabilityDegradations.ResolveMcpToolNames"/> derivations — the ones Devin and
    /// ZCode consume — so an edit to the entitlement partition reaches this target instead of
    /// leaving it rendering the set the others just changed.
    /// <para>
    /// The orchestrator-excluded class is passed in as its code-graph member alone. That class
    /// covers a library-lookup server too, and this target names neither that server nor the
    /// Arbiter one: the widening this renderer carries is the code graph's, so the class is
    /// narrowed here rather than filtered out later, which keeps the per-profile decision inside
    /// the shared derivation.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> ResolveTools(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        McpPlan mcp)
    {
        return
        [
            .. Tools.Resolve(agent.CapabilityProfile, capabilityProfiles),
            .. ResolveMcpToolNames(agent.CapabilityProfile, capabilityProfiles, mcp)
        ];
    }

    /// <summary>
    /// The MCP tool names one principal's entitlement carries, through the shared resolution.
    /// </summary>
    private static IReadOnlyList<string> ResolveMcpToolNames(
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        McpPlan mcp) =>
        CapabilityDegradations.ResolveMcpToolNames(
            profileName,
            capabilityProfiles,
            mcp.CodeGraphToolNames,
            mcp.UniversalToolNames);

    /// <summary>
    /// Builds the degradation records for a primary agent that also renders as an
    /// entry-point skill: the fallback record naming the additional projection, then the
    /// agent's own records with the entry-point caveat folded into the shared
    /// permission-not-expressible record. The path is the scope-resolved deployment path the
    /// skill actually renders at, so a global install's records point at
    /// <c>skills/&lt;name&gt;/SKILL.md</c>, not the project-scope form.
    /// </summary>
    private static IEnumerable<SquadDegradationRecord> BuildPrimaryAgentDegradationRecords(
        SquadAgent agent,
        SquadFallbackProfile fallbackProfile,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        McpPlan mcp,
        string entryPointSkillPath,
        bool reusedCanonicalSkill = false)
    {
        string entryPointDetails = reusedCanonicalSkill
            ? $"The same-name canonical skill at {entryPointSkillPath} is the entry point and " +
              "carries the role; no duplicate entry-point skill is emitted, because the " +
              "canonical skill already occupies that identity."
            : $"Fallback profile '{agent.Fallback}' declares no-primary-agent: " +
              $"{fallbackProfile.NoPrimaryAgent}, so this agent also renders as an entry-point " +
              $"skill at {entryPointSkillPath}.";

        yield return new SquadDegradationRecord(
            SquadTargetCatalog.GetToken(SquadTarget.CommandCode),
            agent.Name,
            agent.Name,
            "role-skill-fallback",
            agent.BodyDigest,
            "Command Code dispatches custom agents only as subagents through its task " +
            "tool and offers no way to start a session as one. " + entryPointDetails +
            " The entry point runs in the main session under the session's permissions — " +
            "the agent's explicit tools allow-list does not apply to it. The agent file is " +
            "kept for task dispatch.");

        foreach (SquadDegradationRecord record in BuildDegradationRecords(
                     agent,
                     capabilityProfiles,
                     mcp,
                     $"The same-name entry point at {entryPointSkillPath} runs under the " +
                     "session's permissions; the agent's explicit tools allow-list does not " +
                     "apply to it."))
        {
            yield return record;
        }
    }

    private static IEnumerable<SquadDegradationRecord> BuildDegradationRecords(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        McpPlan mcp,
        string? entryPointSkillNote = null)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            yield break;
        }

        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
        {
            yield return new SquadDegradationRecord(
                SquadTargetCatalog.GetToken(SquadTarget.CommandCode),
                agent.Name,
                agent.Name,
                "safety-narrowed",
                agent.BodyDigest,
                $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                $"{string.Join(", ", narrowed)}. Command Code has no per-tool approval key in agent " +
                "frontmatter, so these narrow to deny and the corresponding tools are withheld " +
                "from the agent's tools list.");
        }

        List<string> notExpressibleDetails = [];

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
            publishDecision != SquadPermissionDecision.Deny)
        {
            notExpressibleDetails.Add(
                "Capability 'network.publish' has no Command Code tool; web-publish is withheld.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision != SquadPermissionDecision.Deny)
        {
            notExpressibleDetails.Add(
                "Capability 'delegate' cannot spawn subagents; Command Code never gives a subagent the agent tool.");
        }

        // The parent-MCP sentence states the actual condition, and only when the condition
        // holds: an agent whose tools list names any of the roster's MCP tools must not carry a
        // record claiming Squad names none. The test is on the resolved grant rather than on one
        // entitlement class, because the list now carries two classes and an agent can be
        // granted one of them while the other is empty.
        IReadOnlyList<string> grantedMcpToolNames =
            ResolveMcpToolNames(agent.CapabilityProfile, capabilityProfiles, mcp);

        if (grantedMcpToolNames.Count == 0)
        {
            string cause = CapabilityDegradations.GrantsUniversalMcpServers(
                agent.CapabilityProfile,
                capabilityProfiles)
                ? "the toolchain roster declares no tool for the servers this agent is entitled to"
                : $"capability profile '{agent.CapabilityProfile}' does not allow 'filesystem.read'";
            notExpressibleDetails.Add(
                "MCP tools reach a Command Code subagent only when named in its tools list; " +
                $"because {cause}, no MCP tool is named for this agent and parent MCP is not " +
                "inherited.");
        }

        if (entryPointSkillNote is not null)
        {
            notExpressibleDetails.Add(entryPointSkillNote);
        }

        yield return new SquadDegradationRecord(
            SquadTargetCatalog.GetToken(SquadTarget.CommandCode),
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            string.Join(" ", notExpressibleDetails));

        SquadPermissionDecision executeDecision =
            profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec)
                ? exec
                : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision =
            profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write)
                ? write
                : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            SquadTargetCatalog.GetToken(SquadTarget.CommandCode),
            agent.Name,
            agent.Name,
            agent.BodyDigest,
            executeDecision,
            writeDecision,
            ["shell_command"],
            ["edit_file", "write_file"]);

        if (notIsolable is not null)
        {
            yield return notIsolable;
        }
    }

    /// <summary>
    /// The MCP name lists this renderer grants from, resolved once per render.
    /// </summary>
    /// <remarks>
    /// Read from the toolchain roster through the shared derivations rather than a renderer-local
    /// list, so a tool renamed upstream is a source edit, the doctor check reads the same roster,
    /// and this target cannot disagree with the others about which server is which entitlement
    /// class (ADR 0050 decision 4). Two lists rather than one resolved grant, because which class
    /// an agent receives is the per-profile question
    /// <see cref="CapabilityDegradations.ResolveMcpToolNames"/> answers; this record only states
    /// the names each class carries.
    /// </remarks>
    private sealed record McpPlan(
        IReadOnlyList<string> CodeGraphToolNames,
        IReadOnlyList<string> UniversalToolNames)
    {
        public static McpPlan Create(SquadHarnessRenderContext context) => new(
            CapabilityDegradations.CodeGraphMcpToolNames(context.Source.Toolchain.RequiredMcpTools),
            CapabilityDegradations.UniversalMcpToolNames(context.Source.Toolchain.RequiredMcpTools));
    }

    /// <summary>
    /// Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    /// <see cref="CommandCodeYamlFlowSequenceConverter"/>.
    /// </summary>
    private sealed class CommandCodeYamlFlowSequence(IEnumerable<string> values) : List<string>(values);

    /// <summary>
    /// Serializes Command Code agent <c>tools</c> as an inline YAML flow
    /// sequence so an empty grant is the documented form <c>[]</c> rather than an omitted key.
    /// </summary>
    private sealed class CommandCodeYamlFlowSequenceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            return type == typeof(CommandCodeYamlFlowSequence);
        }

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            throw new NotSupportedException("Deserialization of CommandCodeYamlFlowSequence is not supported.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            if (value is not CommandCodeYamlFlowSequence items)
            {
                return;
            }

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Flow));
            foreach (string item in items)
            {
                emitter.Emit(new Scalar(
                    AnchorName.Empty,
                    TagName.Empty,
                    item,
                    ScalarStyle.Plain,
                    true,
                    true));
            }

            emitter.Emit(new SequenceEnd());
        }
    }
}
