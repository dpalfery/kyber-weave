# First full review

Read this in a review's first cycle, which the council's gate run prints as `cycle: 1 mode: full` and `next: read references/first-review.md`, and in any review run outside the council (the pre-push gate's council included). It is step 2's council half: who is seated, with what, and the lens catalogue. The seat-file rules, the refutation pass, adjudication and the verdict stay in `SKILL.md`, because every cycle uses them.

## Council fan-out

**Council.** The seats start from `review seats`, before the gates finish: it writes the cycle's `evidence.md` (the diff, the changed files with each one's diff line range) and one packet per seat holding its lens charter, so read-only seats inspect the change via `view_file` without running git or a gate. The gate run then rewrites `evidence.md` with every gate's result. A review outside the council writes no `evidence.md`; its packets name the diff and the gate report instead.
Invoke one seat per applicable lens: the `now:` seats in one batch with the gate run, the
`after-gates:` seats when it finishes. Each packet names the evidence and the review-guidance file. Every lens declares an
applicability predicate and returns `SKIPPED` with a reason when the diff holds nothing it
owns — auto-skipping is what makes seventeen lenses affordable. Every seat given guidance sections
ends its output with one attestation line, `REVIEW.md sections checked: [<ids>]`, on both its
findings form and its `NO FINDINGS` form (a `review-triage` seat lists `Ground rules`). A
findings or `NO FINDINGS` seat without that line has not shown it applied the guidance; the
report records that as a coverage gap under Council coverage. `SKIPPED` seats return no
attestation, and a missing attestation is never a verdict input. The reviewer dispatches every seat
itself, whoever dispatched it, and never opens a lens charter: each packet carries its own. Where sub-agents cannot dispatch, the `review-council` skill starts the seats from the main session instead. A reviewer applies a lens itself only after one dispatch attempt returned a tool-not-found or permission-denied error, which it quotes in its report; a claim of unavailability with no quoted failed call is not allowed. Then it applies the lenses one at a time, with the same guidance sections and the same attestation line.

## The lens catalogue

The **Runner** column is not advisory. `review-lens` reads code and judges it. `review-triage`
takes a machine artifact — analyzer diagnostics, a manifest diff — and attributes it to the
change; that work is bounded and checkable, so it runs on the `fast` model profile. Sending a
judgement lens to triage buys shallow findings; sending a triage lens to judgement pays a
premium for attribution.

| Lens | Runner | Owns |
|---|---|---|
| [intent-alignment](lenses/intent-alignment.md) | `review-lens` | Does the diff do what its description says, and is it one change? |
| [correctness](lenses/correctness.md) | `review-lens` | Boundaries, absent values, error paths, state, real concurrency |
| [security](lenses/security.md) | `review-lens` | Exploitable vulnerabilities, via the `security-review` skill |
| [authz-tenancy](lenses/authz-tenancy.md) | `review-lens` | Authorization per access, tenant scoping, privilege boundaries |
| [di-composition](lenses/di-composition.md) | `review-lens` | Constructor injection, no locally created collaborators, registration |
| [model-placement](lenses/model-placement.md) | `review-lens` | Type classification against the declared coding standard |
| [prior-art](lenses/prior-art.md) | `review-lens` | Does this already exist? Duplicate types, divergent idioms, generality nothing asked for |
| [duplicate-implementation](lenses/duplicate-implementation.md) | **`review-triage`** | Bodies duplicated across the tree, attributed from the duplicates gate |
| [test-adequacy](lenses/test-adequacy.md) | `review-lens` | Would the tests catch this breaking? Behaviour over implementation |
| [static-analysis-triage](lenses/static-analysis-triage.md) | **`review-triage`** | Analyzer output, attributed and reported by rule id |
| [test-infrastructure](lenses/test-infrastructure.md) | `review-lens` | Is the shared test machinery sound: isolation, caches, fixtures, global state? |
| [documentation-accuracy](lenses/documentation-accuracy.md) | `review-lens` | Is every factual claim in the changed docs, plans and doc comments true of the code? |
| [performance](lenses/performance.md) | `review-lens` | Cost at scale, with a named growth dimension |
| [blast-radius-revertibility](lenses/blast-radius-revertibility.md) | `review-lens` | What else it reaches, and whether it can be undone |
| [supportability](lenses/supportability.md) | `review-lens` | Diagnosability, correlation, what leaks to the caller |
| [dependency-supply-chain](lenses/dependency-supply-chain.md) | **`review-triage`** | What the change asks the project to newly trust |
| [infra-workflow](lenses/infra-workflow.md) | `review-lens` | Migrations, workflows, infrastructure, and their trust boundaries |

Only three lenses are triage, and that is the honest count rather than a hedge. The rest need
a judgement about code that a fast model would make badly. `static-analysis-triage` is
nonetheless the single best candidate in the catalogue: it applies to almost every change that
builds, and it carries the largest input of any lens, so it is both the most mechanical seat
and the most frequently expensive one.

Three lenses consume gate output — `test-adequacy` needs the coverage report,
`static-analysis-triage` needs the analyzer results, and `duplicate-implementation` needs the
duplicates report. When the host has declared a `review.coverage` floor and the gate report
carries no coverage evidence, `test-adequacy` treats that absence as a finding — not a skip,
and not "we did not look".

**Who waits for the gates.** `test-adequacy` (coverage), `static-analysis-triage` (analyzer output)
and `duplicate-implementation` (the duplicates report) start when the gate run finishes. Every other
lens reads only the diff and the changed files, and starts at once.

**No seat for what a gate already proves.** A lens whose whole output would restate a recorded
gate result is not seated; the gate run prints the reason for `--not-seated`:

- `static-analysis-triage` when every gate that ran passed: `gates <ids> passed`. It is seated
  when one failed or did not run.
- `duplicate-implementation` when the duplicates report names no file in the changed-file list:
  `no duplicate cluster names a changed file`.

**No seat for a file pattern the lens rules out.** Where a lens's Applicability names the kinds of
file it needs, `review seats` checks the changed-file list and prints `not-seated:` when none
matches: `dependency-supply-chain` (a manifest, lock file, container image or build input),
`infra-workflow` (infrastructure, workflows, migrations, cloud resource configuration),
`test-infrastructure` (a test project or test directory), and `correctness` and `test-adequacy` on
a documentation-only change. The patterns are wide on purpose; every other applicability question
stays with the seat. The size or risk of the change never shrinks the council.

`test-adequacy` is always seated when the change has code or tests: whether the tests would
catch the change breaking is a judgement no gate makes.

`prior-art` and `duplicate-implementation` divide one concern by unit and by evidence.
`duplicate-implementation` reports **bodies**, from a gate artifact, and may not report what
it found by reading. `prior-art` reports **types**, from reading, and may not report without
quoting the counterpart it found. Neither may report the other's unit.

**Technology checklists.** The per-technology references —
[C#](csharp.md), [Python](python.md), [React](react.md),
[SQL](sql.md), [Pulumi](pulumi.md), [Azure](azure.md),
[GitHub Actions](github-actions.md), [TypeScript](typescript.md),
[Rust](rust.md), [Tauri](tauri.md), [Go](go.md) — are lens
modifiers, not lenses. A lens loads
the checklists for the technologies actually present in the diff. Each checklist points at the
host's declared `<technology>-coding-standard` and states no rule of its own; an undeclared or
draft standard is reported, never replaced with a built-in checklist. A checklist's Review traps
section, where it has one, is a set of non-authoritative detection prompts: they are not rules,
they do not substitute for the declared standard, and a finding raised from one needs quoted
evidence like any other.
