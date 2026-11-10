using System.Globalization;
using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests.Squad;

/// <summary>
/// Every developer agent must be able to run commands on every harness Squad installs to:
/// builds, tests, git and <c>gh</c> are how those agents do their work and prove it.
/// </summary>
/// <remarks>
/// A shell can disappear in three places, so each is pinned: the roster, the capability profile,
/// and the file each harness actually receives. The harnesses differ in how a grant is spelled.
/// Most take a tool list. OpenCode and Kilo take a permission map, where a missing key falls back
/// to the harness default, and Kilo's default asks before all but a short list of read-only
/// commands, which a headless subagent cannot answer. Codex, Cursor and Warp have no per-agent
/// grant, so the agent inherits the session's shell and the only way to lose it is for the
/// rendered file to restrict it.
/// </remarks>
public sealed class SquadDeveloperAgentShellContractTests
{
    private const string ProcessExecute = "process.execute";

    private static readonly string[] DeveloperAgents =
    [
        "csharp-dev",
        "dal-dev",
        "docs-dev",
        "github-devops",
        "go-dev",
        "maui-dev",
        "pulumi-dev",
        "python-dev",
        "react-dev",
        "rust-dev",
        "sql-database-architect",
        "tauri-dev",
        "test-dev",
        "typescript-dev",
    ];

    private static readonly HashSet<string> DeveloperProfiles =
        new(["worker", "publishing-worker", "documentation"], StringComparer.Ordinal);

    private static readonly Dictionary<SquadTarget, ShellContract> ShellContracts = new()
    {
        [SquadTarget.Claude] = new(".claude/agents/{0}.md", ToolList("tools", "Bash", "PowerShell")),
        [SquadTarget.Factory] = new(".factory/droids/{0}.md", ToolList("tools", "Execute")),
        [SquadTarget.Copilot] = new(".github/agents/{0}.agent.md", ToolList("tools", "execute/runInTerminal")),
        [SquadTarget.Pi] = new(".pi/agents/{0}.md", ToolList("tools", "bash")),
        [SquadTarget.ZCode] = new(".zcode/agents/{0}.md", ToolList("tools", "Bash")),
        [SquadTarget.CommandCode] = new(".commandcode/agents/{0}.md", ToolList("tools", "shell_command", "shell_output")),
        [SquadTarget.Devin] = new(".devin/agents/{0}/AGENT.md", ToolList("allowed-tools", "exec")),
        [SquadTarget.Antigravity] = new(".agents/agents/{0}/agent.md", ToolList("tools", "run_command")),
        [SquadTarget.OpenCode] = new(".opencode/agents/{0}.md", PermissionAllows("bash")),
        [SquadTarget.Kilo] = new(".kilo/agents/{0}.md", PermissionAllows("bash")),
        [SquadTarget.Cursor] = new(".cursor/agents/{0}.md", NotReadOnly),
        [SquadTarget.Codex] = new(".codex/agents/{0}.toml", ShellToolNotDisabled),
        [SquadTarget.Warp] = new(".warp/skills/{0}/SKILL.md", CarriesNoToolRestriction, ".warp/skills/role-{0}/SKILL.md"),
    };

    public static TheoryData<SquadTarget> ShippedTargets() =>
        new(SquadCommandComposition.CreateRenderers().SelectMany(renderer => renderer.SupportedTargets));

    [Fact]
    public void TheDeveloperRosterIsExactlyTheAgentsInADeveloperProfile()
    {
        string[] actual = TestSquadCorpus.CanonicalSource.Agents
            .Where(agent => DeveloperProfiles.Contains(agent.CapabilityProfile))
            .Select(agent => agent.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(DeveloperAgents, actual);
    }

    [Fact]
    public void EveryDeveloperProfileAllowsTheShell()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;

        foreach (string profileName in DeveloperProfiles)
        {
            Assert.Equal(SquadPermissionDecision.Allow, ShellDecision(source, profileName));
        }
    }

    [Theory]
    [MemberData(nameof(ShippedTargets))]
    public async Task EachHarnessGivesEveryDeveloperAgentAShell(SquadTarget target)
    {
        Assert.True(
            ShellContracts.TryGetValue(target, out ShellContract? contract),
            $"{SquadTargetCatalog.GetToken(target)} has no shell contract. Add a row saying how it grants a shell.");

        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(target);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        foreach (string agent in DeveloperAgents)
        {
            SquadDeploymentFile file = contract.FindAgentFile(result.Files, agent);
            contract.AssertGrantsShell(Encoding.UTF8.GetString(file.Content.Span), agent);
        }
    }

    /// <summary>
    /// The Kilo grant follows the profile, so it cannot widen an agent the canonical source denies a
    /// shell, and only the translated allow leaves the unexpressed-capabilities record.
    /// </summary>
    [Fact]
    public async Task KiloGrantsBashOnlyWhereTheProfileAllowsTheShell()
    {
        SquadSource source = TestSquadCorpus.CanonicalSource;
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(SquadTarget.Kilo);
        YamlScalarNode bash = new("bash");
        YamlScalarNode permissionKey = new("permission");

        foreach (SquadAgent agent in source.Agents)
        {
            bool allowsShell = ShellDecision(source, agent.CapabilityProfile) == SquadPermissionDecision.Allow;
            SquadDeploymentFile file = Assert.Single(
                result.Files,
                f => f.RelativePath == $".kilo/agents/{agent.Name}.md");
            YamlMappingNode frontmatter = Frontmatter(Encoding.UTF8.GetString(file.Content.Span));
            bool rendersBash = frontmatter.Children.TryGetValue(permissionKey, out YamlNode? node)
                && node is YamlMappingNode permission
                && permission.Children.ContainsKey(bash);
            Assert.True(allowsShell == rendersBash, $"'{agent.Name}': profile allows shell {allowsShell}, renders bash {rendersBash}.");

            SquadDegradationRecord? record = result.Degradations.SingleOrDefault(
                d => d.CanonicalIdentity == agent.Name && d.Code == "permission-not-expressible");
            Assert.True(record is not null, $"'{agent.Name}' has no permission-not-expressible record.");
            Assert.Equal(!allowsShell, record.Details?.Contains(ProcessExecute, StringComparison.Ordinal));
        }
    }

    private static Action<string, string> ToolList(string key, params string[] shellTools) => (text, agent) =>
    {
        Assert.True(
            Frontmatter(text).Children.TryGetValue(new YamlScalarNode(key), out YamlNode? tools),
            $"'{agent}' has no {key} key.");
        HashSet<string> granted = Tokens(tools);
        foreach (string tool in shellTools)
        {
            Assert.True(granted.Contains(tool), $"'{agent}' has no '{tool}' in {key}: {string.Join(", ", granted)}.");
        }
    };

    private static Action<string, string> PermissionAllows(string tool) => (text, agent) =>
    {
        Assert.True(
            Frontmatter(text).Children.TryGetValue(new YamlScalarNode("permission"), out YamlNode? map),
            $"'{agent}' has no permission map, so '{tool}' falls back to the harness default.");
        YamlMappingNode permission = Assert.IsType<YamlMappingNode>(map);
        Assert.True(
            permission.Children.TryGetValue(new YamlScalarNode(tool), out YamlNode? value),
            $"'{agent}' has no '{tool}' entry in its permission map, so it falls back to the harness default.");
        Assert.Equal("allow", Assert.IsType<YamlScalarNode>(value).Value);
    };

    private static void NotReadOnly(string text, string agent) =>
        Assert.False(
            Frontmatter(text).Children.TryGetValue(new YamlScalarNode("readonly"), out YamlNode? value)
            && value is YamlScalarNode { Value: "true" },
            $"'{agent}' renders readonly: true, which blocks state-changing shell commands.");

    private static void ShellToolNotDisabled(string text, string agent)
    {
        int bodyStart = text.IndexOf("developer_instructions", StringComparison.Ordinal);
        string header = bodyStart < 0 ? text : text[..bodyStart];
        string[] featureKeys = header
            .Split('\n')
            .Select(line => line.Split('=')[0].Trim())
            .Where(key => key.StartsWith("features", StringComparison.Ordinal)
                || key.StartsWith("[features", StringComparison.Ordinal)
                || key == "shell_tool")
            .ToArray();
        Assert.True(
            featureKeys.Length == 0,
            $"'{agent}' sets Codex features ({string.Join(", ", featureKeys)}), which can remove the shell from an agent.");
    }

    private static SquadPermissionDecision ShellDecision(SquadSource source, string profileName)
    {
        Assert.True(
            source.CapabilityProfiles.Profiles.TryGetValue(profileName, out SquadCapabilityProfile? profile),
            $"Capability profile '{profileName}' is not declared.");
        Assert.True(
            profile.Permissions.TryGetValue(ProcessExecute, out SquadPermissionDecision decision),
            $"Capability profile '{profileName}' makes no {ProcessExecute} decision.");
        return decision;
    }

    private static void CarriesNoToolRestriction(string text, string agent)
    {
        YamlMappingNode frontmatter = Frontmatter(text);
        Assert.False(frontmatter.Children.ContainsKey(new YamlScalarNode("tools")), $"'{agent}' restricts tools.");
        Assert.False(frontmatter.Children.ContainsKey(new YamlScalarNode("allowed-tools")), $"'{agent}' restricts tools.");
    }

    private static YamlMappingNode Frontmatter(string text)
    {
        const string Fence = "---\n";
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.StartsWith(Fence, normalized, StringComparison.Ordinal);
        int end = normalized.IndexOf("\n---\n", Fence.Length, StringComparison.Ordinal);
        Assert.True(end > 0, "The rendered file has no closing frontmatter fence.");

        YamlStream yaml = [];
        yaml.Load(new StringReader(normalized[Fence.Length..end]));
        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }

    private static HashSet<string> Tokens(YamlNode node) => node switch
    {
        YamlScalarNode scalar => (scalar.Value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal),
        YamlSequenceNode sequence => sequence.Children
            .OfType<YamlScalarNode>()
            .Select(item => item.Value ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal),
        _ => [],
    };

    /// <summary>
    /// <paramref name="PreferredPathFormat"/> is Warp's role skill, which takes a <c>role-</c> prefix
    /// only when a canonical skill already holds the agent's own name.
    /// </summary>
    private sealed record ShellContract(
        string PathFormat,
        Action<string, string> AssertGrantsShell,
        string? PreferredPathFormat = null)
    {
        public SquadDeploymentFile FindAgentFile(IReadOnlyList<SquadDeploymentFile> files, string agent)
        {
            if (PreferredPathFormat is not null)
            {
                string preferred = Format(PreferredPathFormat, agent);
                SquadDeploymentFile? found = files.FirstOrDefault(f => f.RelativePath == preferred);
                if (found is not null)
                {
                    return found;
                }
            }

            string path = Format(PathFormat, agent);
            return Assert.Single(files, f => f.RelativePath == path);
        }

        private static string Format(string format, string agent) =>
            string.Format(CultureInfo.InvariantCulture, format, agent);
    }
}
