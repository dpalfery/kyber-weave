```yaml
- id: correctness/fence-toggle-eats-nested-backticks
  lens: correctness
  severity: major
  confidence: 7
  file: src/KyberWeave.Core/Review/SeatOutputParser.cs
  line: 94
  excerpt: |
    foreach (string line in body)
    {
        if (line.TrimStart().StartsWith(Fence, StringComparison.Ordinal))
        {
            open = !open;
            continue;
        }
  claim: Every line that starts with three backticks after trimming flips the fence state, including lines inside a YAML block scalar such as an excerpt that quotes a markdown code fence.
  evidence: YamlText (lines 86-107) toggles `open` on any `TrimStart()`-ed line beginning with "```". It has no notion of the opening fence's length or info string, and it ignores indentation. The `fenced` test at line 88 also fires on an unfenced seat whose excerpt contains such a line. This repository's reviews are mostly markdown and skill prompts, so excerpts containing ```yaml or ``` are common.
  failure_scenario: |
    A seat wraps its findings in a fence. Finding 1's `excerpt: |` block quotes "    ```yaml". The nested line flips `open` to false and is dropped. Every later line, including the rest of finding 1 and all of findings 2..N, is excluded until the next fence line. The parser then sees a truncated list. It either throws "not valid YAML" or silently returns only the first findings, so a critical finding never reaches VerdictEngine and the verdict can be Approve. For an unfenced seat the same line makes `fenced` true with nothing inside, so the seat is rejected with "no findings list".
  guidance: [R1, R2]
  suggestion: Treat only fence lines at column 0 as delimiters, and close a fence only with a line of the same fence character and at least the opening length. Alternatively, locate the outermost fence pair first (first opener, last closer) and take everything between them.

- id: correctness/declared-line-discards-findings
  lens: correctness
  severity: major
  confidence: 6
  file: src/KyberWeave.Core/Review/SeatOutputParser.cs
  line: 68
  excerpt: |
    if (declared is { } kind)
        return new SeatOutput(lens, kind, [], attestation, indexFallback, discovery);
  claim: Any column-0 line starting "NO FINDINGS:" or "SKIPPED:" makes the parser return an empty findings list and never reads the YAML, even when the file also holds a findings list.
  evidence: Lines 59-62 set `declared` from the first such line anywhere in the file. Line 68 returns before ReadFindings runs. Nothing checks that `body` has no findings, so a contradictory file is treated as a clean one instead of being rejected as unreadable.
  failure_scenario: |
    A seat writes a findings list with a critical finding, then a closing line such as "SKIPPED: ..." or a quoted "NO FINDINGS: ..." at column 0. This can happen when a seat runs its lens in two passes, or when the lens text is echoed. The parse reports Kind=NoFindings with zero findings and no diagnostic. The critical finding is lost, lens coverage reads "noFindings", and the council can Approve.
  guidance: [R1, R2]
  suggestion: When `declared` is set and `body` still holds a non-blank findings structure, throw SeatOutputException for the contradictory seat. Alternatively, accept a declared outcome only when the body is blank or prose-only.

- id: correctness/multi-document-yaml-tail-dropped
  lens: correctness
  severity: minor
  confidence: 5
  file: src/KyberWeave.Core/Review/SeatOutputParser.cs
  line: 142
  excerpt: |
    if (stream.Documents[0].RootNode is not YamlSequenceNode sequence)
        throw new SeatOutputException($"The {seatLens} seat output is not a YAML list of findings.");
  claim: Only the first YAML document is read. Findings in any later `---`-separated document are silently ignored.
  evidence: `stream.Load` parses every document, but `ReadFindings` uses only `Documents[0]` (line 142). The remaining documents are neither read nor reported.
  failure_scenario: A seat that emits one `---`-separated document per finding, or a corrected second list after a first, has only its first list counted. Later critical findings never reach the engine, and nothing reports that they were skipped.
  guidance: [R1]
  suggestion: Throw SeatOutputException when `stream.Documents.Count > 1`, or concatenate the findings of all sequence documents.

- id: correctness/task-gate-stop-on-failure-breaks-on-timeout
  lens: correctness
  severity: minor
  confidence: 6
  file: src/KyberWeave.Core/Review/GateRunner.cs
  line: 170
  excerpt: |
    if (stopOnBlockingFailure && gate.Blocking && !result.Passed)
        break;
  claim: RunTaskGates still stops on a blocking gate that only timed out, while GateRunner.Run now excludes timed-out and deadline-cut gates from the stop (lines 81-86). This is inconsistent with KW-REVIEW-039, which says a timeout is not a failure.
  evidence: The `Run` loop adds `&& !VerdictEngine.IsStoppedForTime(result)` and `NotApplicableReason is null`. The `RunTaskGates` loop was not updated. Its `RunArgv` calls can return `GateOutcome.TimedOut`, and `Passed` is false for them (ExitCode -1).
  failure_scenario: With `--stop-on-failure`, a blocking task gate times out. Every remaining gate is skipped and absent from the report. The "failed" metric at ReviewGatesCommand.cs:357 excludes the timed-out gate, so the run exits 0. The caller sees a pass for a run that skipped the rest of the suite, with no NOT RUN diagnostic for the skipped gates.
  guidance: [R1, R2]
  suggestion: Apply the same `!VerdictEngine.IsStoppedForTime(result)` exclusion in RunTaskGates, or report the unstarted gates as not run.
```

Index fallback: none
Discovery: codegraph 1, docs 0, direct reads 9 (the lens file, plus SeatOutputParser, CitedLineCheck, GateRunner, ReviewVerdictCommand, VerdictEngine, ReviewGatesCommand, ReviewConfigLoader and ReviewModel; the one codegraph call returned trimmed source, so each file was read for the lines it elided)
REVIEW.md sections checked: [Ground rules, R1, R2, R3, R5, R6, R7, R13, R9, R12, R14]
