```yaml
- id: supportability/deadline-cut-hint-wrong-remedy
  lens: supportability
  severity: minor
  confidence: 7
  file: src/KyberWeave.Cli/Commands/Review/ReviewGatesCommand.cs
  line: 499
  excerpt: |
    Hint: $"Re-run it alone with 'kyber-weave review gates --gate {gate.Id}', or raise its timeout-seconds."));
  claim: The NOT RUN hint offers "raise its timeout-seconds" for every time-stopped gate, including gates the council deadline cut, where that change does nothing.
  evidence: GateRunner.cs:300-301 uses the time left when it is less than the gate's own timeout. A gate with Outcome DeadlinePassed (GateRunner.cs:236-243, 318-327) was therefore stopped by `CouncilDeadlineSeconds`, not by its own `timeout-seconds`. AddGateDiagnostic (ReviewGatesCommand.cs:492-500) gives the same hint for TimedOut and DeadlinePassed.
  failure_scenario: A gate with a 900 s timeout is cut by a 600 s council deadline. An operator who follows the hint raises the gate's timeout-seconds, and the next council run is cut at the same point. The setting that governs the cut, review.council-deadline-seconds, is never mentioned.
  guidance: [§3]
  suggestion: Branch the hint on gate.Outcome. For DeadlinePassed, say the council deadline stopped it and name the deadline setting. Keep the raise-timeout advice for TimedOut only.

- id: supportability/deadline-cut-message-reports-remaining-time
  lens: supportability
  severity: minor
  confidence: 7
  file: src/KyberWeave.Core/Review/GateRunner.cs
  line: 324
  excerpt: |
    $"stopped by the council deadline after {timeout.TotalSeconds:0} seconds: {ex.Message}",
  claim: The mid-run deadline message prints the time left when the gate started, not the deadline, while the not-started message prints the deadline itself.
  evidence: On line 301, `timeout` is the remaining budget (`timeLeft`) when `deadlineFirst` is true. DeadlinePassedBeforeStart (line 241) says "the council deadline of {deadline} seconds", so the two messages use "council deadline" for different numbers. The configured deadline is not reported anywhere in the mid-run case.
  failure_scenario: With a 600 s deadline and 450 s already used, the cut gate reports "stopped by the council deadline after 150 seconds". A reader takes the deadline to be 150 s and either misconfigures it or cannot reconcile the figure with the 600 s setting.
  guidance: [§3]
  suggestion: Include the configured deadline, as in "stopped at the council deadline of 600 seconds (ran 150 seconds)". Pass the deadline into RunArgv, or word the message as "ran N seconds before the council deadline".

- id: supportability/seat-declared-outcome-silently-discards-findings
  lens: supportability
  severity: major
  confidence: 6
  file: src/KyberWeave.Core/Review/SeatOutputParser.cs
  line: 68
  excerpt: |
    if (declared is { } kind)
        return new SeatOutput(lens, kind, [], attestation, indexFallback, discovery);
  claim: A seat file that has both a `NO FINDINGS:` or `SKIPPED:` line at the start of a line and a YAML findings list returns the declared outcome with an empty findings list, with no diagnostic.
  evidence: Lines 59-62 lift any line-start `NO FINDINGS:` or `SKIPPED:` into `declared` and drop it from the body. Lines 68-69 return before the body is read as YAML, so the findings are never parsed. ReviewVerdictCommand.TryReadSeats (lines 234-237) adds the seat with no warning, and AccountForLenses records it as noFindings or skipped.
  failure_scenario: A seat writes a critical finding in a YAML block, then a closing `SKIPPED: re-run needed` or `NO FINDINGS` line for another sub-pass. The verdict counts the lens as covered with zero findings and can approve. The record shows nothing that says findings were present and ignored.
  guidance: [R6, §3]
  suggestion: Parse the body when `declared` is set. If it yields findings, throw a SeatOutputException ("contradicts itself") or report a KW-REVIEW-040 diagnostic naming the seat file, so the contradiction is visible.

- id: supportability/cited-line-check-skipped-silently
  lens: supportability
  severity: minor
  confidence: 6
  file: src/KyberWeave.Core/Review/CitedLineCheck.cs
  line: 43
  excerpt: |
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return CitedLineResult.Unchanged(finding);
    }
  claim: When the cited file cannot be read, or is outside the repository, is a symbolic link, or has an unsafe path, the check returns Unchanged. No diagnostic says the line was not verified, and a missing file (a FileNotFoundException, which is an IOException) takes the same silent path.
  evidence: Lines 30-34 and 41-44 return `CitedLineResult.Unchanged`. CheckCitedLines in ReviewVerdictCommand.cs (lines 465-467) handles Unchanged in the default branch by keeping the finding and emitting nothing, so skipped and verified findings look identical in the output.
  failure_scenario: `--path` points at the wrong root, or a seat cites `src/Foo.cs` that does not exist there. Every cited-line check is skipped without a trace. Findings with line numbers beyond the end of the file reach the verdict as if verified, and the reviewer has no signal that the repair step never ran.
  guidance: [R5, §3]
  suggestion: Add a CitedLineKind.Unverified result with a short reason (file not found, unreadable, outside the repository). Have CheckCitedLines report it once per file as an Info or Warning diagnostic naming the finding id and file, with no change to the finding.
```

Discovery: codegraph 0, docs 0, direct reads 8
REVIEW.md sections checked: [Ground rules, §3, R5, R6]
