using System.Globalization;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
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
/// (<c>'codegraph/*'</c>, <c>'kw-docs/*'</c>, <c>'context7/*'</c>).
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
public sealed class CopilotRenderer : SquadHarnessRenderer
{
    private const string CliHooksFileRelativePath = ".github/hooks/kyber-arbiter.json";
    private const string VsCodeHarnessToken = "copilot-vscode";
    private const string CliHarnessToken = "copilot-cli";
    private const int MaxAgentBodyCharacters = 30_000;

    /// <summary>The warning code for an agent body over the Copilot cap. Install and update show it only when the operator channel overlay was applied.</summary>
    internal const string AgentBodyTooLongCode = "agent-body-too-long";

    private static readonly SquadYamlEmitter Yaml = new(new SerializerBuilder()
        .WithTypeConverter(new CopilotToolsFlowSequenceConverter())
        .WithTypeConverter(new CopilotAgentsFlowSequenceConverter())
        .Build());

    private static readonly SquadHarnessSpec HarnessSpec = new(
        SquadTarget.Copilot,
        "Copilot",
        ".github",
        "agents",
        SquadModelPolicy.HarnessValueVerbatim,
        SquadLineBreaks.Preserved);

    public CopilotRenderer()
        : base(HarnessSpec)
    {
    }

    /// <summary>
    /// Returns the absolute <c>&lt;copilot-root&gt;/agents</c> directory (forward slashes) that
    /// agent resource links resolve under at global scope, or null at project scope.
    /// </summary>
    /// <exception cref="SquadRenderValidationException">
    /// Global scope without <see cref="SquadRenderRequest.GlobalRoots"/>: rendering relative
    /// links anyway would ship the defect this rewrite exists to fix.
    /// </exception>
    private static string? ResolveGlobalAgentsRoot(string? copilotGlobalRoot)
    {
        if (copilotGlobalRoot is null)
        {
            return null;
        }

        string root = copilotGlobalRoot
            .Replace('\\', '/')
            .TrimEnd('/');
        return $"{root}/{HarnessSpec.ResolveDirectory("agents", SquadDeploymentScope.Global)}";
    }

    /// <summary>
    /// Resolves the Copilot global root for this render, once. Every consumer, the agents root, the resource links,
    /// and the operator channel probe, derives from this one answer, so they cannot disagree about where Copilot lives.
    /// </summary>
    /// <returns>The root at global scope, or null at project scope.</returns>
    /// <exception cref="SquadRenderValidationException">
    /// Global scope without <see cref="SquadRenderRequest.GlobalRoots"/>: rendering relative
    /// links anyway would ship the defect this rewrite exists to fix.
    /// </exception>
    private static string? ResolveCopilotGlobalRoot(SquadRenderRequest request)
    {
        if (request.Scope != SquadDeploymentScope.Global)
        {
            return null;
        }

        if (request.GlobalRoots is null)
        {
            throw new SquadRenderValidationException(
                "Rendering Copilot at global scope requires SquadRenderRequest.GlobalRoots: " +
                "agent resource links are rewritten to absolute paths under the resolved " +
                "Copilot global root, and without a resolver they would stay relative and " +
                "fail to resolve from a foreign working directory.");
        }

        return request.GlobalRoots.ResolveGlobalRoot(SquadTarget.Copilot);
    }

    /// <summary>
    /// Rewrites the agent's authored links to its declared resources into absolute paths and
    /// records the deviation.
    /// </summary>
    private static string RewriteResourceLinks(
        SquadAgent agent,
        string globalAgentsRoot,
        List<SquadDegradationRecord> degradations)
    {
        if (agent.Resources.Count == 0)
        {
            return agent.InstructionBody;
        }

        HashSet<string> resourcePaths = agent.Resources
            .Select(resource => resource.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        string body = SquadResourceLinkRewriter.Rewrite(
            agent.InstructionBody,
            resourcePaths,
            pathPart => $"{globalAgentsRoot}/{pathPart}",
            out bool changed);

        degradations.Add(new SquadDegradationRecord(
            "copilot",
            agent.Name,
            agent.Name,
            "resource-links-rewritten",
            agent.BodyDigest,
            "Copilot does not reliably anchor a relative link in a user-level agent to " +
            "the agent file, so the agent's " +
            $"{agent.Resources.Count.ToString(CultureInfo.InvariantCulture)} resource file(s) " +
            "stay beside their principal and " +
            (changed
                ? "the agent's authored links were rewritten to absolute paths under the resolved Copilot global root."
                : "no authored link required rewriting.")));

        return body;
    }

    /// <summary>The per-render answers, resolved before any agent renders.</summary>
    private protected override SquadRenderResult Render(SquadHarnessRenderContext context)
    {
        context.GetOrCreateState(CopilotPlan.Create);
        return base.Render(context);
    }

    private protected override void RenderAgent(SquadHarnessRenderContext context, SquadAgent agent)
    {
        CopilotPlan plan = context.GetOrCreateState(CopilotPlan.Create);
        context.AddPrincipal(
            RenderAgentFile(
                context,
                agent,
                plan,
                agent.Name == SquadOperatorChannel.ConductorAgentName ? plan.OperatorChannelOverlay : null),
            agent.Resources);
        context.AddDegradation(BuildPermissionDegradation(agent, context.CapabilityProfiles));
    }

    private protected override void RenderCanonicalSkills(SquadHarnessRenderContext context) =>
        context.EmitCanonicalSkills(skill =>
            context.RenderSkillDocument(skill.Name, skill.Description, skill.InstructionBody, Yaml));

    private protected override void RenderArbiter(SquadHarnessRenderContext context)
    {
        CopilotPlan plan = context.GetOrCreateState(CopilotPlan.Create);
        SquadArbiterWiring? arbiter = context.EnabledArbiter;
        bool shouldRender = context.Scope == SquadDeploymentScope.Global
            ? arbiter is not null
            : plan.ArbiterHooks.Count > 0;
        if (shouldRender)
        {
            string relativePath = context.Scope == SquadDeploymentScope.Global
                ? "hooks/kyber-arbiter.json"
                : CliHooksFileRelativePath;
            context.Files.Add(RenderCliHooksFile(
                arbiter?.HookTimeoutSeconds ?? plan.ArbiterHooks[0].TimeoutSeconds,
                context.Scope,
                relativePath));
        }
    }

    private static SquadDeploymentFile RenderAgentFile(
        SquadHarnessRenderContext context,
        SquadAgent agent,
        CopilotPlan plan,
        SquadOperatorChannel.Overlay? operatorChannelOverlay)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = agent.Description
        };

        string? model = context.ResolveModel(agent);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        // The overlay's tool entry is added after normalisation, so the catalog order stays canonical
        // and the entry sits at the end of the list. It is present only when the overlay is.
        List<string> tools = [.. CopilotToolCatalog.Normalize(agent.CopilotTools)];
        if (operatorChannelOverlay is not null)
        {
            tools.Add(operatorChannelOverlay.ToolEntry);
        }

        frontmatter["tools"] = new CopilotToolsFlowSequence(tools);

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

        SquadArbiterHook? arbiterHook = plan.ArbiterHooks.FirstOrDefault(hook => hook.Caller == agent.Name);
        if (arbiterHook is not null)
        {
            frontmatter["hooks"] = ArbiterHooksFrontmatter(plan.Dispatchers.Contains(agent.Name), arbiterHook, context.Scope);
        }

        string authoredBody = plan.GlobalAgentsRoot is null
            ? agent.InstructionBody
            : RewriteResourceLinks(agent, plan.GlobalAgentsRoot, context.Degradations);
        string normalizedBody = authoredBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (operatorChannelOverlay is not null)
        {
            // The overlay closes the conductor body, after every authored section.
            normalizedBody = $"{normalizedBody.TrimEnd('\n')}\n\n{operatorChannelOverlay.Body}";
        }

        string content = Yaml.Compose(frontmatter, normalizedBody);
        if (content.Length > MaxAgentBodyCharacters)
        {
            context.Warnings.Add(new SquadRenderWarning(
                AgentBodyTooLongCode,
                $"Agent '{agent.Name}' is {content.Length} characters; Copilot caps custom agent files at {MaxAgentBodyCharacters}.",
                "copilot"));
        }

        return context.CreateFile($"{context.AgentsDirectory}/{agent.Name}.agent.md", content);
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
            "copilot",
            agent.Name,
            agent.Name,
            "safety-narrowed",
            agent.BodyDigest,
            $"Copilot capability profile '{profileName}' requires 'ask' for " +
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
    private static Dictionary<string, object?> ArbiterHooksFrontmatter(bool isDispatcher, SquadArbiterHook hook, SquadDeploymentScope scope)
    {
        Dictionary<string, object?> hooks = new(StringComparer.Ordinal)
        {
            ["PreToolUse"] = new List<object?> { HookEntry(hook, scope) }
        };

        if (isDispatcher)
        {
            hooks["PostToolUse"] = new List<object?> { HookEntry(hook, scope) };
        }

        return hooks;
    }

    private static Dictionary<string, object?> HookEntry(SquadArbiterHook hook, SquadDeploymentScope scope) => new(StringComparer.Ordinal)
    {
        ["type"] = "command",
        // The shared helper, not a second interpolation: Copilot runs this through a shell,
        // so the caller has to pass the same validation every other hook surface applies.
        ["command"] = ArbiterHookWiring.HookCommandLine(VsCodeHarnessToken, hook.Caller, scope),
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
    public static SquadDeploymentFile RenderCliHooksFile(
        int timeoutSeconds,
        SquadDeploymentScope scope,
        string relativePath)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteStartObject("hooks");
            WriteCliHookEntry(writer, "preToolUse", timeoutSeconds, scope);
            WriteCliHookEntry(writer, "postToolUse", timeoutSeconds, scope);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return new SquadDeploymentFile(relativePath, stream.ToArray(), "copilot");
    }

    private static void WriteCliHookEntry(Utf8JsonWriter writer, string eventName, int timeoutSeconds, SquadDeploymentScope scope)
    {
        writer.WriteStartArray(eventName);
        writer.WriteStartObject();
        writer.WriteString("type", "command");
        writer.WriteString("matcher", "task");
        string command = ArbiterHookWiring.HookCommandLine(CliHarnessToken, scope);
        writer.WriteString("bash", command);
        writer.WriteString("powershell", command);
        writer.WriteNumber("timeoutSec", timeoutSeconds);
        writer.WriteEndObject();
        writer.WriteEndArray();
    }

    /// <summary>
    /// Strongly-typed sequence wrapper to direct YamlDotNet serialization through
    /// <see cref="CopilotToolsFlowSequenceConverter"/>.
    /// </summary>
    /// <summary>
    /// What a Copilot render resolves once: where Copilot lives at global scope, whether the
    /// operator channel overlay applies, and which Arbiter hooks address this target.
    /// </summary>
    private sealed record CopilotPlan(
        string? GlobalAgentsRoot,
        SquadOperatorChannel.Overlay? OperatorChannelOverlay,
        IReadOnlyList<SquadArbiterHook> ArbiterHooks,
        IReadOnlySet<string> Dispatchers)
    {
        public static CopilotPlan Create(SquadHarnessRenderContext context)
        {
            SquadRenderRequest request = context.Request;
            SquadSource source = context.Source;
            List<SquadRenderWarning> warnings = context.Warnings;
            string? copilotGlobalRoot = ResolveCopilotGlobalRoot(request);
            string? globalAgentsRoot = ResolveGlobalAgentsRoot(copilotGlobalRoot);

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

            // The operator channel attaches only at global scope, and only when the user's Copilot config
            // declares the server it calls. Without the server the conductor must stay exactly as it is,
            // because a tool entry for a server Copilot cannot reach is a tool the agent cannot call.
            SquadOperatorChannel.Overlay? operatorChannelOverlay = null;
            if (copilotGlobalRoot is not null && SquadOperatorChannel.IsGateOn(request.EnvironmentReader))
            {
                SquadOperatorChannel.ServerName server = SquadOperatorChannel.ResolveServerName(request.EnvironmentReader);
                if (server.Warning is { } nameWarning)
                {
                    warnings.Add(nameWarning);
                }
                else if (server.Name is { } serverName)
                {
                    if (CopilotMcpServerProbe.HasServer(copilotGlobalRoot, serverName))
                    {
                        string overlaySourceDirectory = Directory.Exists(request.SourceDirectory)
                            ? request.SourceDirectory
                            : request.PreloadedSource?.RootPath ?? request.SourceDirectory;
                        operatorChannelOverlay = SquadOperatorChannel.LoadOverlay(overlaySourceDirectory, serverName);
                        warnings.Add(SquadOperatorChannel.OverlayApplied());
                    }
                    else
                    {
                        warnings.Add(new SquadRenderWarning(
                            SquadOperatorChannel.ServerAbsentCode,
                            $"The operator channel is enabled, but no '{serverName}' server is declared " +
                            $"under mcpServers in {CopilotMcpServerProbe.McpConfigFileName} in '{copilotGlobalRoot}'. " +
                            "The conductor was rendered without the operator channel.",
                            "copilot"));
                    }
                }
            }

            return new CopilotPlan(globalAgentsRoot, operatorChannelOverlay, arbiterHooks, dispatcherAgents);
        }
    }

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
        public bool Accepts(Type type)
        {
            return type == typeof(CopilotAgentsFlowSequence);
        }

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

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Flow));
            foreach (string agent in agents)
            {
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, agent, ScalarStyle.SingleQuoted, true, true));
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
        public bool Accepts(Type type)
        {
            return type == typeof(CopilotToolsFlowSequence);
        }

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

            emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, SequenceStyle.Flow));
            foreach (string tool in tools)
            {
                if (tool.Contains('*', StringComparison.Ordinal))
                {
                    string unquoted = tool.Trim('\'');
                    emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, unquoted, ScalarStyle.SingleQuoted, true,
                        true));
                }
                else
                {
                    emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, tool, ScalarStyle.Plain, true, true));
                }
            }

            emitter.Emit(new SequenceEnd());
        }
    }
}
