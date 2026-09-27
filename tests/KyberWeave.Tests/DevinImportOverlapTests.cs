using KyberWeave.Core.Squad.Deployment;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Covers the Devin duplicate-import check <c>squad doctor</c> warns on.
/// </summary>
/// <remarks>
/// Devin loads <c>.agents/</c> natively and, by default, imports <c>.claude/</c>,
/// <c>.github/skills/</c>, and <c>.windsurf/skills/</c> through <c>read_config_from</c>
/// (docs.devin.ai/cli/reference/configuration/read-config-from, read 2026-09-27), so a Squad
/// identity deployed for Devin and for one of those targets loads twice.
/// </remarks>
public sealed class DevinImportOverlapTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Workspace()
    {
        string root = Path.Combine(_temp.Path, "ws");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Skill(string workspace, string root, string name)
    {
        string directory = Path.Combine(workspace, root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\n---\n");
    }

    private static void DirectoryAgent(string workspace, string root, string name)
    {
        string directory = Path.Combine(workspace, root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "AGENT.md"), $"---\nname: {name}\n---\n");
    }

    private static void FlatAgent(string workspace, string root, string name)
    {
        string directory = Path.Combine(workspace, root);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{name}.md"), $"---\nname: {name}\n---\n");
    }

    private static Func<string, string?> Files(Dictionary<string, string> byPath) =>
        path => byPath.TryGetValue(path, out string? content) ? content : null;

    private static Func<string, string?> NoFiles => _ => null;

    [Fact]
    public void Inspect_ClaudeAndCopilotSkillsSharingDevinNames_ReportsEachImport()
    {
        string ws = Workspace();
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".devin/skills", "test-dev");
        Skill(ws, ".claude/skills", "code-review");
        Skill(ws, ".claude/skills", "unrelated");
        Skill(ws, ".github/skills", "test-dev");

        DevinImportOverlapReport report = DevinImportOverlap.Inspect(ws, devinUserRoot: null, NoFiles);

        Assert.Collection(
            report.Overlaps,
            claude =>
            {
                Assert.Equal(".claude/skills", claude.SourceRoot);
                Assert.Equal("skill", claude.Kind);
                Assert.Equal("claude", claude.ImportSetting);
                Assert.Equal(["code-review"], claude.Identities);
            },
            copilot =>
            {
                Assert.Equal(".github/skills", copilot.SourceRoot);
                Assert.Equal("copilot", copilot.ImportSetting);
                Assert.Equal(["test-dev"], copilot.Identities);
            });
    }

    [Fact]
    public void Inspect_AntigravityAgentsAndSkills_ReportsANativeRootWithNoSetting()
    {
        string ws = Workspace();
        DirectoryAgent(ws, ".devin/agents", "architect");
        Skill(ws, ".devin/skills", "conductor");
        DirectoryAgent(ws, ".agents/agents", "architect");
        Skill(ws, ".agents/skills", "conductor");

        DevinImportOverlapReport report = DevinImportOverlap.Inspect(ws, devinUserRoot: null, NoFiles);

        Assert.Equal([".agents/agents", ".agents/skills"], report.Overlaps.Select(o => o.SourceRoot));
        Assert.All(report.Overlaps, overlap => Assert.Null(overlap.ImportSetting));
        Assert.Equal("agent", report.Overlaps[0].Kind);
    }

    [Fact]
    public void Inspect_FlatClaudeAgentMatchingADevinDirectoryAgent_IsReported()
    {
        string ws = Workspace();
        DirectoryAgent(ws, ".devin/agents", "code-reviewer");
        FlatAgent(ws, ".claude/agents", "code-reviewer");

        DevinImportOverlapReport report = DevinImportOverlap.Inspect(ws, devinUserRoot: null, NoFiles);

        DevinImportOverlapEntry overlap = Assert.Single(report.Overlaps);
        Assert.Equal(".claude/agents", overlap.SourceRoot);
        Assert.Equal(["code-reviewer"], overlap.Identities);
    }

    [Fact]
    public void Inspect_ImportTurnedOffInProjectConfig_ReportsNothingForThatImport()
    {
        string ws = Workspace();
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".claude/skills", "code-review");
        Skill(ws, ".github/skills", "code-review");

        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            [Path.Combine(ws, ".devin", "config.json")] =
                "{\n  // Squad deploys Devin natively here.\n  \"read_config_from\": { \"claude\": false, },\n}\n"
        };

        DevinImportOverlapReport report = DevinImportOverlap.Inspect(ws, devinUserRoot: null, Files(files));

        DevinImportOverlapEntry overlap = Assert.Single(report.Overlaps);
        Assert.Equal(".github/skills", overlap.SourceRoot);
    }

    [Fact]
    public void Inspect_LocalConfigOutranksProjectAndProjectOutranksUser()
    {
        string ws = Workspace();
        string userRoot = Path.Combine(_temp.Path, "user-devin");
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".claude/skills", "code-review");
        Skill(ws, ".github/skills", "code-review");

        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            // Local re-enables what the project turned off.
            [Path.Combine(ws, ".devin", "config.local.json")] = "{\"read_config_from\":{\"claude\":true}}",
            [Path.Combine(ws, ".devin", "config.json")] = "{\"read_config_from\":{\"claude\":false,\"copilot\":true}}",
            // The project's explicit copilot: true outranks the user's false.
            [Path.Combine(userRoot, "config.json")] = "{\"read_config_from\":{\"copilot\":false}}"
        };

        DevinImportOverlapReport report = DevinImportOverlap.Inspect(ws, userRoot, Files(files));

        Assert.Equal([".claude/skills", ".github/skills"], report.Overlaps.Select(o => o.SourceRoot));
    }

    [Fact]
    public void Inspect_UserConfigTurningAnImportOff_AppliesWhenTheProjectIsSilent()
    {
        string ws = Workspace();
        string userRoot = Path.Combine(_temp.Path, "user-devin");
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".windsurf/skills", "code-review");

        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            [Path.Combine(userRoot, "config.json")] = "{\"read_config_from\":{\"windsurf\":false}}"
        };

        Assert.Empty(DevinImportOverlap.Inspect(ws, userRoot, Files(files)).Overlaps);
        Assert.Single(DevinImportOverlap.Inspect(ws, userRoot, NoFiles).Overlaps);
    }

    [Fact]
    public void Inspect_MalformedConfig_DeclaresNothingRatherThanThrowing()
    {
        string ws = Workspace();
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".claude/skills", "code-review");

        Dictionary<string, string> files = new(StringComparer.Ordinal)
        {
            [Path.Combine(ws, ".devin", "config.json")] = "{ not json",
            [Path.Combine(ws, ".devin", "config.local.json")] = "{\"read_config_from\":{\"claude\":\"no\"}}"
        };

        DevinImportOverlapEntry overlap = Assert.Single(
            DevinImportOverlap.Inspect(ws, devinUserRoot: null, Files(files)).Overlaps);
        Assert.Equal("claude", overlap.ImportSetting);
    }

    [Fact]
    public void Inspect_DirectoryWithoutADefinitionFile_IsNotAnIdentity()
    {
        string ws = Workspace();
        Skill(ws, ".devin/skills", "code-review");
        Directory.CreateDirectory(Path.Combine(ws, ".claude", "skills", "code-review"));

        Assert.Empty(DevinImportOverlap.Inspect(ws, devinUserRoot: null, NoFiles).Overlaps);
    }

    [Fact]
    public void Inspect_NoDevinTree_ReportsNothing()
    {
        string ws = Workspace();
        Skill(ws, ".claude/skills", "code-review");
        Skill(ws, ".agents/skills", "code-review");

        Assert.Empty(DevinImportOverlap.Inspect(ws, devinUserRoot: null, NoFiles).Overlaps);
    }
}
