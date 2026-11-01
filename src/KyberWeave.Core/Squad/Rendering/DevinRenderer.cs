using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into the custom-subagent and skill formats read by Devin
/// Desktop's local agent, Devin Local, which shares the Devin CLI's discovery and file formats.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source of truth.</b> Devin Desktop is Cognition's desktop app, shipped on 2026-06-02 as
/// the successor to Windsurf. Its local agent, Devin Local, is the Devin CLI's agent harness,
/// so it uses the CLI's skill and subagent formats and discovery. The facts below were read on
/// 2026-09-27 from Devin's published documentation (<c>docs.devin.ai/desktop/devin-local</c>,
/// <c>docs.devin.ai/cli/subagents</c>, <c>docs.devin.ai/cli/extensibility/skills</c> and its
/// <c>creating-skills</c> reference, <c>docs.devin.ai/cli/reference/permissions</c>,
/// <c>docs.devin.ai/cli/reference/configuration/read-config-from</c>, and the tool-name table in
/// <c>docs.devin.ai/cli/extensibility/hooks/lifecycle-hooks</c>) and from the Devin CLI
/// changelog. The oldest build this output is correct for is v3000.11.1 (2026-09-21), the
/// first release whose <c>allowed-tools</c> and permission rules recognize <c>write</c>.
/// Anything below that says "not documented" is a reading this renderer takes the
/// non-broadening side of, and each is recorded as a degradation rather than asserted as a
/// mapping.
/// </para>
/// <para>
/// <b>Discovery roots.</b> Custom subagents load from <c>.devin/agents/</c> and
/// <c>.agents/agents/</c> in the workspace, and from <c>agents/</c> in the Devin user
/// configuration directory (<c>~/.config/devin</c>, or <c>%APPDATA%\devin</c> on Windows — see
/// <see cref="SquadGlobalRoots"/>). Skills load natively from <c>.devin/skills/</c> and
/// <c>.agents/skills/</c>, and from <c>skills/</c> in the user directory. This renderer writes
/// only the <c>.devin/</c> roots. Devin additionally imports other tools' trees by default
/// through <c>read_config_from</c> — <c>.windsurf/skills/</c>, <c>.claude/skills/</c> and
/// <c>.claude/commands/</c>, and <c>.github/skills/</c> — and <c>.agents/</c> is where
/// <see cref="AntigravityRenderer"/> writes. Squad's own Claude, Copilot, and Antigravity
/// output is therefore visible to Devin too, so a repository carrying both would load each
/// Squad identity twice; <see cref="DevinImportOverlap"/> reports that from
/// <c>squad doctor</c> rather than this renderer writing Devin's configuration to prevent it.
/// </para>
/// <para>
/// <b>Directory layout for agents.</b> Devin reads a subagent from either
/// <c>agents/&lt;name&gt;.md</c> or <c>agents/&lt;name&gt;/AGENT.md</c>. This renderer emits the
/// directory form only. The flat form would put an agent's resource closure at
/// <c>agents/&lt;name&gt;/…</c> beside <c>agents/&lt;name&gt;.md</c> — the very directory
/// Devin's other layout claims for that name, leaving one identity in both layouts at once. Inside
/// <c>agents/&lt;name&gt;/</c> the closure sits one level deeper, beneath its authored
/// <c>&lt;name&gt;/</c> prefix, so authored links resolve verbatim (as they do for
/// <see cref="AntigravityRenderer"/>) and no resource can land where Devin looks for
/// <c>AGENT.md</c>, <c>AGENTS.md</c>, <c>agent.md</c>, or <c>agents.md</c>.
/// </para>
/// <para>
/// <b>Agent frontmatter.</b> A subagent profile is YAML frontmatter followed by the system
/// prompt. Its documented keys are <c>name</c>, <c>description</c>, <c>model</c>,
/// <c>allowed-tools</c> (alias <c>tools</c>), and <c>max-nesting</c>. This renderer emits
/// <c>name</c>, <c>description</c>, <c>model</c> (only when a <c>devin:</c> harness value
/// resolves to something other than <c>inherit</c>), and <c>allowed-tools</c>, nothing else.
/// Omitting <c>model</c> does not inherit the parent's model on Devin: a custom subagent with
/// no <c>model</c> runs on the default subagent model, which a server-side router picks at
/// spawn time unless an administrator pins one. On a subagent profile <c>allowed-tools</c> is a
/// hard restriction, so it is always emitted; what an empty list means is not documented, so a
/// resolved grant of nothing is a render error rather than an empty list that might mean
/// "every tool".
/// </para>
/// <para>
/// <b>Capability lowering.</b> Devin's core tool names are <c>read</c>,
/// <c>notebook_read</c>, <c>grep</c>, <c>glob</c>, <c>edit</c>, <c>write</c>,
/// <c>apply_patch</c>, <c>notebook_edit</c>, <c>exec</c> with its companions
/// <c>get_output</c>, <c>write_to_process</c>, and <c>kill_shell</c>, <c>webfetch</c>,
/// <c>web_search</c>, <c>todo_write</c>, and <c>skill</c>. Every tool that performs a
/// capability is granted with it, because the tool a model reaches for varies by model: a GPT
/// model edits through <c>apply_patch</c> when <c>agent.codex_tools</c> is on, and a
/// write-capable agent granted only <c>edit</c> and <c>write</c> would then be unable to edit at
/// all. <c>todo_write</c> and <c>skill</c> are granted on every subagent, matching
/// <see cref="ClaudeRenderer"/>'s ungoverned base: the first writes no file and executes
/// nothing, and the second opens only the skill tree this renderer deploys — withholding it
/// would deploy skills no subagent could reach. Only <see cref="SquadPermissionDecision.Allow"/>
/// grants anything further. <c>network.publish</c> has no tool, so an <c>allow</c> for it
/// records <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// <b><c>ask</c> narrows to withheld.</b> Devin prompts for a foreground subagent's tool call
/// only when the session's permission mode does not already approve it — Accept Edits, Smart,
/// Bypass, and Autonomous each auto-approve some of these tools, and a grant made once in a
/// session carries into every later subagent. A background subagent never prompts: it
/// auto-denies anything not already approved. A subagent profile has no <c>permissions</c> key
/// with which to force the prompt, so the approval <c>ask</c> requires cannot be guaranteed,
/// and the tool is withheld and recorded as <c>safety-narrowed</c>, as on Claude, Pi, and ZCode.
/// </para>
/// <para>
/// <b>MCP is granted by concrete tool name, on two entitlements.</b> Devin names an MCP tool
/// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> in its permission rules, so the tools declared in
/// <c>toolchain.yml</c>'s <c>required-mcp-tools</c> are appended to <c>allowed-tools</c> in that
/// form. <c>kyber-weave</c> is granted on the file-read grant alone, the pure orchestrator
/// included, because it carries <c>agent_coding_standards</c> (ADR 0049); <c>codegraph</c> and
/// <c>context7</c> keep the carve-out a role that only routes work has no use for. The
/// orchestrator never exercises the widened half of that rule on Devin: <c>conductor</c> is a
/// primary agent, Devin lowers a primary agent to a <em>skill</em>, and a Devin skill carries no
/// <c>allowed-tools</c> — this renderer emits that key only on the subagent path. So there is no
/// surface on which the orchestrator could be granted anything, and none is fabricated: emitting
/// an <c>allowed-tools</c> list on a skill would auto-approve tools, which is the opposite of the
/// restriction the key means elsewhere. Devin's generic MCP tools
/// (<c>mcp_list_tools</c>, <c>mcp_call_tool</c>, and the rest) are never granted: they reach
/// every configured server's tools, not the declared ones. The servers themselves are the
/// operator's to configure in Devin's <c>mcp_config.json</c>; this renderer never writes it.
/// </para>
/// <para>
/// <b>Delegation is withheld from subagents.</b> The parent agent dispatches a named profile
/// through <c>run_subagent</c>, and a profile reaches further subagents only when its
/// <c>max-nesting</c> allows it. Devin has no runtime-enforced delegation roster — no
/// <c>allowed_subagents</c> equivalent — so granting nested delegation would reach every
/// profile, not the canonical <c>delegates-to</c> list. This renderer therefore emits neither
/// <c>run_subagent</c> nor <c>max-nesting</c>, and an allowed <c>delegate</c> records
/// <c>permission-not-expressible</c> naming the roster that becomes unreachable. The canonical
/// bodies of the delegating roles say what to do when no agent-invocation tool is available.
/// The lowered conductor is unaffected: it runs in the main Devin Local session, where subagent
/// dispatch is always available.
/// </para>
/// <para>
/// <b>Primary-agent lowering.</b> Devin has no primary-agent primitive: Devin Local is the
/// only top-level agent, and a custom profile is always dispatched as a subagent. A
/// <see cref="SquadInvocation.Primary"/> agent whose fallback profile declares
/// <c>no-primary-agent: skill</c> therefore renders as <c>.devin/skills/&lt;name&gt;/SKILL.md</c>,
/// recording <c>role-skill-fallback</c> and <c>permission-not-expressible</c>; <c>omit</c> emits
/// nothing and records <c>omitted</c>. As on Pi, agents and skills are separate namespaces, so a
/// canonical skill already occupying the lowered identity fails the render rather than taking
/// a <c>role-</c> prefix. The lowered skill carries <c>triggers: [user]</c>, so Devin runs it
/// only when the operator asks for it. That follows <see cref="ZCodeRenderer"/>'s reasoning for
/// a slash command — an orchestrator that seizes a turn by description match is worse than one
/// started on purpose — and it matters more on Devin, because Devin Cloud discovers the same
/// <c>.devin/skills/</c> tree but loads no custom subagents ("CLI/Desktop-only today"), so an
/// auto-invoked conductor there would route work to agents that do not exist.
/// </para>
/// <para>
/// <b>Skills carry no tool keys.</b> On a skill,
/// <c>allowed-tools</c> auto-approves the listed tools rather than restricting them, and
/// <c>permissions.allow</c> does the same, so neither is ever emitted. <c>permissions.deny</c>
/// is the documented way to block a tool while an inline skill runs, and would narrow a lowered
/// primary agent — but Devin documents neither how long an inline skill counts as running nor
/// whether its deny rules reach the subagents it dispatches meanwhile. The conductor's profile
/// denies <c>edit</c>, <c>write</c>, and <c>exec</c>, the very tools every implementer it
/// dispatches needs, so emitting the denial risks disabling the whole roster rather than the
/// conductor. It is withheld until a real install shows the rules stay in the conductor's own
/// turn, and the lowered agent's capability decisions are recorded as not enforced.
/// </para>
/// </remarks>
public sealed class DevinRenderer : ISquadRenderer
{
    private const string TargetToken = "devin";
    private const string AgentsDirectory = ".devin/agents";
    private const string SkillsDirectory = ".devin/skills";

    /// <summary>
    /// The declared MCP server every read-entitled subagent is granted, the pure orchestrator
    /// included, because it carries <c>agent_coding_standards</c> (ADR 0049). Devin reads the
    /// entitlement from the envelope profile the harness sees
    /// (<see cref="SquadAgent.DevinCapabilityProfile"/>), not the shared one.
    /// </summary>
    private const string UniversalMcpServer = "kyber-weave";

    /// <summary>
    /// Lowers the semantic capability vocabulary onto Devin's core tool names, every tool that
    /// performs a capability included. <c>network.publish</c> is absent because no tool
    /// expresses it, and <c>delegate</c> because granting it cannot keep the roster (see the
    /// class remarks).
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityTools =
    [
        ("filesystem.read", ["read", "notebook_read"]),
        ("filesystem.search", ["grep", "glob"]),
        ("filesystem.write", ["edit", "write", "apply_patch", "notebook_edit"]),
        ("process.execute", ["exec", "get_output", "write_to_process", "kill_shell"]),
        ("network.read", ["webfetch", "web_search"])
    ];

    /// <summary>
    /// The lowered primary agent's <c>triggers</c>: the operator may start it, the model may not
    /// (see the class remarks).
    /// </summary>
    private static readonly string[] UserOnlyTriggers = ["user"];

    /// <summary>Granted on every subagent regardless of capability profile.</summary>
    private static readonly string[] UngovernedTools = ["todo_write", "skill"];

    /// <summary>
    /// Fixed tool emission order, so a rendered file is byte-stable regardless of how the
    /// capability profile's permissions happen to enumerate.
    /// </summary>
    private static readonly string[] ToolOrder =
    [
        "todo_write", "skill", "read", "notebook_read", "grep", "glob", "edit", "write",
        "apply_patch", "notebook_edit", "exec", "get_output", "write_to_process", "kill_shell",
        "webfetch", "web_search"
    ];

    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    /// <summary>
    /// YamlDotNet does not document <see cref="ISerializer"/> as thread-safe, and the registry
    /// may dispatch renderers concurrently, so serialization takes this lock.
    /// </summary>
    private static readonly object SerializerLock = new();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Devin];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Devin))
        {
            throw new ArgumentException(
                "DevinRenderer was asked to render a target other than Devin.",
                nameof(request));
        }

        if (request.Targets.Count == 0)
        {
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }

        SquadSource source = SquadRenderSource.Load(request, SquadTarget.Devin);
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

        // A subagent shared identity is suppressed because the agent file carries the identical
        // body; a primary shared identity is the thin wrapper whose canonical skill IS the entry
        // point, so suppressing it would leave the wrapper pointing at nothing.
        HashSet<string> suppressedSkillIdentities =
            SquadSharedIdentityProjection.SuppressedSkillIdentities(source, sharedIdentities);

        // Read upfront so the primary-agent fail-closed check can see whether a canonical
        // skill already occupies the lowered identity, whichever loop reaches it first.
        HashSet<string> skillIdentities = source.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The declared vocabulary, not a renderer-local copy: a capability added to
        // profiles/capabilities.yml must appear in a lowered agent's
        // permission-not-expressible details without a renderer change.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

        // Read from canonical source rather than a renderer-local list, so a tool renamed
        // upstream is a source edit, and so the doctor check reads the same roster. Split by
        // entitlement class, because the two classes are granted on different rules.
        IReadOnlyList<string> orchestratorExcludedMcpToolNames =
            QualifiedMcpToolNames(source, server => !IsUniversalMcpServer(server));
        IReadOnlyList<string> universalMcpToolNames =
            QualifiedMcpToolNames(source, IsUniversalMcpServer);
        IReadOnlyList<string> mcpServerNames = DeclaredMcpServerNames(source);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        foreach (SquadAgent agent in source.Agents)
        {
            if (agent.Invocation == SquadInvocation.Subagent)
            {
                SquadDeploymentFile principal = RenderSubagent(
                    agent,
                    source.ModelProfiles.Profiles,
                    source.CapabilityProfiles.Profiles,
                    orchestratorExcludedMcpToolNames,
                    universalMcpToolNames,
                    request.Scope);
                files.Add(principal);
                SquadResourceProjection.Append(files, principal, agent.Resources);

                degradations.AddRange(BuildSubagentDegradations(
                    agent,
                    source.CapabilityProfiles.Profiles,
                    mcpServerNames));
                continue;
            }

            SquadFallbackProfile fallbackProfile = source.FallbackProfiles.Profiles[agent.Fallback];
            if (string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal))
            {
                if (skillIdentities.Contains(agent.Name))
                {
                    if (sharedIdentities.Contains(agent.Name))
                    {
                        // The canonical skill is the entry point and is emitted by the skill
                        // loop, so no lowered skill is emitted here; one identity, one
                        // projection.
                        degradations.AddRange(BuildPrimaryAgentDegradations(
                            agent,
                            fallbackProfile,
                            source.CapabilityProfiles.Profiles,
                            capabilityVocabulary,
                            true));
                        continue;
                    }

                    throw new SquadRenderValidationException(
                        $"Cannot lower primary agent '{agent.Name}' to a Devin skill: a canonical " +
                        $"skill named '{agent.Name}' already occupies that identity. Devin keeps " +
                        "agents and skills in separate namespaces with no role-prefixed fallback, " +
                        "so this fails closed rather than renaming either one.");
                }

                SquadDeploymentFile principal = RenderSkill(
                    agent.Name,
                    agent.Description,
                    agent.InstructionBody,
                    true,
                    request.Scope);
                files.Add(principal);
                SquadResourceProjection.Append(files, principal, agent.Resources);

                degradations.AddRange(BuildPrimaryAgentDegradations(
                    agent,
                    fallbackProfile,
                    source.CapabilityProfiles.Profiles,
                    capabilityVocabulary));
            }
            else if (string.Equals(fallbackProfile.NoPrimaryAgent, "omit", StringComparison.Ordinal))
            {
                degradations.Add(new SquadDegradationRecord(
                    TargetToken,
                    agent.Name,
                    agent.Name,
                    "omitted",
                    agent.BodyDigest,
                    $"Fallback profile '{agent.Fallback}' declares no-primary-agent: " +
                    "omit; Devin has no primary-agent primitive for this agent to render " +
                    "onto, so no file is emitted."));
            }
            else
            {
                throw new SquadRenderValidationException(
                    $"Fallback profile '{agent.Fallback}' declares unsupported no-primary-agent " +
                    $"value '{fallbackProfile.NoPrimaryAgent}' for primary agent '{agent.Name}'.");
            }
        }

        foreach (SquadSkill skill in source.Skills)
        {
            // A profile-declared shared identity has one canonical projection, per the same
            // rule ClaudeRenderer follows. A primary shared identity is the exception: its
            // canonical skill is the entry point and must be emitted.
            if (suppressedSkillIdentities.Contains(skill.Name))
            {
                continue;
            }

            SquadDeploymentFile principal = RenderSkill(
                skill.Name,
                skill.Description,
                skill.InstructionBody,
                false,
                request.Scope);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, skill.Resources);
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static SquadDeploymentFile RenderSubagent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> orchestratorExcludedMcpToolNames,
        IReadOnlyList<string> universalMcpToolNames,
        SquadDeploymentScope scope)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = CollapseToSingleLine(agent.Description)
        };

        string? model = ResolveDevinModel(agent, modelProfiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        frontmatter["allowed-tools"] = ResolveTools(
            agent,
            capabilityProfiles,
            orchestratorExcludedMcpToolNames,
            universalMcpToolNames);

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);
        }

        return new SquadDeploymentFile(
            $"{ResolvePrefixedDirectory(AgentsDirectory, scope)}/{agent.Name}/AGENT.md",
            Encoding.UTF8.GetBytes(content),
            TargetToken);
    }

    private static SquadDeploymentFile RenderSkill(
        string name,
        string description,
        string instructionBody,
        bool userInvokedOnly,
        SquadDeploymentScope scope)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = CollapseToSingleLine(description)
        };

        if (userInvokedOnly)
        {
            frontmatter["triggers"] = UserOnlyTriggers;
        }

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, instructionBody);
        }

        return new SquadDeploymentFile(
            $"{ResolvePrefixedDirectory(SkillsDirectory, scope)}/{name}/SKILL.md",
            Encoding.UTF8.GetBytes(content),
            TargetToken);
    }

    /// <summary>
    /// Global scope writes beneath the Devin user configuration directory itself, so the
    /// project's <c>.devin/</c> prefix is dropped there.
    /// </summary>
    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        return scope == SquadDeploymentScope.Project
            ? baseDirectory
            : baseDirectory[".devin/".Length..];
    }

    /// <summary>
    /// The <c>devin</c> harness value when the model profile declares one, otherwise the
    /// target-neutral <c>default</c>; either an explicit or a defaulted <c>inherit</c> omits
    /// <c>model</c>, which on Devin leaves the subagent on the router-chosen default subagent
    /// model rather than the parent's.
    /// </summary>
    private static string? ResolveDevinModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        string resolved = profile.HarnessModels.TryGetValue(TargetToken, out string? devinModel)
            ? devinModel
            : profile.Default;
        return string.Equals(resolved, "inherit", StringComparison.Ordinal) ? null : resolved;
    }

    /// <summary>
    /// Lowers a capability profile onto Devin's tool names. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants, which keeps the lowering
    /// non-broadening by construction; an unresolvable profile grants only the ungoverned base.
    /// </summary>
    private static string[] ResolveTools(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> orchestratorExcludedMcpToolNames,
        IReadOnlyList<string> universalMcpToolNames)
    {
        HashSet<string> granted = new(UngovernedTools, StringComparer.Ordinal);
        string profileName = agent.DevinCapabilityProfile ?? agent.CapabilityProfile;

        if (capabilityProfiles.TryGetValue(profileName, out SquadCapabilityProfile? profile))
        {
            foreach ((string capability, string[] tools) in CapabilityTools)
            {
                if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                    decision == SquadPermissionDecision.Allow)
                {
                    granted.UnionWith(tools);
                }
            }
        }

        // MCP names append after the built-ins: they come from source, so they cannot join the
        // fixed renderer-local order, and each group is already server-then-tool from the caller.
        string[] resolved =
        [
            .. ToolOrder.Where(granted.Contains),
            .. ResolveMcpToolNames(
                profileName,
                capabilityProfiles,
                orchestratorExcludedMcpToolNames,
                universalMcpToolNames)
        ];
        if (resolved.Length == 0)
        {
            // Unreachable while the ungoverned base is non-empty, and deliberately loud if that
            // ever changes: what Devin does with an empty allow-list is not documented, and the
            // one reading this renderer must never risk is "every tool".
            throw new SquadRenderValidationException(
                $"Agent '{agent.Name}' resolved to an empty Devin 'allowed-tools' list, which " +
                "cannot be emitted as a restriction.");
        }

        return resolved;
    }

    /// <summary>
    /// The MCP tool names a subagent's entitlement carries: the orchestrator-excluded servers
    /// first, then the universal server, each in the roster's server-then-tool order.
    /// </summary>
    /// <remarks>
    /// Read from the envelope profile (<see cref="SquadAgent.DevinCapabilityProfile"/>) because
    /// that is the profile Devin's harness sees; the shared profile's decision is not what the
    /// lowered subagent runs under. The rule itself is the shared predicate in
    /// <see cref="CapabilityDegradations"/>, so this target cannot drift from the others.
    /// </remarks>
    private static IReadOnlyList<string> ResolveMcpToolNames(
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> orchestratorExcludedMcpToolNames,
        IReadOnlyList<string> universalMcpToolNames)
    {
        return
        [
            .. CapabilityDegradations.GrantsOrchestratorExcludedMcpServers(profileName, capabilityProfiles)
                ? orchestratorExcludedMcpToolNames
                : [],
            .. CapabilityDegradations.GrantsUniversalMcpServers(profileName, capabilityProfiles)
                ? universalMcpToolNames
                : []
        ];
    }

    private static IEnumerable<SquadDegradationRecord> BuildSubagentDegradations(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> mcpServerNames)
    {
        string profileName = agent.DevinCapabilityProfile ?? agent.CapabilityProfile;
        if (!capabilityProfiles.TryGetValue(profileName, out SquadCapabilityProfile? profile))
        {
            yield break;
        }

        // 'deny' needs no record: 'allowed-tools' withholds it. Only 'ask' loses meaning.
        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
        {
            yield return new SquadDegradationRecord(
                TargetToken,
                agent.Name,
                agent.Name,
                "safety-narrowed",
                agent.BodyDigest,
                $"Capability profile '{profileName}' requires 'ask' for " +
                $"{string.Join(", ", narrowed)}. Devin prompts for a subagent's tool call " +
                "only in the foreground and only when the session's permission mode has not " +
                "already approved it, a background subagent never prompts, and a subagent " +
                "profile cannot force the prompt, so these narrow to withheld: the " +
                "corresponding tools are absent from the agent's 'allowed-tools' list.");
        }

        List<string> notExpressible = [];

        IReadOnlyList<string> withheldServerNames =
            WithheldMcpServerNames(profileName, capabilityProfiles, mcpServerNames);
        if (withheldServerNames.Count > 0)
        {
            notExpressible.Add(DescribeWithheldMcp(profileName, withheldServerNames, capabilityProfiles));
        }

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publish) &&
            publish == SquadPermissionDecision.Allow)
        {
            notExpressible.Add(
                "Capability 'network.publish' is allowed but no Devin tool exists to express it.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision == SquadPermissionDecision.Allow)
        {
            string roster = agent.DelegatesTo.Count > 0
                ? string.Join(", ", agent.DelegatesTo)
                : "(none declared)";
            notExpressible.Add(
                $"Capability 'delegate' is allowed, but the canonical delegates-to roster ({roster}) " +
                "cannot be enforced: Devin has no 'allowed_subagents' equivalent, so granting " +
                "nested delegation through 'max-nesting' and 'run_subagent' would reach every " +
                "profile. Neither is emitted, and this subagent cannot delegate on Devin.");
        }

        if (notExpressible.Count > 0)
        {
            yield return new SquadDegradationRecord(
                TargetToken,
                agent.Name,
                agent.Name,
                "permission-not-expressible",
                agent.BodyDigest,
                string.Join(" ", notExpressible));
        }

        SquadPermissionDecision executeDecision =
            profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec)
                ? exec
                : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision =
            profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write)
                ? write
                : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            TargetToken,
            agent.Name,
            agent.Name,
            agent.BodyDigest,
            executeDecision,
            writeDecision,
            ["exec"],
            ["edit", "write", "apply_patch", "notebook_edit"]);

        if (notIsolable is not null)
        {
            yield return notIsolable;
        }
    }

    private static IEnumerable<SquadDegradationRecord> BuildPrimaryAgentDegradations(
        SquadAgent agent,
        SquadFallbackProfile fallbackProfile,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary,
        bool reusedCanonicalSkill = false)
    {
        string projectionText = reusedCanonicalSkill
            ? "this agent reuses the same-name canonical skill as its entry point, because the " +
              "canonical skill already occupies that identity; no separate lowered skill is emitted"
            : "this agent renders as a Devin skill instead of a '.devin/agents/' profile";

        yield return new SquadDegradationRecord(
            TargetToken,
            agent.Name,
            agent.Name,
            "role-skill-fallback",
            agent.BodyDigest,
            "Devin has no primary-agent primitive: Devin Local is the only top-level " +
            "agent, and a custom profile is always dispatched as a subagent. Fallback profile " +
            $"'{agent.Fallback}' declares no-primary-agent: {fallbackProfile.NoPrimaryAgent}, " +
            "so " + projectionText + ".");

        string rosterText = agent.DelegatesTo.Count > 0
            ? string.Join(", ", agent.DelegatesTo)
            : "(none declared)";

        yield return new SquadDegradationRecord(
            TargetToken,
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            "Capability decisions " +
            $"({CapabilityDegradations.DescribeCapabilityDecisions(agent, capabilityProfiles, capabilityVocabulary)}) " +
            "are not enforced: a Devin skill runs under the session's permissions. Its " +
            "'allowed-tools' and 'permissions.allow' would pre-approve rather than restrict, " +
            "and 'permissions.deny' is not emitted because Devin does not document whether " +
            "it reaches the subagents the skill dispatches, which need the tools this " +
            $"profile denies. Its delegates-to roster ({rosterText}) is instruction-only.");
    }

    /// <summary>
    /// The fully qualified MCP tool names declared by <c>toolchain.yml</c> for the servers
    /// <paramref name="includeServer"/> admits, in a stable server-then-tool order, in the
    /// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> form Devin's permission rules use.
    /// </summary>
    private static IReadOnlyList<string> QualifiedMcpToolNames(
        SquadSource source,
        Func<string, bool> includeServer)
    {
        ArgumentNullException.ThrowIfNull(includeServer);

        return
        [
            .. source.Toolchain.RequiredMcpTools
                .Where(entry => includeServer(entry.Key))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .SelectMany(entry => entry.Value
                    .OrderBy(tool => tool, StringComparer.Ordinal)
                    .Select(tool => $"mcp__{entry.Key}__{tool}"))
        ];
    }

    /// <summary>The declared MCP server names, for a record naming what was withheld.</summary>
    private static IReadOnlyList<string> DeclaredMcpServerNames(SquadSource source)
    {
        return [.. source.Toolchain.RequiredMcpTools.Keys.OrderBy(name => name, StringComparer.Ordinal)];
    }

    /// <summary>Whether a declared server name is the one every read-entitled subagent gets.</summary>
    private static bool IsUniversalMcpServer(string serverName)
    {
        return string.Equals(serverName, UniversalMcpServer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Why a subagent did not receive the declared MCP tools, naming exactly the servers it lost
    /// and the rule that lost them.
    /// </summary>
    /// <remarks>
    /// The reason is selected rather than hedged: the two withheld classes are withheld for
    /// different causes, and a sentence offering both would be read as both. The explanation
    /// never names a granted server, so the record and the rendered <c>allowed-tools</c> list
    /// cannot contradict one another.
    /// </remarks>
    private static string DescribeWithheldMcp(
        string profileName,
        IReadOnlyList<string> withheldServerNames,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        string reason = CapabilityDegradations.GrantsUniversalMcpServers(profileName, capabilityProfiles)
            ? $"capability profile '{profileName}' is the pure orchestrator profile, which routes " +
              "work rather than researching it, and the carve-out it keeps reaches only the " +
              "code-graph and library servers; a server every agent needs is granted on the " +
              "file-read entitlement alone and is never withheld on the profile's identity"
            : $"capability profile '{profileName}' does not allow 'filesystem.read', so no " +
              "declared MCP server is granted";

        return $"Declared MCP server(s) {string.Join(", ", withheldServerNames)} are withheld: " +
               reason + ".";
    }

    /// <summary>
    /// The declared servers a subagent is withheld from: every server whose own entitlement its
    /// envelope profile does not satisfy.
    /// </summary>
    /// <remarks>
    /// Computed per server rather than as one "granted any MCP" boolean, because the two classes
    /// answer different questions. A record built from a single answer would name the universal
    /// server as withheld from the orchestrator after granting it — the one place a review would
    /// read a claim the render contradicts.
    /// </remarks>
    private static IReadOnlyList<string> WithheldMcpServerNames(
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> declaredMcpServerNames)
    {
        bool grantsUniversal =
            CapabilityDegradations.GrantsUniversalMcpServers(profileName, capabilityProfiles);
        bool grantsOrchestratorExcluded =
            CapabilityDegradations.GrantsOrchestratorExcludedMcpServers(profileName, capabilityProfiles);

        return
        [
            .. declaredMcpServerNames.Where(server =>
                IsUniversalMcpServer(server) ? !grantsUniversal : !grantsOrchestratorExcluded)
        ];
    }

    private static string CollapseToSingleLine(string value)
    {
        return string.Join(
            ' ',
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
