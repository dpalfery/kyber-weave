using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T7 — host-shaped <c>kyber-weave.yml</c> must restore today's MotorcycleRAG governance
/// expectations for docs validation and agent sync-check.
/// </summary>
public class MotorcycleRagHostProfileTests
{
    private const string HostProfileYaml = """
        ontology:
          docs-root: 6-Docs
          excluded-segments:
            - archive
            - node_modules
            - obj
            - bin
          excluded-files:
            - DevOps/build-performance.md
            - DevOps/directory-build-organization.md
            - DevOps/incremental-build.md
            - DevOps/msbuild-antipatterns.md
            - DevOps/msbuild-modernization.md
          catalog:
            component-column: 1
            owner-column: 6
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

    [Fact]
    public void HostProfile_Restores_Docs_Validate_Exclusion_Expectations()
    {
        var config = KyberWeaveConfigLoader.LoadFromYaml(HostProfileYaml);
        using var fixture = new HostProfileDocFixture(config);

        fixture.WithCatalog()
            .Write("6-Docs/archive/old-plan.md", "# archived\n")
            .Write("6-Docs/DevOps/incremental-build.md", "---\nname: upstream/skill\n---\n")
            .Write("6-Docs/reference/current.md", ValidReference);

        var subjects = fixture.LoadSubjects();

        Assert.Single(subjects);
        Assert.Equal("6-Docs/reference/current.md", subjects[0].RelativePath);
        Assert.False(fixture.Validate().HasErrors);
    }

    [Fact]
    public void HostProfile_Restores_Catalog_Column_Mapping()
    {
        var config = KyberWeaveConfigLoader.LoadFromYaml(HostProfileYaml);
        using var fixture = new HostProfileDocFixture(config);

        fixture.WithCatalog(
            """
            | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
            | --- | --- | --- | --- | --- | --- | --- | --- |
            | MotorcycleRAG API | Application | `1-Presentation/Api` | [README](x) | [docs](y) | API maintainers | 2026-07-21 | Current |
            | MotorcycleRAG system | System | repository root | [README](x) | [docs](y) | Maintainers | 2026-07-21 | Current |
            """);

        fixture.Write("6-Docs/api/onboarding.md", """
            ---
            id: api/onboarding
            title: API Onboarding
            doc-type: onboarding
            status: current
            component: MotorcycleRAG API
            source-root: 1-Presentation/Api
            owner: API maintainers
            last-reviewed: 2026-07-21
            ---
            """);

        fixture.WithSourceRoot("1-Presentation/Api");

        var report = fixture.Validate();

        Assert.DoesNotContain(report.Items, i => i.Code == DocSpecValidator.UnknownCatalogValue);
    }

    [Fact]
    public void HostProfile_Restores_Conductor_Sync_Check_Expectations()
    {
        var config = KyberWeaveConfigLoader.LoadFromYaml(HostProfileYaml);
        using var repo = new HostProfileRepoFixture();

        repo.WithConductorSkillDirectory()
            .WithAgent(HarnessKind.Kilo, "conductor");

        foreach (var harness in new[]
                 {
                     HarnessKind.Codex, HarnessKind.Cursor, HarnessKind.Claude,
                     HarnessKind.GitHubCopilot, HarnessKind.OpenCode, HarnessKind.Kilo
                 })
        {
            Assert.True(
                config.Harness.Profiles[harness].MappedRoleSkillOverrides.ContainsKey("conductor"),
                $"Host profile must map conductor for {harness}.");
        }

        var report = AgentSyncLinter.LintSet(repo.LoadAgentSet(), repo.Root, config.Harness);

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
    public void HostProfile_Loads_From_Kyber_Weave_Folder()
    {
        using var repo = new HostProfileRepoFixture();
        repo.WriteHostConfig(HostProfileYaml);

        var config = KyberWeaveConfigLoader.Load(repo.Root);

        Assert.Equal("6-Docs", config.Ontology.DocsRoot);
        Assert.True(config.Harness.Profiles[HarnessKind.Codex].MappedRoleSkillOverrides.ContainsKey("conductor"));
    }

    [Fact]
    public void HostProfile_Loads_From_Legacy_Repo_Root_Kyber_Weave_Yml()
    {
        using var repo = new HostProfileRepoFixture();
        repo.WriteLegacyRootConfig(HostProfileYaml);

        var config = KyberWeaveConfigLoader.Load(repo.Root);

        Assert.Equal("6-Docs", config.Ontology.DocsRoot);
        Assert.True(config.Harness.Profiles[HarnessKind.Codex].MappedRoleSkillOverrides.ContainsKey("conductor"));
    }

    [Fact]
    public void HostProfile_In_Kyber_Weave_Folder_Wins_Over_Legacy_Root_File()
    {
        using var repo = new HostProfileRepoFixture();
        repo.WriteLegacyRootConfig("ontology:\n  docs-root: legacy-docs\n");
        repo.WriteHostConfig(HostProfileYaml);

        var config = KyberWeaveConfigLoader.Load(repo.Root);

        Assert.Equal("6-Docs", config.Ontology.DocsRoot);
    }

    private const string ValidReference = """
        ---
        id: reference/current
        title: Current Doc
        doc-type: reference
        status: current
        owner: Maintainers
        last-reviewed: 2026-07-21
        ---

        # Current
        """;

    private sealed class HostProfileDocFixture : IDisposable
    {
        public string Root { get; }
        private readonly KyberWeaveConfig _config;

        public HostProfileDocFixture(KyberWeaveConfig config)
        {
            _config = config;
            Root = Path.Combine(Path.GetTempPath(), "kw-host-docs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, _config.Ontology.DocsRoot));
        }

        public HostProfileDocFixture WithCatalog(string? tableBody = null)
        {
            tableBody ??= """
                | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
                | --- | --- | --- | --- | --- | --- | --- | --- |
                | MotorcycleRAG API | Application | `1-Presentation/Api` | [README](x) | [docs](y) | API maintainers | 2026-07-21 | Current |
                | MotorcycleRAG system | System | repository root | [README](x) | [docs](y) | Maintainers | 2026-07-21 | Current |
                """;

            Write("6-Docs/catalog.md", $$"""
                ---
                id: system/catalog
                title: Component Catalog
                doc-type: index
                status: current
                owner: Maintainers
                last-reviewed: 2026-07-21
                ---

                # Catalog

                {{tableBody}}
                """);
            return this;
        }

        public HostProfileDocFixture WithSourceRoot(string relativePath)
        {
            Directory.CreateDirectory(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return this;
        }

        public HostProfileDocFixture Write(string relativePath, string content)
        {
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return this;
        }

        public IReadOnlyList<DocumentModel> LoadSubjects() =>
            new DocumentLoader(Root, _config.Ontology).Load().Documents
                .Where(d => d.RelativePath != "6-Docs/catalog.md")
                .ToList();

        public DiagnosticReport Validate() =>
            new DocSpecValidator(Root, _config.Ontology)
                .Validate(new DocumentLoader(Root, _config.Ontology).Load());

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class HostProfileRepoFixture : IDisposable
    {
        public string Root { get; }

        public HostProfileRepoFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "kw-host-repo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public HostProfileRepoFixture WithConductorSkillDirectory()
        {
            Directory.CreateDirectory(Path.Combine(Root, ".agents", "skills", "conductor"));
            File.WriteAllText(
                Path.Combine(Root, ".agents", "skills", "conductor", "SKILL.md"),
                "---\nname: conductor\n---\n");
            return this;
        }

        public HostProfileRepoFixture WithAgent(HarnessKind harness, string roleName)
        {
            var folder = harness switch
            {
                HarnessKind.Kilo => ".kilo/agents",
                HarnessKind.Codex => ".codex/agents",
                HarnessKind.Cursor => ".cursor/agents",
                HarnessKind.Claude => ".claude/agents",
                HarnessKind.GitHubCopilot => ".github/agents",
                HarnessKind.OpenCode => ".opencode/agents",
                _ => throw new ArgumentOutOfRangeException(nameof(harness))
            };

            var dir = Path.Combine(Root, folder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, $"{roleName}.md"),
                $"""
                ---
                name: {roleName}
                description: Host profile contract agent.
                ---
                Body for {roleName}.
                """);
            return this;
        }

        public void WriteHostConfig(string yaml)
        {
            var dir = Path.Combine(Root, ".kyber-weave");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "kyber-weave.yml"), yaml);
        }

        public void WriteLegacyRootConfig(string yaml) =>
            File.WriteAllText(Path.Combine(Root, "kyber-weave.yml"), yaml);

        public AgentSet LoadAgentSet() => AgentLoader.LoadAll(Root);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
