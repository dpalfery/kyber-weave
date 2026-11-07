using System.Text.Json.Nodes;
using KyberWeave.Arbiter;
using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Global-scope hook runtime contract: project config defers, home config decides when the
/// project is unset, malformed or disabled user config fails closed with the home path, and
/// project scope keeps the old behavior.
/// </summary>
public sealed class ArbiterGlobalHookRuntimeTests
{
    private const string Harness = "claude";

    [Fact]
    public async Task GlobalScope_WhenProjectConfigEnablesArbiter_UsesDeferredToProjectDecision()
    {
        string projectRoot = CreateTempDirectory();
        string home = CreateTempDirectory();
        WriteProjectConfig(projectRoot, enabled: true);

        using StringReader stdin = new(BuildPayload(projectRoot));
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", Harness, "--scope", "global", "--home", home],
            stdin,
            stdout,
            stderr);

        string combined = stdout.ToString() + stderr.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("deferred-to-project", combined, StringComparison.OrdinalIgnoreCase);
        Assert.True(string.IsNullOrWhiteSpace(stdout.ToString()),
            $"The global hook should defer to the project and emit no decision document. Output: {stdout}");
        Assert.False(Directory.Exists(Path.Combine(home, ".kyber-weave", "arbiter", "projects")),
            "A project-scoped defer should not write the global user ledger under the home directory.");
    }

    [Fact]
    public async Task GlobalScope_WhenProjectConfigMissing_UsesUserHomeConfigAndWritesLedgerUnderHome()
    {
        string projectRoot = CreateTempDirectory();
        string home = CreateTempDirectory();
        WriteUserConfig(home, "arbiter:\n  enabled: true\n");

        using StringReader stdin = new(BuildPayload(projectRoot));
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", Harness, "--scope", "global", "--home", home],
            stdin,
            stdout,
            stderr);

        string rootKey = SquadPhysicalRootIdentity.KeyOf(projectRoot);
        string ledgerRoot = Path.Combine(home, ".kyber-weave", "arbiter", "projects", rootKey);

        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(ledgerRoot),
            $"A global-scope evaluation without project config should write a project ledger under the home: {ledgerRoot}");
        Assert.Contains("global", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(projectRoot, ".kyber-weave", "arbiter")),
            "Global-scope evaluation must not write its ledger into the working tree.");
    }

    [Fact]
    public async Task GlobalScope_WhenUserConfigIsMissingOrMalformed_FailsClosedWithHomeConfigPath()
    {
        string projectRoot = CreateTempDirectory();
        string home = CreateTempDirectory();

        using StringReader stdin = new(BuildPayload(projectRoot));
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", Harness, "--scope", "global", "--home", home],
            stdin,
            stdout,
            stderr);

        string combined = stdout.ToString() + stderr.ToString();
        string userConfigPath = Path.Combine(home, ".kyber-weave", "kyber-weave.yml");

        Assert.Equal(0, exit);
        Assert.Contains("KW-ARB-HOOK-001", combined, StringComparison.Ordinal);
        Assert.Contains(userConfigPath, combined, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildPayload(string projectRoot)
    {
        string fixturePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "tests",
            "KyberWeave.Tests",
            "Fixtures",
            "arbiter-hooks",
            "claude",
            "pre-agent-dispatch.json");

        JsonNode root = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
        root["cwd"] = projectRoot;
        root["transcript_path"] = Path.Combine(projectRoot, "transcript.jsonl");
        return root.ToJsonString();
    }

    private static void WriteProjectConfig(string projectRoot, bool enabled)
    {
        string configDir = Path.Combine(projectRoot, ".kyber-weave");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, "kyber-weave.yml"),
            enabled
                ? "arbiter:\n  enabled: true\n"
                : "arbiter:\n  enabled: false\n");
    }

    private static void WriteUserConfig(string home, string yaml)
    {
        string configDir = Path.Combine(home, ".kyber-weave");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "kyber-weave.yml"), yaml);
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "kw-arbiter-global-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class ArbiterHookCommandTests
{
    [Fact]
    public async Task HookCommand_ProjectScopeWithoutProjectConfig_FailsClosed()
    {
        string projectRoot = CreateTempDirectory();
        string payload = BuildPayload(projectRoot);

        using StringReader stdin = new(payload);
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", "claude"],
            stdin,
            stdout,
            stderr);

        string combined = stdout.ToString() + stderr.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("KW-ARB-HOOK-001", combined, StringComparison.Ordinal);
    }

    private static string BuildPayload(string projectRoot)
    {
        string fixturePath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot,
            "tests",
            "KyberWeave.Tests",
            "Fixtures",
            "arbiter-hooks",
            "claude",
            "pre-agent-dispatch.json");

        JsonNode root = JsonNode.Parse(File.ReadAllText(fixturePath))!.AsObject();
        root["cwd"] = projectRoot;
        root["transcript_path"] = Path.Combine(projectRoot, "transcript.jsonl");
        return root.ToJsonString();
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "kw-arbiter-project-hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
