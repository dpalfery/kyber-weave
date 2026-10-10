using System.Text;
using System.Text.Json;
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
/// <para>
/// Arbiter hooks lower to two Copilot surfaces because Copilot ships two hook systems.
/// VS Code custom agents run frontmatter hooks only while the agent is active, so each
/// dispatcher gets flat <c>PreToolUse</c> and <c>PostToolUse</c> command lists and each
/// guarded agent a <c>PreToolUse</c> list, keyed to <c>--harness copilot-vscode</c> ([F2]);
/// the Copilot CLI discovers <c>.github/hooks/*.json</c> instead, so one owned file gates
/// its <c>task</c> tool with <c>--harness copilot-cli</c> for both shells ([F3]).
/// <see cref="ArbiterHookWiring"/> decides who is gated and emits commands keyed to the
/// <c>copilot</c> target token, so the per-surface harness token is this renderer's
/// lowering decision: the command line is rebuilt from the wiring's caller and timeout
/// rather than reusing <see cref="SquadArbiterHook.CommandLine"/> verbatim.
/// </para>
/// </remarks>
public sealed class CopilotRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".github/agents";
    private const string SkillsDirectory = ".github/skills";
    private const string CliHooksFileRelativePath = ".github/hooks/kyber-arbiter.json";
    private const string ArbiterCommandName = "kyber-weave-arbiter";
    private const string VsCodeHarnessToken = "copilot-vscode";
    private const string CliHarnessToken = "copilot-cli";
    private const int MaxAgentBodyCharacters = 30_000;

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithTypeConverter(new CopilotToolsFlowSequenceConverter())
        .WithTypeConverter(new CopilotAgentsFlowSequenceConverter())
        .Build();

    /// <summary>
    /// Resolves the directory prefix for agents/skills based on deployment scope.
    /// Under Project scope, keeps `.github/` prefix; under Global scope, removes it.
    /// </summary>
    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        // baseDirectory is like ".github/agents" or ".github/skills"
        // Under Project scope, return as-is
        // Under Global scope, strip the ".github/" prefix
        if (scope == SquadDeploymentScope.Project)
        {
            return baseDirectory;
        }

        // Strip ".github/" prefix for global scope
        return baseDirectory.StartsWith(".github/", StringComparison.Ordinal)
            ? baseDirectory[".github/".Length..]
            : baseDirectory;
    }

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
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

        // A null Arbiter must render byte for byte as before the field existed, and
        // BuildHooks refuses a null wiring, so the guard lives here rather than in the
        // wiring. Only hooks addressed to this target are consumed; BuildHooks also returns
        // empty for a disabled wiring or a Global scope, which is what keeps those renders
        // free of every hook surface.
        List<SquadArbiterHook> arbiterHooks = request.Arbiter is null
            ? []
            : [.. ArbiterHookWiring.BuildHooks(source, request.Arbiter, request.Scope)
                .Where(hook => hook.Target == SquadTargetCatalog.GetToken(SquadTarget.Copilot))];
        IReadOnlySet<string> dispatcherAgents = ArbiterHookWiring.Dispatchers(source);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];
        List<SquadRenderWarning> warnings = [];

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile principal = RenderAgent(
                agent,
                source.ModelProfiles.Profiles,
                warnings,
                request.Scope,
                arbiterHooks,
                dispatcherAgents);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, agent.Resources);

            SquadDegradationRecord? degradation = BuildPermissionDegradation(agent, source.CapabilityProfiles.Profiles);
            if (degradation is not null)
            {
                degradations.Add(degradation);
            }
        }

        foreach (SquadSkill skill in source.Skills)
        {
            // A profile-declared shared identity has one canonical projection. Resolve the
            // set from source so removing or adding a shared identity never requires a
            // renderer-local roster change.
            if (sharedIdentities.Contains(skill.Name))
            {
                continue;
            }

            SquadDeploymentFile principal = RenderSkill(skill, request.Scope);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, skill.Resources);
        }

        if (arbiterHooks.Count > 0)
        {
            files.Add(RenderCliHooksFile(arbiterHooks[0].TimeoutSeconds));
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, warnings, []));
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        List<SquadRenderWarning> warnings,
        SquadDeploymentScope scope,
        IReadOnlyList<SquadArbiterHook> arbiterHooks,
        IReadOnlySet<string> dispatcherAgents)
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

        frontmatter["tools"] = new CopilotToolsFlowSequence(CopilotToolCatalog.Normalize(agent.CopilotTools));

        // Every agent that delegates names its permitted roster in frontmatter:
        // the "agent" tool grants the mechanism, "agents" names who it may reach.
        // GitHub Copilot does not grant a primary agent (conductor, invocation
        // primary) the full roster automatically, so a delegating primary declares
        // one exactly like a subagent does.
        if (agent.DelegatesTo.Count > 0)
        {
            frontmatter["agents"] = new CopilotAgentsFlowSequence(agent.DelegatesTo);
        }

        if (agent.Invocation == SquadInvocation.Subagent)
        {
            // Subagents are dispatched by the conductor, not chosen directly by a human —
            // "user-invocable: false" is Copilot's closest equivalent. Primary agents
            // (conductor) leave this at its default (true, omitted).
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

        SquadArbiterHook? arbiterHook = arbiterHooks.FirstOrDefault(hook => hook.Caller == agent.Name);
        if (arbiterHook is not null)
        {
            frontmatter["hooks"] = ArbiterHooksFrontmatter(dispatcherAgents.Contains(agent.Name), arbiterHook);
        }

        string yaml = YamlSerializer.Serialize(frontmatter);
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

        string content = builder.ToString();
        if (content.Length > MaxAgentBodyCharacters)
        {
            warnings.Add(new SquadRenderWarning(
                "agent-body-too-long",
                $"Agent '{agent.Name}' is {content.Length} characters; Copilot caps custom agent files at {MaxAgentBodyCharacters}.",
                "copilot"));
        }

        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, scope);
        return new SquadDeploymentFile(
            $"{agentsDir}/{agent.Name}.agent.md",
            Encoding.UTF8.GetBytes(content),
            "copilot");
    }

    private static SquadDeploymentFile RenderSkill(SquadSkill skill, SquadDeploymentScope scope)
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

        string normalizedBody = skill.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        builder.Append(normalizedBody);
        if (!normalizedBody.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, scope);
        return new SquadDeploymentFile(
            $"{skillsDir}/{skill.Name}/SKILL.md",
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

    private static SquadDegradationRecord? BuildPermissionDegradation(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        string profileName = agent.CopilotCapabilityProfile ?? agent.CapabilityProfile;
        if (!capabilityProfiles.TryGetValue(profileName, out SquadCapabilityProfile? profile))
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
            Details: $"Copilot capability profile '{profileName}' requires 'ask' for " +
                $"{string.Join(", ", narrowed)}. Copilot's tool allow-list is binary and " +
                "cannot prompt for confirmation, so these narrow to 'deny' and the " +
                "corresponding tools are withheld from the agent's 'tools' list.");
    }

    /// <summary>Builds the <c>hooks</c> frontmatter mapping for one wired agent.</summary>
    /// <remarks>
    /// [F2]: each event holds a flat command list with no matcher, so every tool call
    /// reaches the hook. Dispatchers gate their dispatches on both events; guarded agents
    /// only need the <c>PreToolUse</c> gate, because only their planning-path reads are
    /// audited, not an output the hook would have to block after the fact.
    /// </remarks>
    private static Dictionary<string, object?> ArbiterHooksFrontmatter(bool isDispatcher, SquadArbiterHook hook)
    {
        Dictionary<string, object?> hooks = new(StringComparer.Ordinal)
        {
            ["PreToolUse"] = new List<object?> { HookEntry(hook) }
        };

        if (isDispatcher)
        {
            hooks["PostToolUse"] = new List<object?> { HookEntry(hook) };
        }

        return hooks;
    }

    private static Dictionary<string, object?> HookEntry(SquadArbiterHook hook) => new(StringComparer.Ordinal)
    {
        ["type"] = "command",
        ["command"] = $"{ArbiterCommandName} hook --harness {VsCodeHarnessToken} --caller {hook.Caller}",
        ["timeout"] = hook.TimeoutSeconds
    };

    /// <summary>Renders the owned Copilot CLI hook file gating the CLI's <c>task</c> tool.</summary>
    /// <remarks>
    /// The CLI discovers <c>.github/hooks/*.json</c> and expects Copilot hook format: a
    /// numeric <c>version</c>, camelCase events, and per-shell command fields ([F3]). The
    /// matcher compiles to <c>^(?:task)$</c>, so only subagent dispatches reach the hook.
    /// Every produced hook carries the same wiring timeout, so the first hook's value
    /// stands for the file.
    /// </remarks>
    private static SquadDeploymentFile RenderCliHooksFile(int timeoutSeconds)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartObject("hooks");
            WriteCliHookEntry(writer, "preToolUse", timeoutSeconds);
            WriteCliHookEntry(writer, "postToolUse", timeoutSeconds);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new SquadDeploymentFile(CliHooksFileRelativePath, stream.ToArray(), "copilot");
    }

    private static void WriteCliHookEntry(Utf8JsonWriter writer, string eventName, int timeoutSeconds)
    {
        writer.WriteStartArray(eventName);
        writer.WriteStartObject();
        writer.WriteString("type", "command");
        writer.WriteString("matcher", "task");
        writer.WriteString("bash", $"{ArbiterCommandName} hook --harness {CliHarnessToken}");
        writer.WriteString("powershell", $"{ArbiterCommandName} hook --harness {CliHarnessToken}");
        writer.WriteNumber("timeoutSec", timeoutSeconds);
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    /// <summary>
    /// Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    /// <see cref="CopilotToolsFlowSequenceConverter"/>.
    /// </summary>
    private sealed class CopilotToolsFlowSequence(IEnumerable<string> tools) : List<string>(tools);

    /// <summary>
    /// Prevents the generic YAML serializer from changing the deployed delegation
    /// allow-list shape required by Copilot agent frontmatter.
    /// </summary>
    private sealed class CopilotAgentsFlowSequence(IEnumerable<string> agents) : List<string>(agents);

    /// <summary>
    /// Keeps each delegating agent restricted to its declared delegation roster in emitted
    /// Copilot frontmatter by serializing as an inline single-quoted flow sequence
    /// rather than YamlDotNet's default block list.
    /// </summary>
    private sealed class CopilotAgentsFlowSequenceConverter : IYamlTypeConverter
    {
        public bool Accepts(Type type) => type == typeof(CopilotAgentsFlowSequence);

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
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

        public object ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
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
