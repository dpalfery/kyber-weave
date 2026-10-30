using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the council review-guidance contract for the code-review skill and its agents under
/// <c>products/kyber-squad/</c>: the four language checklists exist as pointers to their declared
/// coding standard, every council entry point loads <c>REVIEW.md</c>, the review lens reports a
/// guidance attribute per finding and lists the guidance sections it checked on a
/// <c>NO FINDINGS</c> report, the code reviewer records a missing attestation as a coverage gap,
/// the correctness and test-adequacy lenses carry the portable technique, and the
/// <c>REVIEW.md</c> lens map names only lenses that exist.
/// </summary>
/// <remarks>
/// This is the RED half of a test-first task. Each assertion names the missing token or file it
/// is looking for, so a failure reads as the missing behaviour rather than a compile or harness
/// fault. Assertions match stable tokens, not full prose, so wording can change without breaking them.
/// </remarks>
public sealed class ReviewGuidanceCouncilContractTests
{
    private const string GuidanceFileName = "REVIEW.md";
    private const string LensMapHeading = "## Lens map";
    private const string EveryOtherLensRow = "every other lens";
    private const string LensColumnHeader = "Lens";

    private static readonly Regex TypeScriptFileToken = new(@"\.ts\b", RegexOptions.CultureInvariant);
    private static readonly Regex CSharpFileToken = new(@"\.cs\b", RegexOptions.CultureInvariant);

    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string ChecklistRoot =>
        Path.Combine(ProductRoot, "skills", "code-review", "references");

    private static string LensesRoot =>
        Path.Combine(ChecklistRoot, "lenses");

    private static string ReviewGuidancePath =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, GuidanceFileName);

    /// <summary>
    /// Prevents a language or framework the council meets from being reviewed with no checklist at
    /// all. Each new checklist is a pointer to its declared coding standard, never a rule of its own,
    /// and says so when no standard is declared rather than reporting the gap as clean.
    /// </summary>
    [Theory]
    [InlineData("typescript.md")]
    [InlineData("rust.md")]
    [InlineData("tauri.md")]
    [InlineData("go.md")]
    public void ALanguageChecklistExistsAndPointsAtItsDeclaredStandard(string fileName)
    {
        string path = Path.Combine(ChecklistRoot, fileName);
        Assert.True(File.Exists(path), $"Review checklist '{path}' is missing.");

        string text = File.ReadAllText(path);
        Assert.True(
            text.Contains("coding-standard>", StringComparison.Ordinal),
            $"'{fileName}' must point at a declared <...-coding-standard> rather than state its own rules.");
        Assert.True(
            text.Contains("standard declared", StringComparison.OrdinalIgnoreCase),
            $"'{fileName}' must say what happens when no standard is declared, not report the gap as clean.");
    }

    /// <summary>
    /// Prevents a language the council reviews from having a checklist that points at a standard the
    /// repository never declared. A checklist resolves <c>&lt;...-coding-standard&gt;</c> through the
    /// Config Reg block in <c>AGENTS.md</c>, so each technology needs that declaration, a standard file
    /// whose front matter is current, and a checklist file. The shipped checklist must not name this
    /// repository's path, because host repositories copy it. Without them the
    /// "standard declared" fallback is the only thing that answers, and it answers every time.
    /// </summary>
    [Theory]
    [InlineData("typescript")]
    [InlineData("rust")]
    [InlineData("tauri")]
    [InlineData("go")]
    public void ATechnologyDeclaresItsCodingStandardInTheRegistryAndItsChecklistExists(string technology)
    {
        string agentsText = ConfigRegBlock(
            File.ReadAllText(Path.Combine(KyberWeaveTestPaths.ToolRoot, "AGENTS.md")));
        string declaration = $"- **<{technology}-coding-standard>**: `docs/standards/{technology}/README.md`";
        string standardPath = Path.Combine(KyberWeaveTestPaths.ToolRoot, "docs", "standards", technology, "README.md");
        string checklistPath = Path.Combine(ChecklistRoot, $"{technology}.md");

        List<string> gaps = [];

        if (!agentsText.Contains(declaration, StringComparison.Ordinal))
        {
            gaps.Add($"'AGENTS.md' Config Reg block does not declare '{declaration}'.");
        }

        if (!File.Exists(standardPath))
        {
            gaps.Add($"Coding standard '{standardPath}' is missing.");
        }
        else
        {
            string[] frontMatter = FrontMatterLines(File.ReadAllText(standardPath));
            foreach (string expected in new[]
                         { "doc-type: coding-standard", $"technology: {technology}", "status: current" })
            {
                if (!frontMatter.Any(line => string.Equals(line, expected, StringComparison.Ordinal)))
                {
                    gaps.Add($"'{standardPath}' front matter does not state '{expected}'.");
                }
            }
        }

        if (!File.Exists(checklistPath))
        {
            gaps.Add($"Review checklist '{checklistPath}' is missing.");
        }
        else if (!File.ReadAllText(checklistPath).Contains($"<{technology}-coding-standard>", StringComparison.Ordinal))
        {
            gaps.Add($"'{technology}.md' does not name its own '<{technology}-coding-standard>' token.");
        }

        Assert.True(
            gaps.Count == 0,
            $"Technology '{technology}' is not fully wired to a declared coding standard: " + string.Join(" ", gaps));
    }

    /// <summary>
    /// Prevents a council entry point from naming the review guidance without applying it. A bare
    /// mention of <c>REVIEW.md</c> or the <c>&lt;review-guidance&gt;</c> path was already present in
    /// code-reviewer.md before the change, so each entry point must carry the token that proves it
    /// applies the guidance: the attestation line, the refuter and in-process sentences, or the
    /// apply-the-guidance instruction.
    /// </summary>
    [Theory]
    [InlineData("skills/code-review/SKILL.md", "REVIEW.md sections checked: [<ids>]")]
    [InlineData("skills/code-review/SKILL.md", "(`REVIEW.md`)")]
    [InlineData("agents/code-reviewer.md", "REVIEW.md sections checked: [<ids>]")]
    [InlineData("agents/code-reviewer.md", "The refuter receives the same REVIEW.md sections")]
    [InlineData("agents/code-reviewer.md", "Each in-process lens applies the same REVIEW.md sections")]
    [InlineData("agents/review-lens.md", "**Apply the review guidance.**")]
    [InlineData("agents/review-lens.md", "REVIEW.md sections checked: [<rule ids")]
    [InlineData("agents/review-triage.md", "**Apply the review guidance.**")]
    [InlineData("agents/review-triage.md", "REVIEW.md sections checked: [Ground rules]")]
    public void EveryCouncilEntryPointAppliesTheReviewGuidance(string relativePath, string token)
    {
        string text = ReadProduct(relativePath);

        Assert.True(
            text.Contains(token, StringComparison.Ordinal),
            $"'{relativePath}' must carry '{token}' to show it applies the review guidance, not merely names it.");
    }

    /// <summary>
    /// Prevents a finding from reaching the reviewer without the rule it applied. The
    /// <c>guidance:</c> field lets the adjudicator check each finding against the host's own rules
    /// rather than trusting that the lens read them.
    /// </summary>
    [Fact]
    public void ReviewLensRequiresAGuidanceAttributeOnEachFinding()
    {
        string text = ReadProduct("agents/review-lens.md");

        AssertContainsAll(text, "agents/review-lens.md", "guidance:", GuidanceFileName);
    }

    /// <summary>
    /// Prevents a clean <c>NO FINDINGS</c> report from passing without proof the guidance was read.
    /// The attestation must sit on the line directly after the report-form <c>NO FINDINGS</c> line,
    /// so a stray mention elsewhere in the file cannot satisfy it.
    /// </summary>
    [Fact]
    public void ReviewLensNoFindingsReportCarriesTheAttestationLine()
    {
        string text = ReadProduct("agents/review-lens.md");

        AssertAttestationFollows(
            text,
            "agents/review-lens.md",
            "NO FINDINGS: <one sentence",
            "REVIEW.md sections checked: [");
    }

    /// <summary>
    /// Prevents a findings-form lens output from omitting the attestation. The findings instruction
    /// must end with the attestation line, and the attestation form must appear after it.
    /// </summary>
    [Fact]
    public void ReviewLensFindingsFormEndsWithTheAttestationLine()
    {
        string text = ReadProduct("agents/review-lens.md");

        int index = text.IndexOf("Return the YAML findings list", StringComparison.Ordinal);
        Assert.True(index >= 0, "'agents/review-lens.md' must keep its findings-form output instruction.");
        Assert.Contains("then the attestation line as its last line", text[index..], StringComparison.Ordinal);
        Assert.True(
            text[index..].Contains("REVIEW.md sections checked: [", StringComparison.Ordinal),
            "'agents/review-lens.md' findings form must carry the 'REVIEW.md sections checked: [' attestation line.");
    }

    /// <summary>
    /// Prevents a triage seat's clean report from passing without its attestation. Triage seats apply
    /// the ground rules only, so the attestation must name <c>[Ground rules]</c> on the line directly
    /// after the report-form <c>NO FINDINGS</c> line.
    /// </summary>
    [Fact]
    public void ReviewTriageNoFindingsReportAttestsItsGroundRules()
    {
        string text = ReadProduct("agents/review-triage.md");

        AssertAttestationFollows(
            text,
            "agents/review-triage.md",
            "NO FINDINGS: <what artifact",
            "REVIEW.md sections checked: [Ground rules]");
    }

    /// <summary>
    /// Prevents the per-finding <c>guidance:</c> field from losing its worked example. The example
    /// shows the rule-id form the adjudicator and refuter read, so a finding can be checked against
    /// the exact section it names.
    /// </summary>
    [Fact]
    public void ReviewLensGuidanceFieldKeepsItsRuleIdExample()
    {
        string text = ReadProduct("agents/review-lens.md");

        AssertContainsAll(text, "agents/review-lens.md", "guidance: <optional", "e.g. [R8, §1]");
    }

    /// <summary>
    /// Prevents a lens that omitted its guidance attestation from passing silently. A missing
    /// attestation is recorded as a coverage gap in the report; it is not a verdict input, so the
    /// reviewer still sees that the guidance was not proven applied.
    /// </summary>
    [Fact]
    public void CodeReviewerRecordsAMissingGuidanceAttestationAsACoverageGap()
    {
        string text = ReadProduct("agents/code-reviewer.md");

        AssertContainsAll(text, "agents/code-reviewer.md", "attestation", "coverage gap");
    }

    /// <summary>
    /// Prevents the correctness lens from missing the portable input technique: malformed or null
    /// input, non-finite numbers, and boundary handling across CRLF line endings. These are the
    /// failures that the earlier council run did not catch.
    /// </summary>
    [Fact]
    public void CorrectnessLensCarriesTheMalformedInputAndBoundaryTechnique()
    {
        string text = ReadProduct("skills/code-review/references/lenses/correctness.md");

        AssertContainsAll(text, "correctness.md", "Malformed", "Non-finite", "CRLF");
    }

    /// <summary>
    /// Prevents a changed <c>.ts</c> or <c>.cs</c> file from passing the test-adequacy lens without
    /// the lens asking whether its behaviour has a test. The lens must name both file types.
    /// </summary>
    [Fact]
    public void TestAdequacyLensCoversChangedTypeScriptAndCSharpFiles()
    {
        string text = ReadProduct("skills/code-review/references/lenses/test-adequacy.md");

        Assert.True(
            TypeScriptFileToken.IsMatch(text),
            "'test-adequacy.md' must cover changed .ts files.");
        Assert.True(
            CSharpFileToken.IsMatch(text),
            "'test-adequacy.md' must cover changed .cs files.");
    }

    /// <summary>
    /// Prevents the council strictness setting from reaching the verdict engine but not the lenses:
    /// the code-review skill must pass <c>review.policy.strictness</c> to every seat, so a strict
    /// host gets lower-confidence findings reported instead of silently dropped at source.
    /// </summary>
    [Fact]
    public void TheCodeReviewSkillPassesTheStrictnessToEverySeat()
    {
        string skill = File.ReadAllText(Path.Combine(ProductRoot, "skills", "code-review", "SKILL.md"));
        Assert.True(
            skill.Contains("review.policy.strictness", StringComparison.Ordinal),
            "The code-review skill must document review.policy.strictness.");
        Assert.True(
            Regex.IsMatch(
                skill,
                @"Pass\s+the\s+strictness\s+and\s+the\s+effective\s+`min-confidence`\s+to\s+\*\*every\s+seat\*\*",
                RegexOptions.CultureInvariant),
            "The code-review skill must tell the reviewer to pass the strictness and effective floor to every seat.");

        string lens = File.ReadAllText(Path.Combine(ProductRoot, "agents", "review-lens.md"));
        Assert.True(
            lens.Contains("`lenient`", StringComparison.Ordinal) &&
            lens.Contains("`balanced`", StringComparison.Ordinal) &&
            lens.Contains("`strict`", StringComparison.Ordinal),
            "The review-lens agent must say how it applies each strictness level it is given.");
    }

    /// <summary>
    /// Prevents the <c>REVIEW.md</c> lens map from routing guidance to lens names that do not exist.
    /// A row naming a missing lens falls into the catch-all and its guidance is never applied, so
    /// every named lens must match a file under <c>references/lenses/</c>.
    /// </summary>
    [Fact]
    public void ReviewGuidanceLensMapNamesOnlyLensesThatExist()
    {
        HashSet<string> lensNames = new(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(LensesRoot, "*.md"))
        {
            lensNames.Add(Path.GetFileNameWithoutExtension(file) ?? string.Empty);
        }

        string[] lines = File.ReadAllLines(ReviewGuidancePath);
        int start = Array.FindIndex(lines, line => line.Trim() == LensMapHeading);
        Assert.True(start >= 0, $"{GuidanceFileName} must keep its '{LensMapHeading}' section.");

        char[] separators = ['/', ','];
        List<string> unknown = [];
        int rowCount = 0;
        for (int i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
        {
            string line = lines[i].Trim();
            if (!line.StartsWith('|'))
            {
                continue;
            }

            string lensColumn = line.Trim('|').Split('|')[0].Trim();
            if (lensColumn == LensColumnHeader ||
                lensColumn.StartsWith("---", StringComparison.Ordinal) ||
                lensColumn.Equals(EveryOtherLensRow, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rowCount++;
            foreach (string name in lensColumn.Split(
                separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (!lensNames.Contains(name))
                {
                    unknown.Add($"'{lensColumn}' names '{name}'");
                }
            }
        }

        Assert.True(rowCount > 0, $"{GuidanceFileName} lens map has no lens rows to check.");
        Assert.True(
            unknown.Count == 0,
            $"{GuidanceFileName} lens map names lenses that do not exist under references/lenses/: " +
            string.Join("; ", unknown) + ".");
    }

    /// <summary>Returns the text between the Config Reg markers, or empty when either is missing.</summary>
    private static string ConfigRegBlock(string text)
    {
        const string start = "<!-- KYBER_WEAVE_CONFIG_REG_START -->";
        const string end = "<!-- KYBER_WEAVE_CONFIG_REG_END -->";
        int from = text.IndexOf(start, StringComparison.Ordinal);
        int to = text.IndexOf(end, StringComparison.Ordinal);
        return from < 0 || to < from ? string.Empty : text[from..to];
    }

    /// <summary>Returns the trimmed lines between the opening and closing <c>---</c> of a document's front matter.</summary>
    private static string[] FrontMatterLines(string text)
    {
        string[] lines = text.Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return [];
        }

        List<string> block = [];
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                return block.ToArray();
            }

            block.Add(lines[i].Trim());
        }

        return [];
    }

    private static string ReadProduct(string relativePath)
    {
        return File.ReadAllText(Path.Combine(ProductRoot, relativePath));
    }

    private static void AssertAttestationFollows(string text, string source, string anchor, string attestation)
    {
        string[] lines = text.Split('\n');
        int index = Array.FindIndex(lines, line => line.TrimEnd('\r').StartsWith(anchor, StringComparison.Ordinal));
        Assert.True(index >= 0, $"'{source}' must keep its report line starting '{anchor}'.");
        Assert.True(
            index + 1 < lines.Length && lines[index + 1].Trim().StartsWith(attestation, StringComparison.Ordinal),
            $"'{source}' must put '{attestation}' on the line directly after '{anchor}'.");
    }

    private static void AssertContainsAll(string text, string source, params string[] tokens)
    {
        string[] missing = tokens
            .Where(token => !text.Contains(token, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"'{source}' is missing required token(s): {string.Join(", ", missing)}.");
    }
}
