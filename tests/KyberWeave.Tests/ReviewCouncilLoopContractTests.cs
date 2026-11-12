using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the review council loop prose: the council re-reviews after every fix round until it
/// returns APPROVE, the loop stops after five cycles or three flat cycles, every review after
/// the first is a verifier cycle, and the seats, verdict and discovery rules the council runs on.
/// Each row names one sentence and one file that must carry it, so a failure names both.
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
    [InlineData("S1", "The council re-reviews after every fix round until it returns APPROVE.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S1", "The council re-reviews after every fix round until it returns APPROVE.", "agents/conductor.md")]
    [InlineData("S1", "The council re-reviews after every fix round until it returns APPROVE.", "agents/code-reviewer.md")]
    [InlineData("S1", "The council re-reviews after every fix round until it returns APPROVE.", "skills/code-review/SKILL.md")]
    [InlineData("S1", "The council re-reviews after every fix round until it returns APPROVE.", "skills/bug-crusher/SKILL.md")]
    [InlineData("S1", "The council re-reviews after every fix round until it returns APPROVE.", "skills/dp-code-reviewer/SKILL.md")]
    [InlineData("S1", "The council re-reviews after every fix round until it returns APPROVE.", "agents/conductor/references/autonomy.md")]
    [InlineData("S2", "The loop stops after 5 review cycles, or after 3 cycles in a row in which the count of surviving critical and major findings does not fall; the findings still open are then listed in the pull request as pending human review, and delivery continues.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S2", "The loop stops after 5 review cycles, or after 3 cycles in a row in which the count of surviving critical and major findings does not fall; the findings still open are then listed in the pull request as pending human review, and delivery continues.", "skills/dp-code-reviewer/SKILL.md")]
    [InlineData("S2", "The loop stops after 5 review cycles, or after 3 cycles in a row in which the count of surviving critical and major findings does not fall; the findings still open are then listed in the pull request as pending human review, and delivery continues.", "agents/conductor/references/autonomy.md")]
    [InlineData("S3", "Every review after the first runs in the verifier mode of the dp-code-reviewer skill: it re-checks the prior findings and the fix delta, not a full council.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S3", "Every review after the first runs in the verifier mode of the dp-code-reviewer skill: it re-checks the prior findings and the fix delta, not a full council.", "agents/code-reviewer.md")]
    [InlineData("S3", "Every review after the first runs in the verifier mode of the dp-code-reviewer skill: it re-checks the prior findings and the fix delta, not a full council.", "skills/code-review/SKILL.md")]
    [InlineData("S3", "Every review after the first runs in the verifier mode of the dp-code-reviewer skill: it re-checks the prior findings and the fix delta, not a full council.", "skills/bug-crusher/SKILL.md")]
    [InlineData("S4", "A gate that times out, or that the council deadline stops, is NOT RUN: it is listed as unverified, and it is never a blocking failure.", "skills/code-review/SKILL.md")]
    [InlineData("S4", "A gate that times out, or that the council deadline stops, is NOT RUN: it is listed as unverified, and it is never a blocking failure.", "agents/code-reviewer.md")]
    [InlineData("S5", "Report a finding only at or above the effective min-confidence your invocation names; when none is named, the floor is 7.", "agents/review-lens.md")]
    [InlineData("S5", "Report a finding only at or above the effective min-confidence your invocation names; when none is named, the floor is 7.", "agents/review-triage.md")]
    [InlineData("S6", "The verdict engine drops findings below the effective min-confidence; you never drop a finding for confidence yourself.", "agents/code-reviewer.md")]
    [InlineData("S6", "The verdict engine drops findings below the effective min-confidence; you never drop a finding for confidence yourself.", "skills/code-review/SKILL.md")]
    [InlineData("S7", "You never change a finding's severity: you keep it, or drop it with a reason through --drop <id>=<reason>.", "agents/code-reviewer.md")]
    [InlineData("S7", "You never change a finding's severity: you keep it, or drop it with a reason through --drop <id>=<reason>.", "skills/code-review/SKILL.md")]
    [InlineData("S8", "Save each seat's output unchanged as <lens>.md in the cycle's seats folder before you adjudicate.", "agents/code-reviewer.md")]
    [InlineData("S8", "Save each seat's output unchanged as <lens>.md in the cycle's seats folder before you adjudicate.", "skills/code-review/SKILL.md")]
    [InlineData("S9", "Record every lens you do not seat with --not-seated <lens>=<reason>; the verdict refuses to compute while a lens has neither a seat file nor a reason (KW-REVIEW-041).", "agents/code-reviewer.md")]
    [InlineData("S9", "Record every lens you do not seat with --not-seated <lens>=<reason>; the verdict refuses to compute while a lens has neither a seat file nor a reason (KW-REVIEW-041).", "skills/code-review/SKILL.md")]
    [InlineData("S10", "Refute in one batched review-lens spawn, and only the findings the verdict command lists as refutation candidates (KW-REVIEW-046).", "agents/code-reviewer.md")]
    [InlineData("S10", "Refute in one batched review-lens spawn, and only the findings the verdict command lists as refutation candidates (KW-REVIEW-046).", "skills/code-review/SKILL.md")]
    [InlineData("S11", "Put Discovery: codegraph N, docs N, direct reads N directly above the attestation line, below any Index fallback line.", "agents/review-lens.md")]
    [InlineData("S11", "Put Discovery: codegraph N, docs N, direct reads N directly above the attestation line, below any Index fallback line.", "agents/review-triage.md")]
    [InlineData("S12", "line is the line number in the file as you read it, never a line offset in the patch file.", "agents/review-lens.md")]
    [InlineData("S12", "line is the line number in the file as you read it, never a line offset in the patch file.", "agents/review-triage.md")]
    [InlineData("S12", "line is the line number in the file as you read it, never a line offset in the patch file.", "skills/code-review/SKILL.md")]
    [InlineData("S13", "When the diff changes a rule's wording in one file, list every other file that carries the same rule text, check each one, and report each sibling the diff left stale.", "skills/code-review/references/lenses/documentation-accuracy.md")]
    [InlineData("S13", "When the diff changes a rule's wording in one file, list every other file that carries the same rule text, check each one, and report each sibling the diff left stale.", "skills/code-review/SKILL.md")]
    [InlineData("S14", "A change of at most 300 changed lines (added plus removed, from git diff --numstat <base>...HEAD) gets the quick review before its pull request is opened; a larger change gets the full pre-push council.", "AGENTS.md")]
    [InlineData("S14", "A change of at most 300 changed lines (added plus removed, from git diff --numstat <base>...HEAD) gets the quick review before its pull request is opened; a larger change gets the full pre-push council.", "skills/create-pull-request/SKILL.md")]
    [InlineData("S14", "A change of at most 300 changed lines (added plus removed, from git diff --numstat <base>...HEAD) gets the quick review before its pull request is opened; a larger change gets the full pre-push council.", "skills/code-review/SKILL.md")]
    [InlineData("S15", "The loop runs on the open pull request: delivery never waits for it, and required CI alone decides ready.", "agents/conductor/references/autonomy.md")]
    [InlineData("S15", "The loop runs on the open pull request: delivery never waits for it, and required CI alone decides ready.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S16", "A failed blocking gate is fixed inside the same loop: it goes to the worker that owns the failing scope, like a finding.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S17", "A verdict command refusal (KW-REVIEW-040, KW-REVIEW-041, KW-REVIEW-042) is yours to fix in the same cycle; it never goes to a developer and never counts as a review cycle.", "agents/code-reviewer.md")]
    [InlineData("S18", "Each cycle writes to .agent_scratchpad/<task-id>/review/cycle-<n>/: the seats folder, gates.json, the findings.json the verdict command writes, and head; keep it until the run ends.", "agents/code-reviewer.md")]
    [InlineData("S18", "Each cycle writes to .agent_scratchpad/<task-id>/review/cycle-<n>/: the seats folder, gates.json, the findings.json the verdict command writes, and head; keep it until the run ends.", "skills/code-review/SKILL.md")]
    [InlineData("S18", "Each cycle writes to .agent_scratchpad/<task-id>/review/cycle-<n>/: the seats folder, gates.json, the findings.json the verdict command writes, and head; keep it until the run ends.", "skills/delivery-execution/SKILL.md")]
    [InlineData("S19", "A verifier cycle reviews the fix delta git diff <prior head>...<head>, re-seats only the lenses that own an outstanding finding or whose predicate the delta newly satisfies, and passes --prior <prior cycle folder>.", "skills/dp-code-reviewer/SKILL.md")]
    [InlineData("S19", "A verifier cycle reviews the fix delta git diff <prior head>...<head>, re-seats only the lenses that own an outstanding finding or whose predicate the delta newly satisfies, and passes --prior <prior cycle folder>.", "skills/code-review/SKILL.md")]
    [InlineData("S20", "For every changed code file, write \"no input site\" or one row per site where a value from outside the process is parsed, converted, or passed to a file or collection API.", "skills/code-review/references/lenses/correctness.md")]
    [InlineData("S20", "For every changed code file, write \"no input site\" or one row per site where a value from outside the process is parsed, converted, or passed to a file or collection API.", "agents/review-lens.md")]
    [InlineData("S21", "Every THROWS or accepted wrong cell is a finding.", "skills/code-review/references/lenses/correctness.md")]
    [InlineData("S22", "Close a correctness seat with Input sites: N traced, M files with none, directly above the Discovery line.", "skills/code-review/references/lenses/correctness.md")]
    [InlineData("S22", "Close a correctness seat with Input sites: N traced, M files with none, directly above the Discovery line.", "agents/review-lens.md")]
    [InlineData("S23", "A finding is FIXED only when the fix refuses its whole class: state the class in one sentence, name at least three other inputs of that class that the original failure scenario did not name, and trace each through the fix.", "skills/dp-code-reviewer/SKILL.md")]
    [InlineData("S23", "A finding is FIXED only when the fix refuses its whole class: state the class in one sentence, name at least three other inputs of that class that the original failure scenario did not name, and trace each through the fix.", "skills/code-review/SKILL.md")]
    [InlineData("S23", "A finding is FIXED only when the fix refuses its whole class: state the class in one sentence, name at least three other inputs of that class that the original failure scenario did not name, and trace each through the fix.", "agents/code-reviewer.md")]
    [InlineData("S24", "The subject of a rule sentence is a claim: for \"a seat\", \"every gate\" or \"any X\", open the call site that decides who the rule applies to.", "skills/code-review/references/lenses/documentation-accuracy.md")]
    [InlineData("S25", "For each rule id or command the changed prose states, find every document that states it and quote the statements side by side; a different subject, condition or flag list is a finding.", "skills/code-review/references/lenses/documentation-accuracy.md")]
    [InlineData("S26", "Say what the code does today for each malformed input you name; when you cannot show it is refused, add one test-adequacy/out-of-scope finding naming correctness as the owner.", "skills/code-review/references/lenses/test-adequacy.md")]
    [InlineData("S27", "Do not add your own list of things to hunt for; when you name focus files, the seat accounts for each one.", "agents/code-reviewer.md")]
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
