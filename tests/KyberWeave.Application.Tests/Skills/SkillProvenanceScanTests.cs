using KyberWeave.Core.Skills.Model;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Skills;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The provenance scan reports a skill it cannot parse or read as a finding and keeps scanning the rest, and it
/// fails a top-level provenance key even when the nested metadata is present.
/// </summary>
/// <remarks>The skills live in an in-memory file system, so nothing here reads the disk.</remarks>
public sealed class SkillProvenanceScanTests
{
    private readonly InMemoryGovernanceReaders _readers = new();

    private string SkillsRoot => Path.Combine(_readers.Root, "skills");

    /// <summary>
    /// A skill whose front matter cannot be parsed is reported as a finding, not thrown:
    /// one broken file must not hide every skill after it from the scan.
    /// </summary>
    [Fact]
    public void UnparseableSkillIsReportedInsteadOfAbortingTheScan()
    {
        AddSkill("broken", "---\nname: broken\n  bad: [unclosed\n---\n");
        AddSkill("healthy", ValidSkill("healthy"));

        IReadOnlyList<string> issues = Scan(_readers.Skills.ReadFile);

        Assert.Contains(
            issues,
            issue => issue.Contains("broken", StringComparison.Ordinal)
                     && issue.Contains("parse", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(issues, issue => issue.Contains("healthy", StringComparison.Ordinal));
    }

    /// <summary>
    /// A skill that exists at enumeration time but cannot be read still leaves the other
    /// skills checked, on the same abort shape as a parse failure.
    /// </summary>
    [Fact]
    public void UnreadableSkillIsReportedInsteadOfAbortingTheScan()
    {
        AddSkill("broken", ValidSkill("broken"));
        AddSkill("healthy", ValidSkill("healthy"));

        IReadOnlyList<string> issues = Scan(
            path => path.Contains("broken", StringComparison.Ordinal)
                ? throw new IOException("the file is locked")
                : _readers.Skills.ReadFile(path));

        Assert.Contains(
            issues,
            issue => issue.Contains("broken", StringComparison.Ordinal)
                     && issue.Contains("read", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(issues, issue => issue.Contains("healthy", StringComparison.Ordinal));
    }

    /// <summary>
    /// The top-level hoist is the trap the docs warn about: nested provenance is present, so
    /// the missing-provenance codes stay quiet, but the stray key must still fail the guard.
    /// </summary>
    [Fact]
    public void TopLevelProvenanceKeyIsReportedEvenWhenNestedMetadataIsPresent()
    {
        AddSkill("hoisted", """
            ---
            name: hoisted
            description: "Provenance is both nested and hoisted."
            license: MIT
            metadata:
              author: dpalfery
              version: 0.1.0
            author: dpalfery
            ---
            Body.
            """);

        IReadOnlyList<string> issues = Scan(_readers.Skills.ReadFile);

        Assert.Contains(
            issues,
            issue => issue.Contains("hoisted", StringComparison.Ordinal)
                     && issue.Contains("author", StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> Scan(Func<string, Skill> parse) =>
        SkillProvenanceScan.Issues(
            SkillsRoot,
            _readers.Files.EnumerateFiles(SkillsRoot, "SKILL.md", recursive: true),
            parse,
            _readers.Files.ReadAllText);

    private void AddSkill(string name, string content) =>
        _readers.Write(Path.Combine(SkillsRoot, name, "SKILL.md"), content);

    private static string ValidSkill(string name) => $"""
        ---
        name: {name}
        description: "A valid skill."
        license: MIT
        metadata:
          author: dpalfery
          version: 0.1.0
        ---
        Body.
        """;
}
