using KyberWeave.Core.Skills.Parsing;
using Xunit;

namespace KyberWeave.Tests;

public class SkillParserTests
{
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
    public void Parses_frontmatter_fields()
    {
        var skill = SkillParser.Parse(Valid, "/tmp/my-skill/SKILL.md", "/tmp/my-skill");
        Assert.Equal("my-skill", skill.Frontmatter.Name);
        Assert.StartsWith("Use to do a thing", skill.Frontmatter.Description);
        Assert.Equal("MIT", skill.Frontmatter.License);
        Assert.Equal("me", skill.Frontmatter.Metadata!["author"]);
        Assert.Equal(new[] { "search", "fetch" }, skill.Frontmatter.AllowedTools);
    }

    [Fact]
    public void Separates_body_from_frontmatter()
    {
        var skill = SkillParser.Parse(Valid, "/tmp/my-skill/SKILL.md", "/tmp/my-skill");
        Assert.Contains("ALWAYS verify first.", skill.InstructionsBody);
        Assert.DoesNotContain("name: my-skill", skill.InstructionsBody);
    }

    [Fact]
    public void Extracts_reference_links()
    {
        var skill = SkillParser.Parse(Valid, "/tmp/my-skill/SKILL.md", "/tmp/nonexistent-dir");
        Assert.Contains(skill.ReferenceLinks, l => l.Target.Contains("scripts/run.py"));
        // directory does not exist, so it should not resolve
        Assert.All(skill.ReferenceLinks, l => Assert.False(l.Resolves));
    }

    [Fact]
    public void Captures_unknown_keys()
    {
        var content = "---\nname: x\ndescription: d\nmystery: 42\n---\n\nbody";
        var skill = SkillParser.Parse(content, "/tmp/x/SKILL.md", "/tmp/x");
        Assert.True(skill.Frontmatter.UnknownKeys.ContainsKey("mystery"));
    }

    [Fact]
    public void Throws_on_missing_frontmatter()
    {
        Assert.Throws<SkillParseException>(() =>
            SkillParser.Parse("# Just a heading\nno frontmatter", "/tmp/x/SKILL.md", "/tmp/x"));
    }
}
