using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
///     Renders canonical Squad source into Antigravity workspace agents and skills via native
///     per-agent emission (Antigravity has a native agent primitive since version 1.2.7).
/// </summary>
/// <remarks>
///     <para>
///         Antigravity is a native target that emits both per-agent directories and canonical
///         skill files under separate namespace roots: agents at <c>.agents/agents/{name}/agent.md</c>
///         and skills at <c>.agents/skills/{name}/SKILL.md</c>. The "Native Both" pattern applies:
///         collisions (agent identity matching a canonical skill) render to both paths with no
///         <c>role-</c> prefix required because the roots are separate. All agents emit native
///         agent.md files, and canonical skills emit SKILL.md files.
///     </para>
///     <para>
///         Agent frontmatter includes validated keys from the capability profile and model tier:
///         <c>name</c>, <c>description</c>, <c>model</c>, <c>enable_write_tools</c>,
///         <c>enable_subagent_tools</c>, <c>reasoning_effort</c>,
///         <c>tools</c> (narrowed by the capability profile's XOR decision on filesystem.write vs.
///         process.execute; see <see cref="CapabilityDegradations" />), and <c>mainAgent: true</c>
///         only for the conductor. Granted shell capabilities do not fully isolate write access
///         via redirection; this residual reachability is recorded as a <c>capability-not-isolable</c>
///         degradation via <see cref="CapabilityDegradations.BuildCapabilityNotIsolable" />. Non-empty
///         <c>delegates-to</c> rosters produce <c>invoke_subagent</c> + <c>enable_subagent_tools: true</c>,
///         plus <c>manage_subagents</c> for orchestrator/conductor; the unenforceable roster is
///         recorded as <c>permission-not-expressible</c>. Declared skills carry no frontmatter capability
///         keys, only identity and license.
///     </para>
///     <para>
///         <b>Arbiter block (Req 6.3, 8.1, 8.2, 22.2).</b> When the render request carries an
///         enabled <see cref="SquadArbiterWiring"/>, the renderer returns an
///         <c>antigravity</c> <see cref="SquadRenderedBlock"/> for <c>.agents/hooks.json</c> under
///         <see cref="SquadDeploymentScope.Project"/> and for <c>hooks.json</c> under
///         <see cref="SquadDeploymentScope.Global"/>, where Antigravity reads it from directly
///         beneath its global root (<c>~/.gemini/config</c>) and never from <c>.agents/</c> there.
///         Antigravity's top-level hook-file keys are group names, so Squad owns the whole
///         top-level group <c>kyber-arbiter</c> — an unknown key there would be read as another
///         group — holding <c>PreToolUse</c> and <c>PostToolUse</c> matcher groups matching
///         <c>^invoke_subagent$</c> with one command hook and the wiring timeout ([F10], D25).
///         The command carries no <c>--caller</c>: a shared hook file gates project-wide, not
///         per agent. Antigravity documents no post-dispatch result, so the render also records
///         <c>arbiter-not-enforced</c> with details <c>no-post-dispatch-feedback</c>, one record
///         per rendered agent (design §10.5 records per target and agent): post-dispatch outcomes
///         are logged and reported by <c>audit</c>, but not delivered back to the harness. A null
///         or disabled wiring renders no block and no arbiter degradation, leaving the owned files
///         byte-identical.
///     </para>
///     <para>
///         <b>MCP reach is declared per agent, in <c>mcpServers</c>, and nowhere else.</b> The
///         <c>kw-docs</c> server is granted to every agent whose capability profile resolves and allows
///         <c>filesystem.read</c> — <c>conductor</c> included, because the server carries
///         <c>docs_get_coding_standard_for_technology</c> and the role that assigns work is the role
///         deciding which standards apply to whom (ADR 0050). The grant goes through
///         <see cref="CapabilityDegradations.GrantsUniversalMcpServers" />, the one shared predicate, rather
///         than a renderer-local copy of the rule. The pure-orchestrator carve-out for <c>codegraph</c>
///         is expressed through <see cref="CapabilityDegradations.GrantsOrchestratorExcludedMcpServers" />,
///         emitting <c>codegraph</c> alongside <c>kw-docs</c> for read-entitled specialists while
///         withholding it from <c>conductor</c>.
///     </para>
///     <para>
///         The emitted entry shape was live-probed against the installed <c>agy</c> 1.4.12 rather than read
///         off vendor documentation, following the discipline ADR 0022 established for every Antigravity
///         capability: <c>mcpServers</c> is an array of objects, each entry
///         <c>{name, command, args}</c>, and that entry — not the single-key map spelling
///         <c>- kw-docs: {command, args}</c>, which listed and ran but was never called — is the one the
///         probe invoked successfully. <c>args</c> carries <c>--repo-root .</c>, workspace-relative, because
///         an absolute path can be overridden by a same-named global server definition and because
///         workspace-relative is what the rest of Squad emits.
///     </para>
///     <para>
///         Three probe findings bound this emission. The <c>tools</c> allow-list does <b>not</b> gate MCP
///         availability — an agent whose tools list held only <c>view_file</c> still called an MCP tool — so
///         no MCP tool name is added there, and none may ever be: Antigravity validates <c>tools</c> entries
///         against its executor registry and fails fast on an unmapped name
///         (<c>unknown component: tool "..." not found in registry</c>, surfaced on stderr with exit code
///         0, so the failure is silent to a caller reading only the exit code). <c>enable_mcp_tools: false</c>
///         does not block a server declared in frontmatter either, so no such key is emitted to enable one.
///         Unknown frontmatter keys are ignored, which is why the entry has to be exactly the probed shape
///         rather than merely a plausible one.
///     </para>
/// </remarks>
public sealed class AntigravityRenderer : SquadHarnessRenderer
{
    private const string ArbiterServer = McpServerNames.Arbiter;
    private const string ArbiterMatcher = "^invoke_subagent$";
    private const string ArbiterNotEnforcedCode = "arbiter-not-enforced";
    private const string NoPostDispatchFeedbackReason = "no-post-dispatch-feedback";

    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.Antigravity,
        "Antigravity",
        ".agents",
        "agents",
        SquadModelPolicy.NotRendered,
        SquadLineBreaks.LineFeedOnly);

    public AntigravityRenderer()
        : base(HarnessSpec)
    {
    }

    /// <summary>
    /// Skills, then agents, then degradations, then resources: Antigravity's established output
    /// order. The Native Both pattern means agents and skills occupy separate namespaces, allowing
    /// both to render without collision.
    /// </summary>
    private protected override SquadRenderResult Render(SquadHarnessRenderContext context)
    {
        McpPlan mcp = context.GetOrCreateState(McpPlan.Create);

        // Emit canonical skills first, preserving the established ordering.
        foreach (SquadSkill skill in context.Source.Skills)
        {
            SquadHarnessRenderContext.ValidateCanonicalName(skill.Name);
            context.Files.Add(context.RenderSkillDocument(skill.Name, skill.Description, skill.InstructionBody));
        }

        foreach (SquadAgent agent in context.Source.Agents)
        {
            RenderAgent(context, agent);
        }

        // Degrade agents only for capability constraints that cannot be expressed in
        // frontmatter (D10: shell-implies-write, D12-delegate: unenforceable roster).
        foreach (SquadAgent agent in context.Source.Agents)
        {
            context.AddDegradation(BuildPermissionDegradation(
                agent,
                context.CapabilityProfiles,
                context.CapabilityVocabulary,
                mcp.DeclaredServers,
                mcp.Shape,
                mcp.UniversalToolsDeclared));

            // D12: Record unenforceable delegates-to roster as permission-not-expressible
            if (agent.DelegatesTo.Count > 0)
            {
                string roster = string.Join(", ", agent.DelegatesTo.Order(StringComparer.Ordinal));
                context.Degradations.Add(context.Degradation(
                    agent,
                    "permission-not-expressible",
                    $"Antigravity does not restrict delegates-to roster enforcement; this agent may invoke agents outside its declared roster [{roster}]."));
            }
        }

        // Project agent resources beside their native agent.md so relative links in the
        // unchanged instruction body resolve, and so a same-named canonical skill's
        // resources cannot collide with them. Skill resources project beneath their own identities.
        foreach (SquadAgent agent in context.Source.Agents)
        {
            SquadResourceProjection.Append(
                context.Files,
                $"{context.AgentsDirectory}/{agent.Name}/agent.md",
                agent.Resources,
                context.Token);
        }

        foreach (SquadSkill skill in context.Source.Skills)
        {
            SquadResourceProjection.Append(
                context.Files,
                $"{context.SkillsDirectory}/{skill.Name}/SKILL.md",
                skill.Resources,
                context.Token);
        }

        RenderArbiter(context);
        return context.ToResult();
    }

    /// <summary>
    /// §10.5: the hook gates a dispatch pre-tool only — Antigravity documents no post-dispatch
    /// result, so outcomes are logged and reported by audit but never delivered back. One record
    /// per rendered agent, recorded beside the block and never without it.
    /// </summary>
    private protected override void RenderArbiter(SquadHarnessRenderContext context)
    {
        if (context.EnabledArbiter is null)
        {
            return;
        }

        context.AddArbiterMatcherGroupBlock(SquadHookBlockFormat.Antigravity, ArbiterMatcher);
        foreach (SquadAgent agent in context.Source.Agents)
        {
            context.Degradations.Add(BuildArbiterDegradation(agent));
        }
    }

    /// <summary>
    /// Records <c>arbiter-not-enforced</c> for one rendered agent with §10.5's
    /// <c>no-post-dispatch-feedback</c> reason: the hook gates a dispatch before it runs,
    /// but post-dispatch outcomes are only logged and reported by <c>audit</c>, never
    /// delivered back. §10.5 records per target and agent, which is also what the
    /// registry's degradation validation requires — every renderer-emitted record names a
    /// rendered agent and carries its instruction digest.
    /// </summary>
    private static SquadDegradationRecord BuildArbiterDegradation(SquadAgent agent) => new(
        Target: "antigravity",
        CanonicalIdentity: agent.Name,
        OutputIdentity: agent.Name,
        Code: ArbiterNotEnforcedCode,
        InstructionDigest: agent.BodyDigest,
        Details: NoPostDispatchFeedbackReason);

    private protected override void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent)
    {
        SquadHarnessRenderContext.ValidateCanonicalName(agent.Name);
        SquadCapabilityProfiles capabilityProfiles = context.Source.CapabilityProfiles;
        SquadModelProfiles modelProfiles = context.Source.ModelProfiles;
        IReadOnlyList<McpServerDeclaration> declaredMcpServers = context.GetOrCreateState(McpPlan.Create).DeclaredServers;

        if (!capabilityProfiles.Profiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
            throw new SquadRenderValidationException(
                $"Agent '{agent.Name}' references undeclared capability profile '{agent.CapabilityProfile}'.");

        // Resolve model tier from the profiles/models.yml antigravity column
        string modelTier = "inherit";
        if (modelProfiles.Profiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? modelProfile))
            if (modelProfile.HarnessModels.TryGetValue("antigravity", out string? antigravityModel))
                modelTier = antigravityModel;

        // Key order: name, description, model, tools, enable_write_tools,
        // enable_subagent_tools, reasoning_effort, mainAgent (conductor only), mcpServers
        YamlMappingNode frontmatter = new();
        frontmatter.Add("name", agent.Name);
        frontmatter.Add("description", context.SingleLine(agent.Description));
        frontmatter.Add("model", modelTier);

        // Build tools list from capability profile with XOR narrowing
        string[] tools = ResolveToolsForAgent(agent, profile);
        if (tools.Length > 0)
        {
            YamlSequenceNode toolsSeq = new();
            foreach (string tool in tools) toolsSeq.Add(tool);

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
        if (agent.Name == "conductor") frontmatter.Add("mainAgent", "true");

        // reasoning_effort: resolved from the approved per-profile mapping (D9)
        string? reasoningEffort = ResolveReasoningEffort(agent.ModelProfile);
        if (!string.IsNullOrEmpty(reasoningEffort)) frontmatter.Add("reasoning_effort", reasoningEffort);

        // mcpServers: the grant vehicle this harness actually reads. Universal servers (kw-docs)
        // are granted to any read-entitled profile (conductor included, per ADR 0050). Every other
        // declared server is orchestrator-excluded: granted to read-entitled specialists and withheld
        // from the pure orchestrator. The declaration list is ordinal, so the emitted order is too.
        bool grantsUniversal = CapabilityDegradations.GrantsUniversalMcpServers(
            agent.CapabilityProfile,
            capabilityProfiles.Profiles);
        bool grantsOrchestratorExcluded = CapabilityDegradations.GrantsOrchestratorExcludedMcpServers(
            agent.CapabilityProfile,
            capabilityProfiles.Profiles);

        List<McpServerDeclaration> mcpServers = [];
        foreach (McpServerDeclaration server in declaredMcpServers)
        {
            bool granted = string.Equals(
                server.Name,
                CapabilityDegradations.UniversalMcpServer,
                StringComparison.Ordinal)
                ? grantsUniversal
                : grantsOrchestratorExcluded;

            if (granted) mcpServers.Add(server);
        }

        if (mcpServers.Count > 0) frontmatter.Add("mcpServers", BuildMcpServersNode(mcpServers));

        context.Files.Add(context.CreateFile(
            $"{context.AgentsDirectory}/{agent.Name}/agent.md",
            SquadMarkdownDocument.Compose(SquadYamlEmitter.Default.Serialize(frontmatter), agent.InstructionBody)));
    }

    private static McpDeclarationShape ReadMcpShape(JsonElement mcpConfiguration)
    {
        if (mcpConfiguration.ValueKind != JsonValueKind.Object
            || !mcpConfiguration.TryGetProperty("mcpServers", out JsonElement servers)
            || servers.ValueKind != JsonValueKind.Object)
            return new McpDeclarationShape(false, false);

        return new McpDeclarationShape(
            true,
            servers.TryGetProperty(CapabilityDegradations.UniversalMcpServer, out _));
    }

    /// <summary>
    ///     The launch definition the squad's own <c>mcp.json</c> declares for
    ///     <paramref name="serverName" />, or <see langword="null" /> when it declares none.
    /// </summary>
    /// <remarks>
    ///     The declaration is the source rather than a literal in this file, so the rendered command
    ///     line is the one the operator's MCP configuration is written to launch. A missing, malformed,
    ///     or renamed entry yields <see langword="null" /> — the fail-closed answer — because emitting a
    ///     guessed command would point the agent at a server nothing is configured to start. The
    ///     canonical source loader has already validated the shape (<c>command</c> a non-empty string,
    ///     <c>args</c> a string array), so the checks here guard a hand-built
    ///     <see cref="SquadSource" /> rather than the shipped JSON.
    /// </remarks>
    private static McpServerDeclaration? ResolveMcpServer(SquadSource source, string serverName)
    {
        if (!source.McpConfiguration.TryGetProperty("mcpServers", out JsonElement servers) ||
            servers.ValueKind != JsonValueKind.Object ||
            !servers.TryGetProperty(serverName, out JsonElement server) ||
            server.ValueKind != JsonValueKind.Object ||
            !server.TryGetProperty("command", out JsonElement commandElement) ||
            !server.TryGetProperty("args", out JsonElement argsElement) ||
            argsElement.ValueKind != JsonValueKind.Array)
            return null;

        string? command = commandElement.ValueKind == JsonValueKind.String
            ? commandElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(command)) return null;

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
            if (value is null) return null;

            args.Add(value);
        }

        return new McpServerDeclaration(serverName, command, args);
    }

    /// <summary>
    ///     The launch definitions the squad's own <c>mcp.json</c> declares, in ordinal order, minus
    ///     the Arbiter hook server, which is not an agent-facing MCP server.
    /// </summary>
    private static IReadOnlyList<McpServerDeclaration> ResolveDeclaredMcpServers(SquadSource source)
    {
        if (!source.McpConfiguration.TryGetProperty("mcpServers", out JsonElement servers) ||
            servers.ValueKind != JsonValueKind.Object)
            return [];

        List<McpServerDeclaration> declarations = [];
        foreach (string name in servers.EnumerateObject()
                     .Select(server => server.Name)
                     .Where(name => !string.Equals(name, ArbiterServer, StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            McpServerDeclaration? declaration = ResolveMcpServer(source, name);
            if (declaration is not null) declarations.Add(declaration);
        }

        return declarations;
    }

    /// <summary>
    ///     The <c>mcpServers</c> sequence an agent's frontmatter carries: in the shape the
    ///     live probe called successfully.
    /// </summary>
    /// <remarks>
    ///     Each entry is an object with <c>name</c>, <c>command</c>, and <c>args</c>. The alternative
    ///     spelling — a sequence whose items each map the server name to its definition — listed and ran
    ///     under <c>agy</c> 1.4.12 but was never actually invoked, so it is not emitted: an unverified
    ///     shape is a guess, and a guessed grant is a fabricated one (ADR 0022's discipline). <c>args</c>
    ///     is written as a flow sequence to keep it on one line beside the command it belongs to.
    /// </remarks>
    private static YamlSequenceNode BuildMcpServersNode(IEnumerable<McpServerDeclaration> declarations)
    {
        YamlSequenceNode sequence = new();
        foreach (McpServerDeclaration declaration in declarations)
        {
            YamlSequenceNode args = new() { Style = SequenceStyle.Flow };
            foreach (string arg in declaration.Args) args.Add(arg);

            YamlMappingNode entry = new();
            entry.Add("name", declaration.Name);
            entry.Add("command", declaration.Command);
            entry.Add("args", args);

            sequence.Add(entry);
        }

        return sequence;
    }

    private static string[] ResolveToolsForAgent(
        SquadAgent agent,
        SquadCapabilityProfile profile)
    {
        List<string> tools = [];

        // filesystem.read
        if (profile.Permissions.TryGetValue("filesystem.read", out SquadPermissionDecision readDecision) &&
            readDecision != SquadPermissionDecision.Deny)
            tools.Add("view_file");

        // filesystem.search: D12 withholds grep_search pending live verification
        if (profile.Permissions.TryGetValue("filesystem.search", out SquadPermissionDecision searchDecision) &&
            searchDecision != SquadPermissionDecision.Deny)
            tools.Add("list_dir");

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
            if (agent.Name == "orchestrator" || agent.Name == "conductor") tools.Add("manage_subagents");
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

    private static SquadDegradationRecord? BuildPermissionDegradation(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary,
        IReadOnlyList<McpServerDeclaration> declaredMcpServers,
        McpDeclarationShape mcpShape,
        bool universalToolsDeclared)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
            throw new SquadRenderValidationException(
                $"Agent '{agent.Name}' references capability profile '{agent.CapabilityProfile}', " +
                "which profiles/capabilities.yml does not declare. " +
                $"Declared profiles: {string.Join(", ", capabilityProfiles.Keys.Order(StringComparer.Ordinal))}. " +
                "Correct the agent's capability-profile value before rendering.");

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
            if (missingServer is not null)
                // A shell-escape agent in a squad that dropped the server would otherwise lose the
                // grant with only the shell-escape record to show for it. The grant gap is a separate
                // condition from the shell gap, so it rides along in the same per-agent record rather
                // than being skipped by this early return. The entitlement check rides along too: a
                // profile that also denies filesystem.read stays grantless after the server comes
                // back, and only this record can say so.
                return notIsolable with
                {
                    Details = JoinSentences(
                        notIsolable.Details,
                        WithEntitlementGap(missingServer, agent.CapabilityProfile, capabilityProfiles))
                };

            string? withheldMcp = DescribeWithheldMcp(
                agent.CapabilityProfile,
                capabilityProfiles,
                declaredMcpServers,
                mcpShape,
                universalToolsDeclared);

            return withheldMcp is null
                ? notIsolable
                : notIsolable with { Details = JoinSentences(notIsolable.Details, withheldMcp) };
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
            details.Add(
                $"Capability profile '{agent.CapabilityProfile}' constrains " +
                $"{string.Join(", ", constrained)} but Antigravity agent frontmatter cannot fully express " +
                "permission boundaries; the deployed agent's behaviour is governed by the capability " +
                "profile and the harness default interaction model.");

        // The MCP sentence states the actual condition and only when it holds. A granted agent's
        // frontmatter carries the entry, so a withholding claim beside it would contradict the
        // render; an ungranted agent carries the sentence naming what it lost and the rule that lost
        // it, which is the only way the two outcomes are distinguishable in this stream.
        string? withheld = DescribeWithheldMcp(
            agent.CapabilityProfile,
            capabilityProfiles,
            declaredMcpServers,
            mcpShape,
            universalToolsDeclared);
        if (withheld is not null) details.Add(withheld);

        if (details.Count == 0) return null;

        return new SquadDegradationRecord(
            "antigravity",
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            string.Join(" ", details));
    }

    /// <summary>
    ///     The sentence a degradation record carries when an MCP server grant did not reach the
    ///     agent, or <see langword="null" /> when there is nothing withheld to report.
    /// </summary>
    /// <remarks>
    ///     Three outcomes, not two. The squad may declare no such server — nothing was granted, so
    ///     nothing was withheld and the record says nothing. The profile may satisfy the entitlement, in
    ///     which case the frontmatter carries the entry and the sentence would be untrue. Only a profile
    ///     outside the rule reaches the sentence, and it names the server(s), the profile, and the
    ///     capability the entitlement rests on, so a reader does not have to re-derive which of several
    ///     conditions removed the grant.
    /// </remarks>
    private static string? DescribeWithheldMcp(
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<McpServerDeclaration> declaredMcpServers,
        McpDeclarationShape mcpShape,
        bool universalToolsDeclared)
    {
        bool declaresUniversalServer = declaredMcpServers.Any(server => string.Equals(
            server.Name,
            CapabilityDegradations.UniversalMcpServer,
            StringComparison.Ordinal));

        if (!declaresUniversalServer)
        {
            string? missingServer = MissingServerSentence(mcpShape, universalToolsDeclared);
            if (missingServer is not null)
                // A missing declaration and a denying profile are two independent reasons the agent has
                // no grant. Reporting only the first would hide the second: an operator who restores the
                // server would watch this agent stay grantless with no record saying why.
                return WithEntitlementGap(missingServer, profileName, capabilityProfiles);

            // No usable declaration at all. Entitled profiles stay silent here — the loader rejects
            // these states before a real source reaches this renderer — but a denying profile is still
            // worth naming, since the grant would be withheld even with a declaration.
            return EntitlementGapSentence(profileName, capabilityProfiles);
        }

        IReadOnlyList<string> declaredServerNames = [.. declaredMcpServers.Select(server => server.Name)];

        IReadOnlyList<string> withheldServerNames = CapabilityDegradations.WithheldMcpServerNames(
            profileName,
            capabilityProfiles,
            declaredServerNames);

        if (withheldServerNames.Count == 0)
        {
            return null;
        }

        return CapabilityDegradations.DescribeWithheldMcp(
            profileName,
            withheldServerNames,
            capabilityProfiles);
    }

    /// <summary>
    ///     The sentence for a squad whose declaration is missing the server while the roster still
    ///     declares its tools, or <see langword="null" /> when there is nothing to report.
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
            ? "The toolchain roster declares the kw-docs MCP tools, but the squad's mcp.json " +
              "declares no 'kw-docs' server, so no per-agent grant could be emitted. Add the " +
              "server back under that exact name."
            : null;
    }

    /// <summary>
    ///     Appends the denied-entitlement clause to a missing-server sentence when the profile would
    ///     stay grantless even with the server declared, and returns the sentence unchanged otherwise.
    /// </summary>
    /// <remarks>
    ///     Both call sites — the D10 early return and the null-server branch — go through this one
    ///     helper so a triple-gap agent is reported the same way on either path.
    /// </remarks>
    private static string WithEntitlementGap(
        string missingServer,
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (CapabilityDegradations.GrantsUniversalMcpServers(profileName, capabilityProfiles)) return missingServer;

        return missingServer + " Capability profile '" + profileName + "' also does not allow '" +
               CapabilityDegradations.FileReadCapability + "', so this agent would not receive the " +
               "grant even with the server declared.";
    }

    /// <summary>
    ///     The entitlement-only sentence for a squad whose declaration cannot be resolved at all:
    ///     no <c>mcpServers</c> object, or an entry the loader rejects. Names only the denying profile,
    ///     never a server, so the pinned silence for malformed declarations stands for entitled profiles.
    /// </summary>
    private static string? EntitlementGapSentence(
        string profileName,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        return CapabilityDegradations.GrantsUniversalMcpServers(profileName, capabilityProfiles)
            ? null
            : $"Capability profile '{profileName}' does not allow " +
              $"'{CapabilityDegradations.FileReadCapability}', so no per-agent grant could be emitted " +
              "for this agent.";
    }

    /// <summary>
    ///     Joins two single-line detail sentences, tolerating a missing first half.
    /// </summary>
    private static string JoinSentences(string? first, string second)
    {
        return string.IsNullOrWhiteSpace(first) ? second : first + " " + second;
    }

    /// <summary>
    ///     The shape of the raw MCP declaration: whether <c>mcpServers</c> is a well-formed object, and whether it
    ///     carries the <c>kw-docs</c> key.
    /// </summary>
    /// <param name="IsObject">Whether <c>mcpServers</c> exists and is a JSON object.</param>
    /// <param name="DeclaresUniversalServer">Whether that object carries the <c>kw-docs</c> key.</param>
    /// <remarks>
    ///     <see cref="ResolveMcpServer" /> answers null for a missing key, a missing <c>mcpServers</c>
    ///     object, and an unusable entry alike. Only the first is an operator-visible misconfiguration: a source
    ///     that declares no <c>mcpServers</c> object at all, or one whose entry the loader rejects, has nothing to
    ///     report to the agent, because the loader rejects those states before a real source reaches this renderer.
    /// </remarks>
    private readonly record struct McpDeclarationShape(bool IsObject, bool DeclaresUniversalServer);

    /// <summary>
    ///     A declared MCP server's launch definition, carried from the source to the frontmatter entry
    ///     without either holding a second copy of the command line.
    /// </summary>
    private sealed record McpServerDeclaration(string Name, string Command, IReadOnlyList<string> Args);

    /// <summary>What a render reads once from the squad's MCP declaration.</summary>
    /// <param name="DeclaredServers">
    /// The launch definitions from the squad's own declaration, so no renderer holds a second copy
    /// of a command line that could drift from mcp.json. Ordinal, and excluding the Arbiter hook
    /// server, which is not an agent-facing MCP server.
    /// </param>
    /// <param name="Shape">
    /// Distinguishes a squad that never declared the key from one that declared it in a form the
    /// loader rejects: the roster names the tools either way, so the first is a squad whose mcp.json
    /// has been renamed or truncated, and every agent silently loses the grant.
    /// </param>
    /// <param name="UniversalToolsDeclared">Whether the toolchain roster names any universal MCP tool.</param>
    private sealed record McpPlan(
        IReadOnlyList<McpServerDeclaration> DeclaredServers,
        McpDeclarationShape Shape,
        bool UniversalToolsDeclared)
    {
        public static McpPlan Create(SquadHarnessRenderContext context) => new(
            ResolveDeclaredMcpServers(context.Source),
            ReadMcpShape(context.Source.McpConfiguration),
            CapabilityDegradations.UniversalMcpToolNames(context.Source.Toolchain.RequiredMcpTools).Count > 0);
    }
}
