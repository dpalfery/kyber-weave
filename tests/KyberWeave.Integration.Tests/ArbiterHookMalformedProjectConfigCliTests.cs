using System.Text.Json.Nodes;
using KyberWeave.Arbiter;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// The Arbiter's hook command, run through its real composition on a real project folder, fails closed with
/// <c>KW-ARB-HOOK-001</c> when the project's configuration is malformed at global scope. Regression test for Kilo review
/// comments 5 and 6 on PR #527; the rule itself is covered in memory by the unit project.
/// </summary>
public sealed class ArbiterHookMalformedProjectConfigCliTests
{
    [Fact]
    public async Task GlobalScope_MalformedProjectConfig_FailsClosedWithKwArbHook001()
    {
        using TempDirectory projectDir = new();
        using TempDirectory homeDir = new();

        string userConfigDir = Path.Combine(homeDir.Path, ".kyber-weave");
        Directory.CreateDirectory(userConfigDir);
        await File.WriteAllTextAsync(Path.Combine(userConfigDir, "kyber-weave.yml"), "arbiter:\n  enabled: true\n");

        string projConfigDir = Path.Combine(projectDir.Path, ".kyber-weave");
        Directory.CreateDirectory(projConfigDir);
        await File.WriteAllTextAsync(Path.Combine(projConfigDir, "kyber-weave.yml"), "invalid: yaml: : content");

        string fixturePath = "tests/KyberWeave.Application.Tests/Fixtures/arbiter-hooks/claude/pre-agent-dispatch.json";

        JsonNode root = JsonNode.Parse(CheckoutCorpusSource.Checkout.ReadText(fixturePath))!.AsObject();
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
    }
}
