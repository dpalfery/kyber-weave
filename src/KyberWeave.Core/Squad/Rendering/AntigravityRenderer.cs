using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Core.Events;
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
/// <b>MCP reach is declared per agent, in <c>mcpServers</c>, and nowhere else.</b> The
/// <c>kyber-weave</c> server is granted to every agent whose capability profile resolves and allows
/// <c>filesystem.read</c> — <c>conductor</c> included, because the server carries
/// <c>agent_coding_standards</c> and the role that assigns work is the role deciding which standards
/// apply to whom (ADR 0049). The grant goes through
/// <see cref="CapabilityDegradations.GrantsUniversalMcpServers"/>, the one shared predicate, rather
/// than a renderer-local copy of the rule. The pure-orchestrator carve-out that survives for
/// <c>codegraph</c> and <c>context7</c> is not expressed here: this renderer declares neither
/// server, so there is nothing to withhold from anyone.
/// </para>
/// <para>
/// The emitted entry shape was live-probed against the installed <c>agy</c> 1.4.12 rather than read
/// off vendor documentation, following the discipline ADR 0022 established for every Antigravity
/// capability: <c>mcpServers</c> is an array of objects, each entry
/// <c>{name, command, args}</c>, and that entry — not the single-key map spelling
/// <c>- kyber-weave: {command, args}</c>, which listed and ran but was never called — is the one the
/// probe invoked successfully. <c>args</c> carries <c>--repo-root .</c>, workspace-relative, because
/// an absolute path can be overridden by a same-named global server definition and because
/// workspace-relative is what the rest of Squad emits.
/// </para>
/// <para>
/// Three probe findings bound this emission. The <c>tools</c> allow-list does <b>not</b> gate MCP
/// availability — an agent whose tools list held only <c>view_file</c> still called an MCP tool — so
/// no MCP tool name is added there, and none may ever be: Antigravity validates <c>tools</c> entries
/// against its executor registry and fails fast on an unmapped name
/// (<c>unknown component: tool "..." not found in registry</c>, surfaced on stderr with exit code
/// 0, so the failure is silent to a caller reading only the exit code). <c>enable_mcp_tools: false</c>
/// does not block a server declared in frontmatter either, so no such key is emitted to enable one.
/// Unknown frontmatter keys are ignored, which is why the entry has to be exactly the probed shape
/// rather than merely a plausible one.
/// </para>
/// </remarks>
public sealed class AntigravityRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".agents/agents";
    private const string SkillsDirectory = ".agents/skills";

    /// <summary>
    /// The server every read-entitled agent gets, and the only one this target declares. Named here
    /// because the grant is a property of the squad's own <c>mcp.json</c> entry whose launch
    /// definition is emitted verbatim; a second server would need its own declaration and its own
    /// probe before it could be rendered (ADR 0049 decision 7).
    /// </summary>
    private const string UniversalMcpServer = "kyber-weave";

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

        SquadSource source = SquadRenderSource.Load(request, SquadTarget.Antigravity);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        // The declared vocabulary, not a renderer-local copy: a capability added to
        // profiles/capabilities.yml must appear in degradation text without a renderer
        // change. Sorted for deterministic details strings.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, request.Scope);

        // Read once: the launch definition comes from the squad's own declaration, so no renderer
        // holds a second copy of the command line that could drift from mcp.json.
        McpServerDeclaration? kyberWeaveServer = ResolveUniversalMcpServer(source);

        // A null server has two causes that must not be conflated: the squad never declared the key,
        // or it declared it in a form the loader rejects. The roster names the tools either way, so
        // the first case is a squad whose mcp.json has been renamed or truncated, and every agent
        // silently loses the grant. The second is rejected at load time and is not reported here.
        McpDeclarationShape mcpShape = ReadMcpShape(source.McpConfiguration);
        bool universalToolsDeclared =
            CapabilityDegradations.UniversalMcpToolNames(source.Toolchain.RequiredMcpTools).Count > 0;

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
                kyberWeaveServer,
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
                capabilityVocabulary,
                kyberWeaveServer,
                mcpShape,
                universalToolsDeclared);
            if (permission is not null)
            {
                degradations.Add(permission);
            }

            // D12: Record unenforceable delegates-to roster as permission-not-expressible
            if (agent.DelegatesTo.Count > 0)
            {
                string roster = string.Join(", ", agent.DelegatesTo.Order(StringComparer.Ordinal));
                degradations.Add(new SquadDegradationRecord(
                    "antigravity",
                    agent.Name,
                    agent.Name,
                    "permission-not-expressible",
                    agent.BodyDigest,
                    $"Antigravity does not restrict delegates-to roster enforcement; this agent may invoke agents outside its declared roster [{roster}]."));
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
        McpServerDeclaration? kyberWeaveServer,
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
        // enable_subagent_tools, reasoning_effort, mainAgent (conductor only), mcpServers
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
        bool filesystemWriteAllow =
            profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision) &&
            writeDecision == SquadPermissionDecision.Allow;
        bool processExecuteAllow =
            profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision execDecision) &&
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

        // mcpServers: the grant vehicle this harness actually reads. Entitlement is the shared
        // rule, so the conductor is granted here like any other read-entitled profile (ADR 0049).
        // The name is deliberately absent from 'tools': Antigravity resolves that list against its
        // executor registry, and an MCP tool name is not a registry component. The server is also
        // not gated by 'enable_mcp_tools', so no key is emitted to switch it on.
        if (kyberWeaveServer is not null &&
            CapabilityDegradations.GrantsUniversalMcpServers(
                agent.CapabilityProfile,
                capabilityProfiles.Profiles))
        {
            frontmatter.Add("mcpServers", BuildMcpServersNode(kyberWeaveServer));
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

    /// <summary>
    ///     The shape of the raw MCP declaration: whether <c>mcpServers</c> is a well-formed object, and whether it
    ///     carries the <c>kyber-weave</c> key.
    /// </summary>
    /// <param name="IsObject">Whether <c>mcpServers</c> exists and is a JSON object.</param>
    /// <param name="DeclaresUniversalServer">Whether that object carries the <c>kyber-weave</c> key.</param>
    /// <remarks>
    ///     <see cref="ResolveUniversalMcpServer"/> answers null for a missing key, a missing <c>mcpServers</c>
    ///     object, and an unusable entry alike. Only the first is an operator-visible misconfiguration: a source
    ///     that declares no <c>mcpServers</c> object at all, or one whose entry the loader rejects, has nothing to
    ///     report to the agent, because the loader rejects those states before a real source reaches this renderer.
    /// </remarks>
    private readonly record struct McpDeclarationShape(bool IsObject, bool DeclaresUniversalServer);

    private static McpDeclarationShape ReadMcpShape(JsonElement mcpConfiguration)
    {
        if (mcpConfiguration.ValueKind != JsonValueKind.Object
            || !mcpConfiguration.TryGetProperty("mcpServers", out JsonElement servers)
            || servers.ValueKind != JsonValueKind.Object)
        {
            return new McpDeclarationShape(false, false);
        }

        return new McpDeclarationShape(
            true,
            servers.TryGetProperty(CapabilityDegradations.UniversalMcpServer, out _));
    }

    /// <summary>
    /// The launch definition the squad's own <c>mcp.json</c> declares for
    /// <see cref="UniversalMcpServer"/>, or <see langword="null"/> when it declares none.
    /// </summary>
    /// <remarks>
    /// The declaration is the source rather than a literal in this file, so the rendered command
    /// line is the one the operator's MCP configuration is written to launch. A missing, malformed,
    /// or renamed entry yields <see langword="null"/> — the fail-closed answer — because emitting a
    /// guessed command would point the agent at a server nothing is configured to start. The
    /// canonical source loader has already validated the shape (<c>command</c> a non-empty string,
    /// <c>args</c> a string array), so the checks here guard a hand-built
    /// <see cref="SquadSource"/> rather than the shipped JSON.
    /// </remarks>
    private static McpServerDeclaration? ResolveUniversalMcpServer(SquadSource source)
    {
        if (!source.McpConfiguration.TryGetProperty("mcpServers", out JsonElement servers) ||
            servers.ValueKind != JsonValueKind.Object ||
            !servers.TryGetProperty(UniversalMcpServer, out JsonElement server) ||
            server.ValueKind != JsonValueKind.Object ||
            !server.TryGetProperty("command", out JsonElement commandElement) ||
            !server.TryGetProperty("args", out JsonElement argsElement) ||
            argsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? command = commandElement.ValueKind == JsonValueKind.String
            ? commandElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        List<string> args = [];
        foreach (JsonElement arg in argsElement.EnumerateArray())
        {
            // Asked exactly the way the command branch above asks it: is this element the kind the
            // declaration requires? GetString alone would not answer that — it throws for any kind
            // but String or Null, so a numeric, boolean, or object element would raise during render
            // instead of withholding the grant, and the caller would see an exception where this
            // method promises a null. A malformed element is one more reason to reject the whole
            // declaration, which is the same answer every other branch gives.
            string? value = arg.ValueKind == JsonValueKind.String ? arg.GetString() : null;
            if (value is null)
            {
                return null;
            }

            args.Add(value);
        }

        return new McpServerDeclaration(UniversalMcpServer, command, args);
    }

    /// <summary>
    /// The <c>mcpServers</c> sequence an agent's frontmatter carries: one entry, in the shape the
    /// live probe called successfully.
    /// </summary>
    /// <remarks>
    /// Each entry is an object with <c>name</c>, <c>command</c>, and <c>args</c>. The alternative
    /// spelling — a sequence whose items each map the server name to its definition — listed and ran
    /// under <c>agy</c> 1.4.12 but was never actually invoked, so it is not emitted: an unverified
    /// shape is a guess, and a guessed grant is a fabricated one (ADR 0022's discipline). <c>args</c>
    /// is written as a flow sequence to keep it on one line beside the command it belongs to.
    /// </remarks>
    private static YamlSequenceNode BuildMcpServersNode(McpServerDeclaration declaration)
    {
        YamlSequenceNode args = new() { Style = SequenceStyle.Flow };
        foreach (string arg in declaration.Args)
        {
            args.Add(arg);
        }

        YamlMappingNode entry = new();
        entry.Add("name", declaration.Name);
        entry.Add("command", declaration.Command);
        entry.Add("args", args);

        return new YamlSequenceNode(entry);
    }

    /// <summary>
    /// A declared MCP server's launch definition, carried from the source to the frontmatter entry
    /// without either holding a second copy of the command line.
    /// </summary>
    private sealed record McpServerDeclaration(string Name, string Command, IReadOnlyList<string> Args);

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
        bool processExecuteAllow =
            profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision execDecision) &&
            execDecision == SquadPermissionDecision.Allow;
        bool filesystemWriteAllow =
            profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision writeDecision) &&
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

    private static string? ResolveReasoningEffort(string modelProfile)
    {
        return modelProfile switch
        {
            "architect" => "high",
            "deep-planning" => "high",
            "reviewer" => "high",
            "general" => "medium",
            "fast" => "low",
            "orchestration" => "minimal",
            _ => null
        };
    }

    private static SquadDeploymentFile RenderSkill(string name, string description, string instructionBody,
        SquadDeploymentScope scope)
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
        IReadOnlyList<string> capabilityVocabulary,
        McpServerDeclaration? kyberWeaveServer,
        McpDeclarationShape mcpShape,
        bool universalToolsDeclared)
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
        SquadPermissionDecision executeDecision =
            profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision execDecision)
                ? execDecision
                : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision =
            profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision declaredWrite)
                ? declaredWrite
                : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            "antigravity",
            agent.Name,
            agent.Name,
            agent.BodyDigest,
            executeDecision,
            writeDecision,
            ["run_command"],
            ["write_to_file", "replace_file_content", "multi_replace_file_content"]);

        if (notIsolable is not null)
        {
            string? missingServer = MissingServerSentence(mcpShape, universalToolsDeclared);
            if (missingServer is null)
            {
                return notIsolable;
            }

            // A shell-escape agent in a squad that dropped the server would otherwise lose the
            // grant with only the shell-escape record to show for it. The grant gap is a separate
            // condition from the shell gap, so it rides along in the same per-agent record rather
            // than being skipped by this early return.
            return notIsolable with
            {
                Details = JoinSentences(notIsolable.Details, missingServer),
            };
        }

        // If process.execute is not the gap, check for other capability constraints
        // that cannot be expressed in agent frontmatter.
        List<string> constrained = capabilityVocabulary
            .Where(capability =>
                profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                decision != SquadPermissionDecision.Deny)
            .ToList();

        List<string> details = [];
        if (constrained.Count > 0)
        {
            details.Add(
                $"Capability profile '{agent.CapabilityProfile}' constrains " +
                $"{string.Join(", ", constrained)} but Antigravity agent frontmatter cannot fully express " +
                "permission boundaries; the deployed agent's behaviour is governed by the capability " +
                "profile and the harness default interaction model.");
        }

        // The MCP sentence states the actual condition and only when it holds. A granted agent's
        // frontmatter carries the entry, so a withholding claim beside it would contradict the
        // render; an ungranted agent carries the sentence naming what it lost and the rule that lost
        // it, which is the only way the two outcomes are distinguishable in this stream.
        string? withheldMcp = DescribeWithheldMcp(
            agent.CapabilityProfile,
            capabilityProfiles,
            kyberWeaveServer,
            mcpShape,
            universalToolsDeclared);
        if (withheldMcp is not null)
        {
            details.Add(withheldMcp);
        }

        if (details.Count == 0)
        {
            return null;
        }

        return new SquadDegradationRecord(
            "antigravity",
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            string.Join(" ", details));
    }

    /// <summary>
    /// The sentence a degradation record carries when the <c>kyber-weave</c> grant did not reach the
    /// agent, or <see langword="null"/> when there is nothing withheld to report.
    /// </summary>
    /// <remarks>
    /// Three outcomes, not two. The squad may declare no such server — nothing was granted, so
    /// nothing was withheld and the record says nothing. The profile may satisfy the entitlement, in
    /// which case the frontmatter carries the entry and the sentence would be untrue. Only a profile
    /// outside the rule reaches the sentence, and it names the one server, the profile, and the
    /// capability the entitlement rests on, so a reader does not have to re-derive which of several
    /// conditions removed the grant.
    /// </remarks>
    /// <summary>
    ///     The sentence for a squad whose declaration is missing the server while the roster still
    ///     declares its tools, or <see langword="null"/> when there is nothing to report.
    /// </summary>
    /// <remarks>
    ///     Only a well-formed <c>mcpServers</c> object without the key is an operator-visible
    ///     misconfiguration. A source that declares no <c>mcpServers</c> object at all, or one whose
    ///     entry the loader rejects, has nothing to report: the loader rejects those states before a
    ///     real source reaches this renderer, so a record there would report a degradation the render
    ///     does not have.
    /// </remarks>
    private static string? MissingServerSentence(McpDeclarationShape mcpShape, bool universalToolsDeclared)
    {
        // The roster declares the tools, so the grant is intended; the squad's mcp.json simply has
        // no server under that name. Nothing was withheld — there was nothing to withhold — so this
        // is a configuration record, not a withholding claim, and it is the only signal an operator
        // gets for a renamed or dropped declaration.
        return universalToolsDeclared && mcpShape.IsObject && !mcpShape.DeclaresUniversalServer
            ? "The toolchain roster declares the kyber-weave MCP tools, but the squad's mcp.json " +
              "declares no 'kyber-weave' server, so no per-agent grant could be emitted. Add the " +
              "server back under that exact name."
            : null;
    }

    /// <summary>
    /// Joins two single-line detail sentences, tolerating a missing first half.</summary>
    private static string JoinSentences(string? first, string second)
    {
        return string.IsNullOrWhiteSpace(first) ? second : first + " " + second;
    }

    private static string? DescribeWithheldMcp(
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        McpServerDeclaration? kyberWeaveServer,
        McpDeclarationShape mcpShape,
        bool universalToolsDeclared)
    {
        if (kyberWeaveServer is null)
        {
            string? missingServer = MissingServerSentence(mcpShape, universalToolsDeclared);
            if (missingServer is null)
            {
                return null;
            }

            // A missing declaration and a denying profile are two independent reasons the agent has
            // no grant. Reporting only the first would hide the second: an operator who restores the
            // server would watch this agent stay grantless with no record saying why.
            if (!CapabilityDegradations.GrantsUniversalMcpServers(profileName, capabilityProfiles))
            {
                return missingServer + " Capability profile '" + profileName + "' also does not allow '" +
                    CapabilityDegradations.FileReadCapability + "', so this agent would not receive the " +
                    "grant even with the server declared.";
            }

            return missingServer;
        }

        if (CapabilityDegradations.GrantsUniversalMcpServers(profileName, capabilityProfiles))
        {
            return null;
        }

        return $"Declared MCP server '{kyberWeaveServer.Name}' is withheld from this agent: capability " +
            $"profile '{profileName}' does not allow '{CapabilityDegradations.FileReadCapability}', " +
            "which is the entitlement the kyber-weave grant rests on.";
    }

    /// <summary>
    /// YAML frontmatter scalars must stay single-line; a folded canonical description
    /// would otherwise emit a block scalar and break consumers that expect a plain string.
    /// </summary>
    private static string ToSingleLineScalar(string value)
    {
        return string.Join(
            ' ',
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
