using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Parsing;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into OpenCode's native subagent and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Subagent and skill contract verified against OpenCode specifications on 2026-09-14:
/// subagents are stored at <c>.opencode/agents/&lt;name&gt;.md</c> containing Markdown with
/// YAML frontmatter. Required keys are <c>name</c>, <c>description</c>, and <c>mode</c>
/// (<c>primary</c> for conductor, <c>subagent</c> for subagents); optional <c>model</c> resolves
/// from <c>models.yml</c> for harness <c>opencode</c> (omitted when <c>inherit</c> or empty).
/// Skills are stored at <c>.opencode/skills/&lt;name&gt;/SKILL.md</c>.
/// </para>
/// <para>
/// OpenCode's <c>permission</c> frontmatter key is an explicit map, and <b>an omitted key is
/// not a withheld one</b>: agent permissions merge with the global config, whose documented
/// behaviour is that most permissions default to <c>allow</c> (opencode.ai/docs/permissions,
/// read 2026-09-21; <c>external_directory</c> and <c>doom_loop</c> are the stated exceptions).
/// Emitting only the granted keys therefore hands back every canonical <c>deny</c> and
/// <c>ask</c> as an ambient allow — the exact escalation the non-broadening guarantee forbids.
/// This renderer consequently pins <b>every</b> key in the taxonomy explicitly, writing
/// <c>deny</c> wherever the lattice does not grant. Only <c>allow</c> grants a permission;
/// <c>ask</c> and <c>deny</c> both withhold. <c>ask</c> is recorded as <see cref="SquadDegradationRecord"/>
/// with code <c>safety-narrowed</c> because OpenCode subagents do not support interactive
/// per-capability confirmation gates.
/// </para>
/// <para>
/// Base ungoverned permissions on every agent: <c>todowrite</c>, <c>skill</c>, <c>question</c>.
/// The first two write nothing and execute nothing; <c>question</c> asks the operator rather
/// than touching the machine, so none of the three can broaden a canonical decision.
/// <c>external_directory</c> and <c>doom_loop</c> map to no capability and are therefore always
/// denied. <c>lsp</c> is governed by <c>filesystem.read</c>: language-server introspection is
/// reading code. Semantic capabilities lower
/// onto OpenCode's permission taxonomy: <c>filesystem.read</c> -&gt; <c>read</c>, <c>filesystem.search</c> -&gt;
/// <c>grep</c>, <c>glob</c>, <c>list</c>, <c>filesystem.write</c> -&gt; <c>edit</c>, <c>process.execute</c> -&gt; <c>bash</c>,
/// <c>network.read</c> -&gt; <c>webfetch</c>, <c>websearch</c>, and <c>delegate</c> -&gt; pattern-based <c>task</c> rules.
/// Server-scoped MCP mapping for the declared <c>kyber-weave</c> server grants <c>kyber-weave_*</c> to agents
/// with <c>filesystem.read: allow</c>, excluding pure orchestrators and shared identities.
/// <c>network.publish</c> has no native tool and is withheld, emitting <c>permission-not-expressible</c>.
/// </para>
/// <para>
/// Skills carry <c>name</c>, single-line <c>description</c>, and <c>license: MIT</c>.
/// Profile-declared shared identities suppress their skill projections per the native
/// single-projection rule.
/// </para>
/// </remarks>
public sealed class OpenCodeRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".opencode/agents";
    private const string SkillsDirectory = ".opencode/skills";

    private const string KyberWeaveMcpPermission = "kyber-weave_*";

    private static readonly string[] BaseUngovernedPermissions = ["todowrite", "skill", "question"];

    /// <summary>
    /// Lowers the semantic capability vocabulary onto OpenCode's built-in permission names.
    /// <c>network.publish</c> is absent deliberately: no built-in publish tool exists.
    /// <c>delegate</c> is handled separately to emit pattern-based <c>task</c> rules.
    /// </summary>
    private static readonly (string Capability, string[] Permissions)[] CapabilityPermissions =
    [
        ("filesystem.read", ["read", "lsp"]),
        ("filesystem.search", ["grep", "glob", "list"]),
        ("filesystem.write", ["edit"]),
        ("process.execute", ["bash"]),
        ("network.read", ["webfetch", "websearch"]),
    ];

    /// <summary>
    /// Emission order, fixed so a rendered agent file is byte-stable regardless of how the
    /// profile's permissions enumerate.
    /// </summary>
    /// <remarks>
    /// Every key OpenCode documents is listed, because a key this renderer leaves out is a key
    /// the global config grants by default. <c>external_directory</c> and <c>doom_loop</c> have
    /// no canonical capability and so are never granted, but they are still emitted: relying on
    /// OpenCode's own default for them would make this map's safety depend on a default the
    /// operator can change.
    /// </remarks>
    private static readonly string[] PermissionOrder =
    [
        "todowrite",
        "skill",
        "question",
        "read",
        "lsp",
        "grep",
        "glob",
        "list",
        "edit",
        "bash",
        "webfetch",
        "websearch",
        "external_directory",
        "doom_loop",
        KyberWeaveMcpPermission,
        "task"
    ];

    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    /// <summary>
    /// YamlDotNet does not document <see cref="ISerializer"/> as thread-safe, and the
    /// registry may dispatch renderers concurrently; serialization takes this lock so a
    /// shared static instance cannot interleave emitter state.
    /// </summary>
    private static readonly object SerializerLock = new();

    /// <summary>
    /// Under <c>Scope: Global</c> the physical root already is OpenCode's global directory
    /// (R18), so the relative path drops the project-scope <c>.opencode/</c> wrapper and
    /// emits the bare <c>agents/</c> / <c>skills/</c> form directly.
    /// </summary>
    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        if (scope == SquadDeploymentScope.Project)
        {
            return baseDirectory;
        }

        return baseDirectory.StartsWith(".opencode/", StringComparison.Ordinal)
            ? baseDirectory[".opencode/".Length..]
            : baseDirectory;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.OpenCode];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.OpenCode))
        {
            throw new ArgumentException(
                "OpenCodeRenderer was asked to render a target other than OpenCode.",
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

        foreach (SquadAgent agent in source.Agents)
        {
            SquadDeploymentFile principal = RenderAgent(
                agent,
                source.ModelProfiles.Profiles,
                source.CapabilityProfiles.Profiles,
                sharedIdentities,
                request.Scope);
            files.Add(principal);
            SquadResourceProjection.Append(files, principal, agent.Resources);

            degradations.AddRange(BuildDegradationRecords(agent, source.CapabilityProfiles.Profiles));
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

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlySet<string> sharedIdentities,
        SquadDeploymentScope scope)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = agent.Description,
            ["mode"] = agent.Invocation == SquadInvocation.Primary ? "primary" : "subagent"
        };

        string? model = ResolveOpenCodeModel(agent, modelProfiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        // Always emit permission map: omitting it inherits ambient tool access (widening).
        frontmatter["permission"] = ResolvePermissions(agent, capabilityProfiles, sharedIdentities);

        string content;
        lock (SerializerLock)
        {
            content = SquadMarkdownDocument.Compose(YamlSerializer, frontmatter, agent.InstructionBody);
        }

        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, scope);
        return new SquadDeploymentFile(
            $"{agentsDir}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            SquadTargetCatalog.GetToken(SquadTarget.OpenCode));
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
            SquadTargetCatalog.GetToken(SquadTarget.OpenCode));
    }

    private static string? ResolveOpenCodeModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("opencode", out string? opencodeModel))
        {
            // Omit model key when override is inherit or empty.
            return string.Equals(opencodeModel, "inherit", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(opencodeModel)
                ? null
                : opencodeModel;
        }

        return string.Equals(profile.Default, "inherit", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(profile.Default)
            ? null
            : profile.Default;
    }

    /// <summary>
    /// Lowers a capability profile onto OpenCode's closed permission map. Only
    /// <see cref="SquadPermissionDecision.Allow"/> grants: <c>ask</c> and <c>deny</c> both
    /// withhold, which keeps the lowering non-broadening by construction.
    /// </summary>
    private static Dictionary<string, object> ResolvePermissions(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles,
        IReadOnlySet<string> sharedIdentities)
    {
        HashSet<string> granted = new(BaseUngovernedPermissions, StringComparer.Ordinal);
        object? taskPermissionValue = null;

        // An unresolvable profile grants nothing beyond the ungoverned permissions. Falling back
        // to "grant everything" here would turn a source error into silent widening.
        if (capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            foreach ((string capability, string[] permissions) in CapabilityPermissions)
            {
                if (profile.Permissions.TryGetValue(capability, out SquadPermissionDecision decision) &&
                    decision == SquadPermissionDecision.Allow)
                {
                    foreach (string permission in permissions)
                    {
                        granted.Add(permission);
                    }
                }
            }

            bool isPureOrchestrator = string.Equals(
                agent.CapabilityProfile,
                "orchestrator",
                StringComparison.Ordinal);

            if (!isPureOrchestrator &&
                !sharedIdentities.Contains(agent.Name) &&
                profile.Permissions.TryGetValue("filesystem.read", out SquadPermissionDecision readDecision) &&
                readDecision == SquadPermissionDecision.Allow)
            {
                granted.Add(KyberWeaveMcpPermission);
            }

            if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
                delegateDecision == SquadPermissionDecision.Allow)
            {
                if (agent.DelegatesTo.Count > 0)
                {
                    Dictionary<string, string> delegatesMap = new(StringComparer.Ordinal);
                    foreach (string target in agent.DelegatesTo.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        delegatesMap[target] = "allow";
                    }

                    taskPermissionValue = delegatesMap;
                }
                else
                {
                    taskPermissionValue = "allow";
                }
            }
        }

        Dictionary<string, object> ordered = new(StringComparer.Ordinal);
        foreach (string key in PermissionOrder)
        {
            if (string.Equals(key, "task", StringComparison.Ordinal))
            {
                // A withheld delegate is an explicit deny, not an absent key: absent means the
                // global default decides, and the global default is allow.
                ordered["task"] = taskPermissionValue ?? "deny";
                continue;
            }

            ordered[key] = granted.Contains(key) ? "allow" : "deny";
        }

        return ordered;
    }

    private static IEnumerable<SquadDegradationRecord> BuildDegradationRecords(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadCapabilityProfile> capabilityProfiles)
    {
        if (!capabilityProfiles.TryGetValue(agent.CapabilityProfile, out SquadCapabilityProfile? profile))
        {
            yield break;
        }

        // 'deny' needs no record: the rendered allowlist withholds the tool. Only 'ask'
        // loses meaning — OpenCode has no per-capability confirmation gate on subagents.
        List<string> narrowed = profile.Permissions
            .Where(pair => pair.Value == SquadPermissionDecision.Ask)
            .Select(pair => pair.Key)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        if (narrowed.Count > 0)
        {
            yield return new SquadDegradationRecord(
                Target: SquadTargetCatalog.GetToken(SquadTarget.OpenCode),
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "safety-narrowed",
                InstructionDigest: agent.BodyDigest,
                Details: $"Capability profile '{agent.CapabilityProfile}' requires 'ask' for " +
                    $"{string.Join(", ", narrowed)}. OpenCode's permissions cannot prompt " +
                    "for per-capability confirmation, so these narrow to 'deny' and the " +
                    "corresponding permissions are withheld.");
        }

        List<string> notExpressibleDetails = [];

        if (profile.Permissions.TryGetValue("network.publish", out SquadPermissionDecision publishDecision) &&
            publishDecision != SquadPermissionDecision.Deny)
        {
            notExpressibleDetails.Add(
                "OpenCode has no native network publishing tool; tool withheld for capability 'network.publish'.");
        }

        if (profile.Permissions.TryGetValue("delegate", out SquadPermissionDecision delegateDecision) &&
            delegateDecision == SquadPermissionDecision.Allow &&
            agent.DelegatesTo.Count > 0)
        {
            notExpressibleDetails.Add(
                "OpenCode ignores task(roster) parentheses when this definition " +
                "runs as a nested subagent; the permitted delegation roster is not enforced " +
                "for nested task spawns. Roster: " +
                string.Join(", ", agent.DelegatesTo) + ".");
        }

        if (notExpressibleDetails.Count > 0)
        {
            yield return new SquadDegradationRecord(
                Target: SquadTargetCatalog.GetToken(SquadTarget.OpenCode),
                CanonicalIdentity: agent.Name,
                OutputIdentity: agent.Name,
                Code: "permission-not-expressible",
                InstructionDigest: agent.BodyDigest,
                Details: string.Join(" ", notExpressibleDetails));
        }

        SquadPermissionDecision executeDecision = profile.Permissions.TryGetValue("process.execute", out SquadPermissionDecision exec)
            ? exec
            : SquadPermissionDecision.Deny;
        SquadPermissionDecision writeDecision = profile.Permissions.TryGetValue("filesystem.write", out SquadPermissionDecision write)
            ? write
            : SquadPermissionDecision.Deny;

        SquadDegradationRecord? notIsolable = CapabilityDegradations.BuildCapabilityNotIsolable(
            targetToken: SquadTargetCatalog.GetToken(SquadTarget.OpenCode),
            canonicalIdentity: agent.Name,
            outputIdentity: agent.Name,
            instructionDigest: agent.BodyDigest,
            executeDecision: executeDecision,
            writeDecision: writeDecision,
            grantedShellTools: ["bash"],
            withheldWriteTools: ["edit"]);

        if (notIsolable is not null)
        {
            yield return notIsolable;
        }
    }
}
