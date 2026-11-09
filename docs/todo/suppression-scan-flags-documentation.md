---
id: todo/suppression-scan-flags-documentation
title: review suppressions flags documentation that quotes a pragma
doc-type: todo
status: draft
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-11-08
---

# review suppressions flags documentation that quotes a pragma

`kyber-weave review suppressions` reads the added lines of a change and fails a warning suppression that
states no reason. It does not filter by file kind: `SuppressionScan.ParseAddedLines` keys added lines by
path and every path is scanned, so a Markdown document that *shows* a pragma to explain the rule is read
as if the document had added one.

Found while closing the todo that wired the gate into the task gates: the closing note for that todo
quoted the pragma it is about, and the task gate failed the change with `KW-REVIEW-022` (caused by
the suppressions gate reporting `KW-REVIEW-036`) on
`docs/archive/todo/...:18`. The note was reworded to describe the pragma rather than quote it, which
makes this tree pass, but the next governed document that needs the literal will hit the same thing.

## Why it is not simply "filter to .cs"

A suppression is not only a C# pragma. `NoWarn` lives in a `.csproj`, `.props` or `.targets` file, and the
scan already covers those on purpose; narrowing `include` to source files would reopen the hole the gate
exists to close. The question is which non-code file kinds should be exempt, not whether `.cs` is the only
file that matters.

## What closing this needs

- Decide the exempt kinds. Markdown and other prose formats are the obvious candidates; a `.yml` workflow
  can legitimately set `NoWarn`, so the answer is not "everything that is not code".
- Are fenced code blocks inside Markdown still scanned? Exempting the whole file is simpler and closes the
  false positive; scanning fences keeps a real pragma in a copy-pasteable example covered.
- Pin whichever rule is chosen with a test on a Markdown change that quotes a pragma, and one on a
  `.csproj` that adds `NoWarn` with no reason, so the boundary cannot drift in either direction.
