using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Codex's native agent TOML and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Native agent target: canonical agents render as Codex's native agent TOML primitive at
/// <c>.codex/agents/&lt;name&gt;.toml</c>. Codex agent TOML files contain the required
/// top-level fields <c>name</c>, <c>description</c>, and multi-line
/// <c>developer_instructions</c>, plus optional <c>model</c>.
/// </para>
/// <para>
/// Canonical skills render as harness skills at <c>.codex/skills/&lt;name&gt;/SKILL.md</c>
/// with YAML frontmatter containing <c>name</c>, <c>description</c>, and <c>license: MIT</c>.
/// Per the native single-projection rule, profile-declared shared identities suppress their
/// skill projections.
/// </para>
/// <para>
/// Model resolution resolves the agent model from <c>models.yml</c> for target <c>codex</c>,
/// falling back to the profile's default when not <c>inherit</c>.
/// </para>
/// <para>
/// Permission degradation: Codex agent configuration has no frontmatter tool allow-list or
/// capability permission lattice. Non-deny profile decisions are recorded as structured
/// degradations with code <c>permission-not-expressible</c> rather than inventing unenforceable fields.
/// </para>
/// <para>
/// <b>Arbiter block (Req 6.2, 8.1, 8.2, 22.2).</b> When the render request carries an
/// enabled <see cref="SquadArbiterWiring"/> under <see cref="SquadDeploymentScope.Project"/>,
/// the renderer returns a <c>codex</c> owned block for <c>.codex/hooks.json</c> holding
/// <c>PreToolUse</c> and <c>PostToolUse</c> matcher groups: the shared project-wide hook
/// <c>kyber-weave-arbiter hook --harness codex</c> (no <c>--caller</c>, since every
/// shared-file target carries project-wide hooks) matching <c>spawn_agent</c>, with the
/// wiring's timeout. No whole file is emitted; the deployment plan splices the block into
/// whatever the user already has. Under Global scope there is no project configuration to
/// enforce from, so nothing renders.
/// </para>
/// </remarks>
public sealed class CodexRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".codex/agents";
    private const string SkillsDirectory = ".codex/skills";

    /// <summary>
    /// Matches Codex's subagent spawn tool under any harness namespace prefix, and the
    /// bare <c>Agent</c> name for the same tool invoked without qualification.
    /// </summary>
    private const string ArbiterMatcher = "^(Agent|(.*[._:/])?spawn_agent)$";

    private const string ArbiterCommandLine = "kyber-weave-arbiter hook --harness codex";

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

        return baseDirectory.StartsWith(".codex/", StringComparison.Ordinal)
            ? baseDirectory[".codex/".Length..]
            : baseDirectory;
    }

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
        {
            throw new ArgumentException(
                "CodexRenderer was asked to render a target other than Codex.",
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

            SquadDegradationRecord? degradation = BuildDegradationRecord(
                agent,
                source.CapabilityProfiles.Profiles,
                capabilityVocabulary);
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

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], [], BuildArbiterBlocks(request)));
    }

    /// <summary>
    /// Builds Squad's owned <c>.codex/hooks.json</c> block, or nothing when the Arbiter is
    /// not enforced at project scope. The render stays byte-identical without the wiring
    /// because files are untouched either way: the block travels separately for the
    /// deployment plan to splice.
    /// </summary>
    private static IReadOnlyList<SquadRenderedBlock> BuildArbiterBlocks(SquadRenderRequest request)
    {
        if (request.Arbiter is null || !request.Arbiter.Enabled || request.Scope != SquadDeploymentScope.Project)
        {
            return [];
        }

        return [BuildArbiterBlock(request.Arbiter.HookTimeoutSeconds)];
    }

    private static SquadRenderedBlock BuildArbiterBlock(int timeoutSeconds) =>
        new(
            SquadTargetCatalog.GetToken(SquadTarget.Codex),
            SquadHookJsonBlock.RelativePath(SquadHookBlockFormat.Codex),
            SquadHookBlockFormat.Codex,
            [
                new SquadRenderedBlockEntry("PreToolUse", MatcherGroup(timeoutSeconds)),
                new SquadRenderedBlockEntry("PostToolUse", MatcherGroup(timeoutSeconds))
            ]);

    private static JsonObject MatcherGroup(int timeoutSeconds) =>
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
        if (!normalizedBody.EndsWith('\n'))
        {
            normalizedBody += "\n";
        }

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

    private static string? ResolveCodexModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("codex", out string? codexModel))
        {
            return codexModel;
        }

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal)
            ? null
            : profile.Default;
    }

    private static SquadDegradationRecord? BuildDegradationRecord(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlyList<string> capabilityVocabulary)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            return null;
        }

        List<string> unexpressed = capabilityVocabulary
            .Where(cap => profile.Permissions.TryGetValue(cap, out SquadPermissionDecision decision) &&
                          decision != SquadPermissionDecision.Deny)
            .ToList();

        if (unexpressed.Count == 0)
        {
            return null;
        }

        string details =
            $"Capability profile '{agent.CapabilityProfile}' constrains {string.Join(", ", unexpressed)} but Codex agents cannot express capability permissions; the deployed agent's behaviour is governed by the harness default, not the canonical profile.";

        return new SquadDegradationRecord(
            Target: "codex",
            CanonicalIdentity: agent.Name,
            OutputIdentity: agent.Name,
            Code: "permission-not-expressible",
            InstructionDigest: agent.BodyDigest,
            Details: details);
    }

    private static string EscapeTomlString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal)
             .Replace("\r", "\\r", StringComparison.Ordinal)
             .Replace("\n", "\\n", StringComparison.Ordinal)
             .Replace("\t", "\\t", StringComparison.Ordinal);

    /// <remarks>
    /// Backslashes must be escaped before triple quotes: otherwise a body containing
    /// <c>\"""</c> (or any backslash) produces invalid TOML when quotes are escaped first.
    /// </remarks>
    private static string EscapeTomlMultiline(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
}
