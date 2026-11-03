using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
///     Renders canonical Squad source into Factory Droids' native custom-droid and skill file formats.
/// </summary>
/// <remarks>
///     <para>
///         Contract verified against Factory documentation (docs.factory.ai/harness/subagents and
///         docs.factory.ai/harness/skills) on 2026-09-16: custom droids are Markdown with YAML
///         frontmatter at <c>.factory/droids/&lt;name&gt;.md</c> (never <c>.factory/agents/</c>);
///         personal droids live at <c>~/.factory/droids/&lt;name&gt;.md</c>. Skills are
///         <c>.factory/skills/&lt;name&gt;/SKILL.md</c> (personal <c>~/.factory/skills/</c>). Project
///         <c>.factory/</c> wins over personal on the same name. Squad does not write the
///         compatibility trees <c>~/.agents/skills/</c> or <c>~/.agent/skills/</c>.
///     </para>
///     <para>
///         Factory's <c>tools</c> key is an allow-list. Omitting it allows every tool — the same
///         silent widening CopilotRenderer remarks record for an unset Copilot <c>tools</c> key,
///         which is why ClaudeRenderer always emits an explicit list. This renderer therefore
///         never omits <c>tools</c> and never emits the rejected scalar <c>tools: all</c>. Only
///         <c>allow</c> grants a documented Factory ID; <c>ask</c> withholds and records
///         <c>safety-narrowed</c> (Factory disables <c>AskUser</c> on subagents).
///         <c>TodoWrite</c> and <c>Skill</c> are auto-injected and are not listed.
///         <c>ExitSpecMode</c> and <c>GenerateDroid</c> cannot be enabled. <c>Task</c> is not
///         available to a subagent.
///     </para>
///     <para>
///         Omitting <c>mcpServers</c> inherits the parent session's MCP tools (widening), so Factory
///         documents the key as an exclusion that is only honoured when it is present: <c>[]</c>
///         excludes every server, an absent key keeps the parent's availability, and a scalar is
///         rejected. This renderer therefore never omits the key and never emits a scalar. Its value
///         is the whitelist the droid is entitled to — the servers the squad's own <c>mcp.json</c>
///         declares (excluding the <c>kyber-weave-arbiter</c> hook server), in ordinal order — for a
///         droid whose capability profile resolves and allows <c>filesystem.read</c>, and <c>[]</c>
///         for every other droid. The declaration is partitioned by the same entitlement classes every
///         renderer uses: universal servers (<c>kyber-weave</c>) are granted to the pure orchestrator
///         (profile id <c>orchestrator</c>) as well, while orchestrator-excluded servers (today
///         <c>codegraph</c>) are granted to the same read-entitled specialists but withheld from the
///         orchestrator. The orchestrator keeps the universal grant because that server carries
///         <c>agent_coding_standards</c>, the tool that hands an agent the standards it is held to,
///         and the role that assigns work is the role deciding which standards apply to whom
///         (ADR 0050); it loses only the research surface its profile is not written for.
///         Squad does not invent Factory MCP server names: a name that reaches a droid came from the
///         squad's own declaration, and a squad that declares none grants none. Only an empty whitelist
///         records <c>permission-not-expressible</c> saying parent MCP was not inherited; a deliberately
///         whitelisted droid is a non-degraded outcome and makes no such claim, so the two outcomes stay
///         distinguishable in the degradation stream.
///     </para>
///     <para>
///         Profile-declared shared identities suppress their skill projections per the native
///         single-projection rule (covering primary agents such as <c>conductor</c> when listed).
///         Under <see cref="SquadDeploymentScope.Global" /> the physical root is <c>~/.factory</c>,
///         so relative paths strip the <c>.factory/</c> prefix.
///     </para>
///     <para>
///         <b>Arbiter block (Req 8.1, 8.2, 22.2).</b> When the render request carries an enabled
///         <see cref="SquadArbiterWiring"/> under <see cref="SquadDeploymentScope.Project"/>, the
///         renderer returns a <c>factory</c> <see cref="SquadRenderedBlock"/> for
///         <c>.factory/hooks.json</c> holding <c>PreToolUse</c> and <c>PostToolUse</c> matcher
///         groups with matcher <c>Task</c>, each with one <c>kyber-weave-arbiter hook --harness
///         factory</c> command hook carrying the wiring timeout ([F11]). Factory reads event
///         names at the top level of <c>hooks.json</c> — no <c>hooks</c> wrapper — so each entry
///         <em>is</em> one matcher group and the block splices under the event containers. The
///         command carries no <c>--caller</c>: a shared hook file gates project-wide, not per
///         agent (design §10.4), and only documented fields are written. A null or disabled
///         wiring, or Global scope (no project configuration to enforce from, Req 22.4), renders
///         no block. Rendering the block is still not enough to splice it: when the user keeps
///         hooks under the <c>hooks</c> key of <c>.factory/settings.json</c> and
///         <c>.factory/hooks.json</c> is absent, creating one would silently disable them, so
///         <see cref="FactoryHooksShadowing"/> has the lifecycle drop the block and record
///         <c>arbiter-not-enforced</c> (§10.8, R18).
///     </para>
///     <para>
///         <strong>Primary-agent entry point:</strong> the Factory app dispatches custom droids as
///         subagents and offers no session-parent selector to start a conversation as one, so a
///         primary agent with a fallback profile declaring <c>no-primary-agent: skill</c> renders as
///         both a droid at <c>.factory/droids/&lt;name&gt;.md</c> (kept for Task dispatch) and an
///         entry-point skill at <c>.factory/skills/&lt;name&gt;/SKILL.md</c> the user can invoke in
///         the main session. The skill body matches the canonical agent body verbatim and its
///         resource closure is projected beside it; the skill runs under the session's permissions,
///         so the droid's explicit <c>tools</c> allow-list and its rendered <c>mcpServers</c> value do
///         not apply to it, and the entry-point caveat names that value rather than assuming it is
///         empty. A canonical skill occupying the entry-point identity fails closed rather than
///         renaming either projection. Under <c>no-primary-agent: omit</c> no entry-point skill is
///         emitted.
///     </para>
/// </remarks>
public sealed class FactoryRenderer : SquadHarnessRenderer
{
    private const string ArbiterServer = "kyber-weave-arbiter";
    private const string ArbiterMatcher = "Task";

    /// <summary>
    ///     The verbatim sentence a droid carries when its rendered <c>mcpServers</c> is empty.
    ///     Keeping the empty case word-for-word is what makes the withholding visible and
    ///     distinguishable from a deliberate whitelist.
    /// </summary>
    private const string McpWithheldSentence =
        "mcpServers: [] excludes every MCP server so parent MCP was not inherited.";

    /// <summary>
    ///     Lowers the semantic capability vocabulary onto Factory's documented tool IDs,
    ///     verified against docs.factory.ai/harness/subagents on 2026-09-16.
    ///     <c>network.publish</c> is absent: no documented Factory tool expresses it.
    ///     <c>delegate</c> is absent: Factory withholds <c>Task</c> from subagents.
    /// </summary>
    /// <remarks>
    ///     Emission order, fixed so a rendered droid file is byte-stable regardless of how the
    ///     profile's permissions enumerate.
    /// </remarks>
    private static readonly SquadToolMap Tools = new(
        [
            ("filesystem.read", ["Read"]),
            ("filesystem.search", ["LS", "Grep", "Glob"]),
            ("filesystem.write", ["Create", "Edit", "ApplyPatch"]),
            ("process.execute", ["Execute"]),
            ("network.read", ["WebSearch", "FetchUrl"])
        ],
        [
            "Read",
            "LS",
            "Grep",
            "Glob",
            "Create",
            "Edit",
            "ApplyPatch",
            "Execute",
            "WebSearch",
            "FetchUrl"
        ]);

    private static readonly SquadYamlEmitter Yaml = new(new SerializerBuilder()
        .WithTypeConverter(new FactoryYamlFlowSequenceConverter())
        .Build());

    private static readonly string[] AllowedReasoningEfforts = ["low", "medium", "high"];

    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.Factory,
        "Factory",
        ".factory",
        "droids",
        SquadModelPolicy.InheritOrBlankOmitted,
        SkillLicense: null);

    public FactoryRenderer()
        : base(HarnessSpec)
    {
    }

    private protected override void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent)
    {
        // The whitelist source is the squad's own mcp.json, enumerated once per render and in
        // ordinal order so a rendered droid stays byte-stable across an edit that only
        // reorders the declaration. The toolchain roster is deliberately not the source: it
        // names servers an install may never configure, and a grant for one would point the
        // droid at a server with nothing behind it.
        IReadOnlyList<string> mcpServers = ResolveMcpServers(
            agent,
            context.CapabilityProfiles,
            context.GetOrCreateState(static c => DeclaredMcpServerNames(c.Source)));

        context.AddPrincipal(RenderDroid(context, agent, mcpServers), agent.Resources);

        context.AddEntryPointSkillProjection(
            agent,
            "a droid",
            () => context.RenderSkillDocument(agent.Name, agent.Description, agent.InstructionBody, Yaml),
            (fallbackProfile, path, reused) => BuildPrimaryAgentDegradationRecords(
                agent,
                fallbackProfile,
                context.CapabilityProfiles,
                mcpServers,
                path,
                reused),
            () => BuildDegradationRecords(agent, context.CapabilityProfiles, mcpServers));
    }

    private protected override void RenderCanonicalSkills(SquadHarnessRenderContext context) =>
        context.EmitCanonicalSkills(skill =>
            context.RenderSkillDocument(skill.Name, skill.Description, skill.InstructionBody, Yaml));

    /// <summary>
    /// Absence and emptiness are different claims here: FactoryHooksShadowing reads null as "no
    /// block was rendered" and an empty list as "a block was rendered and it held nothing", and
    /// only the first is true when the wiring is absent.
    /// </summary>
    private protected override void RenderArbiter(SquadHarnessRenderContext context) =>
        context.AddArbiterMatcherGroupBlock(SquadHookBlockFormat.Factory, ArbiterMatcher);

    private static SquadDeploymentFile RenderDroid(
        SquadHarnessRenderContext context,
        SquadAgent agent,
        IReadOnlyList<string> mcpServers)
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

        // Always emit tools: omitting the key allows every Factory tool (widening). An empty
        // grant is tools: [] (fail-closed), never omitted and never 'all'. An unresolvable profile
        // grants nothing (Factory omit = all tools).
        frontmatter["tools"] = new FactoryYamlFlowSequence(Tools.Resolve(agent.CapabilityProfile, context.CapabilityProfiles));

        // Omitting mcpServers inherits parent MCP. Emit the key always, carrying the droid's
        // whitelist or [] when it is entitled to none, so the widening cannot happen.
        frontmatter["mcpServers"] = new FactoryYamlFlowSequence(mcpServers);

        return context.CreateFile(
            $"{context.AgentsDirectory}/{agent.Name}.md",
            Yaml.Compose(frontmatter, agent.InstructionBody));
    }

    /// <summary>
    ///     Splits an optional trailing <c>[reasoningEffort=&lt;level&gt;]</c> suffix off a model value.
    ///     Factory's droid frontmatter accepts only <c>low | medium | high</c> (docs.factory.ai,
    ///     subagents), so any other level throws <see cref="SquadRenderValidationException" />.
    /// </summary>
    internal static (string BareModel, string? ReasoningEffort) ParseReasoningEffortSuffix(string modelValue) =>
        SquadHarnessLowering.ParseReasoningEffortSuffix(modelValue, AllowedReasoningEfforts, HarnessSpec.DisplayName);

    /// <summary>
    ///     The MCP servers a droid is entitled to declare, filtered from the squad's declared servers
    ///     according to the agent's capability profile: universal servers (<c>kyber-weave</c>) require
    ///     <see cref="CapabilityDegradations.GrantsUniversalMcpServers" />, while orchestrator-excluded
    ///     servers (<c>codegraph</c>) require
    ///     <see cref="CapabilityDegradations.GrantsOrchestratorExcludedMcpServers" />.
    /// </summary>
    /// <remarks>
    ///     Preserves ADR 0050: the conductor routes work rather than researching it, so orchestrator-excluded
    ///     servers like <c>codegraph</c> are withheld from it while allowing <c>kyber-weave</c> for coding
    ///     standards. Non-orchestrator specialists that allow <c>filesystem.read</c> receive both servers.
    /// </remarks>
    private static IReadOnlyList<string> ResolveMcpServers(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> declaredMcpServers)
    {
        bool grantsUniversal =
            CapabilityDegradations.GrantsUniversalMcpServers(agent.CapabilityProfile, capabilityProfiles);
        bool grantsOrchestratorExcluded =
            CapabilityDegradations.GrantsOrchestratorExcludedMcpServers(agent.CapabilityProfile, capabilityProfiles);

        List<string> granted = [];
        foreach (string server in declaredMcpServers)
            if (string.Equals(server, CapabilityDegradations.UniversalMcpServer, StringComparison.Ordinal))
            {
                if (grantsUniversal) granted.Add(server);
            }
            else
            {
                if (grantsOrchestratorExcluded) granted.Add(server);
            }

        return granted;
    }

    /// <summary>
    ///     The MCP server names the squad's own <c>mcp.json</c> declares, in ordinal order.
    /// </summary>
    /// <remarks>
    ///     The declaration is the source rather than the toolchain's <c>required-mcp-tools</c>
    ///     roster because the two answer different questions: the roster lists the tools the
    ///     renderers grant from, and a server the user has not configured still appears there.
    ///     Whitelisting from it would point a droid at a server with nothing behind it, which
    ///     fails silently and is exactly the failure a whitelist is meant to make honest. A
    ///     missing or non-object <c>mcpServers</c> yields no names, so a malformed declaration
    ///     fails closed instead of inheriting the parent session.
    /// </remarks>
    private static IReadOnlyList<string> DeclaredMcpServerNames(SquadSource source)
    {
        if (!source.McpConfiguration.TryGetProperty("mcpServers", out JsonElement servers) ||
            servers.ValueKind != JsonValueKind.Object)
            return [];

        return
        [
            .. servers.EnumerateObject()
                .Select(server => server.Name)
                .Where(name => !string.Equals(name, ArbiterServer, StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The YAML spelling of a rendered whitelist, so the entry-point caveat names the value the
    ///     droid beside it actually carries — <c>mcpServers: [kyber-weave]</c> or, for a droid
    ///     entitled to none, the <c>mcpServers: []</c> the caveat has always named.
    /// </summary>
    private static string DescribeMcpServers(IReadOnlyList<string> mcpServers)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["mcpServers"] = new FactoryYamlFlowSequence(mcpServers)
        };

        // Serialize through the same converter the droid frontmatter uses, so the caveat's
        // spelling is the rendered value rather than a hand-joined approximation of it. A
        // hand-joined name containing a comma would read as two servers in the receipt.
        return Yaml.Serialize(frontmatter).TrimEnd('\r', '\n');
    }

    /// <summary>
    ///     Builds the degradation records for a primary agent that also renders as an
    ///     entry-point skill: the fallback record naming the additional projection, then the
    ///     agent's own records with the entry-point caveat folded into the shared
    ///     permission-not-expressible record. The path is the scope-resolved deployment path the
    ///     skill actually renders at, so a global install's records point at
    ///     <c>skills/&lt;name&gt;/SKILL.md</c>, not the project-scope form.
    /// </summary>
    private static IEnumerable<SquadDegradationRecord> BuildPrimaryAgentDegradationRecords(
        SquadAgent agent,
        SquadFallbackProfile fallbackProfile,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> mcpServers,
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
            SquadTargetCatalog.GetToken(SquadTarget.Factory),
            agent.Name,
            agent.Name,
            "role-skill-fallback",
            agent.BodyDigest,
            "The Factory app dispatches custom droids as subagents and exposes no " +
            "session-parent selector to start a conversation as one. " + entryPointDetails +
            " The entry point runs in the main session under the session's permissions — " +
            "the droid's explicit tools allow-list does not apply to it. The droid file is " +
            "kept for Task dispatch.");

        foreach (SquadDegradationRecord record in BuildDegradationRecords(
                     agent,
                     capabilityProfiles,
                     mcpServers,
                     $"The same-name entry point at {entryPointSkillPath} runs under the " +
                     "session's permissions; the droid's explicit tools allow-list and " +
                     $"{DescribeMcpServers(mcpServers)} do not apply to it."))
            yield return record;
    }

    private static IEnumerable<SquadDegradationRecord> BuildDegradationRecords(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> mcpServers,
        string? entryPointSkillNote = null)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            // An unresolvable profile grants no MCP servers, so the rendered whitelist is empty
            // even though no permission detail is available. The exclusion still has to be
            // visible in the receipt; yielding nothing here would make it silent.
            List<string> unresolvedDetails = [];
            if (mcpServers.Count == 0) unresolvedDetails.Add(McpWithheldSentence);

            if (entryPointSkillNote is not null) unresolvedDetails.Add(entryPointSkillNote);

            if (unresolvedDetails.Count > 0)
                yield return new SquadDegradationRecord(
                    SquadTargetCatalog.GetToken(SquadTarget.Factory),
                    agent.Name,
                    agent.Name,
                    "permission-not-expressible",
                    agent.BodyDigest,
                    string.Join(" ", unresolvedDetails));

            yield break;
        }

        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
            yield return new SquadDegradationRecord(
                SquadTargetCatalog.GetToken(SquadTarget.Factory),
                agent.Name,
                agent.Name,
                "safety-narrowed",
                agent.BodyDigest,
                $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                $"{string.Join(", ", narrowed)}. Factory subagents disable AskUser, so these " +
                "narrow to deny and the corresponding tools are withheld from the droid's tools list.");

        List<string> notExpressibleDetails = [];

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
            publishDecision != SquadPermissionDecision.Deny)
            notExpressibleDetails.Add(
                "Capability 'network.publish' has no documented Factory tool; web-publish is withheld.");

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision != SquadPermissionDecision.Deny)
            notExpressibleDetails.Add(
                "Capability 'delegate' cannot spawn subagents; Factory withholds the Task tool from custom droids.");

        // The withholding sentence describes an empty whitelist only. A whitelisted droid is a
        // non-degraded outcome, so it carries no MCP claim at all — the entry-point caveat is
        // the one place a whitelisted primary's value is named, and it is passed in separately.
        if (mcpServers.Count == 0) notExpressibleDetails.Add(McpWithheldSentence);

        if (entryPointSkillNote is not null) notExpressibleDetails.Add(entryPointSkillNote);

        if (notExpressibleDetails.Count > 0)
            yield return new SquadDegradationRecord(
                SquadTargetCatalog.GetToken(SquadTarget.Factory),
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
            SquadTargetCatalog.GetToken(SquadTarget.Factory),
            agent.Name,
            agent.Name,
            agent.BodyDigest,
            executeDecision,
            writeDecision,
            ["Execute"],
            ["ApplyPatch", "Create", "Edit"]);

        if (notIsolable is not null) yield return notIsolable;
    }

    /// <summary>
    ///     Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    ///     <see cref="FactoryYamlFlowSequenceConverter" />.
    /// </summary>
    private sealed class FactoryYamlFlowSequence(IEnumerable<string> values) : List<string>(values);

    /// <summary>
    ///     Serializes Factory droid <c>tools</c> and <c>mcpServers</c> as inline YAML flow
    ///     sequences so an empty grant is the documented form <c>[]</c> rather than an omitted key.
    /// </summary>
    private sealed class FactoryYamlFlowSequenceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type)
        {
            return type == typeof(FactoryYamlFlowSequence);
        }

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            throw new NotSupportedException("Deserialization of FactoryYamlFlowSequence is not supported.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            if (value is not FactoryYamlFlowSequence items) return;

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Flow));
            foreach (string item in items)
                emitter.Emit(new Scalar(
                    AnchorName.Empty,
                    TagName.Empty,
                    item,
                    ScalarStyle.Plain,
                    true,
                    true));

            emitter.Emit(new SequenceEnd());
        }
    }
}
