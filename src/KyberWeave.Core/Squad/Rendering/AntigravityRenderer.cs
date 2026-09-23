using System.Text;
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
/// <c>role-</c> prefix required because the roots are separate. Shared conductor identities
/// emit only the canonical skill (shared reuse); all other agents emit native agent.md files.
/// </para>
/// <para>
/// Agent frontmatter includes validated keys from the capability profile and model tier:
/// <c>name</c>, <c>description</c>, <c>model</c>, <c>enable_write_tools</c>,
/// <c>enable_subagent_tools</c>, <c>enable_mcp_tools</c>, <c>reasoning_effort</c>,
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
/// </remarks>
public sealed class AntigravityRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".agents/agents";
    private const string SkillsDirectory = ".agents/skills";

    /// <summary>
    /// Shared identities (the fallback profile's <c>shared-identities</c> list) whose
    /// canonical skill is reused instead of emitting a redundant skill projection of that
    /// agent's resources. Read from the loaded fallback profile so a change to
    /// profiles/fallbacks.yml is honored without a renderer change. Native Both emits the
    /// agent at its own path; what is suppressed is the redundant skill projection.
    /// </summary>
    private static HashSet<string> ResolveSharedIdentities(SquadSource source)
    {
        if (!source.FallbackProfiles.Profiles.TryGetValue("role-skill", out SquadFallbackProfile? profile))
        {
            return [];
        }

        return profile.SharedIdentities.ToHashSet(StringComparer.Ordinal);
    }

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

        // Shared identities come from the authoritative fallbacks.yml shared-identities
        // list, not a renderer-local roster — a corpus change to that list is honored
        // without a renderer change. Native Both: shared identities suppress the redundant
        // skill projection, allowing agents and canonical skills to coexist at separate
        // namespace roots.
        HashSet<string> sharedIdentities = ResolveSharedIdentities(source);

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, request.Scope);
        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, request.Scope);

        // Emit canonical skills first, preserving the established ordering.
        foreach (SquadSkill skill in source.Skills)
        {
            files.Add(RenderSkill(skill.Name, skill.Description, skill.InstructionBody, request.Scope));
        }

        // Emit native agent.md files for all agents. Shared identities do not suppress the
        // agent file; the Native Both pattern means agents and skills occupy separate
        // namespaces, and shared identities simply suppress the redundant skill projection
        // of their resources, not the agent file itself.
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

        // Project agent resources beneath their skill identities (the Native Both pattern:
        // agent.md files exist at .agents/agents/<name>, but their resources project
        // under the skills directory for deployment simplicity).
        foreach (SquadAgent agent in source.Agents)
        {
            SquadResourceProjection.Append(
                files,
                $"{skillsDir}/{agent.Name}/SKILL.md",
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

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

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
        string? modelTier = "inherit";
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
        string[] tools = ResolveToolsForAgent(agent, profile, capabilityProfiles);
        if (tools.Length > 0)
        {
            YamlSequenceNode toolsSeq = new();
            foreach (string tool in tools)
            {
                toolsSeq.Add(tool);
            }

            frontmatter.Add("tools", toolsSeq);
        }

        // enable_write_tools: true when filesystem.write is allowed (even if narrowed)
        bool enableWriteTools = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision) &&
                                writeDecision == SquadPermissionDecision.Allow;
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
        SquadCapabilityProfile profile,
        SquadCapabilityProfiles capabilityProfiles)
    {
        List<string> tools = [];

        // filesystem.read
        if (profile.Permissions.TryGetValue("filesystem.read", out SquadPermissionDecision readDecision) &&
            readDecision != SquadPermissionDecision.Deny)
        {
            tools.Add("view_file");
        }

        // filesystem.search - D12: grep_search is withheld pending live verification
        if (profile.Permissions.TryGetValue("filesystem.search", out SquadPermissionDecision searchDecision) &&
            searchDecision != SquadPermissionDecision.Deny)
        {
            tools.Add("list_dir");
            // tools.Add("grep_search");  // D12: withheld
        }

        // filesystem.write - D4/D10: narrowed by XOR with process.execute
        bool processExecuteAllow = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision execDecision) &&
                                   execDecision == SquadPermissionDecision.Allow;
        bool filesystemWriteAllow = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision) &&
                                    writeDecision == SquadPermissionDecision.Allow;

        if (filesystemWriteAllow && !processExecuteAllow)
        {
            // filesystem.write allowed, process.execute not: emit write tools (but withheld ones per D12)
            // tools.Add("write_to_file");  // D12: withheld
            // tools.Add("replace_file_content");  // D12: withheld
            // tools.Add("multi_replace_file_content");  // D12: withheld
        }
        else if (filesystemWriteAllow && processExecuteAllow)
        {
            // Both allowed: emit execute tool only (write tools withheld per D12)
            tools.Add("run_command");
            // tools.Add("write_to_file");  // D12: withheld
            // tools.Add("replace_file_content");  // D12: withheld
            // tools.Add("multi_replace_file_content");  // D12: withheld
        }
        else if (!filesystemWriteAllow && processExecuteAllow)
        {
            // process.execute allowed, filesystem.write not: emit execute tools only
            tools.Add("run_command");
        }

        // process.execute: already handled above in filesystem.write narrowing

        // network.read - D12: withheld pending live verification
        if (profile.Permissions.TryGetValue("network.read", out SquadPermissionDecision netReadDecision) &&
            netReadDecision != SquadPermissionDecision.Deny)
        {
            // tools.Add("search_web");  // D12: withheld
            // tools.Add("read_url_content");  // D12: withheld
        }

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
        SquadPermissionDecision writeDecision = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision_value)
            ? writeDecision_value
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
