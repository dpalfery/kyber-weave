---
id: todo/review-gates-paths-keeps-last-value
title: A comma-separated --paths value gates only its last element, so the evidence is silently void
doc-type: todo
component: ReviewCouncil
status: draft
owner: dpalfery
last-reviewed: 2026-11-07
---

# A comma-separated --paths value gates only its last element, so the evidence is silently void

`kyber-weave review gates --paths <PATH>` takes one changed file per occurrence, and the option
repeats. Passing one value that holds several paths separated by commas does not add them all:
only the **last** element survives into the changed set the gates are selected against.

The failure that makes this worth fixing is quiet rather than loud. A run whose last element is a
file no gate's `include` matches selects no gates at all, and a report of zero gates reads as a
clean pass. It is not one: the caller believed it gated several files and gated none of them, so
the gate evidence a review or a task gate then cites does not exist.

`--paths` arrives as `string[] Paths` on `ReviewGatesSettings`
(`src/KyberWeave.Cli/Commands/Review/ReviewSettings.cs`), bound by the CLI framework, and that is
where the documented "repeat for several" contract and the comma form diverge. Splitting a value
on commas to honour both spellings, or refusing a value that contains one so the caller sees the
mistake, both close it; leaving it silent does not.

**Work.** Decide which of the two — accept a comma-separated value as several paths, or refuse one
with a client-input error that names the repeated-flag form — and pin the behaviour where the
option is bound. A path that a gate's `include` then excludes is already a reported skip, so the
zero-gates report stops arising from a value the caller thought was several paths.

**Acceptance.** A run given several paths in one `--paths` value either gates every one of them or
is refused before any gate starts, and a test pins whichever it is. A run that gates nothing
because of a `--paths` value no longer reports zero gates without saying so.
