using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using YamlDotNet.Serialization;

namespace KyberWeave.Core.Squad.Rendering;

/// <summary>
/// Renders canonical Squad source into Kilo's native agent and skill file formats.
/// </summary>
/// <remarks>
/// <para>
/// Native agent target: canonical agents render as Markdown with YAML frontmatter at
/// <c>.kilo/agents/&lt;name&gt;.md</c>. Required keys are <c>name</c>, <c>description</c>, and <c>mode</c>
/// (<c>primary</c> or <c>subagent</c>); optional <c>model</c> resolves from <c>models.yml</c> for harness <c>kilo</c>
/// (omitted when <c>inherit</c> or empty).
/// </para>
/// <para>
/// Canonical skills render as harness skills at <c>.kilo/skills/&lt;name&gt;/SKILL.md</c>
/// with YAML frontmatter containing <c>name</c>, single-line <c>description</c>, and <c>license: MIT</c>.
/// Per the native single-projection rule, profile-declared shared identities suppress their
/// skill projections.
/// </para>
/// <para>
/// <b>Kilo does have a permission lattice, and it is an approval lattice rather than a visibility
/// allow-list.</b> Agent frontmatter carries a first-class <c>permission</c> map whose values are
/// <c>ask</c>, <c>allow</c>, <c>deny</c>, or null; the deprecated <c>tools: {name: boolean}</c> form
/// is folded into it during the loader's normalization, and an unrecognised frontmatter key is
/// tolerated rather than rejected (verified against the published <c>@kilocode/cli</c> 7.8.18,
/// <c>Kilo-Org/kilocode</c> tag <c>v7.8.18</c> — the loader at <c>packages/opencode/src/config/agent.ts</c>
/// globs <c>{agent,agents}/**/*.md</c>, merges every frontmatter key into the agent config, and
/// validates the result against the agent schema). <c>allow</c> means the call runs without an ask
/// prompt; it does not hide another tool, and only a <c>deny</c> rule whose pattern is <c>*</c>
/// removes one from the model's view. The entry this renderer emits is therefore a pre-approval,
/// not isolation: it removes a prompt — and, because a headless subagent's ask fails closed, a
/// likely failure — from tools a Squad subagent is entitled to call. It restricts nothing.
/// </para>
/// <para>
/// <b>MCP tools are gated by that same map, under a namespaced key.</b> The key is
/// <c>{server}_{tool}</c>, with the server name sanitized only for characters outside
/// <c>[a-zA-Z0-9_-]</c> — so the hyphen in <c>kyber-weave</c> survives, and <c>kyber-weave_*</c> is
/// the server wildcard (a <c>kyber_weave_*</c> spelling would parse cleanly and match nothing). An
/// agent the universal MCP predicate entitles to this server emits that wildcard with
/// <c>allow</c>, the pure orchestrator included, because the server carries
/// <c>agent_coding_standards</c>, the tool that hands an agent the standards it is held to
/// (ADR 0050). An agent the predicate does not entitle emits no entry at all: for a headless
/// subagent, absence of <c>allow</c> is the withholding, and a <c>deny</c> on a server the operator
/// may never have configured would assert more than canonical source does — the capability lattice
/// declares no MCP capability to derive one from.
/// </para>
/// <para>
/// <b>The server half of the wiring stays the operator's.</b> An agent file cannot add an MCP
/// server: the server itself is session-level configuration (the <c>mcp</c> key in
/// <c>kilo.jsonc</c>), and Squad writes no harness settings file. A grant with no configured server
/// reaches nothing, which is why this renderer emits the permission entry and stops there rather
/// than pretending the entry supplies the connection.
/// </para>
/// <para>
/// <b>Capability permissions remain unexpressed, and that gap is recorded.</b> The
/// <c>permission</c> map is keyed by tool name — built-in tools by their own names, MCP tools as
/// <c>{server}_{tool}</c> — so the canonical capability vocabulary (<c>filesystem.read</c>,
/// <c>process.execute</c>, ...) is not a key Kilo reads. Translating the lattice onto Kilo's tool
/// keys is a separate decision this renderer does not take, so every capability a profile
/// constrains is recorded as a structured degradation under code
/// <c>permission-not-expressible</c>, deny decisions included, rather than being silently dropped
/// (preventing capability widening).
/// </para>
/// <para>
/// <b>Agent resources are projected beside their principal with a discovery-guard
/// extension.</b> Kilo's agent loader scans every config root for
/// <c>{agent,agents}/**/*.md</c> files with dotfiles and symlinks followed, and derives
/// an entry name from each match's relative path (verified against
/// <c>Kilo-Org/kilocode</c>, <c>packages/opencode/src/config/agent.ts</c> — the glob
/// inherited verbatim from OpenCode v1.18.26 — on 2026-10-19), so projecting an
/// agent's resource closure beside its principal with a <c>.md</c> extension would
/// register every reference file as a phantom agent. Agent-owned <c>.md</c> resources
/// therefore deploy with a <c>.markdown</c> extension — invisible to the
/// <c>*.md</c>-only glob while staying co-located with, and readable beside, their owning
/// agent — and the owner's authored links are rewritten <c>.md</c> to <c>.markdown</c>
/// via the shared closure-driven rewriter. The deviation from the repository's "authored
/// links resolve verbatim" rule is recorded as <c>resource-links-rewritten</c> so it is
/// visible in the receipt. Skill resources keep the ordinary
/// <see cref="SquadResourceProjection"/> treatment: skill discovery only admits
/// <c>SKILL.md</c> one level deep and never mistakes them for agents.
/// </para>
/// </remarks>
public sealed class KiloRenderer : ISquadRenderer
{
    private const string AgentsDirectory = ".kilo/agents";
    private const string SkillsDirectory = ".kilo/skills";

    /// <summary>
    /// The <c>permission</c> entry naming the whole <c>kyber-weave</c> MCP surface: Kilo gates MCP
    /// tools under the same permission map as built-in ones, keyed <c>{server}_{tool}</c> with the
    /// server name sanitized only for characters outside <c>[a-zA-Z0-9_-]</c>. The hyphen is
    /// load-bearing — an underscore spelling would parse and match nothing — so the key is a
    /// constant rather than assembled at the call site, and a test pins the emitted bytes.
    /// </summary>
    private const string KyberWeaveMcpPermission = "kyber-weave_*";

    private static readonly ThreadLocal<ISerializer> YamlSerializer = new(() => new SerializerBuilder().Build());

    /// <summary>
    /// Under <c>Scope: Global</c> the physical root already is Kilo's global directory
    /// (<c>~/.config/kilo</c>), so the relative path drops the project-scope <c>.kilo/</c>
    /// wrapper and emits the bare <c>agents/</c> / <c>skills/</c> form directly.
    /// </summary>
    private static string ResolvePrefixedDirectory(string baseDirectory, SquadDeploymentScope scope)
    {
        if (scope == SquadDeploymentScope.Project)
        {
            return baseDirectory;
        }

        return baseDirectory.StartsWith(".kilo/", StringComparison.Ordinal)
            ? baseDirectory[".kilo/".Length..]
            : baseDirectory;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Kilo];

    /// <inheritdoc />
    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Targets.Any(target => target != SquadTarget.Kilo))
        {
            throw new ArgumentException(
                "KiloRenderer was asked to render a target other than Kilo.",
                nameof(request));
        }

        if (request.Targets.Count == 0)
        {
            return Task.FromResult(new SquadRenderResult(true, [], [], [], []));
        }

        SquadSource source = SquadRenderSource.Load(request, SquadTarget.Kilo);
        HashSet<string> sharedIdentities = source.FallbackProfiles.Profiles.Values
            .SelectMany(profile => profile.SharedIdentities)
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
            // kyber-weave reaches every read-entitled agent, the pure orchestrator included: it
            // carries agent_coding_standards, and the role that assigns work is the role telling
            // its delegates which standards they are held to (ADR 0050). The predicate is shared so
            // this target cannot drift from the others' answer.
            bool grantsKyberWeaveMcp = CapabilityDegradations.GrantsUniversalMcpServers(
                agent.CapabilityProfile,
                source.CapabilityProfiles.Profiles);

            SquadDeploymentFile principal = RenderAgent(
                agent,
                source.ModelProfiles.Profiles,
                grantsKyberWeaveMcp,
                request.Scope);
            files.Add(principal);
            SquadAgentResourceDiscoveryGuard.Append(
                files,
                agent,
                principal,
                agent.Resources,
                degradations,
                SquadTargetCatalog.GetToken(SquadTarget.Kilo),
                "{agent,agents}/**/*.md");

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

        return Task.FromResult(new SquadRenderResult(true, files, degradations, [], []));
    }

    private static SquadDeploymentFile RenderAgent(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles,
        bool grantsKyberWeaveMcp,
        SquadDeploymentScope scope)
    {
        Dictionary<string, object?> frontmatter = new(StringComparer.Ordinal)
        {
            ["name"] = agent.Name,
            ["description"] = agent.Description,
            ["mode"] = agent.Invocation == SquadInvocation.Primary ? "primary" : "subagent"
        };

        string? model = ResolveKiloModel(agent, modelProfiles);
        if (model is not null)
        {
            frontmatter["model"] = model;
        }

        // The key is a constant rather than assembled here: Kilo sanitizes the server name only
        // for characters outside [a-zA-Z0-9_-], so "kyber-weave" keeps its hyphen and this is the
        // wildcard Kilo's matcher resolves for kyber-weave_docs_status. An unentitled agent emits
        // no entry: absence of "allow" withholds for a headless subagent (its ask fails closed),
        // and a "deny" here would out-assert a canonical source that declares no MCP reach.
        if (grantsKyberWeaveMcp)
        {
            frontmatter["permission"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [KyberWeaveMcpPermission] = "allow"
            };
        }

        // The composed agent file carries discovery-guarded links: an in-closure .md link
        // is rewritten to .markdown so it still resolves after the guard renames the
        // projection. Frontmatter is unchanged.
        string body = SquadAgentResourceDiscoveryGuard.RewriteBody(agent);

        string content = SquadMarkdownDocument.Compose(
            YamlSerializer.Value!,
            frontmatter,
            body);

        string agentsDir = ResolvePrefixedDirectory(AgentsDirectory, scope);
        return new SquadDeploymentFile(
            $"{agentsDir}/{agent.Name}.md",
            Encoding.UTF8.GetBytes(content),
            "kilo");
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

        string content = SquadMarkdownDocument.Compose(
            YamlSerializer.Value!,
            frontmatter,
            skill.InstructionBody);

        string skillsDir = ResolvePrefixedDirectory(SkillsDirectory, scope);
        return new SquadDeploymentFile(
            $"{skillsDir}/{skill.Name}/SKILL.md",
            Encoding.UTF8.GetBytes(content),
            "kilo");
    }

    private static string? ResolveKiloModel(
        SquadAgent agent,
        IReadOnlyDictionary<string, SquadModelProfile> modelProfiles)
    {
        if (!modelProfiles.TryGetValue(agent.ModelProfile, out SquadModelProfile? profile))
        {
            return null;
        }

        if (profile.HarnessModels.TryGetValue("kilo", out string? kiloModel))
        {
            return string.Equals(kiloModel, "inherit", StringComparison.Ordinal)
                ? null
                : kiloModel;
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

        // Kilo's permission map is keyed by tool name (built-in tools by their own names, MCP tools
        // as {server}_{tool}), so the capability vocabulary is not a key it reads and this renderer
        // translates no capability onto one. Every configured decision — allow, ask, deny — is
        // therefore unexpressed at the harness boundary; recording the deny constraints in
        // particular ensures canonical restrictions are not silently dropped (preventing capability
        // widening). No record names MCP servers the agent was denied: the entitlement decides only
        // whether an approval is emitted, and an agent that is not entitled gets no entry rather
        // than a deny, so there is no withheld-server sentence to record.
        List<string> unexpressed = capabilityVocabulary
            .Where(cap => profile.Permissions.ContainsKey(cap))
            .ToList();

        if (unexpressed.Count == 0)
        {
            return null;
        }

        string details =
            $"Capability profile '{agent.CapabilityProfile}' constrains {string.Join(", ", unexpressed)} but Kilo's permission map is keyed by tool name, not by capability, so those decisions are unexpressed; the deployed agent's behaviour is governed by the harness default, not the canonical profile.";

        return new SquadDegradationRecord(
            "kilo",
            agent.Name,
            agent.Name,
            "permission-not-expressible",
            agent.BodyDigest,
            details);
    }
}
