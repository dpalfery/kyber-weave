using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into GitHub Copilot's native custom-agent and
/// agent-skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Contract verified against GitHub's own documentation on 2026-08-16: agents are
/// <c>.github/agents/&lt;name&gt;.agent.md</c> (the double extension is load-bearing —
/// <c>&lt;name&gt;.md</c> is not recognized), frontmatter requires only <c>description</c>,
/// and the body is capped at 30,000 characters. Skills are
/// <c>.github/skills/&lt;name&gt;/SKILL.md</c> requiring <c>name</c> and <c>description</c>.
/// </para>
/// <para>
/// Copilot's <c>tools</c> frontmatter key is a closed allow-list drawn from a documented
/// built-in vocabulary (<c>vscode</c>, <c>execute</c>, <c>read</c>, <c>edit</c>, <c>search</c>,
/// <c>agent</c>, <c>web</c>, <c>todo</c>) and single-quoted MCP server wildcards
/// (<c>'codegraph/*'</c>, <c>'kyber-weave/*'</c>, <c>'context7/*'</c>).
/// Tools are rendered as an inline YAML flow sequence (<c>tools: [vscode, ...]</c>).
/// Naming any tool withholds every tool not named, MCP server tools included,
/// which is what makes a <c>deny</c> in the capability profile enforced rather than merely
/// declared. Omitting the key means "all available tools", so an unset <c>tools</c> is a
/// silent grant of everything — precisely the permission widening
/// <see cref="SquadRendererRegistry"/> exists to catch.
/// </para>
/// <para>
/// The lattice is three-state and the allow-list is binary, so only <c>allow</c> grants a
/// tool. <c>ask</c> narrows to <c>deny</c> — Copilot's frontmatter has no per-tool
/// confirmation gate — and every narrowing is recorded as a
/// <see cref="SquadDegradationRecord"/> with code <c>safety-narrowed</c>.
/// <c>network.publish</c> has no built-in tool at all: it is reachable only through MCP
/// servers, which the closed allow-list already withholds.
/// </para>
/// </remarks>
public sealed class CopilotRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".github/agents";
    private const string SkillsDirectory = ".github/skills";
    private const int MaxAgentBodyCharacters = 30_000;

    private static readonly string[] SharedConductorIdentities = ["conductor", "conductor-v3"];

    private static readonly string[] BaseUngovernedTools = ["vscode", "todo"];

    private static readonly string[] StandardMcpTools = ["'codegraph/*'", "'kyber-weave/*'", "'context7/*'"];

    /// <summary>
    /// Lowers the semantic capability vocabulary onto Copilot's built-in tool names,
    /// verified against GitHub's custom-agent configuration reference on 2026-08-16.
    /// <c>network.publish</c> is absent deliberately: no built-in tool publishes, so it is
    /// reachable only via MCP servers that a closed allow-list already withholds.
    /// </summary>
    private static readonly (string Capability, string[] Tools)[] CapabilityTools =
    [
        ("process.execute", ["execute"]),
        ("filesystem.read", ["read"]),
        ("filesystem.search", ["search"]),
        ("filesystem.write", ["edit"]),
        ("delegate", ["agent"]),
        ("network.read", ["web"]),
    ];

    /// <summary>
    /// Emission order, fixed to Copilot's documented convention and deterministic ordering
    /// so a rendered agent file is byte-stable regardless of how the profile's permissions enumerate.
    /// </summary>
    private static readonly string[] ToolOrder =
    [
        "vscode",
        "execute",
        "read",
        "'codegraph/*'",
        "'kyber-weave/*'",
        "'context7/*'",
        "edit",
        "search",
        "agent",
        "web",
        "todo"
    ];

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithTypeConverter(new CopilotToolsFlowSequenceConverter())
        .WithTypeConverter(new CopilotAgentsFlowSequenceConverter())
        .Build();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Copilot];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Copilot))
        {
            throw new ArgumentException(
                "CopilotRenderer was asked to render a target other than Copilot.",
                nameof(request));
        }

        if (request.Targets.Count == 0)
        {
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }

        SquadSource source = SquadSourceLoader.Load(request.SourceDirectory);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];
        List<SquadRenderWarning> warnings = [];

        foreach (SquadAgent agent in source.Agents)
        {
            files.Add(RenderAgent(
                agent,
                source.ModelProfiles.Profiles,
                source.CapabilityProfiles.Profiles,
                warnings));

            SquadDegradationRecord? degradation = BuildPermissionDegradation(agent, source.CapabilityProfiles.Profiles);
            if (degradation is not null)
            {
                degradations.Add(degradation);
            }
        }

        foreach (SquadSkill skill in source.Skills)
        {
            // Conductor and conductor-v3 are native primary agents on Copilot; suppressing
            // their skill projection here is the single-projection rule
            // SquadRendererRegistry enforces on every native target.
            if (SharedConductorIdentities.Contains(skill.Name, StringComparer.Ordinal))
            {
                continue;
            }

            files.Add(RenderSkill(skill));
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, warnings, []));
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        List<SquadRenderWarning> warnings)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = agent.Description
        };

        string? model = ResolveCopilotModel(agent, modelProfiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        frontmatter["tools"] = new CopilotToolsFlowSequence(ResolveTools(agent, capabilityProfiles));

        if (agent.Invocation == SquadInvocation.Subagent)
        {
            // A subagent that delegates needs its permitted roster named in frontmatter:
            // the "agent" tool grants the mechanism, "agents" names who it may reach. A
            // primary agent is dispatched from the top-level session and receives the
            // full roster from the harness, so declaring one there would only narrow it.
            if (agent.DelegatesTo.Count > 0)
            {
                frontmatter["agents"] = new CopilotAgentsFlowSequence(agent.DelegatesTo);
            }

            // Subagents are dispatched by the conductor, not chosen directly by a human —
            // "user-invocable: false" is Copilot's closest equivalent. Primary agents
            // (conductor, conductor-v3) leave this at its default (true, omitted).
            frontmatter["user-invocable"] = false;
        }

        // metadata values are kept as flat strings rather than nested arrays: GitHub's
        // docs describe metadata only as "key-value pairs for annotation" without
        // specifying accepted value types, and a flat string is the safe reading until
        // that is verified against a live agent.
        Dictionary<string, object?> metadata = new(StringComparer.Ordinal)
        {
            ["capability-profile"] = agent.CapabilityProfile,
            ["fallback"] = agent.Fallback
        };
        if (agent.DelegatesTo.Count > 0)
        {
            metadata["delegates-to"] = string.Join(", ", agent.DelegatesTo);
        }

        if (agent.Aliases.Count > 0)
        {
            metadata["aliases"] = string.Join(", ", agent.Aliases);
        }

        frontmatter["metadata"] = metadata;

        string yaml = YamlSerializer.Serialize(frontmatter);
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append("---\n");

        string normalizedBody = agent.InstructionBody.Replace("\r\n", "\n");
        builder.Append(normalizedBody);
        if (!normalizedBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        string content = builder.ToString();
        if (content.Length > MaxAgentBodyCharacters)
        {
            warnings.Add(new SquadRenderWarning(
                "agent-body-too-long",
                $"Agent '{agent.Name}' is {content.Length} characters; Copilot caps custom agent files at {MaxAgentBodyCharacters}.",
                "copilot"));
        }

        return new SquadDeploymentFile(
            $"{AgentsDirectory}/{agent.Name}.agent.md",
            Encoding.UTF8.GetBytes(content),
            "copilot");
    }

    private static SquadDeploymentFile RenderSkill(SquadSkill skill)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = skill.Name,
            ["description"] = skill.Description,
            ["license"] = "MIT"
        };

        string yaml = YamlSerializer.Serialize(frontmatter);
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append("---\n");

        string normalizedBody = skill.InstructionBody.Replace("\r\n", "\n");
        builder.Append(normalizedBody);
        if (!normalizedBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        return new SquadDeploymentFile(
            $"{SkillsDirectory}/{skill.Name}/SKILL.md",
            Encoding.UTF8.GetBytes(builder.ToString()),
            "copilot");
    }

    private static string? ResolveCopilotModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("copilot", out string? copilotModel))
        {
            return copilotModel;
        }

        // "inherit" means defer to whatever the user has selected in Copilot's own model
        // picker — Copilot's documented default when `model` is omitted entirely.
        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal)
            ? null
            : profile.Default;
    }

    /// <summary>
    /// Lowers a capability profile onto Copilot's closed tool allow-list. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants: <c>ask</c> and <c>deny</c> both
    /// withhold, which keeps the lowering non-broadening by construction.
    /// Standard MCP wildcards (<c>'codegraph/*'</c>, <c>'kyber-weave/*'</c>, <c>'context7/*'</c>)
    /// are granted when <c>filesystem.read</c> is allowed unless the agent is a pure orchestrator
    /// (profile id <c>orchestrator</c> or name in <see cref="SharedConductorIdentities"/>).
    /// <c>vscode</c> and <c>todo</c> are ungoverned base tools granted to all custom agents.
    /// </summary>
    private static IReadOnlyList<string> ResolveTools(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        HashSet<string> granted = new(BaseUngovernedTools, StringComparer.Ordinal);

        // An unresolvable profile grants nothing beyond the ungoverned tools. SquadSourceValidator
        // already rejects an agent naming an undeclared profile, so this is unreachable in a
        // validated bundle — but falling back to "grant everything" here would turn a source
        // error into a silent permission widening.
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

            bool isPureOrchestrator =
                string.Equals(agent.CapabilityProfile, "orchestrator", StringComparison.Ordinal) ||
                SharedConductorIdentities.Contains(agent.Name, StringComparer.Ordinal);

            if (!isPureOrchestrator &&
                profile.Permissions.TryGetValue("filesystem.read", out SquadPermissionDecision readDecision) &&
                readDecision == SquadPermissionDecision.Allow)
            {
                foreach (string mcpTool in StandardMcpTools)
                {
                    granted.Add(mcpTool);
                }
            }
        }

        return ToolOrder.Where(granted.Contains).ToArray();
    }

    private static SquadDegradationRecord? BuildPermissionDegradation(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            return null;
        }

        // 'deny' needs no record: the rendered allow-list withholds the tool, so the
        // decision is enforced exactly as written. Only 'ask' loses meaning, because
        // Copilot's frontmatter has no per-tool confirmation gate to lower it onto.
        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count == 0)
        {
            return null;
        }

        return new SquadDegradationRecord(
            Target: "copilot",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "safety-narrowed",
            InstructionDigest: agent.BodyDigest,
            Details: $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                $"{string.Join(", ", narrowed)}. Copilot's tool allow-list is binary and " +
                "cannot prompt for confirmation, so these narrow to 'deny' and the " +
                "corresponding tools are withheld from the agent's 'tools' list.");
    }

    /// <summary>
    /// Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    /// <see cref="CopilotToolsFlowSequenceConverter"/>.
    /// </summary>
    private sealed class CopilotToolsFlowSequence : List<string>
    {
        public CopilotToolsFlowSequence(IEnumerable<string> tools)
            : base(tools)
        {
        }
    }

    /// <summary>
    /// Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    /// <see cref="CopilotAgentsFlowSequenceConverter"/>.
    /// </summary>
    private sealed class CopilotAgentsFlowSequence : List<string>
    {
        public CopilotAgentsFlowSequence(IEnumerable<string> agents)
            : base(agents)
        {
        }
    }

    /// <summary>
    /// Serializes the delegation roster as an inline YAML flow sequence of single-quoted
    /// names, matching the shape Copilot agent frontmatter uses for <c>agents</c>.
    /// </summary>
    private sealed class CopilotAgentsFlowSequenceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(CopilotAgentsFlowSequence);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            throw new NotSupportedException("Deserialization of CopilotAgentsFlowSequence is not supported.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            if (value is not CopilotAgentsFlowSequence agents)
            {
                return;
            }

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Flow));
            foreach (string agent in agents)
            {
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, agent, ScalarStyle.SingleQuoted, isPlainImplicit: true, isQuotedImplicit: true));
            }

            emitter.Emit(new SequenceEnd());
        }
    }

    /// <summary>
    /// Custom YamlDotNet type converter to serialize Copilot agent tools as an inline
    /// YAML flow sequence with single-quoted wildcard identifiers.
    /// </summary>
    private sealed class CopilotToolsFlowSequenceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(CopilotToolsFlowSequence);

        public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
        {
            throw new NotSupportedException("Deserialization of CopilotToolsFlowSequence is not supported.");
        }

        public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
        {
            if (value is not CopilotToolsFlowSequence tools)
            {
                return;
            }

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, SequenceStyle.Flow));
            foreach (string tool in tools)
            {
                if (tool.Contains('*', StringComparison.Ordinal))
                {
                    string unquoted = tool.Trim('\'');
                    emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, unquoted, ScalarStyle.SingleQuoted, isPlainImplicit: true, isQuotedImplicit: true));
                }
                else
                {
                    emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, tool, ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: true));
                }
            }

            emitter.Emit(new SequenceEnd());
        }
    }
}
