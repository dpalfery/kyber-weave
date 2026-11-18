using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Covers the Devin duplicate-import check <c>squad doctor</c> warns on, through the in-memory file system.
/// </summary>
/// <remarks>
/// Devin loads <c>.agents/</c> natively and, by default, imports <c>.claude/</c>,
/// <c>.github/skills/</c>, and <c>.windsurf/skills/</c> through <c>read_config_from</c>
/// (docs.devin.ai/cli/reference/configuration/read-config-from, read 2026-09-27), so a Squad
/// identity deployed for Devin and for one of those targets loads twice.
/// </remarks>
public sealed class DevinImportOverlapTests
{
    private static readonly string Base = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "devin-overlap");

    private readonly InMemoryFileSystem _files = new(caseSensitive: true);

    private DevinImportOverlap Overlap => new(_files, new FakePathResolver(_files), new FakeProcessEnvironment(currentDirectory: Base));

    private string Workspace()
    {
        string root = Path.Combine(Base, "ws");
        _files.CreateDirectory(root);
        return root;
    }

    private void Config(string path, string content)
    {
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }

    private void Skill(string workspace, string root, string name)
    {
        string directory = Path.Combine(workspace, root, name);
        _files.CreateDirectory(directory);
        _files.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\n---\n");
    }

    private void DirectoryAgent(string workspace, string root, string name)
    {
        string directory = Path.Combine(workspace, root, name);
        _files.CreateDirectory(directory);
        _files.WriteAllText(Path.Combine(directory, "AGENT.md"), $"---\nname: {name}\n---\n");
    }

    private void FlatAgent(string workspace, string root, string name)
    {
        string directory = Path.Combine(workspace, root);
        _files.CreateDirectory(directory);
        _files.WriteAllText(Path.Combine(directory, $"{name}.md"), $"---\nname: {name}\n---\n");
    }

    [Fact]
    public void Inspect_ClaudeAndCopilotSkillsSharingDevinNames_ReportsEachImport()
    {
        string ws = Workspace();
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".devin/skills", "test-dev");
        Skill(ws, ".claude/skills", "code-review");
        Skill(ws, ".claude/skills", "unrelated");
        Skill(ws, ".github/skills", "test-dev");

        DevinImportOverlapReport report = Overlap.Inspect(ws, devinUserRoot: null);

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

        DevinImportOverlapReport report = Overlap.Inspect(ws, devinUserRoot: null);

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

        DevinImportOverlapReport report = Overlap.Inspect(ws, devinUserRoot: null);

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

        Config(Path.Combine(ws, ".devin", "config.json"), "{\n  // Squad deploys Devin natively here.\n  \"read_config_from\": { \"claude\": false, },\n}\n");

        DevinImportOverlapReport report = Overlap.Inspect(ws, devinUserRoot: null);

        DevinImportOverlapEntry overlap = Assert.Single(report.Overlaps);
        Assert.Equal(".github/skills", overlap.SourceRoot);
    }

    [Fact]
    public void Inspect_LocalConfigOutranksProjectAndProjectOutranksUser()
    {
        string ws = Workspace();
        string userRoot = Path.Combine(Base, "user-devin");
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".claude/skills", "code-review");
        Skill(ws, ".github/skills", "code-review");

        Config(Path.Combine(ws, ".devin", "config.local.json"), "{\"read_config_from\":{\"claude\":true}}");
        Config(Path.Combine(ws, ".devin", "config.json"), "{\"read_config_from\":{\"claude\":false,\"copilot\":true}}");
        Config(Path.Combine(userRoot, "config.json"), "{\"read_config_from\":{\"copilot\":false}}");

        DevinImportOverlapReport report = Overlap.Inspect(ws, userRoot);

        Assert.Equal([".claude/skills", ".github/skills"], report.Overlaps.Select(o => o.SourceRoot));
    }

    [Fact]
    public void Inspect_UserConfigTurningAnImportOff_AppliesWhenTheProjectIsSilent()
    {
        string ws = Workspace();
        string userRoot = Path.Combine(Base, "user-devin");
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".windsurf/skills", "code-review");

        Assert.Single(Overlap.Inspect(ws, userRoot).Overlaps);

        Config(Path.Combine(userRoot, "config.json"), "{\"read_config_from\":{\"windsurf\":false}}");

        Assert.Empty(Overlap.Inspect(ws, userRoot).Overlaps);
    }

    [Fact]
    public void Inspect_MalformedConfig_DeclaresNothingRatherThanThrowing()
    {
        string ws = Workspace();
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".claude/skills", "code-review");

        Config(Path.Combine(ws, ".devin", "config.json"), "{ not json");
        Config(Path.Combine(ws, ".devin", "config.local.json"), "{\"read_config_from\":{\"claude\":\"no\"}}");

        DevinImportOverlapEntry overlap = Assert.Single(
            Overlap.Inspect(ws, devinUserRoot: null).Overlaps);
        Assert.Equal("claude", overlap.ImportSetting);
    }

    [Fact]
    public void Inspect_UnreadableConfig_DeclaresNothingRatherThanThrowing()
    {
        string ws = Workspace();
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".claude/skills", "code-review");
        string config = Path.Combine(ws, ".devin", "config.json");
        Config(config, "{\"read_config_from\":{\"claude\":false}}");
        Assert.Empty(Overlap.Inspect(ws, devinUserRoot: null).Overlaps);

        _files.FailWhen(
            operation => operation.Name == "ReadAllText" && string.Equals(operation.Path, config, StringComparison.Ordinal),
            new UnauthorizedAccessException());

        Assert.Single(Overlap.Inspect(ws, devinUserRoot: null).Overlaps);
    }

    /// <summary>
    /// Enumeration is lazy, so an unreadable tree throws mid-loop; the check reports what it
    /// could read instead of taking <c>squad doctor</c> down with it.
    /// </summary>
    [Fact]
    public void Inspect_UnreadableImportedTree_ReportsTheRestInsteadOfThrowing()
    {
        string ws = Workspace();
        Skill(ws, ".devin/skills", "code-review");
        Skill(ws, ".claude/skills", "code-review");
        Skill(ws, ".github/skills", "code-review");
        string unreadable = Path.Combine(ws, ".claude", "skills");
        _files.FailWhen(
            operation => string.Equals(operation.Path, unreadable, StringComparison.Ordinal)
                         && operation.Name.StartsWith("Enumerate", StringComparison.Ordinal),
            new UnauthorizedAccessException());

        DevinImportOverlapReport report = Overlap.Inspect(ws, devinUserRoot: null);

        DevinImportOverlapEntry overlap = Assert.Single(report.Overlaps);
        Assert.Equal(".github/skills", overlap.SourceRoot);
    }

    [Fact]
    public void Inspect_DirectoryWithoutADefinitionFile_IsNotAnIdentity()
    {
        string ws = Workspace();
        Skill(ws, ".devin/skills", "code-review");
        _files.CreateDirectory(Path.Combine(ws, ".claude", "skills", "code-review"));

        Assert.Empty(Overlap.Inspect(ws, devinUserRoot: null).Overlaps);
    }

    [Fact]
    public void Inspect_NoDevinTree_ReportsNothing()
    {
        string ws = Workspace();
        Skill(ws, ".claude/skills", "code-review");
        Skill(ws, ".agents/skills", "code-review");

        Assert.Empty(Overlap.Inspect(ws, devinUserRoot: null).Overlaps);
    }
}
