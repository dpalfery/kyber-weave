using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
///     Renders canonical Squad source into Codex's native agent TOML and skill file formats.
/// </summary>
/// <remarks>
///     <para>
///         Native agent target: canonical agents render as Codex's native agent TOML primitive at
///         <c>.codex/agents/&lt;name&gt;.toml</c>. Codex agent TOML files contain the required
///         top-level fields <c>name</c>, <c>description</c>, and multi-line
///         <c>developer_instructions</c>, plus optional <c>model</c>.
///     </para>
///     <para>
///         Canonical skills render as harness skills at <c>.codex/skills/&lt;name&gt;/SKILL.md</c>
///         with YAML frontmatter containing <c>name</c>, <c>description</c>, and <c>license: MIT</c>.
///         Per the native single-projection rule, profile-declared shared identities suppress their
///         skill projections.
///     </para>
///     <para>
///         Model resolution resolves the agent model from <c>models.yml</c> for target <c>codex</c>,
///         falling back to the profile's default when not <c>inherit</c>.
///     </para>
///     <para>
///         Permission degradation: Codex agent configuration has no frontmatter tool allow-list or
///         capability permission lattice. Non-deny profile decisions are recorded as structured
///         degradations with code <c>permission-not-expressible</c> rather than inventing unenforceable fields.
///     </para>
/// </remarks>
public sealed class CodexRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".codex/agents";
    private const string SkillsDirectory = ".codex/skills";

    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    /// <summary>
    ///     YamlDotNet does not document <see cref="ISerializer" /> as thread-safe, and the
    ///     registry may dispatch renderers concurrently; serialization takes this lock so a
    ///     shared static instance cannot interleave emitter state.
    /// </summary>
    private static readonly object SerializerLock = new();

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Codex];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Codex))
            throw new ArgumentException(
                "CodexRenderer was asked to render a target other than Codex.",
                nameof(request));

        if (request.Targets.Count == 0) return Task.FromResult(new SquadRenderResult(true, [], [], [], []));

        SquadSource source = SquadSourceLoader.Load(request.SourceDirectory);
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
            .ToHashSet(StringComparer.Ordinal);

        HashSet<string> skillIdentities = source.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.Ordinal);

        // A subagent shared identity is suppressed because the agent file carries the identical
        // body; a primary shared identity is the thin wrapper whose canonical skill IS the entry
        // point, so suppressing it would leave the wrapper pointing at nothing.
        HashSet<string> suppressedSkillIdentities =
            SquadSharedIdentityProjection.SuppressedSkillIdentities(source, sharedIdentities);

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];

        // The declared vocabulary, not a renderer-local copy: a capability added to
        // profiles/capabilities.yml must appear in degradation text without a renderer
        // change. Sorted for deterministic details strings.
        string[] capabilityVocabulary = [.. source.CapabilityProfiles.Capabilities.Order(StringComparer.Ordinal)];

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile principal = RenderAgent(
                agent,
                source.ModelProfiles.Profiles,
                request.Scope);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, agent.Resources);

            if (agent.Invocation == SquadInvocation.Primary)
            {
                SquadFallbackProfile fallbackProfile = source.FallbackProfiles.Profiles[agent.Fallback];
                if (string.Equals(fallbackProfile.NoPrimaryAgent, "skill", StringComparison.Ordinal))
                {
                    if (skillIdentities.Contains(agent.Name))
                    {
                        if (!sharedIdentities.Contains(agent.Name))
                            throw new SquadRenderValidationException(
                                $"Cannot render primary agent '{agent.Name}' as a Codex entry-point skill: " +
                                $"a canonical skill named '{agent.Name}' already occupies that identity, " +
                                "and the identity is not declared as shared.");
                    }
                    else
                    {
                        SquadDeploymentFile skillPrincipal = RenderPrimaryAgentEntryPointSkill(agent, request.Scope);
                        files.Add(skillPrincipal);
                        SquadResourceProjection.Append(files, skillPrincipal, agent.Resources);
                    }
                }
                else if (!string.Equals(fallbackProfile.NoPrimaryAgent, "omit", StringComparison.Ordinal))
                {
                    throw new SquadRenderValidationException(
                        $"Fallback profile '{agent.Fallback}' declares unsupported no-primary-agent " +
                        $"value '{fallbackProfile.NoPrimaryAgent}' for primary agent '{agent.Name}'.");
                }
            }

            SquadDegradationRecord? degradation = BuildDegradationRecord(
                agent,
                source.CapabilityProfiles.Profiles,
                capabilityVocabulary);
            if (degradation is not null) degradations.Add(degradation);
        }

        foreach (SquadSkill skill in source.Skills)
        {
            // A profile-declared shared identity has one canonical projection. Resolve the
            // set from source so removing or adding a shared identity never requires a
            // renderer-local roster change. A primary shared identity is the exception: its
            // canonical skill is the entry point and must be emitted.
            if (suppressedSkillIdentities.Contains(skill.Name)) continue;

            SquadDeploymentFile principal = RenderSkill(skill, request.Scope);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, skill.Resources);
        }

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        if (scope == SquadDeploymentScope.Project) return baseDirectory;

        return baseDirectory.StartsWith(".codex/", StringComparison.Ordinal)
            ? baseDirectory[".codex/".Length..]
            : baseDirectory;
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        SquadDeploymentScope scope)
    {
        StringBuilder builder = new();
        builder.Append("name = \"");
        builder.Append(EscapeTomlString(agent.Name));
        builder.Append("\"\ndescription = \"");
        builder.Append(EscapeTomlString(agent.Description));
        builder.Append("\"\n");

        string? model = ResolveCodexModel(agent, modelProfiles);
        if (model is not null)
        {
            builder.Append("model = \"");
            builder.Append(EscapeTomlString(model));
            builder.Append("\"\n");
        }

        string normalizedBody = agent.InstructionBody.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalizedBody.EndsWith('\n')) normalizedBody += "\n";

        builder.Append("developer_instructions = \"\"\"\n");
        builder.Append(EscapeTomlMultiline(normalizedBody));
        builder.Append("\"\"\"\n");

        string content = builder.ToString();
        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, scope);
        return new SquadDeploymentFile(
            $"{agentsDir}/{agent.Name}.toml",
            Encoding.UTF8.GetBytes(content),
            "codex");
    }

    private static SquadDeploymentFile RenderSkill(SquadSkill skill, SquadDeploymentScope scope)
    {
        string singleLineDescription = string.Join(" ", skill.Description.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = skill.Name,
            ["description"] = singleLineDescription,
            ["license"] = "MIT"
        };

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, skill.InstructionBody);
        }

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, scope);
        return new SquadDeploymentFile(
            $"{skillsDir}/{skill.Name}/SKILL.md",
            Encoding.UTF8.GetBytes(content),
            "codex");
    }

    /// <summary>
    ///     Renders a primary agent's canonical body as a user-invoked skill for Codex sessions,
    ///     which do not use the native agent definition as the main-session entry point.
    /// </summary>
    private static SquadDeploymentFile RenderPrimaryAgentEntryPointSkill(
        SquadAgent agent,
        SquadDeploymentScope scope)
    {
        string singleLineDescription = string.Join(" ", agent.Description.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = singleLineDescription,
            ["license"] = "MIT"
        };

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);
        }

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, scope);
        return new SquadDeploymentFile(
            $"{skillsDir}/{agent.Name}/SKILL.md",
            Encoding.UTF8.GetBytes(content),
            "codex");
    }

    private static string? ResolveCodexModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile)) return null;

        if (profile.HarnessModels.TryGetValue("codex", out string? codexModel)) return codexModel;

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal)
            ? null
            : profile.Default;
    }

    private static SquadDegradationRecord? BuildDegradationRecord(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile)) return null;

        List<string> unexpressed = capabilityVocabulary
            .Where(cap => profile.Permissions.TryGetValue(cap, out SquadPermissionDecision decision) &&
                          decision != SquadPermissionDecision.Deny)
            .ToList();

        if (unexpressed.Count == 0) return null;

        string details =
            $"Capability profile '{agent.CapabilityProfile}' constrains {string.Join(", ", unexpressed)} but Codex agents cannot express capability permissions; the deployed agent's behaviour is governed by the harness default, not the canonical profile.";

        return new SquadDegradationRecord(
            "codex",
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            details);
    }

    private static string EscapeTomlString(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    /// <remarks>
    ///     Backslashes must be escaped before triple quotes: otherwise a body containing
    ///     <c>\"""</c> (or any backslash) produces invalid TOML when quotes are escaped first.
    /// </remarks>
    private static string EscapeTomlMultiline(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
    }
}
