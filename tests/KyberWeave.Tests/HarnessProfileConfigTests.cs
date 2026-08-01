using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T2 — harness profile defaults + host override contract. Product kernel must not bake
/// in MotorcycleRAG conductor→skill satisfaction; host overrides restore it when configured.
/// </summary>
public class HarnessProfileConfigTests
{
    private static readonly HarnessKind[] SixHarnesses =
    [
        HarnessKind.Codex,
        HarnessKind.Cursor,
        HarnessKind.Claude,
        HarnessKind.GitHubCopilot,
        HarnessKind.OpenCode,
        HarnessKind.Kilo
    ];

    [Fact]
    public void ProductDefaults_Include_Six_Harness_Namespaces_Without_Conductor_Skill_Mapping()
    {
        var config = HarnessProfileConfig.ProductDefaults;

        Assert.Equal(6, config.Profiles.Count);

        foreach (var harness in SixHarnesses)
        {
            Assert.True(config.Profiles.ContainsKey(harness), $"Missing product default profile for {harness}.");
            var profile = config.Profiles[harness];
            Assert.False(
                profile.MappedRoleSkillOverrides.ContainsKey("conductor"),
                $"Product default for {harness} must not auto-satisfy conductor via skill mapping.");
        }
    }

    [Fact]
    public void HostOverride_Conductor_Mapping_Satisfies_Role_When_Skill_Directory_Present()
    {
        using var repo = new HarnessRepoFixture()
            .WithConductorSkillDirectory()
            .WithAgent(HarnessKind.Kilo, "conductor");

        var yamlPath = repo.WriteHostProfile("""
            harness:
              profiles:
                codex:
                  mapped-role-skill-overrides:
                    conductor: conductor
                cursor:
                  mapped-role-skill-overrides:
                    conductor: conductor
                claude:
                  mapped-role-skill-overrides:
                    conductor: conductor
                githubcopilot:
                  mapped-role-skill-overrides:
                    conductor: conductor
                opencode:
                  mapped-role-skill-overrides:
                    conductor: conductor
                kilo:
                  mapped-role-skill-overrides:
                    conductor: conductor
            """);

        var config = HarnessProfileConfigLoader.Load(yamlPath);

        foreach (var harness in SixHarnesses)
        {
            Assert.True(
                config.Profiles[harness].MappedRoleSkillOverrides.ContainsKey("conductor"),
                $"Host override must map conductor for {harness}.");
        }

        var agentSet = repo.LoadAgentSet();
        var report = AgentSyncLinter.LintSet(agentSet, repo.Root, config);

        var conductorMissing = report.Items
            .Where(i => i.Code == AgentSyncLinter.RuleUnsatisfiedRole && i.Subject == "conductor")
            .ToList();

        Assert.DoesNotContain(
            conductorMissing,
            i => i.Location is not null && i.Location.Contains(".codex/agents", StringComparison.Ordinal));
        Assert.DoesNotContain(
            conductorMissing,
            i => i.Location is not null && i.Location.Contains(".cursor/agents", StringComparison.Ordinal));
        Assert.DoesNotContain(
            conductorMissing,
            i => i.Location is not null && i.Location.Contains(".claude/agents", StringComparison.Ordinal));
        Assert.DoesNotContain(
            conductorMissing,
            i => i.Location is not null && i.Location.Contains(".github/agents", StringComparison.Ordinal));
        Assert.DoesNotContain(
            conductorMissing,
            i => i.Location is not null && i.Location.Contains(".opencode/agents", StringComparison.Ordinal));
        Assert.DoesNotContain(
            conductorMissing,
            i => i.Location is not null && i.Location.Contains(".kilo/agents", StringComparison.Ordinal));
    }

    [Fact]
    public void Unknown_Harness_Profile_Name_Is_Rejected()
    {
        var yamlPath = WriteTempYaml("""
            harness:
              profiles:
                not-a-harness:
                  mapped-role-skill-overrides:
                    conductor: conductor
            """);

        var ex = Assert.ThrowsAny<YamlDotNet.Core.YamlException>(
            () => HarnessProfileConfigLoader.Load(yamlPath));
        Assert.Contains("not-a-harness", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_Harness_Profile_Name_Through_TryLoad_Reports_KW_CONFIG_001()
    {
        var root = Path.Combine(Path.GetTempPath(), "kw-config-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configDir = Directory.CreateDirectory(Path.Combine(root, ".kyber-weave"));
            File.WriteAllText(Path.Combine(configDir.FullName, "kyber-weave.yml"), """
                harness:
                  profiles:
                    not-a-harness:
                      mapped-role-skill-overrides:
                        conductor: conductor
                """);

            var result = KyberWeaveConfigLoader.TryLoad(root);

            Assert.False(result.Success);
            Assert.Null(result.Config);
            Assert.NotNull(result.Error);
            Assert.Contains("not-a-harness", result.Error, StringComparison.Ordinal);
            Assert.Equal(KyberWeaveConfigLoader.ConfigLoadErrorCode, "KW-CONFIG-001");
            Assert.NotNull(result.ConfigPath);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Root_Level_SkillMd_Does_Not_Falsely_Satisfy_Mapped_Role_But_Skill_Dir_Does()
    {
        var hostYaml = """
            harness:
              profiles:
                codex:
                  mapped-role-skill-overrides:
                    conductor: conductor
                cursor:
                  mapped-role-skill-overrides:
                    conductor: conductor
                claude:
                  mapped-role-skill-overrides:
                    conductor: conductor
                githubcopilot:
                  mapped-role-skill-overrides:
                    conductor: conductor
                opencode:
                  mapped-role-skill-overrides:
                    conductor: conductor
                kilo:
                  mapped-role-skill-overrides:
                    conductor: conductor
            """;

        // Root-level SKILL.md alone must NOT satisfy mapped roles.
        using (var repo = new HarnessRepoFixture()
            .WithRootSkillMd()
            .WithAgent(HarnessKind.Kilo, "conductor"))
        {
            var config = HarnessProfileConfigLoader.Load(repo.WriteHostProfile(hostYaml));
            var report = AgentSyncLinter.LintSet(repo.LoadAgentSet(), repo.Root, config);

            var conductorMissing = report.Items
                .Where(i => i.Code == AgentSyncLinter.RuleUnsatisfiedRole && i.Subject == "conductor")
                .ToList();

            Assert.Contains(
                conductorMissing,
                i => i.Location is not null && i.Location.Contains(".codex/agents", StringComparison.Ordinal));
            Assert.Contains(
                conductorMissing,
                i => i.Location is not null && i.Location.Contains(".cursor/agents", StringComparison.Ordinal));
        }

        // Canonical SKILL.md inside the mapped skill directory does satisfy.
        using (var repo = new HarnessRepoFixture()
            .WithConductorSkillDirectory()
            .WithAgent(HarnessKind.Kilo, "conductor"))
        {
            var config = HarnessProfileConfigLoader.Load(repo.WriteHostProfile(hostYaml));
            var report = AgentSyncLinter.LintSet(repo.LoadAgentSet(), repo.Root, config);

            var conductorMissing = report.Items
                .Where(i => i.Code == AgentSyncLinter.RuleUnsatisfiedRole && i.Subject == "conductor")
                .ToList();

            Assert.DoesNotContain(
                conductorMissing,
                i => i.Location is not null && i.Location.Contains(".codex/agents", StringComparison.Ordinal));
            Assert.DoesNotContain(
                conductorMissing,
                i => i.Location is not null && i.Location.Contains(".cursor/agents", StringComparison.Ordinal));
            Assert.DoesNotContain(
                conductorMissing,
                i => i.Location is not null && i.Location.Contains(".kilo/agents", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void WithoutOverride_Missing_Harness_Role_Emits_KW_AGENT_SYNC_001()
    {
        using var repo = new HarnessRepoFixture()
            .WithAgent(HarnessKind.Kilo, "architect");

        var agentSet = repo.LoadAgentSet();
        var report = AgentSyncLinter.LintSet(agentSet, repo.Root, HarnessProfileConfig.ProductDefaults);

        Assert.Contains(
            report.Items,
            i => i.Code == AgentSyncLinter.RuleUnsatisfiedRole &&
                 i.Subject == "architect" &&
                 i.Severity == Severity.Warning);
    }

    private sealed class HarnessRepoFixture : IDisposable
    {
        public string Root { get; }

        public HarnessRepoFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "kw-harness-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public HarnessRepoFixture WithConductorSkillDirectory()
        {
            Directory.CreateDirectory(Path.Combine(Root, ".agents", "skills", "conductor"));
            File.WriteAllText(Path.Combine(Root, ".agents", "skills", "conductor", "SKILL.md"), "---\nname: conductor\n---\n");
            return this;
        }

        public HarnessRepoFixture WithRootSkillMd()
        {
            File.WriteAllText(Path.Combine(Root, "SKILL.md"), "---\nname: conductor\n---\n");
            return this;
        }

        public HarnessRepoFixture WithAgent(HarnessKind harness, string roleName)
        {
            var (folder, fileName) = harness switch
            {
                HarnessKind.Codex => (".codex/agents", $"{roleName}.toml"),
                HarnessKind.Cursor => (".cursor/agents", $"{roleName}.agent.md"),
                HarnessKind.Claude => (".claude/agents", $"{roleName}.md"),
                HarnessKind.GitHubCopilot => (".github/agents", $"{roleName}.agent.md"),
                HarnessKind.OpenCode => (".opencode/agents", $"{roleName}.md"),
                HarnessKind.Kilo => (".kilo/agents", $"{roleName}.md"),
                _ => throw new ArgumentOutOfRangeException(nameof(harness))
            };

            var dir = Path.Combine(Root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, fileName);
            File.WriteAllText(path, $"""
                ---
                name: {roleName}
                description: Test agent for harness profile contract.
                ---
                Instructions for {roleName}.
                """);

            return this;
        }

        public string WriteHostProfile(string yaml) =>
            WriteTempYaml(yaml);

        public AgentSet LoadAgentSet() => AgentLoader.LoadAll(Root);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private string WriteTempYaml(string content)
        {
            var path = Path.Combine(Root, "kyber-weave.yml");
            File.WriteAllText(path, content);
            return path;
        }
    }

    private static string WriteTempYaml(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "kw-harness-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, content);
        return path;
    }
}
