using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Packaging;
using KyberWeave.Domain.CopilotPrompt;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Spectre.Console.Testing;
using Xunit;
using KyberWeave.Tests.Corpus;
using KyberWeave.Testing.Squad;

namespace KyberWeave.Tests;

/// <summary>
/// Task K9 Test Suite: Contract K9 for deterministic pack and release integration in Kyber-Squad.
/// Validates APM distribution packages, Agent Plugins v1.0.0 packages, repository root requirement,
/// output writing with SHA256 checksums, and exclusion of raw Squad corpus from CLI/MCP binary builds.
/// Covers Test Contract K9 from docs/plans/2026-08-14-kyber-squad-unified-agent-skill-deployment.md.
/// </summary>
public sealed class SquadPackAndReleaseTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private readonly InMemoryFileSystem _files = new();
    private readonly TestConsole _console = new() { EmitAnsiSequences = false };
    private readonly string _work;
    private readonly InMemorySquadServices _services;

    public SquadPackAndReleaseTests()
    {
        _work = Path.Combine(_files.Root, "work");
        _files.CreateDirectory(_work);
        _services = new InMemorySquadServices(_files);
    }

    private static readonly string[] CanonicalAgents =
    [
        "architect",
        "bug-crusher",
        "bug-crusher-investigator",
        "code-reviewer",
        "conductor",
        "dev-easy",
        "dev-hard",
        "dev-medium",
        "devops",
        "docs-dev",
        "platform-researcher",
        "product-owner",
        "research-agent",
        "review-lens",
        "review-triage"
    ];

    private static readonly string[] CanonicalSkills =
    [
        "app-docs-standard",
        "architecture-decision-record",
        "azure-cli",
        "azure-naming",
        "bug-crusher",
        "code-review",
        "code-review-loop",
        "create-pull-request",
        "csharp-dev",
        "csp-security",
        "dal-dev",
        "delivery-execution",
        "dp-code-reviewer",
        "github-cli",
        "github-devops",
        "go-dev",
        "kilo-review-loop",
        "lm-studio-cli",
        "maui-dev",
        "pr-review-fix-comments",
        "product-owner",
        "python-dev",
        "review-council",
        "rust-dev",
        "security-review",
        "setup-dev-environment",
        "task-gate",
        "typescript-dev"
    ];

    [Fact]
    public void Pack_Apm_GeneratesDeterministicArchiveWithNormalizedEntries()
    {
        // Arrange
        QualifiedSquadRepoFixture repo = QualifiedSquadRepoFixture.CreateValid(_files);
        string outDir1 = Path.Combine(_work, "apm-out1");
        string outDir2 = Path.Combine(_work, "apm-out2");
        _files.CreateDirectory(outDir1);
        _files.CreateDirectory(outDir2);

        FakeProcessExecutor executor = new FakeProcessExecutor();

        SquadPackCommand command1 = _services.PackCommand(repo.Path, _console);
        SquadPackCommand command2 = _services.PackCommand(repo.Path, _console);

        // Act
        CommandExecution exec1 = Capture(() => command1.Execute(null!, new SquadPackSettings { Format = "apm", Out = outDir1 }));
        CommandExecution exec2 = Capture(() => command2.Execute(null!, new SquadPackSettings { Format = "apm", Out = outDir2 }));

        // Assert
        Assert.Equal(0, exec1.ExitCode);
        Assert.Equal(0, exec2.ExitCode);

        string[] archives1 = GetFiles(outDir1, "kyber-squad-*.zip");
        string[] archives2 = GetFiles(outDir2, "kyber-squad-*.zip");

        Assert.NotEmpty(archives1);
        Assert.NotEmpty(archives2);

        string archivePath1 = archives1[0];
        string archivePath2 = archives2[0];

        // 1. Assert deterministic SHA-256 hash across multiple runs
        string hash1 = ComputeSha256(_files.ReadAllBytes(archivePath1));
        string hash2 = ComputeSha256(_files.ReadAllBytes(archivePath2));
        Assert.Equal(hash1, hash2);

        // 2. Inspect archive entries
        using ZipArchive archive = OpenZip(archivePath1);
        List<string> entryNames = archive.Entries.Select(e => e.FullName).ToList();

        // Strict ordinal entry ordering
        List<string> sortedEntryNames = entryNames.OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(sortedEntryNames, entryNames);

        // LF normalization on all text files
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (IsTextFile(entry.FullName))
            {
                using StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8);
                string text = reader.ReadToEnd();
                Assert.DoesNotContain("\r\n", text, StringComparison.Ordinal);
                Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
            }
        }

        // Presence of manifests and profiles
        Assert.Contains(entryNames, name => name == "squad.yml");
        Assert.Contains(entryNames, name => name == "toolchain.yml");
        Assert.Contains(entryNames, name => name == "bundles/full.yml");
        Assert.Contains(entryNames, name => name == "profiles/models.yml");
        Assert.Contains(entryNames, name => name == "profiles/capabilities.yml");
        Assert.Contains(entryNames, name => name == "profiles/fallbacks.yml");
        Assert.Contains(entryNames, name => name == "mcp.json");

        // Presence of all 15 canonical agents
        Assert.Equal(15, CanonicalAgents.Length);
        foreach (string agent in CanonicalAgents)
        {
            Assert.Contains(entryNames, name => name == $"agents/{agent}.md");
        }

        // Presence of all 28 canonical skills
        Assert.Equal(28, CanonicalSkills.Length);
        foreach (string skill in CanonicalSkills)
        {
            Assert.Contains(entryNames, name => name == $"skills/{skill}/SKILL.md" || name.StartsWith($"skills/{skill}/", StringComparison.Ordinal));
        }

        // No generated target render trees
        Assert.DoesNotContain(entryNames, name =>
            name.StartsWith(".codex/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".claude/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".cursor/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".opencode/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".kilo/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".warp/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".factory/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".pi/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".zcode/", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".devin/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The stub-tree tests above prove the packer's shape, not the shipped inventory. This packs the
    /// real canonical source so the APM package must carry the <c>task-gate</c> exit gate and must
    /// not carry the retired <c>task-reviewer</c> in any entry.
    /// </summary>
    [Fact]
    public void Pack_Apm_CanonicalSourceShipsTaskGateAndNoRetiredTaskReviewer()
    {
        string productRoot = QualifiedSquadRepoFixture.CopyShippedSource(_files, Path.Combine(_work, "shipped"));
        string archivePath = _services.Packer(_work).PackApm(productRoot, _work, "task-gate-inventory");
        using ZipArchive archive = OpenZip(archivePath);
        List<string> entryNames = archive.Entries.Select(entry => entry.FullName).ToList();

        Assert.Contains("skills/task-gate/SKILL.md", entryNames);
        Assert.DoesNotContain(entryNames, name => name.Contains("task-reviewer", StringComparison.Ordinal));
    }

    /// <summary>
    /// The retired <c>second-brain</c> documentation-architecture skill moves to Kyber-Docs
    /// (<c>.apm/skills/kyber-docs-onboarding-and-assessment</c>). The APM package ships the
    /// Squad bundle, so no entry may carry it in any path.
    /// </summary>
    [Fact]
    public void Pack_Apm_CanonicalSourceShipsNoRetiredSecondBrain()
    {
        string productRoot = QualifiedSquadRepoFixture.CopyShippedSource(_files, Path.Combine(_work, "shipped"));
        string archivePath = _services.Packer(_work).PackApm(productRoot, _work, "no-second-brain");
        using ZipArchive archive = OpenZip(archivePath);
        List<string> entryNames = archive.Entries.Select(entry => entry.FullName).ToList();

        Assert.DoesNotContain(entryNames, name => name.Contains("second-brain", StringComparison.Ordinal));
    }

    /// <summary>
    /// Pi reads project agents from <c>.pi/agents/</c>. A rendered Pi tree left inside the
    /// canonical source is target output, not corpus, and must not ship in the APM package.
    /// The source is seeded with one so the exclusion is exercised rather than vacuously true.
    /// </summary>
    [Fact]
    public void Pack_Apm_ExcludesAPiTargetTreeInsideTheSource()
    {
        // Arrange
        QualifiedSquadRepoFixture repo = QualifiedSquadRepoFixture.CreateValid(_files);
        repo.WriteSourceFile(
            ".pi/agents/x.md",
            "---\nname: x\ndescription: Rendered Pi agent.\ntools: read\n---\nYou are x.\n");
        string outDir = Path.Combine(_work, "apm-pi-out");
        SquadPackCommand command = _services.PackCommand(repo.Path, _console);

        // Act
        CommandExecution execution = Capture(() => command.Execute(null!, new SquadPackSettings { Format = "apm", Out = outDir }));

        // Assert
        Assert.True(execution.ExitCode == 0, execution.Output);
        string archivePath = Assert.Single(GetFiles(outDir, "kyber-squad-*.zip"));
        using ZipArchive archive = OpenZip(archivePath);
        List<string> entryNames = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("squad.yml", entryNames);
        Assert.DoesNotContain(entryNames, name => name.StartsWith(".pi/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Devin reads project agents from <c>.devin/agents/</c>. Like a Pi tree, a rendered Devin
    /// tree left inside the canonical source is target output and must not ship in the APM
    /// package; the source is seeded with one so the exclusion is exercised.
    /// </summary>
    [Fact]
    public void Pack_Apm_ExcludesADevinTargetTreeInsideTheSource()
    {
        // Arrange
        QualifiedSquadRepoFixture repo = QualifiedSquadRepoFixture.CreateValid(_files);
        repo.WriteSourceFile(
            ".devin/agents/x/AGENT.md",
            "---\nname: x\ndescription: Rendered Devin agent.\nallowed-tools:\n- read\n---\nYou are x.\n");
        string outDir = Path.Combine(_work, "apm-devin-out");
        SquadPackCommand command = _services.PackCommand(repo.Path, _console);

        // Act
        CommandExecution execution = Capture(() => command.Execute(null!, new SquadPackSettings { Format = "apm", Out = outDir }));

        // Assert
        Assert.True(execution.ExitCode == 0, execution.Output);
        string archivePath = Assert.Single(GetFiles(outDir, "kyber-squad-*.zip"));
        using ZipArchive archive = OpenZip(archivePath);
        List<string> entryNames = archive.Entries.Select(entry => entry.FullName).ToList();
        Assert.Contains("squad.yml", entryNames);
        Assert.DoesNotContain(entryNames, name => name.StartsWith(".devin/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pack_Plugins_GeneratesAgentPluginsV1Archive()
    {
        // Arrange
        QualifiedSquadRepoFixture repo = QualifiedSquadRepoFixture.CreateValid(_files);
        string outDir1 = Path.Combine(_work, "plugins-out1");
        string outDir2 = Path.Combine(_work, "plugins-out2");
        _files.CreateDirectory(outDir1);
        _files.CreateDirectory(outDir2);

        FakeProcessExecutor executor = new FakeProcessExecutor();

        SquadPackCommand command1 = _services.PackCommand(repo.Path, _console);
        SquadPackCommand command2 = _services.PackCommand(repo.Path, _console);

        // Act
        CommandExecution exec1 = Capture(() => command1.Execute(null!, new SquadPackSettings { Format = "plugins", Out = outDir1 }));
        CommandExecution exec2 = Capture(() => command2.Execute(null!, new SquadPackSettings { Format = "plugins", Out = outDir2 }));

        // Assert
        Assert.Equal(0, exec1.ExitCode);
        Assert.Equal(0, exec2.ExitCode);

        string[] archives1 = GetFiles(outDir1, "kyber-squad-plugin-*.zip");
        string[] archives2 = GetFiles(outDir2, "kyber-squad-plugin-*.zip");

        Assert.NotEmpty(archives1);
        Assert.NotEmpty(archives2);

        string archivePath1 = archives1[0];
        string archivePath2 = archives2[0];

        // 1. Assert deterministic SHA-256 hash across multiple runs
        string hash1 = ComputeSha256(_files.ReadAllBytes(archivePath1));
        string hash2 = ComputeSha256(_files.ReadAllBytes(archivePath2));
        Assert.Equal(hash1, hash2);

        // 2. Inspect archive entries
        using ZipArchive archive = OpenZip(archivePath1);
        List<string> entryNames = archive.Entries.Select(e => e.FullName).ToList();

        // Strict ordinal entry ordering
        List<string> sortedEntryNames = entryNames.OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(sortedEntryNames, entryNames);

        // LF normalization on all text files
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (IsTextFile(entry.FullName))
            {
                using StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8);
                string text = reader.ReadToEnd();
                Assert.DoesNotContain("\r\n", text, StringComparison.Ordinal);
                Assert.DoesNotContain("\r", text, StringComparison.Ordinal);
            }
        }

        // Portable components only: contains all 28 canonical skills
        foreach (string skill in CanonicalSkills)
        {
            Assert.Contains(entryNames, name => name == $"skills/{skill}/SKILL.md" || name.StartsWith($"skills/{skill}/", StringComparison.Ordinal));
        }

        // Contains MCP server declaration
        Assert.Contains(entryNames, name => name == "mcp.json" || name == ".mcp/mcp.json" || name == "plugin.json");

        // Strictly NO agents in the portable Agent Plugins v1.0.0 package
        Assert.DoesNotContain(entryNames, name => name.StartsWith("agents/", StringComparison.OrdinalIgnoreCase));

        foreach (string agent in CanonicalAgents)
        {
            Assert.DoesNotContain(entryNames, name => string.Equals(
                name,
                $"agents/{agent}.md",
                StringComparison.OrdinalIgnoreCase));
        }

        // If plugin.json is present, validate Agent Plugins v1.0.0 schema conformance
        ZipArchiveEntry? pluginJsonEntry = archive.GetEntry("plugin.json");
        if (pluginJsonEntry is not null)
        {
            using StreamReader reader = new StreamReader(pluginJsonEntry.Open(), Encoding.UTF8);
            string jsonText = reader.ReadToEnd();
            using JsonDocument jsonDoc = JsonDocument.Parse(jsonText);
            JsonElement root = jsonDoc.RootElement;

            if (root.TryGetProperty("$schema", out JsonElement schemaElement))
            {
                string schemaStr = schemaElement.GetString() ?? string.Empty;
                Assert.Contains("v1", schemaStr, StringComparison.OrdinalIgnoreCase);
            }

            Assert.False(root.TryGetProperty("agents", out _), "Agent Plugins v1 manifest must not declare portable agents.");
        }
    }

    /// <summary>
    /// APM is the complete canonical source distribution, while Agent Plugins is intentionally
    /// limited to skills and MCP. Recursive resources must preserve that boundary.
    /// </summary>
    [Fact]
    public void ResourceClosuresStayInsideApmAndAgentPluginPackageBoundaries()
    {
        QualifiedSquadRepoFixture repo = QualifiedSquadRepoFixture.CreateStub(_files);
        const string agentResource = "agents/conductor/references/plan-path.md";
        const string skillResource = "skills/code-review/references/configuration.md";
        const string agentContent = "# Plan path\n\nExecute a Ready plan.\n";
        const string skillContent = "# Review configuration\n\nThe host's review keys.\n";
        repo.AddResourceClosure(agentResource, agentContent, skillResource, skillContent);
        string outDirectory = Path.Combine(_work, "resource-pack");
        SquadPackCommand command = _services.PackCommand(repo.Path, _console);

        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadPackSettings
            {
                Format = "all",
                Out = outDirectory
            }));

        Assert.Equal(0, execution.ExitCode);
        string apmPath = GetFiles(outDirectory, "kyber-squad-*.zip")
            .Single(path => !Path.GetFileName(path).StartsWith(
                "kyber-squad-plugin-",
                StringComparison.Ordinal));
        string pluginPath = Assert.Single(GetFiles(
            outDirectory,
            "kyber-squad-plugin-*.zip"));
        using ZipArchive apm = OpenZip(apmPath);
        using ZipArchive plugin = OpenZip(pluginPath);

        Assert.Equal(agentContent, ReadArchiveText(apm, agentResource));
        Assert.Equal(skillContent, ReadArchiveText(apm, skillResource));
        Assert.Equal(skillContent, ReadArchiveText(plugin, skillResource));
        Assert.Null(plugin.GetEntry("agents/conductor.md"));
        Assert.Null(plugin.GetEntry(agentResource));
        Assert.Equal(
            apm.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal),
            apm.Entries.Select(entry => entry.FullName));
        Assert.Equal(
            plugin.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal),
            plugin.Entries.Select(entry => entry.FullName));
    }

    [Theory]
    [InlineData("empty-dir")]
    [InlineData("sln-only")]
    [InlineData("squad-yml-only")]
    [InlineData("child-dir")]
    public void Pack_WhenNotRepositoryRootOrNoProductsDirectory_FailsWithCleanDiagnostic(string scenario)
    {
        // Arrange
        string workingDir = Path.Combine(_work, scenario);
        _files.CreateDirectory(workingDir);
        string outDir = Path.Combine(_work, $"pack-out-{scenario}");
        QualifiedSquadRepoFixture? repo = null;

        if (scenario == "sln-only")
        {
            _files.WriteAllText(Path.Combine(workingDir, "KyberWeave.sln"), "solution");
        }
        else if (scenario == "squad-yml-only")
        {
            _files.CreateDirectory(Path.Combine(workingDir, "products", "kyber-squad"));
            _files.WriteAllText(Path.Combine(workingDir, "products", "kyber-squad", "squad.yml"), "schema: kyber-squad.squad/v1");
        }
        else if (scenario == "child-dir")
        {
            repo = QualifiedSquadRepoFixture.CreateValid(_files);
            workingDir = Path.Combine(repo.Path, "src", "KyberWeave.Cli");
            _files.CreateDirectory(workingDir);
        }

        FakeProcessExecutor executor = new FakeProcessExecutor();
        SquadPackCommand command = _services.PackCommand(workingDir, _console);

        // Act
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadPackSettings
            {
                Format = "all",
                Out = outDir
            }));

        // Assert
        Assert.Equal(1, execution.ExitCode);
        Assert.Contains("maintainer-only", execution.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("KyberWeave.sln", execution.Output, StringComparison.Ordinal);
        Assert.Contains("products/kyber-squad/squad.yml", execution.Output, StringComparison.Ordinal);
        Assert.Contains("squad install", execution.Output, StringComparison.Ordinal);

        // Ensure out directory was not created and no process calls were made
        Assert.False(_files.DirectoryExists(outDir));
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public void Pack_WithExplicitOutDirectory_WritesArchivesAndChecksumFile()
    {
        // Arrange
        QualifiedSquadRepoFixture repo = QualifiedSquadRepoFixture.CreateValid(_files);
        string outDir = Path.Combine(_work, "release-dist");

        FakeProcessExecutor executor = new FakeProcessExecutor();
        SquadPackCommand command = _services.PackCommand(repo.Path, _console);

        // Act
        CommandExecution execution = Capture(() => command.Execute(
            null!,
            new SquadPackSettings
            {
                Format = "all",
                Out = outDir
            }));

        // Assert
        Assert.Equal(0, execution.ExitCode);
        Assert.True(_files.DirectoryExists(outDir));

        string[] apmArchives = GetFiles(outDir, "kyber-squad-*.zip")
            .Where(f => !Path.GetFileName(f).StartsWith("kyber-squad-plugin-", StringComparison.Ordinal))
            .ToArray();
        string[] pluginArchives = GetFiles(outDir, "kyber-squad-plugin-*.zip");

        Assert.Single(apmArchives);
        Assert.Single(pluginArchives);

        string apmArchive = apmArchives[0];
        string pluginArchive = pluginArchives[0];

        // Verify checksum file
        string checksumFile = Path.Combine(outDir, "SHA256SUMS.txt");
        Assert.True(_files.FileExists(checksumFile), "Pack with format 'all' must generate SHA256SUMS.txt in the output directory.");

        string checksumContent = _files.ReadAllText(checksumFile);
        string apmHash = ComputeSha256(_files.ReadAllBytes(apmArchive));
        string pluginHash = ComputeSha256(_files.ReadAllBytes(pluginArchive));

        Assert.Contains($"{apmHash}  {Path.GetFileName(apmArchive)}", checksumContent, StringComparison.Ordinal);
        Assert.Contains($"{pluginHash}  {Path.GetFileName(pluginArchive)}", checksumContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseArtifact_AssertsCliAndMcpArchivesContainNoEmbeddedSquadCorpus()
    {
        // Assert that CLI, Core, Squad, Docs and MCP assemblies do not bundle the raw products/kyber-squad corpus in embedded resources
        Assembly cliAssembly = typeof(SquadPackCommand).Assembly;
        Assembly coreAssembly = typeof(KyberWeave.Core.Diagnostics.DiagnosticReport).Assembly;
        Assembly squadAssembly = typeof(SquadSource).Assembly;
        Assembly docsAssembly = typeof(KyberStandardsTemplates).Assembly;
        Assembly mcpAssembly = typeof(DocsTools).Assembly;
        Assembly domainAssembly = typeof(CopilotPromptFile).Assembly;

        string[] cliResources = cliAssembly.GetManifestResourceNames();
        string[] coreResources = coreAssembly.GetManifestResourceNames();
        string[] squadResources = squadAssembly.GetManifestResourceNames();
        string[] docsResources = docsAssembly.GetManifestResourceNames();
        string[] mcpResources = mcpAssembly.GetManifestResourceNames();
        string[] domainResources = domainAssembly.GetManifestResourceNames();

        string[] forbiddenCorpusTokens =
        [
            "products",
            "kyber-squad",
            "squad.yml",
            "bundles",
            "profiles",
            "agents"
        ];

        foreach (string token in forbiddenCorpusTokens)
        {
            Assert.DoesNotContain(cliResources, name => name.Contains(token, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(coreResources, name => name.Contains(token, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(squadResources, name => name.Contains(token, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(docsResources, name => name.Contains(token, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(mcpResources, name => name.Contains(token, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(domainResources, name => name.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        // Also assert that project files do not declare EmbeddedResource or Content targeting products/kyber-squad
        string[] projectFilePaths =
        [
            "src/KyberWeave.Cli/KyberWeave.Cli.csproj",
            "src/KyberWeave.Mcp/KyberWeave.Mcp.csproj",
            "src/KyberWeave.Domain/KyberWeave.Domain.csproj",
            .. LayerPropsFiles("KyberWeave.Domain")
        ];

        foreach (string projPath in projectFilePaths)
        {
            XDocument projDoc = XDocument.Parse(Corpus.ReadText(projPath));

            foreach (XElement element in projDoc.Descendants("EmbeddedResource").Concat(projDoc.Descendants("Content")))
            {
                string? path = element.Attribute("Include")?.Value;
                if (path is null)
                {
                    continue;
                }

                // Standalone standards resources (for example LogicalName-style
                // Standards.*.md) are not the Squad corpus. A path that mentions
                // products or kyber-squad still is, including
                // products/kyber-squad/standards/..., and must be checked.
                if (path.Contains("standards", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains("products", StringComparison.OrdinalIgnoreCase) &&
                    !path.Contains("kyber-squad", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // The Arbiter's embedded agent and lens copies are the same kind of
                // deliberate exception: host repositories have no canonical tree, so
                // the rules read these copies instead.
                if (IsAllowedArbiterEmbeddedInclude(path, element.Attribute("LogicalName")?.Value))
                {
                    continue;
                }

                if (TryGetAllowedKyberStandardTemplateInclude(path, out string standardLogicalName))
                {
                    Assert.Equal(
                        standardLogicalName,
                        element.Attribute("LogicalName")?.Value);
                    continue;
                }

                if (TryGetAllowedGateCatalogueInclude(path, out string family))
                {
                    Assert.Equal(
                        $"Gates.{family}.yml",
                        element.Attribute("LogicalName")?.Value);
                    continue;
                }

                if (IsAllowedCopilotPromptInclude(path))
                {
                    Assert.Equal(
                        "CopilotPrompt.system-prompt.yaml",
                        element.Attribute("LogicalName")?.Value);
                    continue;
                }

                Assert.DoesNotContain("products", path, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("kyber-squad", path, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    #region Helper Fixtures and Methods

    private static string ComputeSha256(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// Core may embed the shipped standards templates from
    /// <c>products/kyber-squad/standards/&lt;name&gt;/&lt;name&gt;.&lt;part&gt;</c> and the shared
    /// rules file, only when LogicalName is <c>Standards.&lt;file&gt;</c>: the embedded name keeps
    /// the file's own name, which already begins with its folder's. Any other
    /// products/kyber-squad path is the prohibited corpus, even if it contains the substring
    /// "standards".
    /// </summary>
    private static bool TryGetAllowedKyberStandardTemplateInclude(string path, out string logicalName)
    {
        string normalized = path.Replace('\\', '/');
        foreach (string scope in KyberStandardsTemplates.AllFolders)
        {
            foreach (KyberStandardsTemplates.Part part in Enum.GetValues<KyberStandardsTemplates.Part>())
            {
                string file = KyberStandardsTemplates.FileName(scope, part);
                if (normalized.EndsWith(
                        $"products/kyber-squad/standards/{scope}/{file}",
                        StringComparison.OrdinalIgnoreCase))
                {
                    logicalName = $"Standards.{file}";
                    return true;
                }
            }
        }

        if (normalized.EndsWith(
                "products/kyber-squad/standards/shared-rules.md",
                StringComparison.OrdinalIgnoreCase))
        {
            logicalName = "Standards.shared-rules.md";
            return true;
        }

        logicalName = string.Empty;
        return false;
    }

    /// <summary>
    /// Core may embed the Arbiter's Squad agent and lens copies from
    /// <c>products/kyber-squad/agents/*.md</c> and
    /// <c>products/kyber-squad/skills/code-review/references/lenses/*.md</c> only,
    /// non-recursive, with logical names under <c>Arbiter.Roster.</c> and
    /// <c>Arbiter.Lens.</c> respectively.
    /// Their logical names carry no raw-corpus token, so the resource-name check above
    /// already passes them; this covers the project-file half. Any other
    /// products/kyber-squad path stays prohibited.
    /// </summary>
    private static bool IsAllowedArbiterEmbeddedInclude(string path, string? logicalName)
    {
        string normalized = path.Replace('\\', '/');
        if (IsSingleSegmentMarkdownUnder(normalized, "products/kyber-squad/agents/"))
        {
            return (logicalName ?? string.Empty).StartsWith("Arbiter.Roster.", StringComparison.Ordinal);
        }

        if (IsSingleSegmentMarkdownUnder(normalized, "products/kyber-squad/skills/code-review/references/lenses/"))
        {
            return (logicalName ?? string.Empty).StartsWith("Arbiter.Lens.", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool IsSingleSegmentMarkdownUnder(string normalizedPath, string prefix)
    {
        int marker = normalizedPath.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        string remainder = normalizedPath[(marker + prefix.Length)..];
        return remainder.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && !remainder.Contains('/', StringComparison.Ordinal);
    }

    [Fact]
    public void Fix5_ArbiterEmbeddedInclude_RejectsNestedPaths()
    {
        Assert.False(IsAllowedArbiterEmbeddedInclude(
            "..\\..\\products\\kyber-squad\\agents\\nested\\evil.md", "Arbiter.Roster.evil.md"));
        Assert.False(IsAllowedArbiterEmbeddedInclude(
            "../../products/kyber-squad/skills/code-review/references/lenses/nested/evil.md", "Arbiter.Lens.evil.md"));
        Assert.False(IsAllowedArbiterEmbeddedInclude(
            "..\\..\\products\\kyber-squad\\agents\\conductor.md", "Raw.conductor.md"));
        Assert.False(IsAllowedArbiterEmbeddedInclude(
            "..\\..\\products\\kyber-squad\\agents\\conductor.md", null));
        Assert.True(IsAllowedArbiterEmbeddedInclude(
            "..\\..\\products\\kyber-squad\\agents\\conductor.md", "Arbiter.Roster.conductor.md"));
        Assert.True(IsAllowedArbiterEmbeddedInclude(
            "..\\..\\products\\kyber-squad\\skills\\code-review\\references\\lenses\\scope.md", "Arbiter.Lens.scope.md"));
    }

    /// <summary>
    /// Core may embed exactly one file from <c>products/kyber-squad/copilot/system-prompt.yaml</c>: the
    /// opt-in Copilot system-prompt text, which the loop then requires to carry the
    /// <c>CopilotPrompt.system-prompt.yaml</c> logical name. Its path is a single fixed file, not a folder
    /// the rule opens, so no sibling or nested product path matches.
    /// </summary>
    private static bool IsAllowedCopilotPromptInclude(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.EndsWith("products/kyber-squad/copilot/system-prompt.yaml", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CopilotPromptInclude_AllowsOnlyTheExactSystemPromptFile()
    {
        Assert.True(IsAllowedCopilotPromptInclude("..\\..\\products\\kyber-squad\\copilot\\system-prompt.yaml"));
        Assert.True(IsAllowedCopilotPromptInclude("../../products/kyber-squad/copilot/system-prompt.yaml"));
        Assert.False(IsAllowedCopilotPromptInclude("../../products/kyber-squad/copilot/other.yaml"));
        Assert.False(IsAllowedCopilotPromptInclude("../../products/kyber-squad/copilot/nested/system-prompt.yaml"));
        Assert.False(IsAllowedCopilotPromptInclude("../../products/kyber-squad/standards/csharp/gates.yml"));
        Assert.False(IsAllowedCopilotPromptInclude("../../products/kyber-squad/copilot/system-prompt.yaml.bak"));
    }

    /// <summary>
    /// Core may embed the five gate catalogue family files from
    /// <c>products/kyber-squad/standards/&lt;family&gt;/gates.yml</c> only when LogicalName
    /// remaps them to <c>Gates.&lt;family&gt;.yml</c>. They are configuration, not the Squad corpus.
    /// </summary>
    private static bool TryGetAllowedGateCatalogueInclude(string path, out string family)
    {
        string normalized = path.Replace('\\', '/');
        string[] families = ["csharp", "typescript", "rust", "go", "python"];
        foreach (string candidate in families)
        {
            if (normalized.EndsWith(
                    $"products/kyber-squad/standards/{candidate}/gates.yml",
                    StringComparison.OrdinalIgnoreCase))
            {
                family = candidate;
                return true;
            }
        }

        family = string.Empty;
        return false;
    }

    private static readonly string[] TextFileExtensions =
        [".md", ".yml", ".yaml", ".json", ".toml", ".txt"];

    /// <summary>
    /// A layer keeps its resources and packages in <c>Areas/&lt;Area&gt;.props</c>, imported by its
    /// project file, so a layer can gain an embedded resource without every agent editing the same
    /// project file. Those files declare resources too, so the corpus scan has to read them.
    /// </summary>
    private static string[] LayerPropsFiles(string project) =>
        [.. Corpus.EnumerateFiles($"src/{project}/Areas", "*.props", recursive: false)];

    private static bool IsTextFile(string entryName) =>
        TextFileExtensions.Contains(
            Path.GetExtension(entryName),
            StringComparer.OrdinalIgnoreCase);

    private static string ReadArchiveText(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry(entryName));
        using StreamReader reader = new(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private CommandExecution Capture(Func<int> execute)
    {
        int exitCode = execute();
        return new CommandExecution(exitCode, _console.Output);
    }

    private string[] GetFiles(string directory, string pattern) =>
        [.. _files.EnumerateFiles(directory, pattern, recursive: false)];

    private ZipArchive OpenZip(string archivePath) => new(new MemoryStream(_files.ReadAllBytes(archivePath)));

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed class QualifiedSquadRepoFixture
    {
        private static int _counter;
        private readonly InMemoryFileSystem _files;

        private QualifiedSquadRepoFixture(InMemoryFileSystem files)
        {
            _files = files;
            Path = System.IO.Path.Combine(files.Root, $"repo-{Interlocked.Increment(ref _counter)}");
            files.CreateDirectory(Path);
        }

        public string Path { get; }

        public static QualifiedSquadRepoFixture CreateValid(InMemoryFileSystem files)
        {
            QualifiedSquadRepoFixture fixture = new(files);

            // Solution marker
            fixture.Write("KyberWeave.sln", "Microsoft Visual Studio Solution File, Format Version 12.00");

            // Copy product source from the shipped corpus if available, or write all 21 agents and 24 skills
            if (ShippedSquadSource.Exists)
            {
                CopyShippedSource(files, System.IO.Path.Combine(fixture.Path, "products", "kyber-squad"));
                // Update toolchain.yml to have a qualifying validated-release
                fixture.Write("products/kyber-squad/toolchain.yml", """
                    schema: kyber-squad.toolchain/v1
                    required-features:
                      - agent-ir/v1
                      - semantic-permissions/v1
                      - structured-degradation/v1
                      - agent-to-skill-lowering/v1
                    validated-release:
                      version: 0.29.0
                      tag-commit: e041462f4a48086dbee3da145c07d71b8a3b84fd
                      asset-sha256: e041462f4a48086dbee3da145c07d71b8a3b84fde041462f4a48086dbee3da14
                    """);
            }
            else
            {
                fixture.WriteStubSquadSource();
            }

            return fixture;
        }

        public static QualifiedSquadRepoFixture CreateStub(InMemoryFileSystem files)
        {
            QualifiedSquadRepoFixture fixture = new(files);
            fixture.Write(
                "KyberWeave.sln",
                "Microsoft Visual Studio Solution File, Format Version 12.00");
            fixture.WriteStubSquadSource();
            return fixture;
        }

        /// <summary>Copies the shipped <c>products/kyber-squad</c> tree into the in-memory file system.</summary>
        /// <param name="files">The file system to copy into.</param>
        /// <param name="destinationDir">Where the tree lands.</param>
        /// <returns>The destination directory.</returns>
        public static string CopyShippedSource(InMemoryFileSystem files, string destinationDir) =>
            ShippedSquadSource.CopyInto(files, destinationDir);

        private void Write(string relativePath, string content)
        {
            string fullPath = System.IO.Path.Combine(Path, relativePath);
            _files.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            _files.WriteAllBytes(fullPath, new UTF8Encoding(false).GetBytes(content));
        }

        private void Append(string fullPath, string content) =>
            _files.WriteAllBytes(
                fullPath,
                [.. _files.ReadAllBytes(fullPath), .. new UTF8Encoding(false).GetBytes(content)]);

        public void AddResourceClosure(
            string agentResource,
            string agentContent,
            string skillResource,
            string skillContent)
        {
            string agentPath = System.IO.Path.Combine(Path, "products", "kyber-squad", "agents", "conductor.md");
            Append(agentPath, "\n[Plan path](conductor/references/plan-path.md)\n");
            Write($"products/kyber-squad/{agentResource}", agentContent);

            string[] segments = skillResource.Split('/');
            string skillPath = System.IO.Path.Combine(
                [Path, "products", "kyber-squad", "skills", segments[1], "SKILL.md"]);
            string resource = string.Join('/', segments[2..]);
            Append(skillPath, $"\n[Resource]({resource})\n");
            Write($"products/kyber-squad/{skillResource}", skillContent);
        }

        public void WriteSourceFile(string sourceRelativePath, string content) =>
            Write($"products/kyber-squad/{sourceRelativePath}", content);

        /// <summary>
        /// A loadable Squad tree for hosts that do not have <c>products/kyber-squad</c>. Every
        /// path <c>squad.yml</c> names must exist, plus stubs for the canonical agents and
        /// skills, or <c>SquadSourceLoader.Load</c> rejects the archive before packing.
        /// </summary>
        private void WriteStubSquadSource()
        {
            const string root = "products/kyber-squad";
            Write($"{root}/squad.yml", """
                schema: kyber-squad.squad/v1
                name: kyber-squad
                version-source: kyber-weave-assembly
                default-bundle: full
                bundles:
                  full: bundles/full.yml
                profiles:
                  models: profiles/models.yml
                  capabilities: profiles/capabilities.yml
                  fallbacks: profiles/fallbacks.yml
                toolchain: toolchain.yml
                mcp: mcp.json
                """);
            string agentEntries = string.Join("\n", CanonicalAgents.Select(name => "  - " + name));
            string skillEntries = string.Join("\n", CanonicalSkills.Select(name => "  - " + name));
            Write($"{root}/bundles/full.yml",
                "schema: kyber-squad.bundle/v1\n" +
                "name: full\n" +
                "agents:\n" +
                agentEntries + "\n" +
                "skills:\n" +
                skillEntries + "\n");
            Write($"{root}/profiles/models.yml", """
                schema: kyber-squad.model-profiles/v1
                profiles:
                  general:
                    default: inherit
                """);
            Write($"{root}/profiles/capabilities.yml", """
                schema: kyber-squad.capability-profiles/v1
                capabilities:
                  - filesystem.read
                  - filesystem.write
                  - delegate
                profiles:
                  worker:
                    permissions:
                      filesystem.read: allow
                      filesystem.write: ask
                      delegate: deny
                """);
            Write($"{root}/profiles/fallbacks.yml", """
                schema: kyber-squad.fallback-profiles/v1
                profiles:
                  role-skill:
                    no-primary-agent: skill
                    no-agent-primitive: skill
                """);
            Write($"{root}/toolchain.yml", """
                schema: kyber-squad.toolchain/v1
                required-features:
                  - agent-ir/v1
                validated-release:
                  version: 0.29.0
                  tag-commit: e041462f4a48086dbee3da145c07d71b8a3b84fd
                  asset-sha256: e041462f4a48086dbee3da145c07d71b8a3b84fde041462f4a48086dbee3da14
                """);
            Write($"{root}/mcp.json", """
                {
                  "mcpServers": {
                    "kyber-weave": {
                      "command": "kyber-weave-mcp",
                      "args": ["--repo-root", "."]
                    }
                  }
                }
                """);

            string[] schemas = ["squad", "bundle", "agent", "model-profiles", "capability-profiles"];
            foreach (string schema in schemas)
            {
                Write($"{root}/schemas/{schema}.schema.json", """
                    {
                      "$schema": "https://json-schema.org/draft/2020-12/schema",
                      "type": "object"
                    }
                    """);
            }

            Write($"{root}/schemas/fallback-profiles.schema.json", """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "$id": "https://kyber-weave.dev/schemas/kyber-squad/fallback-profiles/v1",
                  "type": "object"
                }
                """);

            foreach (string agent in CanonicalAgents)
            {
                Write($"{root}/agents/{agent}.md",
                    "---\n" +
                    "schema: kyber-squad.agent/v1\n" +
                    $"name: {agent}\n" +
                    $"description: Use when acting as {agent}.\n" +
                    "invocation: subagent\n" +
                    "model-profile: general\n" +
                    "capability-profile: worker\n" +
                    "copilot-tools: [vscode]\n" +
                    "delegates-to: []\n" +
                    "fallback: role-skill\n" +
                    "aliases: []\n" +
                    "---\n" +
                    $"You are {agent}.\n");
            }

            foreach (string skill in CanonicalSkills)
            {
                Write($"{root}/skills/{skill}/SKILL.md",
                    "---\n" +
                    $"name: {skill}\n" +
                    $"description: Use when acting as {skill}.\n" +
                    "license: MIT\n" +
                    "metadata:\n" +
                    "  author: Kyber-Weave\n" +
                    "  version: 1.0.0\n" +
                    "---\n" +
                    $"# {skill}\n");
            }
        }

    }

    #endregion
}
