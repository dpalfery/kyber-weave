using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into the custom-agent and skill file formats defined by
/// the third-party <c>@tintinweb/pi-subagents</c> extension for the Pi coding agent.
/// </summary>
/// <remarks>
/// <para>
/// Facts verified 2026-09-14 against the installed <c>@earendil-works/pi-coding-agent</c>
/// <b>0.84.4</b> (package.json; the Homebrew keg label 0.84.1 is stale) and
/// <c>@tintinweb/pi-subagents</c> <b>0.19.0</b> (peer dependency
/// <c>@earendil-works/pi-coding-agent &gt;=0.84.0</c>). Pi core itself ships no sub-agents
/// and no permission prompts ("No sub-agents" / "No permission popups",
/// pi-coding-agent README); the extension is what defines the Claude Code-like custom-agent
/// file format this renderer targets. A project must install the extension (or later, and
/// have it be at least 0.19.0) for the emitted <c>.pi/agents/</c> files to have any effect.
/// </para>
/// <para>
/// <b>Discovery roots and precedence.</b> Agents load lowest to highest precedence from
/// <c>$PI_CODING_AGENT_DIR/agents/*.md</c> (default <c>~/.pi/agent/agents/</c>), then
/// <c>&lt;cwd&gt;/.agents/agents/*.md</c>, then <c>&lt;cwd&gt;/.pi/agents/*.md</c> — later
/// loads overwrite earlier ones by type, and only direct <c>*.md</c> children are read (a
/// resource file must live one directory level deeper than its owning agent, never as a
/// sibling <c>*.md</c>). Skills load from global <c>~/.pi/agent/skills/</c> and
/// <c>~/.agents/skills/</c>, and from project <c>.pi/skills/</c> and <c>.agents/skills/</c>
/// (searched from the working directory up to the git root) — <b>but the project roots load
/// only once the project is trusted</b>, so an untrusted checkout shows the project's agents
/// (the agent loader has no trust check) while hiding its skills, including a lowered
/// primary agent. Skill name collisions resolve first-found-wins by root precedence rank
/// (project settings, project auto-discovered, user settings, user auto-discovered,
/// package), with a diagnostic naming the skipped path; within the project, <c>.pi/skills</c>
/// is added before <c>.agents/skills</c>, so this renderer's own output always wins a
/// same-name collision with an Antigravity-lowered skill under <c>.agents/skills/</c>.
/// </para>
/// <para>
/// <b>Accepted agent frontmatter keys</b> (custom-agents.ts / agent-types.ts): <c>name</c>,
/// <c>description</c>, <c>display_name</c>, <c>color</c>, <c>tools</c>, <c>extensions</c>,
/// <c>exclude_extensions</c>, <c>skills</c>, <c>memory</c>, <c>disallowed_tools</c>,
/// <c>isolation</c>, <c>model</c>, <c>thinking</c>, <c>max_turns</c>, <c>persist_session</c>,
/// <c>output_transcript</c>, <c>session_dir</c>, <c>allowed_subagents</c>,
/// <c>prompt_mode</c>, <c>inherit_context</c>, <c>run_in_background</c>, <c>isolated</c>,
/// <c>enabled</c>. Every other key is silently ignored, so this renderer emits only
/// <c>name</c>, <c>description</c>, <c>model</c>, <c>thinking</c> (conditionally), <c>tools</c>, <c>extensions</c>, and
/// <c>allowed_subagents</c> — the plan's approved subset — never the rest. The <c>thinking</c>
/// key is emitted only when the resolved <c>pi:</c> value carries a <c>[thinking=&lt;level&gt;]</c>
/// suffix; the level is validated against the closed domain and the key is omitted if no suffix is present.
/// A name containing <c>:</c> is skipped by the loader, so the canonical name is emitted verbatim and never
/// decorated. Skill frontmatter accepts <c>name</c> (1-64 lowercase-a-z0-9-hyphen
/// characters, no leading/trailing/doubled hyphen), a required <c>description</c> (up to
/// 1024 characters), and an optional <c>license</c>; unknown fields are ignored there too.
/// </para>
/// <para>
/// <b>The seven-tool vocabulary.</b> <c>tools</c> accepts a CSV or array drawn from
/// <c>read, bash, edit, write, grep, find, ls</c>; omitting the key grants every built-in
/// (silent widening for any canonical <c>deny</c>), and an unknown name fails loudly. This
/// renderer therefore always emits <c>tools</c> — the fixed CSV order
/// <c>read, grep, find, ls, edit, write, bash</c>, or the literal <c>none</c> when nothing is
/// granted — mirroring why <see cref="ClaudeRenderer"/> always emits its own allow-list.
/// Only <see cref="SquadPermissionDecision.Allow"/> grants a tool; <c>ask</c> and <c>deny</c>
/// both withhold, because Pi has no per-capability permission prompt and <c>tools</c> is
/// binary — an <c>ask</c> decision is therefore recorded as <c>safety-narrowed</c> rather
/// than expressed in frontmatter (owner decision Q1-A, 2026-09-14). <c>network.read</c> and
/// <c>network.publish</c> have no built-in Pi tool at all, so an <c>allow</c> decision for
/// either records <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// <b><c>extensions</c>.</b> <c>extensions: false</c> loads no extensions for the agent —
/// emitted unconditionally on every rendered agent per the owner's Q1-A decision, so Squad
/// agents on Pi reach no CodeGraph, docs MCP, or web tooling beyond the seven built-ins.
/// This combines with <c>allowed_subagents</c> without conflict, because nested-subagent
/// tools are injected directly rather than through a loaded extension.
/// </para>
/// <para>
/// <b><c>allowed_subagents</c> enforcement and depth cap.</b> The key is off by default and
/// runtime-enforced: an out-of-list, unknown, or disabled subagent type is rejected with no
/// fallback, so this renderer emits it — the canonical <c>delegates-to</c> roster, CSV in
/// canonical order — only when the capability profile allows <c>delegate</c> and the roster
/// is non-empty; an allowed-but-empty roster would need the literal <c>all</c>, which reaches
/// <c>general-purpose</c> (every tool) and is therefore recorded as
/// <c>permission-not-expressible</c> rather than emitted. <c>maxSubagentDepth</c> defaults to
/// 2 (main session 0, subagent 1, nested child 2); this renderer never writes
/// <c>.pi/subagents.json</c>; onboarding recommends <c>fallbackSubagent: none</c> so an
/// unresolvable <c>Agent</c> call fails rather than silently running with every tool.
/// </para>
/// <para>
/// <b>Primary-agent lowering.</b> Pi core has no primary-agent selection primitive —
/// <c>.pi/SYSTEM.md</c> and <c>--system-prompt</c> replace the whole session prompt, not a
/// per-agent one — so a canonical agent with <see cref="SquadInvocation.Primary"/> cannot
/// render at <c>.pi/agents/&lt;name&gt;.md</c> the way the four other native targets do.
/// Its fallback profile decides the outcome: <c>no-primary-agent: skill</c> renders it as a
/// top-level <c>.pi/skills/&lt;name&gt;/SKILL.md</c> (running it at depth 0, where nested
/// subagent delegation stays available to its own roster, unlike depth 1 if it were forced
/// into <c>.pi/agents/</c>), recording <c>role-skill-fallback</c> and, because a top-level
/// skill enforces none of the capability lattice, <c>permission-not-expressible</c> for every
/// vocabulary capability; <c>no-primary-agent: omit</c> emits nothing and records
/// <c>omitted</c>. Unlike <see cref="AntigravityRenderer"/>'s role-skill fallback, Pi has
/// separate agent and skill namespaces, so there is no role-prefixed escape hatch: if the
/// lowered identity is already occupied by a canonical skill, rendering fails closed with
/// <see cref="SquadRenderValidationException"/> naming it, rather than emitting a
/// <c>role-</c>-prefixed duplicate the way a genuinely primitive-less fallback target would.
/// </para>
/// </remarks>
public sealed class PiRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".pi/agents";
    private const string SkillsDirectory = ".pi/skills";

    /// <summary>
    /// Lowers the semantic capability vocabulary onto Pi's built-in tool names, verified
    /// against the pi-subagents 0.19.0 README ("Tool &amp; extension scoping") on 2026-09-14.
    /// <c>network.read</c> and <c>network.publish</c> are absent deliberately: neither has a
    /// built-in Pi tool. <c>delegate</c> is handled separately because an allowed, non-empty
    /// roster emits <c>allowed_subagents</c>, not a tool.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityTools =
    [
        ("filesystem.read", ["read"]),
        ("filesystem.search", ["grep", "find", "ls"]),
        ("filesystem.write", ["edit", "write"]),
        ("process.execute", ["bash"]),
    ];

    /// <summary>
    /// Fixed <c>tools</c> CSV emission order, so a rendered agent file is byte-stable
    /// regardless of how the profile's permissions enumerate.
    /// </summary>
    private static readonly string[] ToolOrder = ["read", "grep", "find", "ls", "edit", "write", "bash"];

    private static readonly HashSet<string> AllowedThinkingLevels = new(StringComparer.Ordinal)
    {
        "off", "minimal", "low", "medium", "high", "max"
    };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    /// <summary>
    /// YamlDotNet does not document <see cref="ISerializer"/> as thread-safe, and the
    /// registry may dispatch renderers concurrently; serialization takes this lock so a
    /// shared static instance cannot interleave emitter state.
    /// </summary>
    private static readonly object SerializerLock = new();

    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        if (scope == SquadDeploymentScope.Project)
        {
            return baseDirectory;
        }

        return baseDirectory.StartsWith(".pi/", StringComparison.Ordinal)
            ? baseDirectory[".pi/".Length..]
            : baseDirectory;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Pi];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Pi))
        {
            throw new ArgumentException(
                "PiRenderer was asked to render a target other than Pi.",
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

        // Read upfront so the primary-agent fail-closed check (R3) can see whether a
        // canonical skill already occupies the lowered identity before any file is built,
        // regardless of which loop encounters the collision first.
        HashSet<string> skillIdentities = source.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The declared vocabulary, not a renderer-local copy: a capability added to
        // profiles/capabilities.yml must appear in a lowered primary agent's
        // permission-not-expressible details without a renderer change.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        foreach (SquadAgent agent in source.Agents)
        {
            if (agent.Invocation == SquadInvocation.Subagent)
            {
                SquadDeploymentFile principal = RenderSubagentAgent(
                    agent,
                    source.ModelProfiles.Profiles,
                    source.CapabilityProfiles.Profiles,
                    request.Scope);
                files.Add(principal);
                SquadResourceProjection.Append(files, principal, agent.Resources);

                degradations.AddRange(BuildSubagentDegradations(agent, source.CapabilityProfiles.Profiles));
                continue;
            }

            SquadFallbackProfile fallbackProfile = source.FallbackProfiles.Profiles[agent.Fallback];
            if (string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal))
            {
                if (skillIdentities.Contains(agent.Name))
                {
                    throw new SquadRenderValidationException(
                        $"Cannot lower primary agent '{agent.Name}' to a Pi skill: a canonical " +
                        $"skill named '{agent.Name}' already occupies that identity. Pi has " +
                        "separate agent and skill namespaces with no role-prefixed fallback " +
                        "mechanism, so this is a fail-closed condition rather than a naming " +
                        "collision this renderer can resolve on its own.");
                }

                SquadDeploymentFile principal = RenderSkill(agent.Name, agent.Description, agent.InstructionBody, request.Scope);
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
                    Target: "pi",
                    CanonicalIdentity: agent.Name,
                    OutputIdentity: agent.Name,
                    Code: "omitted",
                    InstructionDigest: agent.BodyDigest,
                    Details: $"Fallback profile '{agent.Fallback}' declares " +
                        "no-primary-agent: omit; Pi has no primary-agent primitive for this " +
                        "agent to render onto, so no file is emitted."));
            }
            else
            {
                throw new SquadRenderValidationException(
                    $"Fallback profile '{agent.Fallback}' declares unsupported " +
                    $"no-primary-agent value '{fallbackProfile.NoPrimaryAgent}' for primary " +
                    $"agent '{agent.Name}'.");
            }
        }

        foreach (SquadSkill skill in source.Skills)
        {
            // A profile-declared shared identity has one canonical projection, per the same
            // rule ClaudeRenderer follows. Resolved from source so a fallbacks.yml change is
            // honored without a renderer change.
            if (sharedIdentities.Contains(skill.Name))
            {
                continue;
            }

            SquadDeploymentFile principal = RenderSkill(skill.Name, skill.Description, skill.InstructionBody, request.Scope);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, skill.Resources);
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static SquadDeploymentFile RenderSubagentAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        SquadDeploymentScope scope)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = CollapseToSingleLine(agent.Description)
        };

        var (model, thinkingLevel) = ResolvePiModelAndThinking(agent, modelProfiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        if (thinkingLevel is not null)
        {
            frontmatter["thinking"] = thinkingLevel;
        }

        // Always emit tools: omitting the key grants every built-in (widening).
        IReadOnlyList<string> tools = ResolveTools(agent, capabilityProfiles);
        frontmatter["tools"] = tools.Count == 0 ? "none" : string.Join(", ", tools);

        // Every agent, unconditionally (owner decision Q1-A, 2026-09-14).
        frontmatter["extensions"] = false;

        if (IsDelegateAllowed(agent, capabilityProfiles) && agent.DelegatesTo.Count > 0)
        {
            frontmatter["allowed_subagents"] = string.Join(", ", agent.DelegatesTo);
        }

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);
        }

        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, scope);
        return new SquadDeploymentFile(
            $"{agentsDir}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            "pi");
    }

    private static SquadDeploymentFile RenderSkill(string name, string description, string instructionBody, SquadDeploymentScope scope)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = name,
            ["description"] = CollapseToSingleLine(description),
            ["license"] = "MIT"
        };

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, instructionBody);
        }

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, scope);
        return new SquadDeploymentFile(
            $"{skillsDir}/{name}/SKILL.md",
            Encoding.UTF8.GetBytes(content),
            "pi");
    }

    /// <summary>
    /// Parses and validates a thinking level suffix from a model string. A model value like
    /// <c>zai/glm-5.3[thinking=high]</c> splits into bare model id <c>zai/glm-5.3</c> and level
    /// <c>high</c>. The level is validated against the closed six-value domain
    /// <c>off | minimal | low | medium | high | max</c> with <see cref="StringComparison.Ordinal"/>
    /// (case-sensitive); an invalid level throws <see cref="SquadRenderValidationException"/>.
    /// Returns a tuple of (bareModelId, thinkingLevel) where thinkingLevel is null if no suffix.
    /// </summary>
    private static (string BareModel, string? ThinkingLevel) ParseThinkingSuffix(string? modelValue)
    {
        if (string.IsNullOrEmpty(modelValue))
        {
            return (modelValue ?? string.Empty, null);
        }

        const string suffixPrefix = "[thinking=";
        int suffixStartIdx = modelValue.LastIndexOf(suffixPrefix, StringComparison.Ordinal);
        if (suffixStartIdx < 0)
        {
            // No suffix present
            return (modelValue, null);
        }

        string bareModel = modelValue[..suffixStartIdx];
        int levelStartIdx = suffixStartIdx + suffixPrefix.Length;
        int suffixEndIdx = modelValue.IndexOf(']', levelStartIdx);
        if (suffixEndIdx < 0)
        {
            throw new SquadRenderValidationException(
                $"Malformed thinking suffix in model value '{modelValue}': missing closing ']'.");
        }

        if (suffixEndIdx != modelValue.Length - 1)
        {
            throw new SquadRenderValidationException(
                $"Malformed thinking suffix in model value '{modelValue}': the suffix must be the last element of the value.");
        }

        if (bareModel.Length == 0)
        {
            throw new SquadRenderValidationException(
                $"Malformed model value '{modelValue}': the model id before the thinking suffix is empty.");
        }

        string level = modelValue[levelStartIdx..suffixEndIdx];

        // Validate level against closed domain
        if (!AllowedThinkingLevels.Contains(level))
        {
            throw new SquadRenderValidationException(
                $"Invalid thinking level '{level}' in model value. Allowed values are: off, minimal, low, medium, high, max.");
        }

        return (bareModel, level);
    }

    /// <summary>
    /// Resolves the harness-specific model and optional thinking level. Mirrors
    /// <c>ClaudeRenderer.ResolveClaudeModel</c> (R12): the <c>pi</c> harness override when
    /// the model profile declares one, otherwise the target-neutral <c>default</c>; either an
    /// explicit or a defaulted <c>inherit</c> omits both keys.
    /// </summary>
    private static (string? Model, string? ThinkingLevel) ResolvePiModelAndThinking(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return (null, null);
        }

        string? resolvedModel;
        if (profile.HarnessModels.TryGetValue("pi", out string? piModel))
        {
            resolvedModel = string.Equals(piModel, "inherit", StringComparison.Ordinal) ? null : piModel;
        }
        else
        {
            resolvedModel = string.Equals(profile.Default, "inherit", StringComparison.Ordinal) ? null : profile.Default;
        }

        if (resolvedModel is null)
        {
            return (null, null);
        }

        return ParseThinkingSuffix(resolvedModel);
    }

    /// <summary>
    /// Lowers a capability profile onto Pi's closed <c>tools</c> vocabulary. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants: <c>ask</c> and <c>deny</c> both
    /// withhold, which keeps the lowering non-broadening by construction. An unresolvable
    /// profile grants nothing, mirroring <c>ClaudeRenderer.ResolveTools</c>'s safe-deny
    /// fallback rather than turning a source error into silent widening.
    /// </summary>
    private static IReadOnlyList<string> ResolveTools(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        HashSet<string> granted = new(StringComparer.Ordinal);

        if (capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            foreach ((string capability, string[] tools) in CapabilityTools)
            {
                if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                    decision == SquadPermissionDecision.Allow)
                {
                    foreach (string tool in tools)
                    {
                        granted.Add(tool);
                    }
                }
            }
        }

        return ToolOrder.Where(granted.Contains).ToArray();
    }

    private static bool IsDelegateAllowed(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles) =>
        capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile) &&
        profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision decision) &&
        decision == SquadPermissionDecision.Allow;

    private static IEnumerable<SquadDegradationRecord> BuildSubagentDegradations(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            yield break;
        }

        // 'deny' needs no record: the rendered tools list withholds it. Only 'ask' loses
        // meaning — Pi has no per-capability permission prompt (R9).
        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
        {
            yield return new SquadDegradationRecord(
                Target: "pi",
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "safety-narrowed",
                InstructionDigest: agent.BodyDigest,
                Details: $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                    $"{string.Join(", ", narrowed)}. Pi has no per-capability permission prompt, " +
                    "and the 'tools' key is binary, so these narrow to withheld: the " +
                    "corresponding tools are absent from the agent's 'tools' list.");
        }

        List<string> notExpressibleDetails = [];

        if (profile.Permissions.TryGetValue("network.read", out SquadPermissionDecision readDecision) &&
            readDecision == SquadPermissionDecision.Allow)
        {
            notExpressibleDetails.Add(
                "Capability 'network.read' is allowed but no built-in Pi tool exists to express it.");
        }

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
            publishDecision == SquadPermissionDecision.Allow)
        {
            notExpressibleDetails.Add(
                "Capability 'network.publish' is allowed but no built-in Pi tool exists to express it.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision == SquadPermissionDecision.Allow &&
            agent.DelegatesTo.Count == 0)
        {
            notExpressibleDetails.Add(
                "Capability 'delegate' is allowed but the canonical delegates-to roster is " +
                "empty; expressing 'all' would require 'allowed_subagents' to reach every " +
                "registered agent type, including 'general-purpose', which no empty roster " +
                "authorizes.");
        }

        if (notExpressibleDetails.Count > 0)
        {
            yield return new SquadDegradationRecord(
                Target: "pi",
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "permission-not-expressible",
                InstructionDigest: agent.BodyDigest,
                Details: string.Join(" ", notExpressibleDetails));
        }

        SquadPermissionDecision executeDecision = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec)
            ? exec
            : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write)
            ? write
            : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: "pi",
            canonicalIdentity: agent.Name,
            outputIdentity: agent.Name,
            instructionDigest: agent.BodyDigest,
            executeDecision: executeDecision,
            writeDecision: writeDecision,
            grantedShellTools: ["bash"],
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
            Target: "pi",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "role-skill-fallback",
            InstructionDigest: agent.BodyDigest,
            Details: "Pi core has no primary-agent selection primitive: '.pi/SYSTEM.md' and " +
                "'--system-prompt' replace the whole session prompt, not a per-agent one. " +
                $"Fallback profile '{agent.Fallback}' declares no-primary-agent: " +
                $"{fallbackProfile.NoPrimaryAgent}, so this agent renders as a top-level Pi " +
                "skill instead of a '.pi/agents/' file.");

        string rosterText = agent.DelegatesTo.Count > 0
            ? string.Join(", ", agent.DelegatesTo)
            : "(none declared)";

        yield return new SquadDegradationRecord(
            Target: "pi",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: "Capability decisions " +
                $"({CapabilityDegradations.DescribeCapabilityDecisions(agent, capabilityProfiles, capabilityVocabulary)}) " +
                "are not enforced: a top-level Pi skill runs under the harness default tool " +
                "set, not the canonical capability lattice, and its delegates-to roster " +
                $"({rosterText}) is instruction-only, not a runtime-enforced " +
                "'allowed_subagents' list.");
    }

    private static string CollapseToSingleLine(string value) =>
        string.Join(
            ' ',
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
