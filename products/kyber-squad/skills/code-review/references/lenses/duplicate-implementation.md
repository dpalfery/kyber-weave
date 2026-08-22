# Lens: duplicate-implementation

**Runner: `review-triage`.** This is a triage lens — its input is the duplicates gate's
report, and the work is attributing that report to the change rather than judging code. It
runs on the fast model profile. If you are `review-lens`, you were misrouted; say so rather
than searching the tree for duplicates yourself.

## Applicability

Applies when the duplicates gate produced a report **and** the diff touches at least one file
named in at least one cluster. Runs **after** that gate.

Skip, and say which:

```
SKIPPED: duplicate-implementation had no duplicates report to attribute.
SKIPPED: the repository has no CodeGraph index, so duplicate detection did not run.
SKIPPED: no duplicate cluster names a file this diff touches.
```

These are three different statements and the reviewer treats them differently. The middle one
matters most: indexing is the repository owner's decision, and a review that read an absent
check as a passed one would be lying by omission.

## What this lens owns

Attributing duplicate clusters to this change.

The gate found symbols whose normalized bodies are byte-identical. That is a fact, and you do
not re-derive it, re-measure it, or argue about the threshold. Your question is narrower and
it is the only one a model is needed for: **did this change add one of these copies?**

- A cluster whose members are **all** pre-existing is not this change's finding. The
  duplication was there before, and reporting it makes the author responsible for a codebase
  they inherited.
- A cluster where the diff **added** a member is the finding. The change introduced a second
  copy of code that already existed.
- A cluster where the diff **modified** one member of an existing cluster is also the finding,
  and a sharper one: the copies have started to diverge, which is the failure the duplication
  was always going to produce.

## Reading the report

The gate writes a `kyber-weave.review-duplicates/v1` document. Each cluster carries an `id`,
the `normalizedLines` count, and every `member` with its `name`, `file`, `startLine`, and
`endLine`. Cross the member list against the diff to decide attribution.

Two fields in the report header are yours to act on, not just to read:

- **`indexAvailable: false`** — skip with the second reason above. Do not fall back to
  searching for duplicates by hand; that is a different lens's job and a different evidence
  standard.
- **`symbolsUnreadable` above zero** — the index names files or spans the working tree no
  longer has, so it is stale. Say so in every finding's `evidence`, and drop your `confidence`
  accordingly. A cluster computed from a stale index may quote a body that has already
  changed, and the adjudicating reviewer's quote check will catch it as an invalidated run.

## Reporting

`id: duplicate-implementation/<method-name>`, one finding per attributed cluster.

- **`excerpt`** — the body the diff added, quoted from the file at the member's line range.
- **`evidence`** — the gate, the cluster id, and the path and line of **every** other member.
  Naming the other copies is the whole of your authority here; a finding that says "this is
  duplicated" without saying *of what* is not actionable.
- **`failure_scenario`** — the second edit site, concretely. Not "this violates DRY":

  > A change to the enum canonicalization at `SquadStateStore.cs:621` must also be made at
  > `SquadTransaction.cs:2496`. Nothing links them, so a fix applied to one leaves the other
  > accepting the value the fix rejected.

- **`suggestion`** — that the two be reconciled. Which shared home they should move to is the
  author's call, not yours.

**Severity.** `minor` by default. `major` where the duplicated body encodes a **rule** —
validation, authorization, tenant scoping, parsing, a serialization or wire contract — because
those are the bodies whose copies drift into disagreeing, and disagreement there is a defect
rather than a maintenance cost. Say which rule, in the claim.

**Confidence** is normally high: a deterministic gate established the match. Where it is not,
the reason is almost always attribution — you could not establish which member the diff added,
or the report flagged a stale index. Score that honestly and name it.

## What this lens must not report

- Clusters with no member added or modified by this change.
- Duplicates you found by reading rather than from the gate report. Your authority comes from
  citing the tool; a match you spotted by eye belongs to `prior-art`, which holds a different
  evidence standard for exactly that reason.
- Test doubles, fixtures, fakes, builders, and arrange helpers. Tests duplicate setup on
  purpose, and a shared helper in a test suite is often the worse design. The gate reports
  them because the gate reports facts; you filter them because the reviewer does not need them.
- Generated code, and bodies whose shape a framework or an external schema dictates.
- Disagreement with the threshold. The repository configured `review.duplicates.minimum-lines`;
  that decision is not under review here, and a finding that relitigates it wastes attention.
- Which abstraction should be extracted. The finding is that one body exists twice.
