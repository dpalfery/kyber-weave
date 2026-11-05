using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Security;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Every skill under <c>.apm/skills</c> must declare provenance in the nested
/// <c>metadata:</c> map the gate reads. A top-level <c>author:</c>/<c>version:</c> satisfies
/// nothing and adds <c>KW-SKILL-SPEC-009</c>, so this parses with the real engine and scans
/// with the real scanner instead of grepping the text. The check is repository-wide because
/// the fix was reverted once; a single-skill assertion would not notice.
/// </summary>
public sealed class SkillProvenanceTests
{
    private static readonly HashSet<string> ProvenanceCodes = new(StringComparer.Ordinal)
    {
        "KW-SKILL-SEC-030",
        "KW-SKILL-SEC-031",
        "KW-SKILL-SEC-032",
    };

    [Fact]
    public void EveryApmSkillDeclaresNestedProvenanceMetadata()
    {
        string skillsRoot = Path.Combine(KyberWeaveTestPaths.ToolRoot, ".apm", "skills");

        Assert.Empty(SkillProvenanceIssues(skillsRoot));
    }

    /// <summary>
    /// A skill whose front matter cannot be parsed is reported as a finding, not thrown:
    /// one broken file must not hide every skill after it from the scan.
    /// </summary>
    [Fact]
    public void UnparseableSkillIsReportedInsteadOfAbortingTheScan()
    {
        using TempSkillsDir skills = TempSkillsDir.Create();
        skills.Add("broken", "---\nname: broken\n  bad: [unclosed\n---\n");
        skills.Add("healthy", ValidSkill("healthy"));

        IReadOnlyList<string> issues = SkillProvenanceIssues(skills.Root);

        Assert.Contains(issues, issue => issue.Contains("parse", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(issues, issue => issue.Contains("healthy", StringComparison.Ordinal));
    }

    /// <summary>
    /// The top-level hoist is the trap the docs warn about: nested provenance is present, so
    /// the missing-provenance codes stay quiet, but the stray key must still fail the guard.
    /// </summary>
    [Fact]
    public void TopLevelProvenanceKeyIsReportedEvenWhenNestedMetadataIsPresent()
    {
        using TempSkillsDir skills = TempSkillsDir.Create();
        skills.Add("hoisted", """
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

        IReadOnlyList<string> issues = SkillProvenanceIssues(skills.Root);

        Assert.Contains(issues, issue => issue.Contains("author", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SkillProvenanceIssues(string skillsRoot)
    {
        string[] skillFiles = Directory
            .EnumerateFiles(skillsRoot, "SKILL.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(skillFiles);

        SkillScanner scanner = new();
        List<string> issues = [];

        foreach (string skillFile in skillFiles)
        {
            string relative = Path.GetRelativePath(skillsRoot, skillFile);
            Skill skill;
            try
            {
                skill = SkillParser.ParseFile(skillFile);
            }
            catch (SkillParseException exception)
            {
                issues.Add($"{relative}: cannot parse front matter: {exception.Message}");
                continue;
            }

            foreach (Diagnostic diagnostic in scanner.Scan(skill))
            {
                if (ProvenanceCodes.Contains(diagnostic.Code))
                {
                    issues.Add($"{relative}: {diagnostic.Code} {diagnostic.Message}");
                }
            }

            foreach (string key in skill.Frontmatter.UnknownKeys.Keys)
            {
                issues.Add($"{relative}: unexpected top-level front matter key '{key}'");
            }
        }

        return issues;
    }

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

    private sealed class TempSkillsDir : IDisposable
    {
        private TempSkillsDir() => Directory.CreateDirectory(Root);

        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "kw-skill-provenance-" + Guid.NewGuid().ToString("N"));

        public static TempSkillsDir Create() => new();

        public void Add(string name, string content)
        {
            string directory = Path.Combine(Root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
