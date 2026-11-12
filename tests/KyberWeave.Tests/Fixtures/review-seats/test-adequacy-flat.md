```yaml
- id: test-adequacy/council-deadline-nonpositive-untested
  lens: test-adequacy
  severity: major
  confidence: 9
  file: src/KyberWeave.Core/Configuration/ReviewConfigLoader.cs
  line: 779
  excerpt: |
    if (seconds <= 0)
        throw new YamlException("review.council-deadline-seconds must be greater than zero.");
  claim: The new guard that refuses a zero or negative council deadline has no test.
  evidence: GateRunnerDeadlineTests.cs covers the key at 42 (TheCouncilDeadlineIsReadFromTheHostConfiguration) and the absent key at 600 (AHostWithNoDeadlineKeyGetsSixHundredSeconds). A grep of tests/ for "council-deadline" finds only 1, 2 and 42, so no test uses 0 or a negative value.
  failure_scenario: Someone deletes the guard, or changes it to `< 0`. A host writing `council-deadline-seconds: 0` is then accepted, and every council gate becomes DeadlinePassed before it starts. The verdict approves on no gate evidence (KW-REVIEW-039 is a warning only). The comment on the guard describes exactly this outcome. The whole suite stays green.
  guidance: [R15, §10]
  suggestion: Add a theory over 0 and -5 (and a non-integer value) that loads the config and asserts the load fails with a message naming review.council-deadline-seconds.

- id: test-adequacy/seat-parser-malformed-input-untested
  lens: test-adequacy
  severity: major
  confidence: 9
  file: src/KyberWeave.Core/Review/SeatOutputParser.cs
  line: 172
  excerpt: |
    if (!Enum.TryParse(severityText, ignoreCase: true, out ReviewSeverity severity)
        || !Enum.IsDefined(severity)
        || int.TryParse(severityText, CultureInfo.InvariantCulture, out _))
  claim: SeatOutputParser is a new parser with many refusal branches, and only one malformed input is tested, an unclosed flow sequence.
  evidence: |
    SeatOutputParserTests.cs has one malformed case, UnparseableYamlThrowsATypedErrorCarryingTheReason. These branches have no test:
    - a numeric or unknown severity, such as `severity: 2`
    - a non-integer confidence or line
    - a missing id or file
    - a list item that is not a mapping
    - a root that is not a list
    - a field that is a nested mapping (the "not text" branch)
    - empty or whitespace-only seat text, which hits the "no findings list" throw
    - a CRLF seat file
    - a malformed Discovery line, which should read as null
  failure_scenario: |
    Two regressions pass unnoticed.
    (a) The `int.TryParse(severityText)` clause is dropped. `severity: 2` then parses as an undefined enum value and flows to the engine as a finding with a nonsense severity.
    (b) The `findings.Count == 0` throw is dropped. An empty or garbled seat file then reads as a seat with zero findings and no refusal. The lens is covered as clean without anyone having reviewed it.
  guidance: [R15, §10]
  suggestion: Add a theory of malformed seat texts (bad severity, bad confidence, missing id, non-list root, empty text, CRLF-wrapped valid input). Assert SeatOutputException and its message for each. Also assert that a malformed Discovery line yields Discovery == null.

- id: test-adequacy/seat-parser-broad-exception-assertion
  lens: test-adequacy
  severity: minor
  confidence: 8
  file: tests/KyberWeave.Tests/SeatOutputParserTests.cs
  line: 5072
  excerpt: |
    Exception error = Assert.ThrowsAny<Exception>(() => SeatOutputParser.Parse("correctness", broken));

    Assert.IsNotType<NotImplementedException>(error);
  claim: The only parser failure test accepts any exception except NotImplementedException, and its doc comment still describes the skeleton stage.
  evidence: |
    The method's doc comment says the typed exception "is not in the skeleton yet". The assertion never checks for SeatOutputException.
    ReviewVerdictCommand.TryReadSeats catches only `SeatOutputException or IOException or UnauthorizedAccessException`.
  failure_scenario: |
    A refactor lets a raw YamlException or InvalidOperationException escape Parse. This test still passes.
    The verdict command would then crash instead of reporting KW-REVIEW-040.
  guidance: [R15, §10]
  suggestion: Use `Assert.Throws<SeatOutputException>`, assert the message names the lens, and drop the stale skeleton comment.

- id: test-adequacy/cited-line-guards-untested
  lens: test-adequacy
  severity: major
  confidence: 8
  file: src/KyberWeave.Core/Review/CitedLineCheck.cs
  line: 36
  excerpt: |
    if (!IsRepositoryRelative(finding.File)
        || !DocsRootPath.IsContainedFile(repositoryRoot, finding.File, []))
    {
        return CitedLineResult.Unchanged(finding);
    }
  claim: The path-containment guard, the no-excerpt drop, the missing-file path and the multi-line excerpt anchor in CitedLineCheck have no test.
  evidence: |
    The command tests exercise only three cases: a relocated line, an ambiguous or absent excerpt that is dropped, and a line inside the file left alone.
    No test cites a `..` or rooted path, a nonexistent file, a missing excerpt, or a multi-line excerpt.
    The guard exists because the path comes from seat text.
  failure_scenario: |
    The IsRepositoryRelative check is removed. A seat citing `../../etc/passwd` or an absolute path then makes the verdict command read a file outside the repository.
    Likewise, if the `anchor is null` branch is removed, a finding with no excerpt and a line past EOF gets an exception or a wrong relocation. Nothing fails.
  guidance: [R15, §10]
  suggestion: Add CitedLineCheck unit tests for a `..` path, a rooted path, a missing file, a past-EOF line with no excerpt (Unresolved), a multi-line excerpt anchored on its first non-blank line, and `Line == lines.Length` (Unchanged).

- id: test-adequacy/verdict-command-refusals-untested
  lens: test-adequacy
  severity: major
  confidence: 8
  file: src/KyberWeave.Cli/Commands/Review/ReviewVerdictCommand.cs
  line: 145
  excerpt: |
    if (settings.Seats is not null && settings.Findings is not null)
    {
        report.Add(new Diagnostic(
            UnreadableInput,
            Severity.Error,
            "--seats and --findings name two sources for the same findings. Pass one of them.",
  claim: Several new refusal paths in ReviewVerdictCommand have no test, though they are the command's malformed-input handling.
  evidence: |
    ReviewVerdictSeatsCommandTests covers a bad seat file, an unaccounted lens, --not-seated, --drop with an unknown id, and a positive --prior. These paths have no test:
    - `--seats` together with `--findings`
    - a `--seats` folder that does not exist
    - `--not-seated` with no `=` or an unknown lens (KW-041 error)
    - `--drop` with no `=` or a blank reason (KW-042 error)
    - `--prior` pointing at a folder with no findings.json
    - a seat file named for no catalogue lens (warning)
    - several unreadable seat files, which must all be reported
  failure_scenario: |
    TrySplitReason is changed to accept an empty reason.
    `--drop id=` then removes a finding with no stated reason. The command's rule is that a drop needs one.
    Or the unknown-lens check on --not-seated is dropped, so a typo such as `--not-seated securty=skip` is accepted. The real security lens stays unaccounted, but coverage is falsely complete if it was matched loosely.
    The suite stays green.
  guidance: [R15, §10]
  suggestion: Add facts for each listed refusal. Assert exit 1 and the specific KW code, plus the offending value in the message.

- id: test-adequacy/bare-warning-contains
  lens: test-adequacy
  severity: minor
  confidence: 6
  file: tests/KyberWeave.Tests/ReviewVerdictSeatsCommandTests.cs
  line: 4701
  excerpt: |
    Assert.Contains(DiscoveryMissing, run.Output, StringComparison.Ordinal);
    Assert.Contains("warning", run.Output, StringComparison.OrdinalIgnoreCase);
  claim: A bare case-insensitive "warning" substring over the whole console output does not show that this diagnostic has Warning severity.
  evidence: |
    The same pattern is used in ALineBeyondTheFileEndWithNoUniqueMatchIsDroppedAsAWarning.
    The JSON report format can carry the word "warning" from a severity-count field or from other diagnostics (for example KW-REVIEW-043 text), not only from this finding.
  failure_scenario: |
    The diagnostic is downgraded to Info, which is a real behaviour change since the verdict command's contract is "warning, never refusal".
    The test still passes if any other output carries the word "warning".
  guidance: [R14, §10]
  suggestion: Parse the JSON output and assert the severity of the diagnostic whose code is KW-REVIEW-044 (and KW-REVIEW-043).

- id: test-adequacy/minor-only-loop-unscoped-substrings
  lens: test-adequacy
  severity: minor
  confidence: 7
  file: tests/KyberWeave.Tests/SquadAutonomyRemediationTests.cs
  line: 5216
  excerpt: |
    AssertAll(execution,
        "Minor/Info",
        "never loop");
  claim: The rewritten minor-only test pins two unrelated substrings anywhere in a large skill file, and the sibling test keeps bare "Rule" and "Verdict" pins.
  evidence: |
    These checks are made over the whole execution-contract text, not over one sentence. Elsewhere the same PR moved other contract tests to full-sentence AssertSentence pins.
    The change retargeted the verdict-table test but kept `AssertAll(skill, "Rule", "Verdict", "major-count-blocks")`. "Rule" and "Verdict" are single common words.
  failure_scenario: |
    The Minor/Info sentence is changed to "Minor/Info findings always loop".
    The test still passes, because "Minor/Info" appears in another sentence and "never loop" appears in an unrelated one.
  guidance: [R14, §10]
  suggestion: Replace with one AssertSentence over the full Minor/Info-never-loop sentence, and drop or replace the bare "Rule" and "Verdict" pins with a contiguous table-header assertion.
```

Index fallback: not-used; reviewed the materialized patch and REVIEW.md directly, with no per-file index call.
Discovery: codegraph 0, docs 0, direct reads 8
REVIEW.md sections checked: [Ground rules, §10, R14, R15]
