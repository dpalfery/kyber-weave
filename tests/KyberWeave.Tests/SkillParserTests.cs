using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using Xunit;

namespace KyberWeave.Tests;

public class SkillParserTests
{
    private static readonly string[] ExpectedAllowedTools = ["search", "fetch"];

    private const string Valid = """
---
name: my-skill
description: Use to do a thing. Use when X. Do NOT use for Y.
license: MIT
metadata:
  author: me
  version: 1.0.0
allowed-tools: search fetch
---

# My Skill

ALWAYS verify first.

See scripts/run.py for details.
""";

    [Fact]
    public void ParsesFrontmatterFields()
    {
        Skill skill = SkillParser.Parse(Valid, "/tmp/my-skill/SKILL.md", "/tmp/my-skill");
        Assert.Equal("my-skill", skill.Frontmatter.Name);
        Assert.StartsWith("Use to do a thing", skill.Frontmatter.Description, StringComparison.Ordinal);
        Assert.Equal("MIT", skill.Frontmatter.License);
        Assert.Equal("me", skill.Frontmatter.Metadata!["author"]);
        Assert.Equal(ExpectedAllowedTools, skill.Frontmatter.AllowedTools);
    }

    [Fact]
    public void SeparatesBodyFromFrontmatter()
    {
        Skill skill = SkillParser.Parse(Valid, "/tmp/my-skill/SKILL.md", "/tmp/my-skill");
        Assert.Contains("ALWAYS verify first.", skill.InstructionsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("name: my-skill", skill.InstructionsBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractsReferenceLinks()
    {
        Skill skill = SkillParser.Parse(Valid, "/tmp/my-skill/SKILL.md", "/tmp/nonexistent-dir");
        Assert.Contains(skill.ReferenceLinks, l => l.Target.Contains("scripts/run.py", StringComparison.Ordinal));
        // directory does not exist, so it should not resolve
        Assert.All(skill.ReferenceLinks, l => Assert.False(l.Resolves));
    }

    [Fact]
    public void CapturesUnknownKeys()
    {
        string content = "---\nname: x\ndescription: d\nmystery: 42\n---\n\nbody";
        Skill skill = SkillParser.Parse(content, "/tmp/x/SKILL.md", "/tmp/x");
        Assert.True(skill.Frontmatter.UnknownKeys.ContainsKey("mystery"));
    }

    [Fact]
    public void ThrowsOnMissingFrontmatter()
    {
        Assert.Throws<SkillParseException>(() =>
            SkillParser.Parse("# Just a heading\nno frontmatter", "/tmp/x/SKILL.md", "/tmp/x"));
    }

    [Fact]
    public void ReferenceWithFragmentAnchorResolvesWhenTargetFileExists()
    {
        using TempDirectory tempDir = new TempDirectory();
        string referencesDir = Path.Combine(tempDir.Path, "references");
        Directory.CreateDirectory(referencesDir);
        File.WriteAllText(Path.Combine(referencesDir, "guide.md"), "# Guide\n\n## Overview");

        string content = """
---
name: fragment-skill
description: Tests fragment anchor resolution.
---

# Fragment Skill

See [Guide](references/guide.md#overview) for details.
""";

        Skill skill = SkillParser.Parse(content, Path.Combine(tempDir.Path, "SKILL.md"), tempDir.Path);
        SkillReferenceLink link = Assert.Single(skill.ReferenceLinks);
        Assert.Equal("references/guide.md", link.Target);
        Assert.True(link.Resolves);
    }

    [Fact]
    public void ConfigRegTokensAreSkippedAsNonFileReferences()
    {
        using TempDirectory tempDir = new TempDirectory();
        string content = """
---
name: token-skill
description: Tests config reg token skipping.
---

# Token Skill

Consult [Docs Root](<docs-root>) and [Plan Index](<plan-index>).
""";

        Skill skill = SkillParser.Parse(content, Path.Combine(tempDir.Path, "SKILL.md"), tempDir.Path);
        Assert.Empty(skill.ReferenceLinks);
    }

    [Fact]
    public void ForeignAbsolutePathsAreSkipped()
    {
        using TempDirectory tempDir = new TempDirectory();
        string content = """
---
name: absolute-skill
description: Tests foreign absolute path skipping.
---

# Absolute Skill

See [drive](C:/foo/bar.md), [posix](/etc/bar.md), [unc](\\share\path\file.md), and [unc-slash](//unc/path/file.md).
""";

        Skill skill = SkillParser.Parse(content, Path.Combine(tempDir.Path, "SKILL.md"), tempDir.Path);
        Assert.Empty(skill.ReferenceLinks);
    }
}

