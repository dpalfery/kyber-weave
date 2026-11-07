using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Cli.Update;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Regression tests for Kilo review comments 1 through 15 on PR #527.
/// </summary>
public sealed class ArbiterKiloReviewRemediationTests
{
    [Fact]
    public async Task Item1_CursorRenderer_GlobalScope_EmitsHooksJsonDirectlyUnderGlobalRoot()
    {
        using ArbiterSquadFixture fixture = ArbiterSquadFixture.Create();
        SquadRendererRegistry registry = new([new CursorRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: fixture.Path,
            Targets: [SquadTarget.Cursor],
            Scope: SquadDeploymentScope.Global,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.Equal("cursor", block.Target);
        Assert.Equal("hooks.json", block.RelativePath);
    }

    [Fact]
    public void Item2_ArbiterHookWiring_GlobalScope_CopilotReportsEnforcedGlobal()
    {
        IReadOnlyList<SquadDegradationRecord> records = ArbiterHookWiring.TargetDegradations(
            [SquadTarget.Copilot],
            SquadDeploymentScope.Global);

        SquadDegradationRecord record = Assert.Single(records);
        Assert.Equal("copilot", record.Target);
        Assert.Equal("enforced-global", record.Details);

        string? hypotheticalReason = ArbiterHookWiring.DegradationReason(
            "copilot-vscode",
            SquadDeploymentScope.Global,
            ArbiterHookWiring.HookedTargets,
            ArbiterHookWiring.FallbackTargets);
        Assert.Equal("hooks-disabled-by-harness", hypotheticalReason);
    }

    [Fact]
    public async Task Item3_CopilotRenderer_ProjectScope_OmitsCliHooksWhenNoAgentsWired()
    {
        using ArbiterSquadFixture fixture = ArbiterSquadFixture.Create();
        string agentsDir = Path.Combine(fixture.Path, "agents");
        foreach (string file in Directory.GetFiles(agentsDir, "*.md"))
        {
            if (Path.GetFileName(file) != "docs-dev.md")
            {
                File.Delete(file);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.Path, "bundles", "full.yml"), """
            schema: kyber-squad.bundle/v1
            name: full
            agents:
              - docs-dev
            skills: []
            """);

        SquadRendererRegistry registry = new([new CopilotRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: fixture.Path,
            Targets: [SquadTarget.Copilot],
            Scope: SquadDeploymentScope.Project,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.DoesNotContain(result.Files, f => f.RelativePath == ".github/hooks/kyber-arbiter.json");
    }

    [Fact]
    public async Task Item4_ClaudeRenderer_GlobalScope_OmitsFrontmatterHooksOnAgents()
    {
        using ArbiterSquadFixture fixture = ArbiterSquadFixture.Create();
        SquadRendererRegistry registry = new([new ClaudeRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: fixture.Path,
            Targets: [SquadTarget.Claude],
            Scope: SquadDeploymentScope.Global,
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: 5)));

        Assert.True(result.Success, string.Join("; ", result.Errors));
        foreach (SquadDeploymentFile file in result.Files.Where(f => f.RelativePath.EndsWith(".md", StringComparison.Ordinal)))
        {
            string text = Encoding.UTF8.GetString(file.Content.Span);
            Assert.DoesNotContain("hooks:", text, StringComparison.Ordinal);
        }

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.Equal("claude", block.Target);
        Assert.Equal("settings.json", block.RelativePath);
    }

    [Fact]
    public async Task Items5And6_GlobalScope_MalformedProjectConfig_FailsClosedWithKwArbHook001()
    {
        using TempDirectory projectDir = new();
        using TempDirectory homeDir = new();

        string userConfigDir = Path.Combine(homeDir.Path, ".kyber-weave");
        Directory.CreateDirectory(userConfigDir);
        await File.WriteAllTextAsync(Path.Combine(userConfigDir, "kyber-weave.yml"), "arbiter:\n  enabled: true\n");

        string projConfigDir = Path.Combine(projectDir.Path, ".kyber-weave");
        Directory.CreateDirectory(projConfigDir);
        await File.WriteAllTextAsync(Path.Combine(projConfigDir, "kyber-weave.yml"), "invalid: yaml: : content");

        string fixturePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "tests",
            "KyberWeave.Tests",
            "Fixtures",
            "arbiter-hooks",
            "claude",
            "pre-agent-dispatch.json");

        JsonNode root = JsonNode.Parse(await File.ReadAllTextAsync(fixturePath))!.AsObject();
        root["cwd"] = projectDir.Path;
        root["transcript_path"] = Path.Combine(projectDir.Path, "transcript.jsonl");

        using StringReader stdin = new(root.ToJsonString());
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", "claude", "--scope", "global", "--home", homeDir.Path],
            stdin,
            stdout,
            stderr);

        string combined = stdout.ToString() + stderr.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("KW-ARB-HOOK-001", combined, StringComparison.Ordinal);
        Assert.Contains("failed to load project config", combined, StringComparison.OrdinalIgnoreCase);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            Composition.LoadHostConfig(projectDir.Path, HookScope.Global, homeDir.Path));
        Assert.Contains("KW-ARB-HOOK-001", ex.Message, StringComparison.Ordinal);
        Assert.Contains("failed to load project config", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Item7_ArbiterConfigLoader_LoadUserConfig_PopulatesRulesFromDefaultsAndUserYaml()
    {
        string yaml = """
            arbiter:
              enabled: true
              rules:
                - id: custom-rule
                  trigger: delegate
                  question: Does this task touch custom user rules?
                  answers: [deny, allow]
                  effects: { deny: deny, allow: allow }
            """;

        ArbiterConfig config = ArbiterConfigLoader.LoadUserConfig(yaml);
        Assert.NotEmpty(config.Rules);
        Assert.Contains(config.Rules, r => r.Id == "custom-rule");
        Assert.Contains(config.Rules, r => r.Id.StartsWith("KW-ARB-", StringComparison.Ordinal));
    }

    [Fact]
    public void Item8_UserHostConfig_ReadGlobalTargets_NullEmptyOrMalformedYaml_ReturnsEmptyList()
    {
        using TempDirectory home = new();
        string configDir = Path.Combine(home.Path, ".kyber-weave");
        Directory.CreateDirectory(configDir);
        string configPath = Path.Combine(configDir, "kyber-weave.yml");

        File.WriteAllText(configPath, "");
        Assert.Empty(UserHostConfig.ReadGlobalTargets(home.Path));

        File.WriteAllText(configPath, "   \n\r\n  ");
        Assert.Empty(UserHostConfig.ReadGlobalTargets(home.Path));

        File.WriteAllText(configPath, "arbiter: : malformed yaml");
        Assert.Empty(UserHostConfig.ReadGlobalTargets(home.Path));

        File.WriteAllText(configPath, "arbiter:\n  global:\n    targets: 12345");
        Assert.Empty(UserHostConfig.ReadGlobalTargets(home.Path));
    }

    [Fact]
    public void Item9_HostConfigYaml_AlignsUnquotingAndCaseInsensitivity()
    {
        const string initial = """
            arbiter:
              global:
                targets:
                  - 'a''b'
                  - 'Cursor'
            """;

        string merged = HostConfigYaml.WithArbiterGlobalTargets(initial, ["a'b", "cursor", "kilo"]);
        Assert.Contains("- 'kilo'", merged, StringComparison.Ordinal);
        Assert.Single(merged.Split('\n'), line => line.Contains("a''b", StringComparison.Ordinal) || line.Contains("a'b", StringComparison.Ordinal));
        Assert.Single(merged.Split('\n'), line => line.Contains("cursor", StringComparison.OrdinalIgnoreCase));

        string removed = HostConfigYaml.WithoutArbiterGlobalTarget(merged, "CURSOR");
        Assert.DoesNotContain("cursor", removed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Item10_KyberWeaveConfigLoader_TryLoadUserConfig_NonexistentExplicitPath_FailsNamingExplicitPath()
    {
        using TempDirectory home = new();
        string defaultDir = Path.Combine(home.Path, ".kyber-weave");
        Directory.CreateDirectory(defaultDir);
        File.WriteAllText(Path.Combine(defaultDir, "kyber-weave.yml"), "arbiter:\n  enabled: true\n");

        string customNonexistent = Path.Combine(home.Path, "custom-does-not-exist.yml");
        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoadUserConfig(home.Path, customNonexistent);

        Assert.False(result.Success);
        Assert.Equal(customNonexistent, result.ConfigPath);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Item11_McpLiveProbe_PathContainingRevMarker_MatchesCorrectRoot()
    {
        using TempDirectory root = new();
        using TempDirectory stubs = new();

        string dirWithRev = Path.Combine(root.Path, "my rev= test");
        Directory.CreateDirectory(dirWithRev);
        Directory.CreateDirectory(Path.Combine(dirWithRev, ".codegraph"));

        string stub = DoctorHarness.WriteStub(
            stubs.Path,
            "read _initialize\n" +
            "echo '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2024-11-05\",\"serverInfo\":{\"name\":\"test\",\"version\":\"1.0\"}}}'\n" +
            "read _notif\n" +
            "read _tool\n" +
            "echo '{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"isError\":false,\"content\":[{\"type\":\"text\",\"text\":\"provenance: root=" + dirWithRev + " rev=abc1234 dirty=false documents=7\"}]}}'\n");

        DoctorReport report = RepositoryDoctor.Run(
            dirWithRev,
            DoctorMode.Check,
            DoctorHarness.Options(stub),
            DoctorHarness.Executor(dirWithRev),
            _ => null);

        Assert.True(DoctorHarness.CheckNamed(report, "mcp.root").Passed);
    }

    [Fact]
    public void Item12_ArbiterCommandComposition_TryResolveConfig_EmptyRepoRoot_FailsCleanly()
    {
        DiagnosticReport report = new();
        ArbiterSettings settings = new() { Path = "" };
        bool success = ArbiterCommandComposition.TryResolveConfig(settings, report, out KyberWeaveConfig _, out string? _);

        Assert.False(success);
        Assert.NotEmpty(report.Items);
        Assert.Contains(report.Items, d => d.Code == "KW-CONFIG-001");
    }

    [Fact]
    public void Items13And14_ArbiterHookRefresher_FileShimTargetsAndReceiptUpdate()
    {
        using TempDirectory temp = new();
        string home = Path.Combine(temp.Path, "home");
        Directory.CreateDirectory(home);

        UserHostConfig.WriteGlobalTargets(home, ["kilo", "opencode", "pi", "copilot", "cursor"]);

        SquadGlobalRoots roots = new(_ => null, home);
        string kiloRoot = roots.ResolveGlobalRoot(SquadTarget.Kilo);
        string opencodeRoot = roots.ResolveGlobalRoot(SquadTarget.OpenCode);
        string piRoot = roots.ResolveGlobalRoot(SquadTarget.Pi);
        string copilotRoot = roots.ResolveGlobalRoot(SquadTarget.Copilot);
        string cursorRoot = roots.ResolveGlobalRoot(SquadTarget.Cursor);

        string kiloHook = Path.Combine(kiloRoot, "plugin", "kyber-arbiter.ts");
        string opencodeHook = Path.Combine(opencodeRoot, "plugins", "kyber-arbiter.ts");
        string piHook = Path.Combine(piRoot, "extensions", "kyber-arbiter.ts");
        string copilotHook = Path.Combine(copilotRoot, "hooks", "kyber-arbiter.json");
        string cursorHook = Path.Combine(cursorRoot, "hooks.json");

        Directory.CreateDirectory(Path.GetDirectoryName(kiloHook)!);
        Directory.CreateDirectory(Path.GetDirectoryName(opencodeHook)!);
        Directory.CreateDirectory(Path.GetDirectoryName(piHook)!);
        Directory.CreateDirectory(Path.GetDirectoryName(copilotHook)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cursorHook)!);

        File.WriteAllText(kiloHook, "// old-kilo-shim");
        File.WriteAllText(opencodeHook, "// old-opencode-shim");
        File.WriteAllText(piHook, "// old-pi-shim");
        File.WriteAllText(copilotHook, "{\"version\": 1, \"hooks\": {}}");
        File.WriteAllText(cursorHook, "{\"hooks\": {\"preToolUse\": [{\"command\": \"kyber-weave-arbiter hook --harness cursor --timeout 1\", \"matcher\": \"Task\"}], \"postToolUse\": []}}");

        using SquadHomeHarness harness = new(temp.Path);
        SquadStateStore store = new(harness.UserPaths);
        string receiptPath = store.ResolveReceiptPath(home, SquadDeploymentScope.Global);
        Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);

        string oldSha = new('a', 64);
        string oldDigest = new('b', 64);

        SquadReceipt receipt = new(
            SquadStateStore.ReceiptSchemaV3,
            SquadDeploymentScope.Global,
            home,
            DateTimeOffset.UtcNow,
            [],
            [
                new SquadOwnedFile("plugin/kyber-arbiter.ts", oldSha, "kilo", false),
                new SquadOwnedFile("plugins/kyber-arbiter.ts", oldSha, "opencode", false),
                new SquadOwnedFile("extensions/kyber-arbiter.ts", oldSha, "pi", false),
                new SquadOwnedFile("hooks/kyber-arbiter.json", oldSha, "copilot", false),
            ])
        {
            Blocks =
            [
                new SquadOwnedBlock("hooks.json", "cursor", false,
                [
                    new SquadOwnedBlockEntry("/hooks/preToolUse/0", oldDigest)
                ])
            ]
        };
        File.WriteAllText(receiptPath, store.SerializeReceipt(receipt));

        Func<string, string?> env = name => name == "HOME" ? home : null;
        List<string> log = [];
        ArbiterHookRefresher refresher = new(log.Add, env);

        ArbiterHookRefreshResult refreshResult = refresher.Refresh([]);
        Assert.Equal(5, refreshResult.Refreshed);

        Assert.Contains("kilo", File.ReadAllText(kiloHook), StringComparison.Ordinal);
        Assert.Contains("opencode", File.ReadAllText(opencodeHook), StringComparison.Ordinal);
        Assert.Contains("pi", File.ReadAllText(piHook), StringComparison.Ordinal);
        Assert.Contains("copilot", File.ReadAllText(copilotHook), StringComparison.Ordinal);
        Assert.Contains("cursor", File.ReadAllText(cursorHook), StringComparison.Ordinal);

        SquadReceipt? updatedReceipt = store.ReadReceipt(home, SquadDeploymentScope.Global);
        Assert.NotNull(updatedReceipt);

        SquadOwnedBlock cursorBlock = Assert.Single(updatedReceipt.Blocks, b => b.Target == "cursor");
        Assert.NotEqual(oldDigest, cursorBlock.Entries[0].Sha256);

        SquadOwnedFile kiloFile = Assert.Single(updatedReceipt.Files, f => f.Target == "kilo");
        Assert.NotEqual(oldSha, kiloFile.Sha256);
    }

    [Fact]
    public void Item15_SquadHookJsonBlock_Cursor_DoesNotMatchClaudeSignature()
    {
        JsonObject claudeEntry = new()
        {
            ["command"] = "kyber-weave-arbiter hook --harness claude",
            ["matcher"] = "Task",
            ["timeout"] = 5,
        };
        JsonObject cursorEntry = new()
        {
            ["command"] = "kyber-weave-arbiter hook --harness cursor",
            ["matcher"] = "Task",
            ["timeout"] = 5,
        };

        Assert.False(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Cursor, claudeEntry));
        Assert.True(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Cursor, cursorEntry));

        Assert.True(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Claude, claudeEntry));
        Assert.False(SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Claude, cursorEntry));
    }

    [Fact]
    public void ReadGlobalTargets_WhenFileThrowsIOExceptionOrAccessDenied_ReturnsEmptyList()
    {
        using TempDirectory home = new();
        string configDir = Path.Combine(home.Path, ".kyber-weave");
        Directory.CreateDirectory(configDir);
        string configPath = Path.Combine(configDir, "kyber-weave.yml");

        File.WriteAllText(configPath, "arbiter:\n  global:\n    targets:\n      - 'cursor'\n");

        using (FileStream lockStream = new(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            IReadOnlyList<string> targets = UserHostConfig.ReadGlobalTargets(home.Path);
            Assert.Empty(targets);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(configPath, UnixFileMode.None);
            try
            {
                IReadOnlyList<string> targets = UserHostConfig.ReadGlobalTargets(home.Path);
                Assert.Empty(targets);
            }
            finally
            {
                File.SetUnixFileMode(configPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public void ResolveGlobalHookPath_WhenGlobalRootsThrowsArgumentException_ReturnsNullWithoutThrowing()
    {
        using TempDirectory home = new();

        ThrowingGlobalRoots throwingRoots = new(new ArgumentException("Invalid or relative root override"));
        string? result = ArbiterHookRefresher.ResolveGlobalHookPath(SquadTarget.Cursor, home.Path, globalRoots: throwingRoots);
        Assert.Null(result);

        Func<string, string?> envWithRelativePath = varName => varName == "CURSOR_CONFIG_DIR" ? "relative/path" : null;
        string? envResult = ArbiterHookRefresher.ResolveGlobalHookPath(SquadTarget.Cursor, home.Path, environmentReader: envWithRelativePath);
        Assert.Null(envResult);
    }

    private sealed class ThrowingGlobalRoots(Exception exception) : ISquadGlobalRootResolver
    {
        public string ResolveGlobalRoot(SquadTarget target) => throw exception;
    }
}
