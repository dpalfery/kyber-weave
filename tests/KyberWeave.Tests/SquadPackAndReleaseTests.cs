using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Mcp;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Task K9 Test Suite: Contract K9 for deterministic pack and release integration in Kyber-Squad.
/// Validates APM distribution packages, Agent Plugins v1.0.0 packages, repository root requirement,
/// output writing with SHA256 checksums, and exclusion of raw Squad corpus from CLI/MCP binary builds.
/// Covers Test Contract K9 from docs/plans/2026-08-14-kyber-squad-unified-agent-skill-deployment.md.
/// </summary>
public sealed class SquadPackAndReleaseTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static readonly string[] CanonicalAgents =
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
        "review-lens",
        "review-triage",
        "sql-database-architect",
        "task-reviewer",
        "task-reviewer-v3",
        "tauri-dev",
        "test-dev"
    ];

    private static readonly string[] CanonicalSkills =
    [
        "app-docs-standard",
        "architecture-decision-record",
        "azure-cli",
        "azure-naming",
        "bug-crusher",
        "code-review",
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
        "resharper-clt",
        "second-brain",
        "security-review",
        "setup-dev-environment",
        "test-dev"
    ];

    [Fact]
    public void Pack_Apm_GeneratesDeterministicArchiveWithNormalizedEntries()
    {
        // Arrange
        using QualifiedSquadRepoFixture repo = QualifiedSquadRepoFixture.CreateValid();
        string outDir1 = Path.Combine(_temp.Path, "apm-out1");
        string outDir2 = Path.Combine(_temp.Path, "apm-out2");
        Directory.CreateDirectory(outDir1);
        Directory.CreateDirectory(outDir2);

        FakeProcessExecutor executor = new FakeProcessExecutor();

        SquadPackCommand command1 = new SquadPackCommand(executor, workingDirectory: repo.Path);
        SquadPackCommand command2 = new SquadPackCommand(executor, workingDirectory: repo.Path);

        // Act
        CommandExecution exec1 = Capture(() => command1.Execute(null!, new SquadPackSettings { Format = "apm", Out = outDir1 }));
        CommandExecution exec2 = Capture(() => command2.Execute(null!, new SquadPackSettings { Format = "apm", Out = outDir2 }));

        // Assert
        Assert.Equal(0, exec1.ExitCode);
        Assert.Equal(0, exec2.ExitCode);

        string[] archives1 = Directory.GetFiles(outDir1, "kyber-squad-*.zip");
        string[] archives2 = Directory.GetFiles(outDir2, "kyber-squad-*.zip");

        Assert.NotEmpty(archives1);
        Assert.NotEmpty(archives2);

        string archivePath1 = archives1[0];
        string archivePath2 = archives2[0];

        // 1. Assert deterministic SHA-256 hash across multiple runs
        string hash1 = ComputeSha256(File.ReadAllBytes(archivePath1));
        string hash2 = ComputeSha256(File.ReadAllBytes(archivePath2));
        Assert.Equal(hash1, hash2);

        // 2. Inspect archive entries
        using ZipArchive archive = ZipFile.OpenRead(archivePath1);
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
                Assert.DoesNotContain("\r\n", text);
                Assert.DoesNotContain("\r", text);
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

        // Presence of all 24 canonical agents
        Assert.Equal(24, CanonicalAgents.Length);
        foreach (string agent in CanonicalAgents)
        {
            Assert.Contains(entryNames, name => name == $"agents/{agent}.md");
        }

        // Presence of all 24 canonical skills
        Assert.Equal(24, CanonicalSkills.Length);
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
            name.StartsWith(".factory/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pack_Plugins_GeneratesAgentPluginsV1Archive()
    {
        // Arrange
        using QualifiedSquadRepoFixture repo = QualifiedSquadRepoFixture.CreateValid();
        string outDir1 = Path.Combine(_temp.Path, "plugins-out1");
        string outDir2 = Path.Combine(_temp.Path, "plugins-out2");
        Directory.CreateDirectory(outDir1);
        Directory.CreateDirectory(outDir2);

        FakeProcessExecutor executor = new FakeProcessExecutor();

        SquadPackCommand command1 = new SquadPackCommand(executor, workingDirectory: repo.Path);
        SquadPackCommand command2 = new SquadPackCommand(executor, workingDirectory: repo.Path);

        // Act
        CommandExecution exec1 = Capture(() => command1.Execute(null!, new SquadPackSettings { Format = "plugins", Out = outDir1 }));
        CommandExecution exec2 = Capture(() => command2.Execute(null!, new SquadPackSettings { Format = "plugins", Out = outDir2 }));

        // Assert
        Assert.Equal(0, exec1.ExitCode);
        Assert.Equal(0, exec2.ExitCode);

        string[] archives1 = Directory.GetFiles(outDir1, "kyber-squad-plugin-*.zip");
        string[] archives2 = Directory.GetFiles(outDir2, "kyber-squad-plugin-*.zip");

        Assert.NotEmpty(archives1);
        Assert.NotEmpty(archives2);

        string archivePath1 = archives1[0];
        string archivePath2 = archives2[0];

        // 1. Assert deterministic SHA-256 hash across multiple runs
        string hash1 = ComputeSha256(File.ReadAllBytes(archivePath1));
        string hash2 = ComputeSha256(File.ReadAllBytes(archivePath2));
        Assert.Equal(hash1, hash2);

        // 2. Inspect archive entries
        using ZipArchive archive = ZipFile.OpenRead(archivePath1);
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
                Assert.DoesNotContain("\r\n", text);
                Assert.DoesNotContain("\r", text);
            }
        }

        // Portable components only: contains all 24 canonical skills
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
            Assert.DoesNotContain(entryNames, name => name.EndsWith($"{agent}.md", StringComparison.OrdinalIgnoreCase));
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

    [Theory]
    [InlineData("empty-dir")]
    [InlineData("sln-only")]
    [InlineData("squad-yml-only")]
    [InlineData("child-dir")]
    public void Pack_WhenNotRepositoryRootOrNoProductsDirectory_FailsWithCleanDiagnostic(string scenario)
    {
        // Arrange
        string workingDir = Path.Combine(_temp.Path, scenario);
        Directory.CreateDirectory(workingDir);
        string outDir = Path.Combine(_temp.Path, $"pack-out-{scenario}");
        QualifiedSquadRepoFixture? repo = null;

        if (scenario == "sln-only")
        {
            File.WriteAllText(Path.Combine(workingDir, "KyberWeave.sln"), "solution");
        }
        else if (scenario == "squad-yml-only")
        {
            Directory.CreateDirectory(Path.Combine(workingDir, "products", "kyber-squad"));
            File.WriteAllText(Path.Combine(workingDir, "products", "kyber-squad", "squad.yml"), "schema: kyber-squad.squad/v1");
        }
        else if (scenario == "child-dir")
        {
            repo = QualifiedSquadRepoFixture.CreateValid();
            workingDir = Path.Combine(repo.Path, "src", "KyberWeave.Cli");
            Directory.CreateDirectory(workingDir);
        }

        using QualifiedSquadRepoFixture? ownedRepo = repo;

        FakeProcessExecutor executor = new FakeProcessExecutor();
        SquadPackCommand command = new SquadPackCommand(executor, workingDirectory: workingDir);

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
        Assert.False(Directory.Exists(outDir));
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public void Pack_WithExplicitOutDirectory_WritesArchivesAndChecksumFile()
    {
        // Arrange
        using QualifiedSquadRepoFixture repo = QualifiedSquadRepoFixture.CreateValid();
        string outDir = Path.Combine(_temp.Path, "release-dist");

        FakeProcessExecutor executor = new FakeProcessExecutor();
        SquadPackCommand command = new SquadPackCommand(executor, workingDirectory: repo.Path);

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
        Assert.True(Directory.Exists(outDir));

        string[] apmArchives = Directory.GetFiles(outDir, "kyber-squad-*.zip")
            .Where(f => !Path.GetFileName(f).StartsWith("kyber-squad-plugin-", StringComparison.Ordinal))
            .ToArray();
        string[] pluginArchives = Directory.GetFiles(outDir, "kyber-squad-plugin-*.zip");

        Assert.Single(apmArchives);
        Assert.Single(pluginArchives);

        string apmArchive = apmArchives[0];
        string pluginArchive = pluginArchives[0];

        // Verify checksum file
        string checksumFile = Path.Combine(outDir, "SHA256SUMS.txt");
        Assert.True(File.Exists(checksumFile), "Pack with format 'all' must generate SHA256SUMS.txt in the output directory.");

        string checksumContent = File.ReadAllText(checksumFile);
        string apmHash = ComputeSha256(File.ReadAllBytes(apmArchive));
        string pluginHash = ComputeSha256(File.ReadAllBytes(pluginArchive));

        Assert.Contains($"{apmHash}  {Path.GetFileName(apmArchive)}", checksumContent);
        Assert.Contains($"{pluginHash}  {Path.GetFileName(pluginArchive)}", checksumContent);
    }

    [Fact]
    public void ReleaseArtifact_AssertsCliAndMcpArchivesContainNoEmbeddedSquadCorpus()
    {
        // Assert that CLI, Core, and MCP assemblies do not bundle the raw products/kyber-squad corpus in embedded resources
        Assembly cliAssembly = typeof(SquadPackCommand).Assembly;
        Assembly coreAssembly = typeof(SquadSource).Assembly;
        Assembly mcpAssembly = typeof(DocsTools).Assembly;

        string[] cliResources = cliAssembly.GetManifestResourceNames();
        string[] coreResources = coreAssembly.GetManifestResourceNames();
        string[] mcpResources = mcpAssembly.GetManifestResourceNames();

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
            Assert.DoesNotContain(mcpResources, name => name.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        // Also assert that project files do not declare EmbeddedResource or Content targeting products/kyber-squad
        string toolRoot = KyberWeaveTestPaths.ToolRoot;
        string[] projectFilePaths =
        [
            Path.Combine(toolRoot, "src", "KyberWeave.Cli", "KyberWeave.Cli.csproj"),
            Path.Combine(toolRoot, "src", "KyberWeave.Core", "KyberWeave.Core.csproj"),
            Path.Combine(toolRoot, "src", "KyberWeave.Mcp", "KyberWeave.Mcp.csproj")
        ];

        foreach (string projPath in projectFilePaths)
        {
            XDocument projDoc = XDocument.Load(projPath);

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

                if (TryGetAllowedKyberStandardTemplateInclude(path, out string technology))
                {
                    Assert.Equal(
                        $"Standards.{technology}.md",
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
    /// Core may embed the ten coding-standard READMEs from
    /// <c>products/kyber-squad/standards/&lt;tech&gt;/README.md</c> only when LogicalName
    /// remaps them to <c>Standards.&lt;tech&gt;.md</c>. Any other products/kyber-squad path
    /// is the prohibited corpus, even if it contains the substring "standards".
    /// </summary>
    private static bool TryGetAllowedKyberStandardTemplateInclude(string path, out string technology)
    {
        string normalized = path.Replace('\\', '/');
        foreach (string candidate in KyberStandardsTemplates.All)
        {
            if (normalized.EndsWith(
                    $"products/kyber-squad/standards/{candidate}/README.md",
                    StringComparison.OrdinalIgnoreCase))
            {
                technology = candidate;
                return true;
            }
        }

        technology = string.Empty;
        return false;
    }

    private static bool IsTextFile(string entryName)
    {
        string ext = Path.GetExtension(entryName).ToLowerInvariant();
        return ext is ".md" or ".yml" or ".yaml" or ".json" or ".toml" or ".txt";
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);

    private sealed class QualifiedSquadRepoFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public string Path => _temp.Path;

        public static QualifiedSquadRepoFixture CreateValid()
        {
            QualifiedSquadRepoFixture fixture = new QualifiedSquadRepoFixture();

            // Solution marker
            fixture.Write("KyberWeave.sln", "Microsoft Visual Studio Solution File, Format Version 12.00");

            // Copy product source from real repo if available, or write all 24 agents and 24 skills
            string realSquadSource = System.IO.Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");
            if (Directory.Exists(realSquadSource))
            {
                CopyDirectory(realSquadSource, System.IO.Path.Combine(fixture.Path, "products", "kyber-squad"));
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

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            Directory.CreateDirectory(destinationDir);
            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = System.IO.Path.GetRelativePath(sourceDir, file);
                string destFile = System.IO.Path.Combine(destinationDir, relativePath);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destFile)!);
                File.Copy(file, destFile, true);
            }
        }

        private void Write(string relativePath, string content)
        {
            string fullPath = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        }

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
                      "args": []
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

        public void Dispose() => _temp.Dispose();
    }

    #endregion
}
