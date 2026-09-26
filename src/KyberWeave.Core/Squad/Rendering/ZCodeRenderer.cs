using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into ZCode's three native primitives: subagents under
/// <c>.zcode/agents/</c>, skills under <c>.zcode/skills/</c>, and — for a primary-invocation
/// agent, which ZCode has no primitive for — a slash command under <c>.zcode/commands/</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Source of truth.</b> Every behaviour below was verified on 2026-09-21 against the
/// <c>zai-org/ZCode</c> repository at version <b>3.14.0</b> (Apache-2.0), not against the
/// published documentation. The two disagree on the decisive point: the
/// <c>zcode.z.ai/en/docs/subagents</c> page states that workspace/project-level subagents are
/// not available, while <c>bootstrap/src/subagents.ts</c> loads a project root and
/// <c>services/src/subagents/subagentStorage.ts</c> exports
/// <c>resolveWorkspaceSubagentRoot</c>, which the desktop settings service uses to list,
/// create, and edit them. Project scope is real; the docs page is stale.
/// </para>
/// <para>
/// <b>Discovery roots and precedence.</b> Subagents load from
/// <c>$ZCODE_STORAGE_DIR/agents/</c> (default <c>~/.zcode</c>) then
/// <c>&lt;cwd&gt;/.zcode/agents/</c>; <c>normalizeAgentProfiles</c> collapses the two with a
/// last-write-wins map, so <b>project overrides user</b> on a name collision. Skills load
/// from <c>~/.zcode/skills/</c> and <c>~/.agents/skills/</c>, then from
/// <c>.zcode/skills/</c> and <c>.agents/skills/</c> at every directory from the working
/// directory up to the git worktree root — but <c>discoverSkills</c> dedupes by <b>path</b>,
/// not name, so a same-named user and project skill both load and <c>loadSkill</c> takes the
/// first by name from a stable sort, which is the user root. Skill precedence is therefore
/// the reverse of agent precedence. That asymmetry is upstream's; this renderer does not try
/// to correct it, and onboarding tells an operator to pick one scope per repository.
/// <c>.agents/skills/</c> being a ZCode root also means an Antigravity deployment in the same
/// repository is already visible to ZCode, with <c>.zcode</c> registered first within a scope.
/// </para>
/// <para>
/// <b>Accepted agent frontmatter keys</b> (<c>core/src/subagent/profile.ts</c>): <c>name</c>,
/// <c>description</c>, <c>model</c>, <c>thoughtLevel</c>, <c>color</c>, <c>tools</c>,
/// <c>disallowedTools</c>, <c>skills</c>, <c>permissionMode</c>, <c>maxTurns</c>,
/// <c>background</c>, <c>injectAgentsMd</c>, <c>mcpServers</c>. Only <c>name</c> and
/// <c>description</c> are required and unknown keys are ignored, so this renderer emits
/// <c>name</c>, <c>description</c>, <c>model</c>, and <c>tools</c> and nothing else.
/// <c>permissionMode</c> is deliberately never emitted: <c>sanitizeProjectAgentProfile</c>
/// strips it from project-scope profiles anyway, because repository content may not raise a
/// child runtime to bypass/yolo. <c>general-purpose</c> and <c>Explore</c> are reserved.
/// </para>
/// <para>
/// <b>Frontmatter is not YAML.</b> <c>profile-frontmatter.ts</c> is a hand-rolled line
/// parser: one <c>key: value</c> per physical line, with an empty value opening a
/// <c>- item</c> list. Block and folded scalars are silently dropped — a
/// <c>description: &gt;-</c> yields the literal string <c>&gt;-</c> and the wrapped
/// continuation lines vanish. Every value this renderer emits is therefore a single physical
/// line, and the scalar form matches ZCode's own writer
/// (<c>serializeSubagentMarkdown</c> → <c>formatYamlScalar</c>): a plain scalar when safe,
/// otherwise double-quoted with <c>\\</c>, <c>\"</c>, <c>\n</c>, and <c>\r</c> escaped. Note
/// that <c>unquoteScalar</c> slices the outer quotes without unescaping, so a description
/// containing a literal <c>"</c> round-trips with the backslash visible. That is ZCode's own
/// behaviour for its own output; reproducing it is more honest than inventing an encoding
/// only ZCode's reader would accept.
/// </para>
/// <para>
/// <b>Skills are the one exception, and they are lossless.</b> The skill adapter's
/// <c>parseFlatYaml</c> does support block scalars (<c>parseBlockScalarStyle</c> /
/// <c>readBlockScalar</c>), and the desktop skill service parses skill frontmatter with a real
/// YAML parser. A skill description is therefore emitted as a folded block scalar —
/// <c>description: &gt;-</c> followed by one indented line — which needs no quoting and no
/// escaping, so an embedded <c>"</c> survives intact on both readers. The agent parser cannot
/// do this: a source comment there records that the agent side reads only the top-level
/// <c>description: &gt;</c> and skips the indented continuation, which is exactly the failure
/// the single-line rule above exists to avoid. Agents and commands therefore keep the quoted
/// form and its escape artifact; skills do not.
/// </para>
/// <para>
/// <b>An empty tool list is not an empty grant.</b> <c>resolveAllowedTools</c> reduces both a
/// missing <c>tools</c> key and an explicit <c>tools: []</c> to an empty
/// <c>request.allowedTools</c>, and <c>resolveSubagentToolAllowlist</c> treats that as
/// <c>inheritsAvailableTools</c> — the child then receives <b>every</b> tool the parent has,
/// plus parent MCP via <c>shouldBorrowParentMcp</c>. Emitting <c>[]</c> would therefore be
/// maximal widening dressed as maximal restriction. The key is always emitted with at least
/// the ungoverned base below, and a resolved grant of nothing is a fail-closed render error
/// rather than an empty list.
/// </para>
/// <para>
/// <b>Ungoverned base tools.</b> <c>TodoWrite</c> and <c>Skill</c> are granted on every
/// rendered principal, matching <see cref="ClaudeRenderer"/>'s base. <c>TodoWrite</c> writes
/// no file and executes nothing. <c>Skill</c> is what makes the 24 canonical skills this
/// renderer deploys reachable at all; withholding it would deploy a skill tree no agent could
/// open. The <c>skills</c> frontmatter key is deliberately not emitted, because
/// <c>resolveSubagentSkillPort</c> treats an absent list as "every discovered skill" and a
/// present one as a hard filter — and the canonical model does not declare per-agent skill
/// rosters. Only <see cref="SquadPermissionDecision.Allow"/> grants anything further;
/// <c>ask</c> and <c>deny</c> both withhold, because ZCode has no per-capability permission
/// prompt, which keeps the lowering non-broadening by construction and records <c>ask</c> as
/// <c>safety-narrowed</c>. <c>network.publish</c> has no built-in tool at all, so an
/// <c>allow</c> for it records <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// <b>MCP is granted by concrete tool name, and fails closed when a server is missing.</b>
/// <c>registerMcpTools</c> admits a tool only on an exact set membership test against
/// <c>toolAllowlist</c>, with no wildcard expansion, so <see cref="ClaudeRenderer"/>'s
/// <c>mcp__kyber-weave__*</c> server-selector form registers nothing here — and worse, it is
/// still collected into <c>requiredServerNames</c>, so it imposes a connected-server
/// requirement while granting no tool. This renderer therefore emits the fully qualified
/// <c>mcp__&lt;server&gt;__&lt;tool&gt;</c> names declared by <c>toolchain.yml</c>'s
/// <c>required-mcp-tools</c>, which <c>registerMcpTools</c> matches exactly and
/// <c>validateSubagentMcpRequirements</c> treats as hard requirements: an agent whose server
/// is not connected, or whose tool is absent from the parent startup snapshot, fails with a
/// configuration error rather than running under-equipped. That is the intended behaviour —
/// <c>squad doctor</c> reports a missing server against the same declared roster, so the
/// breakage surfaces at diagnosis time rather than mid-run.
/// </para>
/// <para>
/// The roster is read from canonical source rather than hardcoded here because it is an
/// external contract that drifts — context7 renamed <c>get-library-docs</c> to
/// <c>query-docs</c> — and because a harness matching by exact name breaks on a rename
/// instead of degrading. Keeping one declared roster also means the renderer and the doctor
/// check cannot disagree. <c>mcpServers</c> is still never emitted: it would scope the
/// borrowed connection set, but the tool allow-list already decides what the model can see,
/// and naming a server there adds a second, redundant failure mode.
/// </para>
/// <para>
/// A pure orchestrator gets no MCP, matching <see cref="ClaudeRenderer"/>'s carve-out: a role
/// that only routes work has no use for documentation or code-graph lookups, and granting
/// them would hand it a research surface its own capability profile denies. It records
/// <c>permission-not-expressible</c> for the withheld servers so the omission is visible.
/// </para>
/// <para>
/// <b>Delegation cannot be scoped.</b> <c>toolNameFromSpec</c> truncates every specifier at
/// <c>(</c>, so <c>Agent(architect, research-agent)</c> is stored as bare <c>Agent</c> and
/// <c>Bash(git:*)</c> as bare <c>Bash</c>. ZCode has no runtime-enforced delegation roster —
/// no <c>allowed_subagents</c> equivalent — so an allowed <c>delegate</c> grants bare
/// <c>Agent</c> and records <c>permission-not-expressible</c> naming the roster that is not
/// enforced. This is the same shape <see cref="PiRenderer"/> uses for its own unenforceable
/// case: record the gap rather than claim a mapping.
/// </para>
/// <para>
/// <b>Primary-agent lowering goes to a command, not a skill.</b> ZCode has no primary-agent
/// primitive — <c>ZCODE_AGENT_MODE_OPTIONS</c> is a closed set of permission modes
/// (<c>build</c>, <c>edit</c>, <c>plan</c>, <c>yolo</c>), not a selectable agent. Unlike every
/// other target on the roster, ZCode does have project-scoped slash commands, so a
/// <see cref="SquadInvocation.Primary"/> agent whose fallback profile declares
/// <c>no-primary-agent: skill</c> renders at <c>.zcode/commands/&lt;name&gt;.md</c> and is
/// invoked as <c>/&lt;name&gt;</c>, recording <c>role-skill-fallback</c> and
/// <c>permission-not-expressible</c>. <c>omit</c> emits nothing and records <c>omitted</c>.
/// Command frontmatter uses <b>kebab-case</b> keys, unlike agents' camelCase, and an unknown
/// key is a warning diagnostic, so only <c>description</c>, <c>allowed-tools</c>, and
/// <c>model</c> are emitted.
/// </para>
/// <para>
/// <b>Agent and command resources are rewritten into the skills tree.</b> Both
/// <c>.zcode/agents/</c> and <c>.zcode/commands/</c> are scanned <b>recursively</b>
/// (<c>listMarkdownFiles</c>; <c>scanMarkdownFiles</c> to depth 12), and a command's name is
/// derived from its relative path with separators mapped to <c>:</c>. Projecting an agent's
/// resource closure beside its principal is therefore not merely noisy but wrong:
/// <c>.zcode/commands/conductor/references/plan-path.md</c> would register as a real command
/// named <c>conductor:references:plan-path</c>, because a command with no frontmatter falls
/// back to its first body line for a description. So for agents and the lowered command only,
/// resources are projected under <c>.zcode/skills/&lt;owner&gt;/…</c> and the owner's authored
/// links are rewritten to <c>../skills/…</c> — a relative form identical under both scopes.
/// <c>.zcode/skills/&lt;owner&gt;/</c> without a <c>SKILL.md</c> is not a skill, because
/// <c>scanSkillFilesUnderRoot</c> only admits a directory containing one. The rewrite is
/// driven by the declared resource closure, never by a regex over prose, and is recorded as
/// <c>resource-links-rewritten</c> so the deviation from the repository's
/// "authored links resolve verbatim" rule is visible in the receipt. Skill resources keep the
/// ordinary <see cref="SquadResourceProjection"/> treatment: the skill scanner is one level
/// deep and never mistakes them for skills.
/// </para>
/// </remarks>
public sealed class ZCodeRenderer : ISquadRenderer
{
    private const string TargetToken = "zcode";
    private const string AgentsDirectory = ".zcode/agents";
    private const string SkillsDirectory = ".zcode/skills";
    private const string CommandsDirectory = ".zcode/commands";

    /// <summary>
    /// The capability profile whose holder routes work rather than researching it, and which
    /// <see cref="ClaudeRenderer"/> also excludes from the standard MCP grant.
    /// </summary>
    private const string PureOrchestratorProfile = "orchestrator";

    /// <summary>
    /// ZCode drops a skill whose description exceeds this length outright rather than
    /// truncating it (<c>skill_description_too_long</c> is an <c>error</c> severity and
    /// <c>parseSkill</c> returns null), so a canonical description over the cap must fail the
    /// render rather than deploy a silently missing skill.
    /// </summary>
    private const int MaxDescriptionLength = 1024;

    /// <summary>
    /// Lowers the semantic capability vocabulary onto ZCode's built-in tool names, verified
    /// against <c>core/src/tool/provider-visible-order.ts</c>. <c>network.publish</c> is absent
    /// deliberately: no built-in tool expresses it. <c>delegate</c> maps to bare
    /// <c>Agent</c> but is handled separately, because granting it always loses the roster.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityTools =
    [
        ("filesystem.read", ["Read"]),
        ("filesystem.search", ["Grep", "Glob"]),
        ("filesystem.write", ["Edit", "Write"]),
        ("process.execute", ["Bash"]),
        ("network.read", ["WebFetch", "WebSearch"]),
        ("delegate", ["Agent"]),
    ];

    /// <summary>
    /// Granted on every rendered principal regardless of capability profile, matching
    /// <see cref="ClaudeRenderer"/>'s own ungoverned base. Neither can broaden a canonical
    /// decision: <c>TodoWrite</c> writes no file and executes nothing, and <c>Skill</c> only
    /// opens the skill tree this renderer itself deploys.
    /// </summary>
    private static readonly string[] UngovernedTools = ["TodoWrite", "Skill"];

    /// <summary>
    /// Fixed <c>tools</c> emission order, so a rendered agent is byte-stable regardless of how
    /// the capability profile's permissions happen to enumerate.
    /// </summary>
    private static readonly string[] ToolOrder =
    [
        "TodoWrite", "Skill", "Read", "Grep", "Glob", "Edit", "Write", "Bash",
        "WebFetch", "WebSearch", "Agent"
    ];

    /// <summary>
    /// A Markdown inline link or image whose target is captured for resource-link rewriting.
    /// Matching the link syntax rather than the bare path keeps the rewrite from touching
    /// prose that merely mentions a filename.
    /// </summary>
    private static readonly Regex MarkdownLinkTarget = new(
        @"(?<prefix>\]\()(?<target>[^)\s]+)(?<suffix>\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.ZCode];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.ZCode))
        {
            throw new ArgumentException(
                "ZCodeRenderer was asked to render a target other than ZCode.",
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

        // Read upfront so the lowered-command collision check can see whether a canonical
        // skill already occupies the identity the resource closure would claim, regardless of
        // which loop reaches it first.
        HashSet<string> skillIdentities = source.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.Ordinal);

        // The declared vocabulary, not a renderer-local copy: a capability added to
        // profiles/capabilities.yml must appear in a lowered agent's
        // permission-not-expressible details without a renderer change.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

        // Read from canonical source rather than a renderer-local list, so a tool renamed
        // upstream is a source edit, and so the doctor check reads the same roster.
        IReadOnlyList<string> qualifiedMcpToolNames = QualifiedMcpToolNames(source);
        IReadOnlyList<string> mcpServerNames = DeclaredMcpServerNames(source);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        foreach (SquadAgent agent in source.Agents)
        {
            if (agent.Invocation == SquadInvocation.Subagent)
            {
                RenderSubagent(
                    agent,
                    source,
                    request.Scope,
                    skillIdentities,
                    qualifiedMcpToolNames,
                    mcpServerNames,
                    files,
                    degradations);
                continue;
            }

            RenderPrimaryAgent(
                agent,
                source,
                request.Scope,
                skillIdentities,
                capabilityVocabulary,
                qualifiedMcpToolNames,
                mcpServerNames,
                files,
                degradations);
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

            SquadDeploymentFile principal = RenderSkill(
                skill.Name,
                skill.Description,
                skill.InstructionBody,
                request.Scope);
            files.Add(principal);

            // Skill resources keep the ordinary treatment: scanSkillFilesUnderRoot is one
            // level deep and admits only a directory holding a SKILL.md, so a resource beside
            // the skill is never mistaken for one.
            SquadResourceProjection.Append(files, principal, skill.Resources);
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static void RenderSubagent(
        SquadAgent agent,
        SquadSource source,
        SquadDeploymentScope scope,
        IReadOnlySet<string> skillIdentities,
        IReadOnlyList<string> qualifiedMcpToolNames,
        IReadOnlyList<string> mcpServerNames,
        List<SquadDeploymentFile> files,
        List<SquadDegradationRecord> degradations)
    {
        string body = RewriteResourceLinks(agent, skillIdentities, degradations, out bool rewritten);

        Dictionary<string, string> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = CollapseToSingleLine(agent.Description)
        };

        string? model = ResolveZCodeModel(agent, source.ModelProfiles.Profiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        IReadOnlyList<string> tools = ResolveTools(
            agent, source.CapabilityProfiles.Profiles, qualifiedMcpToolNames);

        string agentsDirectory = ResolvePrefixedDirectory(AgentsDirectory, scope);
        files.Add(new SquadDeploymentFile(
            $"{agentsDirectory}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(SquadMarkdownDocument.Compose(
                ComposeFrontmatter(frontmatter, ("tools", tools)),
                body)),
            TargetToken));

        AppendRewrittenResources(files, agent, scope, rewritten);
        degradations.AddRange(BuildCapabilityDegradations(
            agent, source.CapabilityProfiles.Profiles, mcpServerNames));

    }

    private static void RenderPrimaryAgent(
        SquadAgent agent,
        SquadSource source,
        SquadDeploymentScope scope,
        IReadOnlySet<string> skillIdentities,
        IReadOnlyList<string> capabilityVocabulary,
        IReadOnlyList<string> qualifiedMcpToolNames,
        IReadOnlyList<string> mcpServerNames,
        List<SquadDeploymentFile> files,
        List<SquadDegradationRecord> degradations)
    {
        SquadFallbackProfile fallbackProfile = source.FallbackProfiles.Profiles[agent.Fallback];

        if (string.Equals(fallbackProfile.NoPrimaryAgent, "omit", StringComparison.Ordinal))
        {
            degradations.Add(new SquadDegradationRecord(
                Target: TargetToken,
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "omitted",
                InstructionDigest: agent.BodyDigest,
                Details: $"Fallback profile '{agent.Fallback}' declares no-primary-agent: omit; " +
                    "ZCode has no primary-agent primitive for this agent to render onto, so no " +
                    "file is emitted."));
            return;
        }

        if (!string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal))
        {
            throw new SquadRenderValidationException(
                $"Fallback profile '{agent.Fallback}' declares unsupported no-primary-agent " +
                $"value '{fallbackProfile.NoPrimaryAgent}' for primary agent '{agent.Name}'.");
        }

        // A command name is lowercased and must match ^[a-z0-9][a-z0-9_:-]{0,63}$
        // (COMMAND_NAME_PATTERN); a canonical name that cannot satisfy it would be dropped
        // with a custom_command_invalid_name diagnostic rather than deployed.
        if (!IsValidCommandName(agent.Name))
        {
            throw new SquadRenderValidationException(
                $"Cannot lower primary agent '{agent.Name}' to a ZCode command: the name does " +
                "not match ZCode's command-name pattern ^[a-z0-9][a-z0-9_:-]{0,63}$, so the " +
                "command would be rejected at load with 'custom_command_invalid_name' rather " +
                "than deployed.");
        }

        string body = RewriteResourceLinks(agent, skillIdentities, degradations, out bool rewritten);

        Dictionary<string, string> frontmatter = new(StringComparer.Ordinal)
        {
            ["description"] = CollapseToSingleLine(agent.Description)
        };

        string? model = ResolveZCodeModel(agent, source.ModelProfiles.Profiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        IReadOnlyList<string> tools = ResolveTools(
            agent, source.CapabilityProfiles.Profiles, qualifiedMcpToolNames);

        string commandsDirectory = ResolvePrefixedDirectory(CommandsDirectory, scope);
        files.Add(new SquadDeploymentFile(
            $"{commandsDirectory}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(SquadMarkdownDocument.Compose(
                ComposeFrontmatter(frontmatter, ("allowed-tools", tools)),
                body)),
            TargetToken));

        AppendRewrittenResources(files, agent, scope, rewritten);
        degradations.AddRange(BuildPrimaryAgentDegradations(
            agent,
            fallbackProfile,
            source.CapabilityProfiles.Profiles,
            capabilityVocabulary,
            mcpServerNames));
    }

    private static SquadDeploymentFile RenderSkill(
        string name,
        string description,
        string instructionBody,
        SquadDeploymentScope scope)
    {
        string collapsed = CollapseToSingleLine(description);
        if (collapsed.Length > MaxDescriptionLength)
        {
            throw new SquadRenderValidationException(
                $"Skill '{name}' has a {collapsed.Length.ToString(CultureInfo.InvariantCulture)}-" +
                $"character description; ZCode drops a skill whose description exceeds " +
                $"{MaxDescriptionLength.ToString(CultureInfo.InvariantCulture)} characters " +
                "outright rather than truncating it, so deploying this would silently omit the " +
                "skill from the harness.");
        }

        // name, description, when_to_use, license, metadata are SAFE_FRONTMATTER_KEYS; any
        // key outside that set clears safeToAutoLoad and disables implicit invocation, so the
        // emitted set stays inside it.
        StringBuilder frontmatter = new();
        frontmatter.Append("name: ").Append(FormatScalar(name)).Append('\n');

        // A folded block scalar rather than a quoted one: both skill readers support it, and
        // it carries an embedded quote without the escape artifact the agent form leaves.
        // The description is already collapsed to one line, so folding is a no-op on content.
        frontmatter.Append("description: >-\n  ").Append(collapsed).Append('\n');
        frontmatter.Append("license: MIT\n");

        string skillsDirectory = ResolvePrefixedDirectory(SkillsDirectory, scope);
        return new SquadDeploymentFile(
            $"{skillsDirectory}/{name}/SKILL.md",
            Encoding.UTF8.GetBytes(SquadMarkdownDocument.Compose(
                frontmatter.ToString(),
                instructionBody)),
            TargetToken);
    }

    /// <summary>
    /// Projects an agent's or lowered command's resource closure under the skills tree, where
    /// neither the recursive agent scan nor the recursive command scan can reach it.
    /// </summary>
    private static void AppendRewrittenResources(
        List<SquadDeploymentFile> files,
        SquadAgent agent,
        SquadDeploymentScope scope,
        bool rewritten)
    {
        if (!rewritten)
        {
            return;
        }

        string skillsDirectory = ResolvePrefixedDirectory(SkillsDirectory, scope);
        foreach (SquadResource resource in agent.Resources)
        {
            files.Add(new SquadDeploymentFile(
                $"{skillsDirectory}/{resource.RelativePath}",
                Encoding.UTF8.GetBytes(resource.Content),
                TargetToken));
        }
    }

    /// <summary>
    /// Rewrites the owner's authored links to its declared resources so they resolve from the
    /// skills tree, and records the deviation.
    /// </summary>
    /// <remarks>
    /// Driven by the declared resource closure rather than a pattern over prose: only a
    /// Markdown link target that exactly equals a declared resource's relative path is
    /// rewritten, so a body that merely names a file in text is untouched. The emitted
    /// <c>../skills/&lt;path&gt;</c> form is correct from both <c>.zcode/agents/</c> and
    /// <c>.zcode/commands/</c>, and identical under project and global scope because both
    /// directories sit one level below the same root.
    /// </remarks>
    private static string RewriteResourceLinks(
        SquadAgent agent,
        IReadOnlySet<string> skillIdentities,
        List<SquadDegradationRecord> degradations,
        out bool rewritten)
    {
        rewritten = false;
        if (agent.Resources.Count == 0)
        {
            return agent.InstructionBody;
        }

        // The closure lands under .zcode/skills/<first-segment>/. If a canonical skill already
        // owns that identity, its SKILL.md and these resources would share a directory, and a
        // later canonical resource could overwrite a skill's own file. Fail closed rather than
        // resolve it here.
        foreach (string owner in agent.Resources
                     .Select(resource => FirstSegment(resource.RelativePath))
                     .Distinct(StringComparer.Ordinal))
        {
            if (skillIdentities.Contains(owner))
            {
                throw new SquadRenderValidationException(
                    $"Cannot project resources for agent '{agent.Name}' into the ZCode skills " +
                    $"tree: a canonical skill named '{owner}' already occupies " +
                    $"'.zcode/skills/{owner}/'. ZCode scans '.zcode/agents/' and " +
                    "'.zcode/commands/' recursively, so the closure cannot stay beside its " +
                    "principal, and it cannot share a directory with a canonical skill either.");
            }
        }

        HashSet<string> resourcePaths = agent.Resources
            .Select(resource => resource.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        bool changed = false;
        string body = MarkdownLinkTarget.Replace(agent.InstructionBody, match =>
        {
            string target = match.Groups["target"].Value;
            int suffixIndex = target.IndexOfAny(['#', '?']);
            string pathPart = suffixIndex < 0 ? target : target[..suffixIndex];
            string linkSuffix = suffixIndex < 0 ? string.Empty : target[suffixIndex..];
            if (pathPart.Length == 0 || !resourcePaths.Contains(Uri.UnescapeDataString(pathPart)))
            {
                return match.Value;
            }

            changed = true;
            return $"{match.Groups["prefix"].Value}../skills/{pathPart}{linkSuffix}{match.Groups["suffix"].Value}";
        });

        rewritten = true;
        degradations.Add(new SquadDegradationRecord(
            Target: TargetToken,
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "resource-links-rewritten",
            InstructionDigest: agent.BodyDigest,
            Details: $"ZCode scans '.zcode/agents/' and '.zcode/commands/' recursively, so this " +
                $"agent's {agent.Resources.Count.ToString(CultureInfo.InvariantCulture)} " +
                "resource file(s) cannot be projected beside their principal without " +
                "registering as phantom agents or commands. They are projected under " +
                "'.zcode/skills/' instead, and " +
                (changed
                    ? "the agent's authored links were rewritten to '../skills/' so they still resolve."
                    : "no authored link required rewriting.")));

        return body;
    }

    /// <summary>
    /// Emits agent and command frontmatter in the scalar form ZCode's own writer produces,
    /// with the tool list last.
    /// </summary>
    /// <remarks>
    /// The tool list is a required parameter rather than an optional one, because every
    /// principal with a tool surface must carry it: omitting the key grants every built-in
    /// tool, which would silently widen every canonical <c>deny</c>. Skills do not come
    /// through here — they have no tool surface and their description uses a block scalar
    /// this composer does not emit.
    /// </remarks>
    private static string ComposeFrontmatter(
        IReadOnlyDictionary<string, string> scalars,
        (string Key, IReadOnlyList<string> Values) toolList)
    {
        StringBuilder builder = new();
        foreach ((string key, string value) in scalars)
        {
            builder.Append(key).Append(": ").Append(FormatScalar(value)).Append('\n');
        }

        (string listKey, IReadOnlyList<string> values) = toolList;
        if (values.Count == 0)
        {
            // Unreachable: ResolveTools already fails closed on an empty grant. Kept as a
            // throw rather than an emit because '[]' is ZCode's inherit-everything literal,
            // so the branch that used to produce it was a widening trap waiting for a caller.
            throw new SquadRenderValidationException(
                $"Refusing to emit an empty '{listKey}' list: ZCode reads it as " +
                "inherit-every-parent-tool, not as an empty grant.");
        }

        builder.Append(listKey).Append(":\n");
        foreach (string value in values)
        {
            builder.Append("  - ").Append(FormatScalar(value)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Mirrors ZCode's <c>formatYamlScalar</c>: a plain scalar when its own
    /// <c>isSafePlainYamlScalar</c> predicate would accept it, otherwise a double-quoted
    /// scalar escaped by its own <c>escapeYamlString</c> rules.
    /// </summary>
    private static string FormatScalar(string value) =>
        IsSafePlainScalar(value) ? value : $"\"{EscapeScalar(value)}\"";

    private static bool IsSafePlainScalar(string value)
    {
        if (value.Length == 0 || !string.Equals(value.Trim(), value, StringComparison.Ordinal))
        {
            return false;
        }

        if (SafePlainReserved.IsMatch(value) ||
            SafePlainNumeric.IsMatch(value) ||
            SafePlainLeading.IsMatch(value))
        {
            return false;
        }

        return !value.Contains('#', StringComparison.Ordinal) &&
            !SafePlainMappingLike.IsMatch(value) &&
            SafePlainShape.IsMatch(value);
    }

    private static readonly Regex SafePlainReserved = new(
        "^(?:false|null|true|~)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex SafePlainNumeric = new(
        @"^[-+]?(?:\d+|\d*\.\d+)(?:e[-+]?\d+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex SafePlainLeading = new(
        @"^[*?:,\[\]{}&!|>'""%@`-]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex SafePlainMappingLike = new(
        @":\s",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex SafePlainShape = new(
        @"^[A-Za-z0-9_./@*][A-Za-z0-9_./@*\s()-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex CommandName = new(
        "^[a-z0-9][a-z0-9_:-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static string EscapeScalar(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal);

    private static bool IsValidCommandName(string name) => CommandName.IsMatch(name);

    private static string FirstSegment(string relativePath)
    {
        int separator = relativePath.IndexOf('/', StringComparison.Ordinal);
        return separator < 0 ? relativePath : relativePath[..separator];
    }

    /// <summary>
    /// Under global scope the deployment root is already the ZCode storage directory, so the
    /// portable path drops its <c>.zcode/</c> prefix and the same three subtrees hang off it.
    /// </summary>
    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        if (scope == SquadDeploymentScope.Project)
        {
            return baseDirectory;
        }

        return baseDirectory.StartsWith(".zcode/", StringComparison.Ordinal)
            ? baseDirectory[".zcode/".Length..]
            : baseDirectory;
    }

    /// <summary>
    /// Resolves the harness-specific model exactly as <c>ClaudeRenderer.ResolveClaudeModel</c>
    /// does: the <c>zcode</c> harness override when the model profile declares one, otherwise
    /// the target-neutral <c>default</c>; either an explicit or a defaulted <c>inherit</c>
    /// omits the key, since that is the harness's own deferral value.
    /// </summary>
    private static string? ResolveZCodeModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue(TargetToken, out string? zcodeModel))
        {
            return string.Equals(zcodeModel, "inherit", StringComparison.Ordinal) ? null : zcodeModel;
        }

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal) ? null : profile.Default;
    }

    /// <summary>
    /// Lowers a capability profile onto ZCode's built-in tool vocabulary. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants: <c>ask</c> and <c>deny</c> both
    /// withhold, which keeps the lowering non-broadening by construction. An unresolvable
    /// profile grants nothing but <c>TodoWrite</c>, mirroring <c>ClaudeRenderer.ResolveTools</c>'s
    /// safe-deny fallback rather than turning a source error into silent widening.
    /// </summary>
    private static IReadOnlyList<string> ResolveTools(
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
                    foreach (string tool in tools)
                    {
                        granted.Add(tool);
                    }
                }
            }
        }

        // MCP names append after the built-ins rather than joining ToolOrder: they come from
        // source, so they cannot be part of a fixed renderer-local order, and they are already
        // emitted in a stable server-then-tool order by the caller.
        IReadOnlyList<string> resolved =
        [
            .. ToolOrder.Where(granted.Contains),
            .. GrantsMcp(agent, capabilityProfiles) ? qualifiedMcpToolNames : []
        ];
        if (resolved.Count == 0)
        {
            // Unreachable while the ungoverned base is non-empty, and deliberately loud if
            // that ever changes: an empty list is ZCode's inherit-everything signal, so the
            // one thing this renderer must never emit is the thing an empty grant would
            // produce.
            throw new SquadRenderValidationException(
                $"Agent '{agent.Name}' resolved to an empty ZCode tool list. ZCode reads both a " +
                "missing 'tools' key and 'tools: []' as inherit-every-parent-tool, so an empty " +
                "list cannot be emitted as a restriction.");
        }

        return resolved;
    }

    private static IEnumerable<SquadDegradationRecord> BuildCapabilityDegradations(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> mcpServerNames)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            yield break;
        }

        // 'deny' needs no record: the rendered tools list withholds it. Only 'ask' loses
        // meaning — ZCode has no per-capability permission prompt.
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
                    $"{string.Join(", ", narrowed)}. ZCode has no per-capability permission " +
                    "prompt, and the 'tools' key is a binary allow-list, so these narrow to " +
                    "withheld: the corresponding tools are absent from the agent's 'tools' list.");
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
                "Capability 'network.publish' is allowed but no built-in ZCode tool exists to express it.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision == SquadPermissionDecision.Allow)
        {
            string roster = agent.DelegatesTo.Count > 0
                ? string.Join(", ", agent.DelegatesTo)
                : "(none declared)";
            notExpressible.Add(
                "Capability 'delegate' is allowed, so the 'Agent' tool is granted, but the " +
                $"canonical delegates-to roster ({roster}) is not enforced: ZCode's " +
                "'toolNameFromSpec' truncates every tool specifier at '(', so an " +
                "'Agent(<roster>)' entry would be stored as bare 'Agent', and ZCode has no " +
                "'allowed_subagents' equivalent to scope it.");
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
            grantedShellTools: ["Bash"],
            withheldWriteTools: ["Edit", "Write"]);

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
        IReadOnlyList<string> mcpServerNames)
    {
        yield return new SquadDegradationRecord(
            Target: TargetToken,
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "role-skill-fallback",
            InstructionDigest: agent.BodyDigest,
            Details: "ZCode has no primary-agent selection primitive: its agent modes are a " +
                "closed set of permission modes (build, edit, plan, yolo), not selectable " +
                $"agents. Fallback profile '{agent.Fallback}' declares no-primary-agent: " +
                $"{fallbackProfile.NoPrimaryAgent}, and ZCode has a project-scoped slash-command " +
                "primitive, so this agent renders as a command at '.zcode/commands/" +
                $"{agent.Name}.md' and is invoked as '/{agent.Name}' rather than as a skill.");

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
                "are not enforced as the canonical lattice: a command's 'allowed-tools' is a " +
                "flat allow-list evaluated against the session the command runs in, not a " +
                $"per-agent capability boundary, and its delegates-to roster ({rosterText}) is " +
                "instruction-only." +
                (mcpServerNames.Count > 0 && !GrantsMcp(agent, capabilityProfiles)
                    ? " " + DescribeWithheldMcp(agent, mcpServerNames)
                    : string.Empty));
    }

    /// <summary>
    /// The fully qualified MCP tool names declared by <c>toolchain.yml</c>, in a stable order,
    /// ready to append to a tool allow-list ZCode matches by exact name.
    /// </summary>
    private static IReadOnlyList<string> QualifiedMcpToolNames(SquadSource source) =>
    [
        .. source.Toolchain.RequiredMcpTools
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .SelectMany(entry => entry.Value
                .OrderBy(tool => tool, StringComparer.Ordinal)
                .Select(tool => $"mcp__{entry.Key}__{tool}"))
    ];

    /// <summary>
    /// Why a principal did not receive the declared MCP tools. Only a profile outside the
    /// standard grant reaches this: everything else is granted by concrete tool name.
    /// </summary>
    private static string DescribeWithheldMcp(
        SquadAgent agent,
        IReadOnlyList<string> mcpServerNames) =>
        $"Declared MCP server(s) {string.Join(", ", mcpServerNames)} are withheld: capability " +
        $"profile '{agent.CapabilityProfile}' does not allow 'filesystem.read', or is the pure " +
        "orchestrator profile that routes work rather than researching it. A role that only " +
        "routes has no use for documentation or code-graph lookups, and granting them would " +
        "hand it a research surface its own profile denies.";

    /// <summary>The declared MCP server names, for a record naming what was withheld.</summary>
    private static IReadOnlyList<string> DeclaredMcpServerNames(SquadSource source) =>
        [.. source.Toolchain.RequiredMcpTools.Keys.OrderBy(name => name, StringComparer.Ordinal)];

    /// <summary>
    /// Whether a principal is entitled to the declared MCP tools, mirroring
    /// <see cref="ClaudeRenderer"/>'s rule: any agent allowed to read the filesystem, except a
    /// pure orchestrator, which routes work rather than researching it.
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
