using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the review council loop sentences the prose rule catalog does not cover (the Discovery
/// line's place in a seat's output, the cycle folder layout, and the rule-subject check), and keeps
/// the retired loop, confidence and verdict wording out of every product file and the root
/// <c>AGENTS.md</c>. The rest of the loop's rules are checked by the catalog,
/// <c>Fixtures/prose-rules/catalog.yml</c>. Each row names one sentence and one file that must
/// carry it, so a failure names both.
/// </summary>
/// <remarks>
/// Sentences are compared after normalisation: backticks and asterisks are removed and every
/// whitespace run collapses to one space, so a re-wrap or emphasis change does not move a fact.
/// Retired phrases are compared raw and ordinal, so the old wording cannot survive in any file.
/// </remarks>
public sealed class ReviewCouncilLoopContractTests
{
    private static string ProductRoot =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static string Normalise(string text)
    {
        string unmarked = text
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal);

        return Regex.Replace(unmarked, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Paths are relative to the kyber-squad product root unless they start with <c>AGENTS.md</c>,
    /// which names the repository root file.
    /// </summary>
    private static string ResolvePath(string file)
    {
        return file.StartsWith("AGENTS.md", StringComparison.Ordinal)
            ? Path.Combine(KyberWeaveTestPaths.ToolRoot, "AGENTS.md")
            : Path.Combine(ProductRoot, file.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The retired wording is searched in every file under the product root and in the root
    /// <c>AGENTS.md</c>, so no surface can keep the old loop, confidence or verdict rules.
    /// </summary>
    private static IEnumerable<string> ScannedFiles()
    {
        foreach (string file in Directory.EnumerateFiles(ProductRoot, "*", SearchOption.AllDirectories))
        {
            yield return file;
        }

        yield return Path.Combine(KyberWeaveTestPaths.ToolRoot, "AGENTS.md");
    }

    [Theory]
    [InlineData("S11", "Put Discovery: codegraph N, docs N, direct reads N directly above the attestation line, below any Index fallback line.", "agents/review-lens.md")]
    [InlineData("S11", "Put Discovery: codegraph N, docs N, direct reads N directly above the attestation line, below any Index fallback line.", "agents/review-triage.md")]
    [InlineData("S18", "Each cycle writes to .agent_scratchpad/<task-id>/review/cycle-<n>/: the seats folder, gates.json, the findings.json the verdict command writes, and head; keep it until the run ends.", "agents/code-reviewer.md")]
    [InlineData("S18", "Each cycle writes to .agent_scratchpad/<task-id>/review/cycle-<n>/: the seats folder, gates.json, the findings.json the verdict command writes, and head; keep it until the run ends.", "skills/code-review/SKILL.md")]
    [InlineData("S18", "Each cycle writes to .agent_scratchpad/<task-id>/review/cycle-<n>/: the seats folder, gates.json, the findings.json the verdict command writes, and head; keep it until the run ends.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S24", "The subject of a rule sentence is a claim: for \"a seat\", \"every gate\" or \"any X\", open the call site that decides who the rule applies to.", "skills/code-review/references/lenses/documentation-accuracy.md")]
    public void SentenceIsStatedInItsFile(string sentenceId, string sentence, string file)
    {
        string path = ResolvePath(file);
        Assert.True(File.Exists(path), $"{sentenceId}: '{file}' is missing, so it cannot carry the sentence.");

        string text = Normalise(File.ReadAllText(path));
        string expected = Normalise(sentence);

        Assert.True(
            text.Contains(expected, StringComparison.Ordinal),
            $"{sentenceId} is missing from {file}: {expected}");
    }

    [Theory]
    [InlineData("exactly once over the accumulated run")]
    [InlineData("earns one remediation pass")]
    [InlineData("one bounded remediation pass")]
    [InlineData("fails a second council review")]
    [InlineData("then review once")]
    [InlineData("reviews the finished fix once")]
    [InlineData("Use once at the end of a run")]
    [InlineData("Findings below 7 are noise")]
    [InlineData("Drop anything below confidence 7")]
    [InlineData("Discovery reporting is exempt")]
    [InlineData("thirteen lens invocations")]
    [InlineData("kyber-weave review verdict . --findings")]
    [InlineData("re-check **only that finding**")]
    [InlineData("leave the defect alone")]
    public void RetiredPhraseIsAbsentFromEveryProductFileAndTheRootAgentsFile(string retiredPhrase)
    {
        List<string> files = ScannedFiles().ToList();
        Assert.NotEmpty(files);

        string[] offenders = files
            .Where(file => File.ReadAllText(file).Contains(retiredPhrase, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(KyberWeaveTestPaths.ToolRoot, file))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Retired phrase '{retiredPhrase}' is still present in: {string.Join(", ", offenders)}");
    }
}
