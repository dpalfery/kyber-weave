using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Antigravity workspace agents and skills via native
/// per-agent emission (Antigravity has a native agent primitive since version 1.2.7).
/// </summary>
/// <remarks>
/// <para>
/// Antigravity is a native target that emits both per-agent directories and canonical
/// skill files under separate namespace roots: agents at <c>.agents/agents/{name}/agent.md</c>
/// and skills at <c>.agents/skills/{name}/SKILL.md</c>. The "Native Both" pattern applies:
/// collisions (agent identity matching a canonical skill) render to both paths with no
/// <c>role-</c> prefix required because the roots are separate. All agents emit native
/// agent.md files, and canonical skills emit SKILL.md files.
/// </para>
/// <para>
/// Agent frontmatter includes validated keys from the capability profile and model tier:
/// <c>name</c>, <c>description</c>, <c>model</c>, <c>enable_write_tools</c>,
/// <c>enable_subagent_tools</c>, <c>reasoning_effort</c>,
/// <c>tools</c> (narrowed by the capability profile's XOR decision on filesystem.write vs.
/// process.execute; see <see cref="CapabilityDegradations"/>), and <c>mainAgent: true</c>
/// only for the conductor. Granted shell capabilities do not fully isolate write access
/// via redirection; this residual reachability is recorded as a <c>capability-not-isolable</c>
/// degradation via <see cref="CapabilityDegradations.BuildCapabilityNotIsolable"/>. Non-empty
/// <c>delegates-to</c> rosters produce <c>invoke_subagent</c> + <c>enable_subagent_tools: true</c>,
/// plus <c>manage_subagents</c> for orchestrator/conductor; the unenforceable roster is
/// recorded as <c>permission-not-expressible</c>. Declared skills carry no frontmatter capability
/// keys, only identity and license.
/// </para>
/// <para>
/// <b>Arbiter block (Req 6.3, 8.1, 8.2, 22.2).</b> When the render request carries an
/// enabled <see cref="SquadArbiterWiring"/> under
/// <see cref="SquadDeploymentScope.Project"/>, the renderer returns an
/// <c>antigravity</c> <see cref="SquadRenderedBlock"/> for <c>.agents/hooks.json</c>.
/// Antigravity's top-level hook-file keys are group names, so Squad owns the whole
/// top-level group <c>kyber-arbiter</c> — an unknown key there would be read as another
/// group — holding <c>PreToolUse</c> and <c>PostToolUse</c> matcher groups matching
/// <c>^invoke_subagent$</c> with one command hook and the wiring timeout ([F10], D25).
/// The command carries no <c>--caller</c>: a shared hook file gates project-wide, not
/// per agent. Antigravity documents no post-dispatch result, so the render also records
/// <c>arbiter-not-enforced</c> with details <c>no-post-dispatch-feedback</c>, one record
/// per rendered agent (design §10.5 records per target and agent): post-dispatch outcomes
/// are logged and reported by <c>audit</c>, but not delivered back to the harness. A null
/// or disabled wiring, or Global scope (no project configuration to enforce from, Req
/// 22.4), renders no block and no arbiter degradation, leaving the owned files
/// byte-identical.
/// </para>
/// </remarks>
public sealed class AntigravityRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".agents/agents";
    private const string SkillsDirectory = ".agents/skills";

    private const string ArbiterMatcher = "^invoke_subagent$";
    private const string ArbiterCommandLine = "kyber-weave-arbiter hook --harness antigravity";
    private const string ArbiterNotEnforcedCode = "arbiter-not-enforced";
    private const string NoPostDispatchFeedbackReason = "no-post-dispatch-feedback";

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

        return baseDirectory.StartsWith(".agents/", StringComparison.Ordinal)
            ? baseDirectory[".agents/".Length..]
            : baseDirectory;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Antigravity];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Antigravity))
        {
            throw new ArgumentException(
                "AntigravityRenderer was asked to render a target other than Antigravity.",
                nameof(request));
        }

        if (request.Targets.Count == 0)
        {
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }

        SquadSource source = SquadSourceLoader.Load(request.SourceDirectory);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        // The declared vocabulary, not a renderer-local copy: a capability added to
        // profiles/capabilities.yml must appear in degradation text without a renderer
        // change. Sorted for deterministic details strings.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, request.Scope);

        // Emit canonical skills first, preserving the established ordering.
        foreach (SquadSkill skill in source.Skills)
        {
            files.Add(RenderSkill(skill.Name, skill.Description, skill.InstructionBody, request.Scope));
        }

        // Emit native agent.md files for all agents. The Native Both pattern means agents
        // and skills occupy separate namespaces, allowing both to render without collision.
        foreach (SquadAgent agent in source.Agents)
        {
            files.Add(RenderAgent(
                agent,
                source.CapabilityProfiles,
                source.ModelProfiles,
                request.Scope));
        }

        // Degrade agents only for capability constraints that cannot be expressed in
        // frontmatter (D10: shell-implies-write, D12-delegate: unenforceable roster).
        foreach (SquadAgent agent in source.Agents)
        {
            // Records capability permission constraints that cannot be fully expressed
            // in frontmatter.
            SquadDegradationRecord? permission = BuildPermissionDegradation(
                agent,
                source.CapabilityProfiles.Profiles,
                capabilityVocabulary);
            if (permission is not null)
            {
                degradations.Add(permission);
            }

            // D12: Record unenforceable delegates-to roster as permission-not-expressible
            if (agent.DelegatesTo.Count > 0)
            {
                string roster = string.Join(", ", agent.DelegatesTo.Order(StringComparer.Ordinal));
                degradations.Add(new SquadDegradationRecord(
                    Target: "antigravity",
                    CanonicalIdentity: agent.Name,
                    OutputIdentity: agent.Name,
                    Code: "permission-not-expressible",
                    InstructionDigest: agent.BodyDigest,
                    Details: $"Antigravity does not restrict delegates-to roster enforcement; this agent may invoke agents outside its declared roster [{roster}]."));
            }
        }

        // Project agent resources beside their native agent.md so relative links in the
        // unchanged instruction body resolve, and so a same-named canonical skill's
        // resources cannot collide with them.
        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, request.Scope);
        foreach (SquadAgent agent in source.Agents)
        {
            SquadResourceProjection.Append(
                files,
                $"{agentsDir}/{agent.Name}/agent.md",
                agent.Resources,
                "antigravity");
        }

        // Project skill resources beneath their own identities.
        foreach (SquadSkill skill in source.Skills)
        {
            SquadResourceProjection.Append(
                files,
                $"{skillsDir}/{skill.Name}/SKILL.md",
                skill.Resources,
                "antigravity");
        }

        // A null Arbiter must render byte for byte as before the field existed, so the
        // guard lives here: only an enabled wiring at Project scope emits the owned
        // hook group. Under Global scope there is no project configuration to enforce
        // from (Req 22.4).
        List<SquadRenderedBlock> blocks = [];
        if (request.Arbiter is not null && request.Arbiter.Enabled && request.Scope == SquadDeploymentScope.Project)
        {
            blocks.Add(BuildArbiterBlock(request.Arbiter.HookTimeoutSeconds));

            // §10.5: the hook gates a dispatch pre-tool only — Antigravity documents no
            // post-dispatch result, so outcomes are logged and reported by audit but
            // never delivered back. One record per rendered agent, recorded beside the
            // block and never without it.
            foreach (SquadAgent agent in source.Agents)
            {
                degradations.Add(BuildArbiterDegradation(agent));
            }
        }

        return Task.FromResult(new SquadRenderResult(
            true,
            files,
            degradations,
            [],
            [],
            blocks.Count > 0 ? blocks : null));
    }

    /// <summary>
    /// Builds Squad's owned <c>kyber-arbiter</c> group for <c>.agents/hooks.json</c>.
    /// </summary>
    /// <remarks>
    /// [F10]: the block entries are the group's <c>PreToolUse</c>/<c>PostToolUse</c>
    /// event arrays, each holding one matcher group for <c>^invoke_subagent$</c> with one
    /// command hook and the timeout in seconds. The splice sets the group key itself
    /// (design §10.4: top-level keys are group names, so Squad owns the whole group).
    /// Only documented fields are written — no <c>enabled</c> flag and no sentinel key,
    /// because JSON hook files carry no comments (D25).
    /// </remarks>
    private static SquadRenderedBlock BuildArbiterBlock(int timeoutSeconds) =>
        new(
            SquadTargetCatalog.GetToken(SquadTarget.Antigravity),
            SquadHookJsonBlock.RelativePath(SquadHookBlockFormat.Antigravity),
            SquadHookBlockFormat.Antigravity,
            [
                new SquadRenderedBlockEntry("PreToolUse", ArbiterMatcherGroup(timeoutSeconds)),
                new SquadRenderedBlockEntry("PostToolUse", ArbiterMatcherGroup(timeoutSeconds)),
            ]);

    private static JsonObject ArbiterMatcherGroup(int timeoutSeconds) =>
        new()
        {
            ["matcher"] = ArbiterMatcher,
            ["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = ArbiterCommandLine,
                    ["timeout"] = timeoutSeconds,
                }),
        };

    /// <summary>
    /// Records <c>arbiter-not-enforced</c> for one rendered agent with §10.5's
    /// <c>no-post-dispatch-feedback</c> reason: the hook gates a dispatch before it runs,
    /// but post-dispatch outcomes are only logged and reported by <c>audit</c>, never
    /// delivered back. §10.5 records per target and agent, which is also what the
    /// registry's degradation validation requires — every renderer-emitted record names a
    /// rendered agent and carries its instruction digest.
    /// </summary>
    private static SquadDegradationRecord BuildArbiterDegradation(SquadAgent agent) => new(
        Target: SquadTargetCatalog.GetToken(SquadTarget.Antigravity),
        CanonicalIdentity: agent.Name,
        OutputIdentity: agent.Name,
        Code: ArbiterNotEnforcedCode,
        InstructionDigest: agent.BodyDigest,
        Details: NoPostDispatchFeedbackReason);

    /// <summary>
    /// Canonical names become directory names in the rendered tree, so a name carrying
    /// path syntax would not be a portable relative path. The registry's validation pass
    /// would reject it at deployment time; rejecting it here keeps the failure at the
    /// source, before any file is built. A valid canonical name is a single non-empty
    /// path segment with no separators and no traversal.
    /// </summary>
    private static void ValidateCanonicalName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal) ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.Trim('.').Length == 0)
        {
            throw new SquadRenderValidationException(
                $"Canonical name '{name}' is not a valid single-segment path for rendering.");
        }
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        SquadCapabilityProfiles capabilityProfiles,
        SquadModelProfiles modelProfiles,
        SquadDeploymentScope scope)
    {
        ValidateCanonicalName(agent.Name);

        if (!capabilityProfiles.Profiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            throw new SquadRenderValidationException(
                $"Agent '{agent.Name}' references undeclared capability profile '{agent.CapabilityProfile}'.");
        }

        // Resolve model tier from the profiles/models.yml antigravity column
        string modelTier = "inherit";
        if (modelProfiles.Profiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? modelProfile))
        {
            if (modelProfile.HarnessModels.TryGetValue("antigravity", out string? antigravityModel))
            {
                modelTier = antigravityModel;
            }
        }

        // Key order: name, description, model, tools, enable_write_tools,
        // enable_subagent_tools, reasoning_effort, mainAgent (conductor only)
        YamlMappingNode frontmatter = new();
        frontmatter.Add("name", agent.Name);
        frontmatter.Add("description", ToSingleLineScalar(agent.Description));
        frontmatter.Add("model", modelTier);

        // Build tools list from capability profile with XOR narrowing
        string[] tools = ResolveToolsForAgent(agent, profile);
        if (tools.Length > 0)
        {
            YamlSequenceNode toolsSeq = new();
            foreach (string tool in tools)
            {
                toolsSeq.Add(tool);
            }

            frontmatter.Add("tools", toolsSeq);
        }

        // enable_write_tools: true when filesystem.write or process.execute is allowed (ADR 0022 section 4, D4)
        bool filesystemWriteAllow = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision) &&
                                    writeDecision == SquadPermissionDecision.Allow;
        bool processExecuteAllow = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision execDecision) &&
                                   execDecision == SquadPermissionDecision.Allow;
        bool enableWriteTools = filesystemWriteAllow || processExecuteAllow;
        frontmatter.Add("enable_write_tools", enableWriteTools ? "true" : "false");

        // enable_subagent_tools: true when delegates-to is non-empty
        bool enableSubagentTools = agent.DelegatesTo.Count > 0;
        frontmatter.Add("enable_subagent_tools", enableSubagentTools ? "true" : "false");

        // mainAgent: true only for conductor
        if (agent.Name == "conductor")
        {
            frontmatter.Add("mainAgent", "true");
        }

        // reasoning_effort: resolved from the approved per-profile mapping (D9)
        string? reasoningEffort = ResolveReasoningEffort(agent.ModelProfile);
        if (!string.IsNullOrEmpty(reasoningEffort))
        {
            frontmatter.Add("reasoning_effort", reasoningEffort);
        }

        string yaml;
        lock (SerializerLock)
        {
            yaml = YamlSerializer.Serialize(frontmatter);
        }

        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append("---\n");

        string normalizedBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        builder.Append(normalizedBody);
        if (!normalizedBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, scope);
        return new SquadDeploymentFile(
            $"{agentsDir}/{agent.Name}/agent.md",
            Encoding.UTF8.GetBytes(builder.ToString()),
            "antigravity");
    }

    private static string[] ResolveToolsForAgent(
        SquadAgent agent,
        SquadCapabilityProfile profile)
    {
        List<string> tools = [];

        // filesystem.read
        if (profile.Permissions.TryGetValue("filesystem.read", out SquadPermissionDecision readDecision) &&
            readDecision != SquadPermissionDecision.Deny)
        {
            tools.Add("view_file");
        }

        // filesystem.search: D12 withholds grep_search pending live verification
        if (profile.Permissions.TryGetValue("filesystem.search", out SquadPermissionDecision searchDecision) &&
            searchDecision != SquadPermissionDecision.Deny)
        {
            tools.Add("list_dir");
        }

        // filesystem.write - D4/D10: narrowed by XOR with process.execute
        // Note: replace_file_content and multi_replace_file_content are withheld per D12 pending live verification.
        bool processExecuteAllow = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision execDecision) &&
                                   execDecision == SquadPermissionDecision.Allow;
        bool filesystemWriteAllow = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision) &&
                                    writeDecision == SquadPermissionDecision.Allow;

        if (filesystemWriteAllow && !processExecuteAllow)
        {
            // filesystem.write allowed, process.execute not: emit write tools
            tools.Add("write_to_file");
        }
        else if (filesystemWriteAllow && processExecuteAllow)
        {
            // Both allowed: emit execute and write tools
            tools.Add("run_command");
            tools.Add("write_to_file");
        }
        else if (!filesystemWriteAllow && processExecuteAllow)
        {
            // process.execute allowed, filesystem.write not: emit execute tools only
            tools.Add("run_command");
        }

        // process.execute: already handled above in filesystem.write narrowing

        // network.read: D12 withholds search_web and read_url_content pending live verification

        // Delegation: D12 - emit invoke_subagent + manage_subagents for orchestrator
        if (agent.DelegatesTo.Count > 0)
        {
            tools.Add("invoke_subagent");
            if (agent.Name == "orchestrator" || agent.Name == "conductor")
            {
                tools.Add("manage_subagents");
            }
        }

        return tools.OrderBy(t => t, StringComparer.Ordinal).ToArray();
    }

    private static string? ResolveReasoningEffort(string modelProfile) => modelProfile switch
    {
        "architect" => "high",
        "deep-planning" => "high",
        "reviewer" => "high",
        "general" => "medium",
        "fast" => "low",
        "orchestration" => "minimal",
        _ => null
    };

    private static SquadDeploymentFile RenderSkill(string name, string description, string instructionBody, SquadDeploymentScope scope)
    {
        ValidateCanonicalName(name);

        // Key order is the frontmatter contract: name, description, license.
        // YamlMappingNode preserves child order; Dictionary does not define one. No model
        // or permission keys — models.yml has no antigravity entry, and skills cannot
        // express the capability lattice.
        YamlMappingNode frontmatter = new();
        frontmatter.Add("name", name);
        frontmatter.Add("description", ToSingleLineScalar(description));
        frontmatter.Add("license", "MIT");

        string yaml;
        lock (SerializerLock)
        {
            yaml = YamlSerializer.Serialize(frontmatter);
        }

        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append("---\n");

        string normalizedBody = instructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        builder.Append(normalizedBody);
        if (!normalizedBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, scope);
        return new SquadDeploymentFile(
            $"{skillsDir}/{name}/SKILL.md",
            Encoding.UTF8.GetBytes(builder.ToString()),
            "antigravity");
    }

    private static SquadDegradationRecord? BuildPermissionDegradation(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            throw new SquadRenderValidationException(
                $"Agent '{agent.Name}' references capability profile '{agent.CapabilityProfile}', " +
                "which profiles/capabilities.yml does not declare. " +
                $"Declared profiles: {string.Join(", ", capabilityProfiles.Keys.Order(StringComparer.Ordinal))}. " +
                "Correct the agent's capability-profile value before rendering.");
        }

        // D10: Check for capability-not-isolable: process.execute allow + filesystem.write
        // not-allow means write is reachable through shell redirection. This is recorded
        // via the shared CapabilityDegradations helper.
        SquadPermissionDecision executeDecision = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision execDecision)
            ? execDecision
            : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision declaredWrite)
            ? declaredWrite
            : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: "antigravity",
            canonicalIdentity: agent.Name,
            outputIdentity: agent.Name,
            instructionDigest: agent.BodyDigest,
            executeDecision: executeDecision,
            writeDecision: writeDecision,
            grantedShellTools: ["run_command"],
            withheldWriteTools: ["write_to_file", "replace_file_content", "multi_replace_file_content"]);

        if (notIsolable is not null)
        {
            return notIsolable;
        }

        // If process.execute is not the gap, check for other capability constraints
        // that cannot be expressed in agent frontmatter.
        List<string> constrained = capabilityVocabulary
            .Where(capability =>
                profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision != SquadPermissionDecision.Deny)
            .ToList();

        if (constrained.Count == 0)
        {
            return null;
        }

        return new SquadDegradationRecord(
            Target: "antigravity",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: $"Capability profile '{agent.CapabilityProfile}' constrains " +
                $"{string.Join(", ", constrained)} but Antigravity agent frontmatter cannot fully express " +
                "permission boundaries; the deployed agent's behaviour is governed by the capability " +
                "profile and the harness default interaction model.");
    }

    /// <summary>
    /// YAML frontmatter scalars must stay single-line; a folded canonical description
    /// would otherwise emit a block scalar and break consumers that expect a plain string.
    /// </summary>
    private static string ToSingleLineScalar(string value) =>
        string.Join(
            ' ',
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
