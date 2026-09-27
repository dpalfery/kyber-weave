using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into the custom-subagent and skill formats read by Devin
/// Desktop's local agent, Devin Local, which shares the Devin CLI's discovery and file formats.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source of truth.</b> Devin Desktop is Cognition's desktop app, shipped on 2026-06-02 as
/// the successor to Windsurf. Its local agent, Devin Local, uses the Devin CLI's skill and
/// subagent formats and discovery. The facts below were cross-checked on 2026-09-27 against
/// Devin's published documentation (<c>docs.devin.ai/desktop/devin-local</c>,
/// <c>docs.devin.ai/cli/subagents</c>, <c>docs.devin.ai/cli/extensibility/skills</c>,
/// <c>docs.devin.ai/cli/reference/permissions</c>) and against integrations that record which
/// Devin CLI build they were verified on (v3000.6.7 and v3000.10.21, the latter via
/// <c>devin doctor</c> and <c>devin skills list</c>). The documentation host was not reachable
/// from the environment this renderer was written in, so the documented facts were read
/// through search excerpts and those integrations rather than fetched directly. Anything
/// below that says "not documented" is a reading this renderer takes the non-broadening side
/// of, and each is recorded as a degradation rather than asserted as a mapping.
/// </para>
/// <para>
/// <b>Discovery roots.</b> Custom subagents load from <c>.devin/agents/</c> and
/// <c>.agents/agents/</c> in the workspace, and from <c>agents/</c> in the Devin user
/// configuration directory (<c>~/.config/devin</c>, or <c>%APPDATA%\devin</c> on Windows — see
/// <see cref="SquadGlobalRoots"/>). Skills load from <c>.devin/skills/</c> and
/// <c>.agents/skills/</c>, and from <c>skills/</c> in the user directory. The legacy Windsurf
/// root <c>.windsurf/skills/</c> is read only when <c>.devin/skills/</c> does not exist, so
/// this renderer never writes it: deploying there would be shadowed by the first
/// <c>.devin/skills/</c> anything else creates. <c>.agents/agents/&lt;name&gt;/agent.md</c> and
/// <c>.agents/skills/</c> are also <see cref="AntigravityRenderer"/>'s output, so an Antigravity
/// deployment in the same repository is already visible to Devin; onboarding tells an operator
/// to pick one of the two per repository rather than rely on how Devin orders duplicates.
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
/// prompt. Its documented keys include <c>name</c>, <c>description</c>, <c>model</c>,
/// <c>allowed-tools</c>, <c>permissions</c>, and <c>max-nesting</c>. This renderer emits
/// <c>name</c>, <c>description</c>, <c>model</c> (only when a <c>devin:</c> harness value
/// resolves to something other than <c>inherit</c>), and <c>allowed-tools</c>, nothing else.
/// On a subagent profile <c>allowed-tools</c> is a hard restriction, so it is always emitted;
/// what an absent or empty list means is not documented, so a resolved grant of nothing is a
/// render error rather than an empty list that might mean "every tool".
/// </para>
/// <para>
/// <b>Capability lowering.</b> Devin's built-in tool names are <c>read</c>, <c>grep</c>,
/// <c>glob</c>, <c>edit</c>, <c>write</c>, <c>exec</c>, <c>webfetch</c>, <c>web_search</c>, and
/// <c>skill</c>. <c>skill</c> is granted on every subagent, matching
/// <see cref="ClaudeRenderer"/>'s ungoverned base: it opens only the skill tree this renderer
/// deploys, and withholding it would deploy skills no subagent could reach. Only
/// <see cref="SquadPermissionDecision.Allow"/> grants anything further. Devin's permission
/// rules do have an <c>ask</c> list, but whether a subagent — which runs in its own
/// conversation chain — can surface that prompt to the operator is not documented, so
/// <c>ask</c> withholds the tool and records <c>safety-narrowed</c>, as on Pi and ZCode.
/// <c>network.publish</c> has no built-in tool, so an <c>allow</c> for it records
/// <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// <b>MCP is granted by concrete tool name.</b> Devin names an MCP tool
/// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> in its permission rules, so the tools declared in
/// <c>toolchain.yml</c>'s <c>required-mcp-tools</c> are appended to <c>allowed-tools</c> in that
/// form, for every role allowed to read the filesystem except the pure orchestrator — the
/// same rule <see cref="ClaudeRenderer"/> and <see cref="ZCodeRenderer"/> apply. The servers
/// themselves are the operator's to configure in Devin's <c>mcp_config.json</c>; this renderer
/// never writes it.
/// </para>
/// <para>
/// <b>Delegation is withheld from subagents.</b> The parent agent dispatches a named profile
/// through <c>run_subagent</c>, and a profile reaches further subagents only when its
/// <c>max-nesting</c> allows it. Devin has no runtime-enforced delegation roster — no
/// <c>allowed_subagents</c> equivalent — so granting nested delegation would reach every
/// profile, not the canonical <c>delegates-to</c> list. This renderer therefore emits neither
/// <c>run_subagent</c> nor <c>max-nesting</c>, and an allowed <c>delegate</c> records
/// <c>permission-not-expressible</c> naming the roster that becomes unreachable. The lowered
/// conductor is unaffected: it runs in the main Devin Local session, where subagent dispatch is
/// always available.
/// </para>
/// <para>
/// <b>Primary-agent lowering.</b> Devin has no primary-agent primitive: Devin Local is the
/// only top-level agent, and a custom profile is always dispatched as a subagent. A
/// <see cref="SquadInvocation.Primary"/> agent whose fallback profile declares
/// <c>no-primary-agent: skill</c> therefore renders as <c>.devin/skills/&lt;name&gt;/SKILL.md</c>,
/// recording <c>role-skill-fallback</c> and <c>permission-not-expressible</c>; <c>omit</c> emits
/// nothing and records <c>omitted</c>. As on Pi, agents and skills are separate namespaces, so a
/// canonical skill already occupying the lowered identity fails the render rather than taking
/// a <c>role-</c> prefix.
/// </para>
/// <para>
/// <b>Skills carry only <c>name</c> and <c>description</c>.</b> On a skill,
/// <c>allowed-tools</c> auto-approves the listed tools rather than restricting them, and
/// <c>permissions</c> add to the session's rules rather than replacing them. Both would widen
/// what runs without a prompt, and neither can narrow, so no skill — canonical or lowered —
/// carries either key, and a lowered primary agent's capability decisions are recorded as not
/// enforced.
/// </para>
/// </remarks>
public sealed class DevinRenderer : ISquadRenderer
{
    private const string TargetToken = "devin";
    private const string AgentsDirectory = ".devin/agents";
    private const string SkillsDirectory = ".devin/skills";

    /// <summary>
    /// The capability profile whose holder routes work rather than researching it, and which
    /// <see cref="ClaudeRenderer"/> and <see cref="ZCodeRenderer"/> also exclude from the
    /// standard MCP grant.
    /// </summary>
    private const string PureOrchestratorProfile = "orchestrator";

    /// <summary>
    /// Lowers the semantic capability vocabulary onto Devin's built-in tool names.
    /// <c>network.publish</c> is absent because no built-in tool expresses it, and
    /// <c>delegate</c> because granting it cannot keep the roster (see the class remarks).
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityTools =
    [
        ("filesystem.read", ["read"]),
        ("filesystem.search", ["grep", "glob"]),
        ("filesystem.write", ["edit", "write"]),
        ("process.execute", ["exec"]),
        ("network.read", ["webfetch", "web_search"]),
    ];

    /// <summary>Granted on every subagent regardless of capability profile.</summary>
    private static readonly string[] UngovernedTools = ["skill"];

    /// <summary>
    /// Fixed <c>allowed-tools</c> emission order, so a rendered agent is byte-stable regardless
    /// of how the capability profile's permissions happen to enumerate.
    /// </summary>
    private static readonly string[] ToolOrder =
        ["skill", "read", "grep", "glob", "edit", "write", "exec", "webfetch", "web_search"];

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

        SquadSource source = SquadSourceLoader.Load(request.SourceDirectory);
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

        // Read upfront so the primary-agent fail-closed check can see whether a canonical
        // skill already occupies the lowered identity, whichever loop reaches it first.
        HashSet<string> skillIdentities = source.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The declared vocabulary, not a renderer-local copy: a capability added to
        // profiles/capabilities.yml must appear in a lowered agent's
        // permission-not-expressible details without a renderer change.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

        IReadOnlyList<string> qualifiedMcpToolNames = QualifiedMcpToolNames(source);
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
                    qualifiedMcpToolNames,
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
                    Target: TargetToken,
                    CanonicalIdentity: agent.Name,
                    OutputIdentity: agent.Name,
                    Code: "omitted",
                    InstructionDigest: agent.BodyDigest,
                    Details: $"Fallback profile '{agent.Fallback}' declares no-primary-agent: " +
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
            // rule ClaudeRenderer follows.
            if (sharedIdentities.Contains(skill.Name))
            {
                continue;
            }

            SquadDeploymentFile principal = RenderSkill(
                skill.Name,
                skill.Description,
                skill.InstructionBody,
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
        IReadOnlyList<string> qualifiedMcpToolNames,
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

        frontmatter["allowed-tools"] = ResolveTools(agent, capabilityProfiles, qualifiedMcpToolNames);

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
        SquadDeploymentScope scope)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = CollapseToSingleLine(description)
        };

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
    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope) =>
        scope == SquadDeploymentScope.Project
            ? baseDirectory
            : baseDirectory[".devin/".Length..];

    /// <summary>
    /// The <c>devin</c> harness value when the model profile declares one, otherwise the
    /// target-neutral <c>default</c>; either an explicit or a defaulted <c>inherit</c> omits
    /// <c>model</c>, leaving Devin's own model selection in charge.
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
        IReadOnlyList<string> qualifiedMcpToolNames)
    {
        HashSet<string> granted = new(UngovernedTools, StringComparer.Ordinal);

        if (capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
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
        // fixed renderer-local order, and the caller already supplies them server-then-tool.
        string[] resolved =
        [
            .. ToolOrder.Where(granted.Contains),
            .. GrantsMcp(agent, capabilityProfiles) ? qualifiedMcpToolNames : []
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

    private static IEnumerable<SquadDegradationRecord> BuildSubagentDegradations(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> mcpServerNames)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
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
                Target: TargetToken,
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "safety-narrowed",
                InstructionDigest: agent.BodyDigest,
                Details: $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                    $"{string.Join(", ", narrowed)}. Devin does not document that a subagent, " +
                    "which runs in its own conversation chain, can surface a permission prompt, " +
                    "so these narrow to withheld: the corresponding tools are absent from the " +
                    "agent's 'allowed-tools' list.");
        }

        List<string> notExpressible = [];

        if (mcpServerNames.Count > 0 && !GrantsMcp(agent, capabilityProfiles))
        {
            notExpressible.Add(DescribeWithheldMcp(agent, mcpServerNames));
        }

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publish) &&
            publish == SquadPermissionDecision.Allow)
        {
            notExpressible.Add(
                "Capability 'network.publish' is allowed but no built-in Devin tool exists to express it.");
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
                Target: TargetToken,
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "permission-not-expressible",
                InstructionDigest: agent.BodyDigest,
                Details: string.Join(" ", notExpressible));
        }

        SquadPermissionDecision executeDecision = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec)
            ? exec
            : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write)
            ? write
            : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: TargetToken,
            canonicalIdentity: agent.Name,
            outputIdentity: agent.Name,
            instructionDigest: agent.BodyDigest,
            executeDecision: executeDecision,
            writeDecision: writeDecision,
            grantedShellTools: ["exec"],
            withheldWriteTools: ["edit", "write"]);

        if (notIsolable is not null)
        {
            yield return notIsolable;
        }
    }

    private static IEnumerable<SquadDegradationRecord> BuildPrimaryAgentDegradations(
        SquadAgent agent,
        SquadFallbackProfile fallbackProfile,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary)
    {
        yield return new SquadDegradationRecord(
            Target: TargetToken,
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "role-skill-fallback",
            InstructionDigest: agent.BodyDigest,
            Details: "Devin has no primary-agent primitive: Devin Local is the only top-level " +
                "agent, and a custom profile is always dispatched as a subagent. Fallback profile " +
                $"'{agent.Fallback}' declares no-primary-agent: {fallbackProfile.NoPrimaryAgent}, " +
                "so this agent renders as a Devin skill instead of a '.devin/agents/' profile.");

        string rosterText = agent.DelegatesTo.Count > 0
            ? string.Join(", ", agent.DelegatesTo)
            : "(none declared)";

        yield return new SquadDegradationRecord(
            Target: TargetToken,
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: "Capability decisions " +
                $"({CapabilityDegradations.DescribeCapabilityDecisions(agent, capabilityProfiles, capabilityVocabulary)}) " +
                "are not enforced: a Devin skill runs under the session's permissions, which its " +
                "'allowed-tools' and 'permissions' keys can only add to, never narrow, and its " +
                $"delegates-to roster ({rosterText}) is instruction-only.");
    }

    /// <summary>
    /// The fully qualified MCP tool names declared by <c>toolchain.yml</c>, in a stable
    /// server-then-tool order, in the <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> form Devin's
    /// permission rules use.
    /// </summary>
    private static IReadOnlyList<string> QualifiedMcpToolNames(SquadSource source) =>
    [
        .. source.Toolchain.RequiredMcpTools
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .SelectMany(entry => entry.Value
                .OrderBy(tool => tool, StringComparer.Ordinal)
                .Select(tool => $"mcp__{entry.Key}__{tool}"))
    ];

    /// <summary>The declared MCP server names, for a record naming what was withheld.</summary>
    private static IReadOnlyList<string> DeclaredMcpServerNames(SquadSource source) =>
        [.. source.Toolchain.RequiredMcpTools.Keys.OrderBy(name => name, StringComparer.Ordinal)];

    private static string DescribeWithheldMcp(
        SquadAgent agent,
        IReadOnlyList<string> mcpServerNames) =>
        $"Declared MCP server(s) {string.Join(", ", mcpServerNames)} are withheld: capability " +
        $"profile '{agent.CapabilityProfile}' does not allow 'filesystem.read', or is the pure " +
        "orchestrator profile that routes work rather than researching it.";

    /// <summary>
    /// Whether a subagent is entitled to the declared MCP tools: any role allowed to read the
    /// filesystem, except a pure orchestrator.
    /// </summary>
    private static bool GrantsMcp(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles) =>
        !string.Equals(agent.CapabilityProfile, PureOrchestratorProfile, StringComparison.Ordinal) &&
        capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile) &&
        profile.Permissions.TryGetValue("filesystem.read", out SquadPermissionDecision decision) &&
        decision == SquadPermissionDecision.Allow;

    private static string CollapseToSingleLine(string value) =>
        string.Join(
            ' ',
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
