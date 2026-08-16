using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Packaging;

namespace KyberWeave.Tests.Fakes;

/// <summary>
/// Deterministic in-memory fake implementing <see cref="IApmRunner"/> for testing APM compiler,
/// lowering, structured degradation, and packaging contracts.
/// </summary>
public sealed class FakeApmRunner : IApmRunner
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
        "dal-dev",
        "docs-dev",
        "dotnet-dev",
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
        "csp-security",
        "dal-dev",
        "dotnet-dev",
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
        "dal-dev",
        "dotnet-dev",
        "github-devops",
        "maui-dev",
        "product-owner",
        "python-dev",
        "test-dev"
    ];

    private readonly List<ApmRenderRequest> _renderRequests = [];
    private readonly List<ApmPackRequest> _packRequests = [];

    private Func<ApmRenderRequest, ApmRenderResult>? _renderHandler;
    private Func<ApmPackRequest, ApmPackResult>? _packHandler;
    private string? _renderFailure;
    private string? _packFailure;
    private bool _simulatePermissionWidening;
    private string? _permissionWideningAgent;
    private bool _simulateMissingDigest;
    private string? _missingDigestAgent;
    private bool _simulateCorruptedDigest;
    private string? _corruptedDigestAgent;
    private bool _simulateDuplicateProjection;
    private string? _duplicateProjectionTarget;
    private string? _duplicateProjectionIdentity;
    private bool _includeAgentsInPluginManifest;
    private string? _customPluginManifestJson;
    private readonly List<ApmWarning> _warnings = [];

    public IReadOnlyList<ApmRenderRequest> RenderRequests => _renderRequests;
    public IReadOnlyList<ApmPackRequest> PackRequests => _packRequests;

    public FakeApmRunner WithRenderHandler(Func<ApmRenderRequest, ApmRenderResult> handler)
    {
        _renderHandler = handler;
        return this;
    }

    public FakeApmRunner WithPackHandler(Func<ApmPackRequest, ApmPackResult> handler)
    {
        _packHandler = handler;
        return this;
    }

    public FakeApmRunner WithRenderFailure(string errorMessage)
    {
        _renderFailure = errorMessage;
        return this;
    }

    public FakeApmRunner WithPackFailure(string errorMessage)
    {
        _packFailure = errorMessage;
        return this;
    }

    public FakeApmRunner WithPermissionWidening(string agent, string capability)
    {
        _simulatePermissionWidening = true;
        _permissionWideningAgent = agent;
        return this;
    }

    public FakeApmRunner WithMissingDigest(string agent)
    {
        _simulateMissingDigest = true;
        _missingDigestAgent = agent;
        return this;
    }

    public FakeApmRunner WithCorruptedDigest(string agent)
    {
        _simulateCorruptedDigest = true;
        _corruptedDigestAgent = agent;
        return this;
    }

    public FakeApmRunner WithDuplicateProjection(string target, string identity)
    {
        _simulateDuplicateProjection = true;
        _duplicateProjectionTarget = target;
        _duplicateProjectionIdentity = identity;
        return this;
    }

    public FakeApmRunner WithAgentsInPluginManifest(bool include = true)
    {
        _includeAgentsInPluginManifest = include;
        return this;
    }

    public FakeApmRunner WithCustomPluginManifestJson(string json)
    {
        _customPluginManifestJson = json;
        return this;
    }

    public FakeApmRunner WithWarning(string code, string message, string? target = null)
    {
        _warnings.Add(new ApmWarning(code, message, target));
        return this;
    }

    public Task<ApmRenderResult> RenderAsync(
        ApmRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _renderRequests.Add(request);

        if (_renderFailure is not null)
        {
            return Task.FromResult(new ApmRenderResult(
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
            return Task.FromResult(new ApmRenderResult(
                Success: false,
                Files: [],
                Degradations: [],
                Warnings: _warnings,
                Errors: [$"Permission widening detected for agent '{_permissionWideningAgent}': 'deny' widened to 'allow'."]));
        }

        List<SquadDeploymentFile> files = [];
        List<ApmDegradationRecord> degradations = [];
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
                        degradations.Add(new ApmDegradationRecord(
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

                        degradations.Add(new ApmDegradationRecord(
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

                        degradations.Add(new ApmDegradationRecord(
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

        return Task.FromResult(new ApmRenderResult(
            Success: errors.Count == 0,
            Files: files,
            Degradations: degradations,
            Warnings: _warnings,
            Errors: errors));
    }

    public Task<ApmPackResult> PackAsync(
        ApmPackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _packRequests.Add(request);

        if (_packFailure is not null)
        {
            return Task.FromResult(new ApmPackResult(
                Success: false,
                CreatedArchives: [],
                Errors: [_packFailure],
                Warnings: _warnings));
        }

        if (_packHandler is not null)
        {
            return Task.FromResult(_packHandler(request));
        }

        List<string> archives = [];
        string? pluginManifestJson = null;

        if (request.Format is ApmPackFormat.Apm or ApmPackFormat.All)
        {
            string apmArchive = Path.Combine(request.OutputDirectory, $"kyber-squad-{request.Version}.zip");
            archives.Add(apmArchive);
        }

        if (request.Format is ApmPackFormat.Plugins or ApmPackFormat.All)
        {
            string pluginsArchive = Path.Combine(request.OutputDirectory, $"kyber-squad-plugin-{request.Version}.zip");
            archives.Add(pluginsArchive);

            pluginManifestJson = _customPluginManifestJson ?? GenerateAgentPluginsV1Manifest(_includeAgentsInPluginManifest);
        }

        return Task.FromResult(new ApmPackResult(
            Success: true,
            CreatedArchives: archives,
            Errors: [],
            Warnings: _warnings,
            PluginManifestJson: pluginManifestJson));
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
        SquadTarget.Copilot => $".github/agents/{agentName}.md",
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

    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    private static string GenerateAgentPluginsV1Manifest(bool includeAgents)
    {
        Dictionary<string, object?> manifest = new()
        {
            ["$schema"] = "https://agent-plugins.org/v1/schema.json",
            ["name"] = "kyber-squad",
            ["version"] = "1.0.0",
            ["description"] = "Kyber-Squad portable skills and MCP surface",
            ["skills"] = CanonicalSkills.Select(s => new Dictionary<string, string>
            {
                ["name"] = s,
                ["path"] = $"skills/{s}"
            }).ToList(),
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["kyber-weave"] = new Dictionary<string, object>
                {
                    ["command"] = "kyber-weave-mcp",
                    ["args"] = Array.Empty<string>()
                }
            }
        };

        if (includeAgents)
        {
            manifest["agents"] = CanonicalAgents.Select(a => new Dictionary<string, string>
            {
                ["name"] = a,
                ["path"] = $"agents/{a}.md"
            }).ToList();
        }

        return JsonSerializer.Serialize(manifest, IndentedJsonOptions);
    }
}
