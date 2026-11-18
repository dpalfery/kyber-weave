using KyberWeave.Application.Agents;
using KyberWeave.Cli.Commands;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Tests.Configuration;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;
using YamlDotNet.Core;

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
    public void ProductDefaultsIncludeSixHarnessNamespacesWithoutConductorSkillMapping()
    {
        HarnessProfileConfig config = HarnessProfileConfig.ProductDefaults;

        Assert.Equal(6, config.Profiles.Count);

        foreach (HarnessKind harness in SixHarnesses)
        {
            Assert.True(config.Profiles.ContainsKey(harness), $"Missing product default profile for {harness}.");
            HarnessCapabilityProfile profile = config.Profiles[harness];
            Assert.False(
                profile.MappedRoleSkillOverrides.ContainsKey("conductor"),
                $"Product default for {harness} must not auto-satisfy conductor via skill mapping.");
        }
    }

    [Fact]
    public void HostOverrideConductorMappingSatisfiesRoleWhenSkillDirectoryPresent()
    {
        HarnessRepoFixture repo = new HarnessRepoFixture()
            .WithConductorSkillDirectory()
            .WithAgent(HarnessKind.Kilo, "conductor");

        string yamlPath = repo.WriteHostProfile("""
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

        HarnessProfileConfig config = repo.Config.LoadHarness(yamlPath);

        foreach (HarnessKind harness in SixHarnesses)
        {
            Assert.True(
                config.Profiles[harness].MappedRoleSkillOverrides.ContainsKey("conductor"),
                $"Host override must map conductor for {harness}.");
        }

        AgentSet agentSet = repo.LoadAgentSet();
        DiagnosticReport report = AgentSyncLinter.LintSet(agentSet, repo.Root, config, repo.Files.FileExists);

        List<Diagnostic> conductorMissing = report.Items
            .Where(i => i is { Code: AgentSyncLinter.RuleUnsatisfiedRole, Subject: "conductor" })
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
    public void UnknownHarnessProfileNameIsRejected()
    {
        InMemoryConfigFiles config = new();
        string yamlPath = config.Write(Path.Combine(config.Files.Root, "host", "kyber-weave.yml"), """
            harness:
              profiles:
                not-a-harness:
                  mapped-role-skill-overrides:
                    conductor: conductor
            """);

        YamlException ex = Assert.ThrowsAny<YamlException>(
            () => config.LoadHarness(yamlPath));
        Assert.Contains("not-a-harness", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownHarnessProfileNameThroughTryLoadReportsKWCONFIG001()
    {
        InMemoryConfigFiles files = new();
        string root = Path.Combine(files.Files.Root, "repo");
        files.WriteRepositoryConfig(root, """
            harness:
              profiles:
                not-a-harness:
                  mapped-role-skill-overrides:
                    conductor: conductor
            """);

        KyberWeaveConfigLoadResult result = files.TryLoad(root);

        Assert.False(result.Success);
        Assert.Null(result.Config);
        Assert.NotNull(result.Error);
        Assert.Contains("not-a-harness", result.Error, StringComparison.Ordinal);
        Assert.NotNull(result.ConfigPath);

        // The rule code is emitted by the CLI's load helper, so it is asserted on the diagnostic that carries it
        // rather than on the failed result, which holds no code.
        DiagnosticReport report = new();
        bool loaded = CommandHelpers.TryLoadConfig(
            files.Repository, root, null, report, out KyberWeaveConfig config);

        Assert.False(loaded);
        Assert.Same(KyberWeaveConfigDefaults.Product, config);
        Diagnostic diagnostic = Assert.Single(report.Items);
        Assert.Equal("KW-CONFIG-001", diagnostic.Code);
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.Contains("not-a-harness", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(result.ConfigPath, diagnostic.FilePath);
    }

    [Fact]
    public void RootLevelSkillMdDoesNotFalselySatisfyMappedRoleButSkillDirDoes()
    {
        string hostYaml = """
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
        {
            HarnessRepoFixture repo = new HarnessRepoFixture()
            .WithRootSkillMd()
            .WithAgent(HarnessKind.Kilo, "conductor");

            HarnessProfileConfig config = repo.Config.LoadHarness(repo.WriteHostProfile(hostYaml));
            DiagnosticReport report = AgentSyncLinter.LintSet(repo.LoadAgentSet(), repo.Root, config, repo.Files.FileExists);

            List<Diagnostic> conductorMissing = report.Items
                .Where(i => i is { Code: AgentSyncLinter.RuleUnsatisfiedRole, Subject: "conductor" })
                .ToList();

            Assert.Contains(
                conductorMissing,
                i => i.Location is not null && i.Location.Contains(".codex/agents", StringComparison.Ordinal));
            Assert.Contains(
                conductorMissing,
                i => i.Location is not null && i.Location.Contains(".cursor/agents", StringComparison.Ordinal));
        }

        // Canonical SKILL.md inside the mapped skill directory does satisfy.
        {
            HarnessRepoFixture repo = new HarnessRepoFixture()
            .WithConductorSkillDirectory()
            .WithAgent(HarnessKind.Kilo, "conductor");

            HarnessProfileConfig config = repo.Config.LoadHarness(repo.WriteHostProfile(hostYaml));
            DiagnosticReport report = AgentSyncLinter.LintSet(repo.LoadAgentSet(), repo.Root, config, repo.Files.FileExists);

            List<Diagnostic> conductorMissing = report.Items
                .Where(i => i is { Code: AgentSyncLinter.RuleUnsatisfiedRole, Subject: "conductor" })
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
    public void WithoutOverrideMissingHarnessRoleEmitsKWAGENTSYNC001()
    {
        HarnessRepoFixture repo = new HarnessRepoFixture()
            .WithAgent(HarnessKind.Kilo, "architect");

        AgentSet agentSet = repo.LoadAgentSet();
        DiagnosticReport report = AgentSyncLinter.LintSet(agentSet, repo.Root, HarnessProfileConfig.ProductDefaults, repo.Files.FileExists);

        Assert.Contains(
            report.Items,
            i => i is { Code: AgentSyncLinter.RuleUnsatisfiedRole, Subject: "architect", Severity: Severity.Warning });
    }

    private sealed class HarnessRepoFixture
    {
        public HarnessRepoFixture()
        {
            Config = new InMemoryConfigFiles();
            Root = Path.Combine(Files.Root, "repo");
            Files.CreateDirectory(Root);
        }

        public InMemoryConfigFiles Config { get; }

        public InMemoryFileSystem Files => Config.Files;

        public string Root { get; }

        public HarnessRepoFixture WithConductorSkillDirectory()
        {
            string directory = Path.Combine(Root, ".agents", "skills", "conductor");
            Files.CreateDirectory(directory);
            Files.WriteAllText(Path.Combine(directory, "SKILL.md"), "---\nname: conductor\n---\n");
            return this;
        }

        public HarnessRepoFixture WithRootSkillMd()
        {
            Files.WriteAllText(Path.Combine(Root, "SKILL.md"), "---\nname: conductor\n---\n");
            return this;
        }

        public HarnessRepoFixture WithAgent(HarnessKind harness, string roleName)
        {
            (string folder, string fileName) = harness switch
            {
                HarnessKind.Codex => (".codex/agents", $"{roleName}.toml"),
                HarnessKind.Cursor => (".cursor/agents", $"{roleName}.agent.md"),
                HarnessKind.Claude => (".claude/agents", $"{roleName}.md"),
                HarnessKind.GitHubCopilot => (".github/agents", $"{roleName}.agent.md"),
                HarnessKind.OpenCode => (".opencode/agents", $"{roleName}.md"),
                HarnessKind.Kilo => (".kilo/agents", $"{roleName}.md"),
                _ => throw new ArgumentOutOfRangeException(nameof(harness))
            };

            string dir = Path.Combine(Root, folder.Replace('/', Path.DirectorySeparatorChar));
            Files.CreateDirectory(dir);
            Files.WriteAllText(Path.Combine(dir, fileName), $"""
                ---
                name: {roleName}
                description: Test agent for harness profile contract.
                ---
                Instructions for {roleName}.
                """);

            return this;
        }

        public string WriteHostProfile(string yaml) => Config.Write(Path.Combine(Root, "kyber-weave.yml"), yaml);

        public AgentSet LoadAgentSet()
        {
            FakePathResolver paths = new(Files);
            FakeProcessEnvironment environment = new(currentDirectory: Root, isFileSystemCaseInsensitive: !Files.CaseSensitive);
            return new AgentFileReader(Files, paths, environment).LoadAll(Root);
        }
    }
}
