using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// The one fake for <see cref="ISquadRenderer"/>: it records every render request and answer, and
/// produces the result the test configured — a canonical catalog by default, or the scripted
/// content, failure, blocks or inner renderer a factory names.
/// </summary>
/// <remarks>
/// The parameterless fake renders the canonical agent and skill catalog, which lifecycle,
/// lowering and packaging tests expect. A test that only needs a specific answer configures one
/// through a factory (<see cref="Empty"/>, <see cref="Failing"/>, <see cref="ScriptedAgents"/>,
/// <see cref="Emitting"/>, <see cref="WithBlocks"/>, <see cref="Wrapping"/>) or scripts it with
/// <see cref="Responding"/>, so it stays a configuration of this fake rather than another local
/// double.
/// </remarks>
public sealed class FakeSquadRenderer : ISquadRenderer
{
    private static readonly IReadOnlyList<string> CanonicalAgents =
    [
        "architect",
        "azure-reader",
        "bug-crusher-investigator",
        "code-reviewer",
        "conductor",
        "csharp-dev",
        "dal-dev",
        "docs-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "pulumi-dev",
        "python-dev",
        "react-dev",
        "research-agent",
        "review-lens",
        "review-triage",
        "sql-database-architect",
        "tauri-dev",
        "test-dev"
    ];

    private static readonly IReadOnlyList<string> CanonicalSkills =
    [
        "app-docs-standard",
        "architecture-decision-record",
        "azure-cli",
        "azure-naming",
        "bug-crusher",
        "code-review",
        "create-pull-request",
        "csharp-dev",
        "csp-security",
        "dal-dev",
        "dp-code-reviewer",
        "github-cli",
        "github-devops",
        "lm-studio-cli",
        "maui-dev",
        "pr-review-fix-comments",
        "product-owner",
        "python-dev",
        "security-review",
        "setup-dev-environment",
        "task-gate",
        "testing"
    ];

    private static readonly IReadOnlyList<string> DistinctBodyCollisionIdentities =
    [
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "python-dev"
    ];

    private readonly Lock _gate = new();
    private readonly List<SquadRenderRequest> _renderRequests = [];
    private readonly List<SquadRenderResult> _results = [];

    /// <summary>A fake that renders nothing and succeeds, advertising every target or the ones named.</summary>
    internal static FakeSquadRenderer Empty(params SquadTarget[] targets) =>
        Responding(static _ => new SquadRenderResult(true, [], [], [], []), targets);

    /// <summary>A fake whose every render reports failure with <paramref name="errors"/>.</summary>
    internal static FakeSquadRenderer Failing(params string[] errors) =>
        Responding(_ => new SquadRenderResult(false, [], [], [], errors));

    /// <summary>
    /// A fake that renders one file per named agent per requested target, at <c>agents/{name}.md</c>
    /// with the bytes <c>canonical {name}</c>, so a release can drop or add an agent.
    /// </summary>
    internal static FakeSquadRenderer ScriptedAgents(params string[] agentNames)
    {
        ArgumentNullException.ThrowIfNull(agentNames);
        return Responding(request =>
        {
            List<SquadDeploymentFile> files = [];
            foreach (SquadTarget target in request.Targets)
            {
                foreach (string agent in agentNames)
                {
                    files.Add(AgentFile(target, agent));
                }
            }

            return new SquadRenderResult(true, files, [], [], []);
        });
    }

    /// <summary>A fake that returns <paramref name="files"/> unchanged, advertising the named targets.</summary>
    internal static FakeSquadRenderer Emitting(
        IReadOnlyList<SquadDeploymentFile> files,
        params SquadTarget[] targets) =>
        Responding(_ => new SquadRenderResult(true, files, [], [], []), targets);

    /// <summary>A fake that returns <paramref name="blocks"/> and no files, advertising <paramref name="target"/>.</summary>
    internal static FakeSquadRenderer WithBlocks(SquadTarget target, params SquadRenderedBlock[] blocks) =>
        Responding(_ => new SquadRenderResult(true, [], [], [], [], blocks), [target]);

    /// <summary>A fake that renders through <paramref name="inner"/> and records what it answered.</summary>
    internal static FakeSquadRenderer Wrapping(ISquadRenderer inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new FakeSquadRenderer { Inner = inner };
    }

    /// <summary>A fake whose answer is whatever <paramref name="respond"/> returns for the request.</summary>
    internal static FakeSquadRenderer Responding(
        Func<SquadRenderRequest, SquadRenderResult> respond,
        params SquadTarget[] targets)
    {
        ArgumentNullException.ThrowIfNull(respond);
        return new FakeSquadRenderer
        {
            Respond = respond,
            NamedTargets = targets.Length == 0 ? null : targets,
        };
    }

    /// <summary>Every render request received, in call order.</summary>
    public IReadOnlyList<SquadRenderRequest> RenderRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _renderRequests];
            }
        }
    }

    /// <summary>How many renders the fake ran.</summary>
    internal int RenderCount => RenderRequests.Count;

    /// <summary>Every answer the fake returned, in call order.</summary>
    internal IReadOnlyList<SquadRenderResult> Results
    {
        get
        {
            lock (_gate)
            {
                return [.. _results];
            }
        }
    }

    /// <summary>Every warning code the renders emitted, in call order, keeping repeats.</summary>
    internal IReadOnlyList<string> WarningCodes =>
        [.. Results.SelectMany(static result => result.Warnings).Select(static warning => warning.Code)];

    /// <summary>
    /// The fake renders whatever targets it is asked for — unlike the real registry, it
    /// carries no coverage gate, so existing lifecycle tests that exercise the full
    /// catalog roster keep working unchanged.
    /// </summary>
    public IReadOnlyCollection<SquadTarget> SupportedTargets => NamedTargets ?? Inner?.SupportedTargets ?? SquadTargetCatalog.All;

    private IReadOnlyCollection<SquadTarget>? NamedTargets { get; init; }

    /// <summary>Builds the answer; when null the catalog render below answers.</summary>
    private Func<SquadRenderRequest, SquadRenderResult>? Respond { get; init; }

    /// <summary>The renderer to delegate to; when set it wins over <see cref="Respond"/> and the catalog.</summary>
    private ISquadRenderer? Inner { get; init; }

    /// <summary>Raised for every request instead of answering.</summary>
    internal Exception? Fault { get; init; }

    public async Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _renderRequests.Add(request);
        }

        if (Fault is not null)
        {
            throw Fault;
        }

        SquadRenderResult result = Inner is not null
            ? await Inner.RenderAsync(request, cancellationToken).ConfigureAwait(false)
            : Respond is not null ? Respond(request) : RenderCatalog(request);

        lock (_gate)
        {
            _results.Add(result);
        }

        return result;
    }

    private static SquadDeploymentFile AgentFile(SquadTarget target, string agent) => new(
        $"agents/{agent}.md",
        Encoding.UTF8.GetBytes($"canonical {agent}\n"),
        SquadTargetCatalog.GetToken(target));

    private static SquadRenderResult RenderCatalog(SquadRenderRequest request)
    {
        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];
        List<string> errors = [];

        foreach (SquadTarget target in request.Targets)
        {
            string targetToken = SquadTargetCatalog.GetToken(target);
            bool isFallback = target is SquadTarget.Antigravity or SquadTarget.Warp;

            if (isFallback)
            {
                // Fallback targets: emit canonical skills + lowered agent role skills
                foreach (string skill in CanonicalSkills)
                {
                    string path = GetTargetSkillFilePath(target, skill);
                    files.Add(new SquadDeploymentFile(path, GetSkillContent(skill), targetToken));
                }

                foreach (string agent in CanonicalAgents)
                {
                    string canonicalBody = GetAgentBody(agent);
                    string digest = ComputeSha256(canonicalBody);

                    if (DistinctBodyCollisionIdentities.Contains(agent, StringComparer.Ordinal))
                    {
                        // Collision identity: lowered to role-<name> skill
                        string outputIdentity = $"role-{agent}";
                        string path = GetTargetSkillFilePath(target, outputIdentity);
                        files.Add(new SquadDeploymentFile(path, GetRoleSkillContent(agent, canonicalBody), targetToken));

                        degradations.Add(new SquadDegradationRecord(
                            Target: targetToken,
                            CanonicalIdentity: agent,
                            OutputIdentity: outputIdentity,
                            Code: "role-skill-fallback",
                            InstructionDigest: digest,
                            Details: "Agent lowered to role-prefixed skill to preserve distinct canonical skill."));
                    }
                    else
                    {
                        // Non-collision agent: lowered to unoccupied <name> skill
                        string path = GetTargetSkillFilePath(target, agent);
                        files.Add(new SquadDeploymentFile(path, GetRoleSkillContent(agent, canonicalBody), targetToken));

                        degradations.Add(new SquadDegradationRecord(
                            Target: targetToken,
                            CanonicalIdentity: agent,
                            OutputIdentity: agent,
                            Code: "role-skill-fallback",
                            InstructionDigest: digest,
                            Details: "Agent lowered to role skill."));
                    }
                }
            }
            else
            {
                // Native target: emit native agents + native skills
                foreach (string agent in CanonicalAgents)
                {
                    string agentPath = GetTargetAgentFilePath(target, agent);
                    string canonicalBody = GetAgentBody(agent);
                    files.Add(new SquadDeploymentFile(agentPath, GetNativeAgentContent(target, agent, canonicalBody), targetToken));
                }

                foreach (string skill in CanonicalSkills)
                {
                    string skillPath = GetTargetSkillFilePath(target, skill);
                    files.Add(new SquadDeploymentFile(skillPath, GetSkillContent(skill), targetToken));
                }
            }
        }

        return new SquadRenderResult(
            Success: errors.Count == 0,
            Files: files,
            Degradations: degradations,
            Warnings: [],
            Errors: errors);
    }

    [SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "SHA256SUMS.txt and the digests it is compared against are lowercase hex by sha256sum convention; ToUpperInvariant would not match the published format.")]
    private static string ComputeSha256(string utf8LfText)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(utf8LfText.Replace("\r\n", "\n", StringComparison.Ordinal));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string GetAgentBody(string name) =>
        $"You are {name}.\nExecute role responsibilities with rigor.\n";

    private static byte[] GetSkillContent(string name) =>
        Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: Canonical skill for {name}.\nlicense: MIT\n---\n# {name}\nSkill instructions.\n");

    private static byte[] GetRoleSkillContent(string name, string body) =>
        Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: Lowered role skill for {name}.\nlicense: MIT\n---\n# {name}\n{body}");

    private static byte[] GetNativeAgentContent(SquadTarget target, string name, string body)
    {
        if (target == SquadTarget.Codex)
        {
            return Encoding.UTF8.GetBytes($"name = \"{name}\"\ndescription = \"Native Codex agent for {name}.\"\ninstructions = \"\"\"\n{body}\"\"\"\n");
        }

        return Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: Native agent for {name}.\n---\n{body}");
    }

    private static string GetTargetAgentFilePath(SquadTarget target, string agentName) => target switch
    {
        SquadTarget.Codex => $".codex/agents/{agentName}.toml",
        SquadTarget.Cursor => $".cursor/agents/{agentName}.md",
        SquadTarget.Claude => $".claude/agents/{agentName}.md",
        SquadTarget.Copilot => $".github/agents/{agentName}.agent.md",
        SquadTarget.OpenCode => $".opencode/agents/{agentName}.md",
        SquadTarget.Kilo => $".kilo/agents/{agentName}.md",
        SquadTarget.Factory => $".factory/droids/{agentName}.md",
        SquadTarget.Pi => $".pi/agents/{agentName}.md",
        _ => throw new ArgumentException($"Target {target} does not have a native agent file path.", nameof(target))
    };

    private static string GetTargetSkillFilePath(SquadTarget target, string skillName) => target switch
    {
        SquadTarget.Codex => $".codex/skills/{skillName}/SKILL.md",
        SquadTarget.Cursor => $".cursor/skills/{skillName}/SKILL.md",
        SquadTarget.Claude => $".claude/skills/{skillName}/SKILL.md",
        SquadTarget.Copilot => $".github/skills/{skillName}/SKILL.md",
        SquadTarget.OpenCode => $".opencode/skills/{skillName}/SKILL.md",
        SquadTarget.Kilo => $".kilo/skills/{skillName}/SKILL.md",
        SquadTarget.Antigravity => $".agents/skills/{skillName}/SKILL.md",
        SquadTarget.Warp => $".warp/skills/{skillName}/SKILL.md",
        SquadTarget.Factory => $".factory/skills/{skillName}/SKILL.md",
        SquadTarget.Pi => $".pi/skills/{skillName}/SKILL.md",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown Squad target.")
    };
}
