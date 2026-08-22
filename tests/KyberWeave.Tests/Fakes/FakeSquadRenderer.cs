using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Deterministic in-memory fake implementing <see cref="ISquadRenderer"/> for testing
/// lifecycle orchestration, lowering, structured degradation, and packaging contracts.
/// </summary>
public sealed class FakeSquadRenderer : ISquadRenderer
{
    public static readonly IReadOnlyList<string> CanonicalAgents =
    [
        "architect",
        "architect-v3",
        "azure-reader",
        "bug-crusher-investigator",
        "code-reviewer",
        "conductor",
        "conductor-v3",
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
        "sql-database-architect",
        "tauri-dev",
        "test-dev"
    ];

    public static readonly IReadOnlyList<string> CanonicalSkills =
    [
        "app-docs-standard",
        "architecture-decision-record",
        "azure-cli",
        "azure-naming",
        "bug-crusher",
        "code-review",
        "conductor",
        "conductor-v3",
        "create-pull-request",
        "create-pull-request-github",
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
        "second-brain",
        "security-review",
        "setup-dev-environment",
        "test-dev"
    ];

    public static readonly IReadOnlyList<string> SharedConductorIdentities =
    [
        "conductor",
        "conductor-v3"
    ];

    public static readonly IReadOnlyList<string> DistinctBodyCollisionIdentities =
    [
        "csharp-dev",
        "dal-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "python-dev",
        "test-dev"
    ];

    private readonly List<SquadRenderRequest> _renderRequests = [];

    private Func<SquadRenderRequest, SquadRenderResult>? _renderHandler;
    private string? _renderFailure;
    private bool _simulatePermissionWidening;
    private string? _permissionWideningAgent;
    private bool _simulateMissingDigest;
    private string? _missingDigestAgent;
    private bool _simulateCorruptedDigest;
    private string? _corruptedDigestAgent;
    private bool _simulateDuplicateProjection;
    private string? _duplicateProjectionTarget;
    private string? _duplicateProjectionIdentity;
    private readonly List<SquadRenderWarning> _warnings = [];

    public IReadOnlyList<SquadRenderRequest> RenderRequests => _renderRequests;

    /// <summary>
    /// The fake renders whatever targets it is asked for — unlike the real registry, it
    /// carries no coverage gate, so existing lifecycle tests that exercise the full
    /// ten-target roster keep working unchanged.
    /// </summary>
    public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = SquadTargetCatalog.All;

    public FakeSquadRenderer WithRenderHandler(Func<SquadRenderRequest, SquadRenderResult> handler)
    {
        _renderHandler = handler;
        return this;
    }

    public FakeSquadRenderer WithRenderFailure(string errorMessage)
    {
        _renderFailure = errorMessage;
        return this;
    }

    public FakeSquadRenderer WithPermissionWidening(string agent, string capability)
    {
        _simulatePermissionWidening = true;
        _permissionWideningAgent = agent;
        return this;
    }

    public FakeSquadRenderer WithMissingDigest(string agent)
    {
        _simulateMissingDigest = true;
        _missingDigestAgent = agent;
        return this;
    }

    public FakeSquadRenderer WithCorruptedDigest(string agent)
    {
        _simulateCorruptedDigest = true;
        _corruptedDigestAgent = agent;
        return this;
    }

    public FakeSquadRenderer WithDuplicateProjection(string target, string identity)
    {
        _simulateDuplicateProjection = true;
        _duplicateProjectionTarget = target;
        _duplicateProjectionIdentity = identity;
        return this;
    }

    public FakeSquadRenderer WithWarning(string code, string message, string? target = null)
    {
        _warnings.Add(new SquadRenderWarning(code, message, target));
        return this;
    }

    public Task<SquadRenderResult> RenderAsync(
        SquadRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _renderRequests.Add(request);

        if (_renderFailure is not null)
        {
            return Task.FromResult(new SquadRenderResult(
                Success: false,
                Files: [],
                Degradations: [],
                Warnings: _warnings,
                Errors: [_renderFailure]));
        }

        if (_renderHandler is not null)
        {
            return Task.FromResult(_renderHandler(request));
        }

        if (_simulatePermissionWidening)
        {
            return Task.FromResult(new SquadRenderResult(
                Success: false,
                Files: [],
                Degradations: [],
                Warnings: _warnings,
                Errors: [$"Permission widening detected for agent '{_permissionWideningAgent}': 'deny' widened to 'allow'."]));
        }

        List<SquadDeploymentFile> files = [];
        List<SquadDegradationRecord> degradations = [];
        List<string> errors = [];

        foreach (SquadTarget target in request.Targets)
        {
            string targetToken = SquadTargetCatalog.GetToken(target);
            bool isFallback = target is SquadTarget.Gemini or SquadTarget.Antigravity or SquadTarget.Warp;

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

                    if (_simulateMissingDigest && string.Equals(agent, _missingDigestAgent, StringComparison.Ordinal))
                    {
                        digest = string.Empty;
                    }
                    else if (_simulateCorruptedDigest && string.Equals(agent, _corruptedDigestAgent, StringComparison.Ordinal))
                    {
                        digest = "0000000000000000000000000000000000000000000000000000000000000000";
                    }

                    if (SharedConductorIdentities.Contains(agent, StringComparer.Ordinal))
                    {
                        // Conductor shared identity: already emitted as canonical skill; single-projection rule applies.
                        degradations.Add(new SquadDegradationRecord(
                            Target: targetToken,
                            CanonicalIdentity: agent,
                            OutputIdentity: agent,
                            Code: "role-skill-fallback",
                            InstructionDigest: digest,
                            Details: "Reused identical shared canonical skill; agent primitive lowered to skill."));
                    }
                    else if (DistinctBodyCollisionIdentities.Contains(agent, StringComparer.Ordinal))
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
                    // Conductor single-projection rule on native targets:
                    // conductor and conductor-v3 are native primary agents; suppress duplicate skill projection.
                    if (SharedConductorIdentities.Contains(skill, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    string skillPath = GetTargetSkillFilePath(target, skill);
                    files.Add(new SquadDeploymentFile(skillPath, GetSkillContent(skill), targetToken));
                }

                // If duplicate projection is simulated:
                if (_simulateDuplicateProjection &&
                    (string.IsNullOrEmpty(_duplicateProjectionTarget) || string.Equals(_duplicateProjectionTarget, targetToken, StringComparison.OrdinalIgnoreCase)))
                {
                    string dupIdentity = _duplicateProjectionIdentity ?? "conductor";
                    string dupSkillPath = GetTargetSkillFilePath(target, dupIdentity);
                    files.Add(new SquadDeploymentFile(dupSkillPath, GetSkillContent(dupIdentity), targetToken));
                }
            }
        }

        return Task.FromResult(new SquadRenderResult(
            Success: errors.Count == 0,
            Files: files,
            Degradations: degradations,
            Warnings: _warnings,
            Errors: errors));
    }

    public static string ComputeSha256(string utf8LfText)
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

    public static string GetTargetAgentFilePath(SquadTarget target, string agentName) => target switch
    {
        SquadTarget.Codex => $".codex/agents/{agentName}.toml",
        SquadTarget.Cursor => $".cursor/agents/{agentName}.md",
        SquadTarget.Claude => $".claude/agents/{agentName}.md",
        SquadTarget.Copilot => $".github/agents/{agentName}.agent.md",
        SquadTarget.OpenCode => $".opencode/agents/{agentName}.md",
        SquadTarget.Kilo => $".kilo/agents/{agentName}.md",
        SquadTarget.Factory => $".factory/agents/{agentName}.md",
        _ => throw new ArgumentException($"Target {target} does not have a native agent file path.", nameof(target))
    };

    public static string GetTargetSkillFilePath(SquadTarget target, string skillName) => target switch
    {
        SquadTarget.Codex => $".codex/skills/{skillName}/SKILL.md",
        SquadTarget.Cursor => $".cursor/skills/{skillName}/SKILL.md",
        SquadTarget.Claude => $".claude/skills/{skillName}/SKILL.md",
        SquadTarget.Copilot => $".github/skills/{skillName}/SKILL.md",
        SquadTarget.OpenCode => $".opencode/skills/{skillName}/SKILL.md",
        SquadTarget.Kilo => $".kilo/skills/{skillName}/SKILL.md",
        SquadTarget.Gemini => $".gemini/skills/{skillName}/SKILL.md",
        SquadTarget.Antigravity => $".agent/skills/{skillName}/SKILL.md",
        SquadTarget.Warp => $".warp/skills/{skillName}/SKILL.md",
        SquadTarget.Factory => $".factory/skills/{skillName}/SKILL.md",
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unknown Squad target.")
    };
}
