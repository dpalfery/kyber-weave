using KyberWeave.Application.Agents;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.Configuration;
using KyberWeave.Data.Review;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// T7 — host-shaped <c>kyber-weave.yml</c> must restore today's MotorcycleRAG governance
/// expectations for docs validation and agent sync-check.
/// </summary>
public class MotorcycleRagHostProfileTests
{
    private readonly InMemoryFileSystem _disk = new();

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
    public void HostProfileRestoresDocsValidateExclusionExpectations()
    {
        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml(HostProfileYaml);
        HostProfileDocFixture fixture = new(_disk, config);

        fixture.WithCatalog()
            .Write("6-Docs/archive/old-plan.md", "# archived\n")
            .Write("6-Docs/DevOps/incremental-build.md", "---\nname: upstream/skill\n---\n")
            .Write("6-Docs/reference/current.md", ValidReference);

        IReadOnlyList<DocumentModel> subjects = fixture.LoadSubjects();

        Assert.Single(subjects);
        Assert.Equal("6-Docs/reference/current.md", subjects[0].RelativePath);
        Assert.False(fixture.Validate().HasErrors);
    }

    [Fact]
    public void HostProfileRestoresCatalogColumnMapping()
    {
        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml(HostProfileYaml);
        HostProfileDocFixture fixture = new(_disk, config);

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

        DiagnosticReport report = fixture.Validate();

        Assert.DoesNotContain(report.Items, i => i.Code == DocSpecValidator.UnknownCatalogValue);
    }

    [Fact]
    public void HostProfileRestoresConductorSyncCheckExpectations()
    {
        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml(HostProfileYaml);
        HostProfileRepoFixture repo = new(_disk);

        repo.WithConductorSkillDirectory();
        repo.WithAgent(HarnessKind.Kilo, "conductor");

        foreach (HarnessKind harness in new[]
                 {
                     HarnessKind.Codex, HarnessKind.Cursor, HarnessKind.Claude,
                     HarnessKind.GitHubCopilot, HarnessKind.OpenCode, HarnessKind.Kilo
                 })
        {
            Assert.True(
                config.Harness.Profiles[harness].MappedRoleSkillOverrides.ContainsKey("conductor"),
                $"Host profile must map conductor for {harness}.");
        }

        DiagnosticReport report = AgentSyncLinter.LintSet(repo.LoadAgentSet(), repo.Root, config.Harness, _disk.FileExists);

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
    public void HostProfileLoadsFromKyberWeaveFolder()
    {
        HostProfileRepoFixture repo = new(_disk);
        repo.WriteHostConfig(HostProfileYaml);

        KyberWeaveConfig config = repo.LoadConfig();

        Assert.Equal("6-Docs", config.Ontology.DocsRoot);
        Assert.True(config.Harness.Profiles[HarnessKind.Codex].MappedRoleSkillOverrides.ContainsKey("conductor"));
    }

    [Fact]
    public void HostProfileLoadsFromLegacyRepoRootKyberWeaveYml()
    {
        HostProfileRepoFixture repo = new(_disk);
        repo.WriteLegacyRootConfig(HostProfileYaml);

        KyberWeaveConfig config = repo.LoadConfig();

        Assert.Equal("6-Docs", config.Ontology.DocsRoot);
        Assert.True(config.Harness.Profiles[HarnessKind.Codex].MappedRoleSkillOverrides.ContainsKey("conductor"));
    }

    [Fact]
    public void HostProfileInKyberWeaveFolderWinsOverLegacyRootFile()
    {
        HostProfileRepoFixture repo = new(_disk);
        repo.WriteLegacyRootConfig("ontology:\n  docs-root: legacy-docs\n");
        repo.WriteHostConfig(HostProfileYaml);

        KyberWeaveConfig config = repo.LoadConfig();

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

    private sealed class HostProfileDocFixture
    {
        private readonly InMemoryFileSystem _disk;
        private readonly KyberWeaveConfig _config;
        private readonly FakePathResolver _paths;
        private readonly FakeProcessEnvironment _environment;

        public HostProfileDocFixture(InMemoryFileSystem disk, KyberWeaveConfig config)
        {
            _disk = disk;
            _config = config;
            Root = Path.Combine(disk.Root, "kw", "host-docs");
            _paths = new FakePathResolver(disk);
            _environment = new FakeProcessEnvironment(currentDirectory: Path.Combine(disk.Root, "kw", "work"));
            _disk.CreateDirectory(Path.Combine(Root, _config.Ontology.DocsRoot));
        }

        private string Root { get; }

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

        public void WithSourceRoot(string relativePath)
        {
            _disk.CreateDirectory(Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        public HostProfileDocFixture Write(string relativePath, string content)
        {
            string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            _disk.CreateDirectory(Path.GetDirectoryName(full)!);
            _disk.WriteAllText(full, content);
            return this;
        }

        public IReadOnlyList<DocumentModel> LoadSubjects() =>
            Loader().Load().Documents
                .Where(d => d.RelativePath != "6-Docs/catalog.md")
                .ToList();

        public DiagnosticReport Validate() =>
            new DocSpecValidator(Root, _config.Ontology, _disk, _paths, _environment).Validate(Loader().Load());

        private DocumentLoader Loader() =>
            new(new InMemoryRepositoryFileStore(_disk), Root, _paths, _environment, _config.Ontology);
    }

    private sealed class HostProfileRepoFixture
    {
        private readonly InMemoryFileSystem _disk;
        private readonly FakePathResolver _paths;
        private readonly FakeProcessEnvironment _environment;

        public HostProfileRepoFixture(InMemoryFileSystem disk)
        {
            _disk = disk;
            Root = Path.Combine(disk.Root, "kw", "host-repo");
            _paths = new FakePathResolver(disk);
            _environment = new FakeProcessEnvironment(currentDirectory: Path.Combine(disk.Root, "kw", "work"));
            _disk.CreateDirectory(Root);
        }

        public string Root { get; }

        public void WithConductorSkillDirectory()
        {
            _disk.CreateDirectory(Path.Combine(Root, ".agents", "skills", "conductor"));
            _disk.WriteAllText(
                Path.Combine(Root, ".agents", "skills", "conductor", "SKILL.md"),
                "---\nname: conductor\n---\n");
        }

        public void WithAgent(HarnessKind harness, string roleName)
        {
            string folder = harness switch
            {
                HarnessKind.Kilo => ".kilo/agents",
                HarnessKind.Codex => ".codex/agents",
                HarnessKind.Cursor => ".cursor/agents",
                HarnessKind.Claude => ".claude/agents",
                HarnessKind.GitHubCopilot => ".github/agents",
                HarnessKind.OpenCode => ".opencode/agents",
                _ => throw new ArgumentOutOfRangeException(nameof(harness))
            };

            string dir = Path.Combine(Root, folder.Replace('/', Path.DirectorySeparatorChar));
            _disk.CreateDirectory(dir);
            _disk.WriteAllText(
                Path.Combine(dir, $"{roleName}.md"),
                $"""
                ---
                name: {roleName}
                description: Synthetic agent for {roleName}
                ---
                Instructions body.
                """);
        }

        public void WriteHostConfig(string yaml)
        {
            string dir = Path.Combine(Root, ".kyber-weave");
            _disk.CreateDirectory(dir);
            _disk.WriteAllText(Path.Combine(dir, "kyber-weave.yml"), yaml);
        }

        public void WriteLegacyRootConfig(string yaml) =>
            _disk.WriteAllText(Path.Combine(Root, "kyber-weave.yml"), yaml);

        public AgentSet LoadAgentSet() => new AgentFileReader(_disk, _paths, _environment).LoadAll(Root);

        public KyberWeaveConfig LoadConfig() =>
            new FileKyberWeaveConfigRepository(
                new FileRepositoryTreeProbe(_disk, _paths, _environment),
                _disk,
                _paths,
                _environment).Load(Root);
    }
}
