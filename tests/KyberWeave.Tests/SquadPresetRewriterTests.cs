using System.Text;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
///     Unit tests for the shared preset rewrite helper: skill files are never rewritten and only
///     deployed agent files whose profile category resolves gain a new model token. A file the
///     roster cannot identify keeps its bytes and produces no diff row instead of falling back
///     to the general category.
/// </summary>
public sealed class SquadPresetRewriterTests
{
    [Fact]
    public void SkillFilesPassThroughByteIdentical()
    {
        string skillContent = "---\nname: bug-crusher\nmodel: some-model\n---\nYou are bug-crusher.\n";
        string backslashSkillContent = "---\nname: reviewer\nmodel: some-model\n---\nYou are reviewer.\n";
        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal)
        {
            [".claude/skills/bug-crusher/SKILL.md"] = Encoding.UTF8.GetBytes(skillContent),
            ["opencode\\skills\\reviewer\\SKILL.md"] = Encoding.UTF8.GetBytes(backslashSkillContent)
        };

        SquadPresetRewriteResult result = Rewrite(contents);

        Assert.Empty(result.Diffs);
        Assert.Equal(skillContent, Encoding.UTF8.GetString(ReadResult(result, ".claude/skills/bug-crusher/SKILL.md")));
        Assert.Equal(
            backslashSkillContent,
            Encoding.UTF8.GetString(ReadResult(result, "opencode\\skills\\reviewer\\SKILL.md")));
    }

    [Fact]
    public void EntryPointSkillProjectionsForRosterAgentsPassThroughByteIdentical()
    {
        // The parent directory names a roster agent, so the old receipt loop resolved it through
        // the roster and rewrote it. Path shape alone must keep it out of the rewrite.
        string projectionContent = "---\nname: architect\nmodel: bundled-model\n---\nYou are architect.\n";
        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal)
        {
            [".claude/skills/architect/SKILL.md"] = Encoding.UTF8.GetBytes(projectionContent)
        };

        SquadPresetRewriteResult result = Rewrite(contents);

        Assert.Empty(result.Diffs);
        Assert.Equal(
            projectionContent,
            Encoding.UTF8.GetString(ReadResult(result, ".claude/skills/architect/SKILL.md")));
    }

    [Fact]
    public void RosterAgentIsRewrittenToItsCategoryToken()
    {
        string initial = "---\nname: architect\nmodel: bundled-model\n---\nYou are architect.\n";
        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal)
        {
            [".claude/agents/architect.md"] = Encoding.UTF8.GetBytes(initial)
        };

        SquadPresetRewriteResult result = Rewrite(contents);

        SquadModelDiffItem diff = Assert.Single(result.Diffs);
        Assert.Equal("architect", diff.Agent);
        Assert.Equal("bundled-model", diff.OldModel);
        Assert.Equal("claude-plan-model", diff.NewModel);
        string updated = Encoding.UTF8.GetString(ReadResult(result, ".claude/agents/architect.md"));
        Assert.Contains("model: claude-plan-model", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("bundled-model", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownIdentityPassesThroughWithNoDiffRow()
    {
        string initial = "---\nname: mystery\nmodel: keep-me\n---\nYou are mystery.\n";
        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal)
        {
            [".claude/agents/mystery.md"] = Encoding.UTF8.GetBytes(initial)
        };

        SquadPresetRewriteResult result = Rewrite(contents);

        Assert.Empty(result.Diffs);
        Assert.Equal(initial, Encoding.UTF8.GetString(ReadResult(result, ".claude/agents/mystery.md")));
    }

    [Fact]
    public void UnknownProfileCategoryDoesNotFallBackToGeneral()
    {
        // The general category resolves for this target, but an unresolvable category must pass
        // through instead of borrowing the general token.
        string initial = "---\nname: architect\nmodel-profile: nosuch\nmodel: keep-me\n---\nYou are architect.\n";
        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal)
        {
            [".claude/agents/architect.md"] = Encoding.UTF8.GetBytes(initial)
        };

        SquadPresetRewriteResult result = Rewrite(contents);

        Assert.Empty(result.Diffs);
        Assert.Equal(initial, Encoding.UTF8.GetString(ReadResult(result, ".claude/agents/architect.md")));
    }

    [Fact]
    public void ModelProfileFieldSelectsCategoryOverRosterName()
    {
        string initial =
            "---\nname: stranger\nmodel-profile: deep-planning\nmodel: bundled-model\n---\nYou are stranger.\n";
        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal)
        {
            [".claude/agents/stranger.md"] = Encoding.UTF8.GetBytes(initial)
        };

        SquadPresetRewriteResult result = Rewrite(contents);

        SquadModelDiffItem diff = Assert.Single(result.Diffs);
        Assert.Equal("stranger", diff.Agent);
        Assert.Equal("claude-plan-model", diff.NewModel);
        string updated = Encoding.UTF8.GetString(ReadResult(result, ".claude/agents/stranger.md"));
        Assert.Contains("model: claude-plan-model", updated, StringComparison.Ordinal);
    }

    [Fact]
    public void InheritCategoryRemovesTheModelLine()
    {
        string initial = "---\nname: speedster\nmodel: stale-model\n---\nYou are speedster.\n";
        string inheritWithoutModel = "---\nname: speedster\n---\nYou are speedster.\n";
        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal)
        {
            [".claude/agents/speedster.md"] = Encoding.UTF8.GetBytes(initial),
            [".claude/agents/speedster-bare.md"] = Encoding.UTF8.GetBytes(inheritWithoutModel)
        };

        SquadPresetRewriteResult result = Rewrite(contents);

        SquadModelDiffItem diff = Assert.Single(result.Diffs);
        Assert.Equal("speedster", diff.Agent);
        Assert.Equal("stale-model", diff.OldModel);
        Assert.Null(diff.NewModel);
        string updated = Encoding.UTF8.GetString(ReadResult(result, ".claude/agents/speedster.md"));
        Assert.DoesNotContain("model:", updated, StringComparison.Ordinal);
        Assert.Equal(
            inheritWithoutModel,
            Encoding.UTF8.GetString(ReadResult(result, ".claude/agents/speedster-bare.md")));
    }

    [Fact]
    public void ChangedFilesPreserveCrlfNewlines()
    {
        string initial = "---\r\nname: architect\r\nmodel: bundled-model\r\n---\r\nYou are architect.\r\n";
        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal)
        {
            [".claude/agents/architect.md"] = Encoding.UTF8.GetBytes(initial)
        };

        SquadPresetRewriteResult result = Rewrite(contents);

        Assert.Single(result.Diffs);
        string updated = Encoding.UTF8.GetString(ReadResult(result, ".claude/agents/architect.md"));
        Assert.Contains("model: claude-plan-model", updated, StringComparison.Ordinal);
        Assert.Contains("\r\n", updated, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", updated.Replace("\r\n", string.Empty, StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AgentWithUnchangedModelPassesThroughByteIdentical()
    {
        string initial = "---\nname: architect\nmodel: claude-plan-model\n---\nYou are architect.\n";
        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal)
        {
            [".claude/agents/architect.md"] = Encoding.UTF8.GetBytes(initial)
        };

        SquadPresetRewriteResult result = Rewrite(contents);

        Assert.Empty(result.Diffs);
        Assert.Equal(initial, Encoding.UTF8.GetString(ReadResult(result, ".claude/agents/architect.md")));
    }

    [Fact]
    public void FilesOutsideTargetTokensPassThroughUnchanged()
    {
        string initial = "---\nname: architect\nmodel: bundled-model\n---\nYou are architect.\n";
        string markdownResource = "---\nname: architect\n---\nReference material.\n";
        Dictionary<string, byte[]> contents = new(StringComparer.Ordinal)
        {
            [".opencode/agents/architect.md"] = Encoding.UTF8.GetBytes(initial),
            [".claude/agents/notes.markdown"] = Encoding.UTF8.GetBytes(markdownResource)
        };

        SquadPresetRewriteResult result = Rewrite(contents);

        Assert.Empty(result.Diffs);
        Assert.Equal(initial, Encoding.UTF8.GetString(ReadResult(result, ".opencode/agents/architect.md")));
        Assert.Equal(markdownResource, Encoding.UTF8.GetString(ReadResult(result, ".claude/agents/notes.markdown")));
    }

    private static SquadPresetRewriteResult Rewrite(Dictionary<string, byte[]> contents)
    {
        List<SquadOwnedFile> files = new();
        foreach (string relativePath in contents.Keys.OrderBy(path => path, StringComparer.Ordinal))
        {
            string target = relativePath.StartsWith(".opencode/", StringComparison.Ordinal) ? "opencode" : "claude";
            files.Add(new SquadOwnedFile(relativePath, new string('0', 64), target, false));
        }

        HashSet<string> targetTokens = new(StringComparer.OrdinalIgnoreCase) { "claude" };
        SquadModelProfiles profiles = CreateProfiles();
        Dictionary<string, string> roster = new(StringComparer.OrdinalIgnoreCase)
        {
            ["architect"] = "deep-planning",
            ["speedster"] = "fast"
        };

        return SquadPresetFileRewriter.Rewrite(
            files,
            targetTokens,
            profiles,
            roster,
            file => contents.TryGetValue(file.RelativePath, out byte[]? content) ? content : null);
    }

    private static SquadModelProfiles CreateProfiles()
    {
        Dictionary<string, SquadModelProfile> profiles = new(StringComparer.Ordinal)
        {
            ["deep-planning"] = new SquadModelProfile(
                "plan-default-model",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "claude-plan-model" }),
            ["fast"] = new SquadModelProfile(
                "inherit",
                new Dictionary<string, string>(StringComparer.Ordinal)),
            ["general"] = new SquadModelProfile(
                "general-default-model",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["claude"] = "general-claude-model" })
        };
        return new SquadModelProfiles("kyber-squad.model-profiles/v1", profiles, "test");
    }

    private static byte[] ReadResult(SquadPresetRewriteResult result, string relativePath)
    {
        foreach (SquadDeploymentFile file in result.Files)
        {
            if (string.Equals(file.RelativePath, relativePath, StringComparison.Ordinal))
                return file.Content.ToArray();
        }

        throw new InvalidOperationException($"Rewritten files contain no entry for '{relativePath}'.");
    }
}
