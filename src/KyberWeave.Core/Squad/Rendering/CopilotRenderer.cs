using System.Globalization;
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

    /// <summary>
    /// Returns the absolute <c>&lt;copilot-root&gt;/agents</c> directory (forward slashes) that
    /// agent resource links resolve under at global scope, or null at project scope.
    /// </summary>
    /// <exception cref="SquadRenderValidationException">
    /// Global scope without <see cref="SquadRenderRequest.GlobalRoots"/>: rendering relative
    /// links anyway would ship the defect this rewrite exists to fix.
    /// </exception>
    private static string? ResolveGlobalAgentsRoot(SquadRenderRequest request)
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

        string root = request.GlobalRoots.ResolveGlobalRoot(SquadTarget.Copilot)
            .Replace('\\', '/')
            .TrimEnd('/');
        return $"{root}/{ResolvePrefixedDirectory(AgentsDirectory, SquadDeploymentScope.Global)}";
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
            Target: "copilot",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "resource-links-rewritten",
            InstructionDigest: agent.BodyDigest,
            Details: "Copilot does not reliably anchor a relative link in a user-level agent to " +
                "the agent file, so the agent's " +
                $"{agent.Resources.Count.ToString(CultureInfo.InvariantCulture)} resource file(s) " +
                "stay beside their principal and " +
                (changed
                    ? "the agent's authored links were rewritten to absolute paths under the resolved Copilot global root."
                    : "no authored link required rewriting.")));

        return body;
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

        // A subagent shared identity is suppressed because the agent file carries the identical
        // body; a primary shared identity is the thin wrapper whose canonical skill IS the entry
        // point, so suppressing it would leave the wrapper pointing at nothing.
        HashSet<string> suppressedSkillIdentities =
            SquadSharedIdentityProjection.SuppressedSkillIdentities(source, sharedIdentities);

        string? globalAgentsRoot = ResolveGlobalAgentsRoot(request);

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
                globalAgentsRoot,
                degradations);
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
            // renderer-local roster change. A primary shared identity is the exception: its
            // canonical skill is the entry point and must be emitted.
            if (suppressedSkillIdentities.Contains(skill.Name))
            {
                continue;
            }

            SquadDeploymentFile principal = RenderSkill(skill, request.Scope);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, skill.Resources);
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, warnings, []));
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        List<SquadRenderWarning> warnings,
        SquadDeploymentScope scope,
        string? globalAgentsRoot,
        List<SquadDegradationRecord> degradations)
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

        string yaml = YamlSerializer.Serialize(frontmatter);
        StringBuilder builder = new();
        builder.Append("---\n");
        builder.Append(yaml);
        if (!yaml.EndsWith('\n'))
        {
            builder.Append('\n');
        }

        builder.Append("---\n");

        string authoredBody = globalAgentsRoot is null
            ? agent.InstructionBody
            : RewriteResourceLinks(agent, globalAgentsRoot, degradations);
        string normalizedBody = authoredBody.Replace("\r\n", "\n", StringComparison.Ordinal);
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
