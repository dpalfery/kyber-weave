---
name: code-review
description: "Use when reviewing written code — a diff, a branch, a pull request, or work an agent claims is finished. Fans out a parallel council of specialist review lenses over the change, runs the host's declared deterministic gates, and computes an auditable Approve / Request-changes / Needs-human verdict. Do NOT use for writing or fixing code, for authoring tests, or for a standalone vulnerability scan — invoke the security-review skill for that."
license: MIT
metadata:
  author: David R Palfery
  version: 4.0.0
---

# Code review

Review has three layers, and keeping them apart is the whole design.

| Layer | What it is | Who decides |
|---|---|---|
| **Gates** | Repeatable commands — build, tests, coverage, analyzers, scanners | Nobody. They measure. |
| **Council** | Specialist lenses reading the diff in parallel | A model, per lens, within its remit |
| **Verdict** | Fixed rules over the findings and gate results | Unit-tested code, no model |

The judgement in a review is the council's. The gate that judgement passes through is not.
A verdict produced by the same model that produced the findings cannot be audited, cannot be
regression-tested, and can legitimately differ between two runs over the same diff — so the
last step is arithmetic instead.

## Procedure

### 1. Scope the change

Establish the diff, the files it touches, the technologies present, and the stated intent —
the request, the commit or pull-request description, and any governing plan or specification
under the paths declared as **<plan-index>** and **<specification-index>** in the root
`AGENTS.md` registry.

Two things fall out of this and govern everything after it: **which lenses can possibly
apply**, and **whether the change is reviewable at all**. A diff past the host's
`review.policy.max-reviewable-lines` ceiling is not made safe by reviewing it harder; it
escalates.

### 2. Start the gates and the council together

They are independent. Neither waits on the other.

**Gates.** One command, and it is the only thing this skill executes:

```bash
kyber-weave review gates . --out gates.json
```

It runs what the host declared under `review.gates` in `.kyber-weave/kyber-weave.yml`, in
declaration order, and normalizes every result into one document. Never substitute an ad-hoc
command for a declared gate, and never skip a blocking gate because you expect it to pass —
the output is cited as evidence, and evidence you produced by hand is not evidence.

If the host has declared no gates, the command says so. Record that: a review with no
executed evidence is a weaker review, and the report must not read as though everything
passed.

**Council.** Invoke one seat per applicable lens, all in flight at once, each named with its
lens file and the review scope. Every lens declares an applicability predicate and returns
`SKIPPED` with a reason when the diff holds nothing it owns — auto-skipping is what makes
thirteen lenses affordable.

The **Runner** column is not advisory. `review-lens` reads code and judges it. `review-triage`
takes a machine artifact — analyzer diagnostics, a manifest diff — and attributes it to the
change; that work is bounded and checkable, so it runs on the `fast` model profile. Sending a
judgement lens to triage buys shallow findings; sending a triage lens to judgement pays a
premium for attribution.

| Lens | Runner | Owns |
|---|---|---|
| [intent-alignment](references/lenses/intent-alignment.md) | `review-lens` | Does the diff do what its description says, and is it one change? |
| [correctness](references/lenses/correctness.md) | `review-lens` | Boundaries, absent values, error paths, state, real concurrency |
| [security](references/lenses/security.md) | `review-lens` | Exploitable vulnerabilities, via the `security-review` skill |
| [authz-tenancy](references/lenses/authz-tenancy.md) | `review-lens` | Authorization per access, tenant scoping, privilege boundaries |
| [di-composition](references/lenses/di-composition.md) | `review-lens` | Constructor injection, no locally created collaborators, registration |
| [model-placement](references/lenses/model-placement.md) | `review-lens` | Type classification against the declared coding standard |
| [test-adequacy](references/lenses/test-adequacy.md) | `review-lens` | Would the tests catch this breaking? Behaviour over implementation |
| [static-analysis-triage](references/lenses/static-analysis-triage.md) | **`review-triage`** | Analyzer output, attributed and reported by rule id |
| [performance](references/lenses/performance.md) | `review-lens` | Cost at scale, with a named growth dimension |
| [blast-radius-revertibility](references/lenses/blast-radius-revertibility.md) | `review-lens` | What else it reaches, and whether it can be undone |
| [supportability](references/lenses/supportability.md) | `review-lens` | Diagnosability, correlation, what leaks to the caller |
| [dependency-supply-chain](references/lenses/dependency-supply-chain.md) | **`review-triage`** | What the change asks the project to newly trust |
| [infra-workflow](references/lenses/infra-workflow.md) | `review-lens` | Migrations, workflows, infrastructure, and their trust boundaries |

Only two lenses are triage, and that is the honest count rather than a hedge. The rest need a
judgement about code that a fast model would make badly. `static-analysis-triage` is
nonetheless the single best candidate in the catalogue: it applies to almost every change that
builds, and it carries the largest input of any lens, so it is both the most mechanical seat
and the most frequently expensive one.

Two lenses consume gate output — `test-adequacy` needs the coverage report and
`static-analysis-triage` needs the analyzer results. Issue those when their gate completes,
not behind a barrier on all gates.

**Technology checklists.** The per-technology references —
[C#](references/csharp.md), [Python](references/python.md), [React](references/react.md),
[SQL](references/sql.md), [Pulumi](references/pulumi.md), [Azure](references/azure.md),
[GitHub Actions](references/github-actions.md) — are lens modifiers, not lenses. A lens loads
the checklists for the technologies actually present in the diff.

### 3. Confirm before believing

For every `major` or `critical` finding, spend one more `review-lens` invocation trying to
**refute** it. Say so explicitly in the invocation: the confirming instance argues the
finding is wrong and defaults to refuted when it cannot establish otherwise.

This is the adversarial pass the `security-review` skill already applies to vulnerability
candidates, generalized to every lens and for the same reason — a confident wrong finding
costs more trust than a missed finding costs safety. A refuted finding is dropped, not
downgraded.

### 4. Adjudicate

The reviewer applies the checks no individual lens can, because no lens sees the others:

- **Drop anything missing `excerpt`, `evidence`, or `failure_scenario`.** No charitable
  reconstruction — a finding you complete on the reporter's behalf is one you are inventing.
- **Verify the quote.** Open the file and confirm the excerpt is really there. A finding
  quoting code that does not exist invalidates that lens's whole run, and the report says so.
- **Reconcile duplicates** — keep the most concrete, and note the corroboration.
- **Reconcile contradictions** — read the code and resolve, or escalate as unresolved.
- **Check the claims against the gates.** Any assertion that something was built, tested, or
  verified, with no corresponding gate result, is a `critical` finding.

### 5. Compute the verdict

```bash
kyber-weave review verdict . --findings findings.json --gates gates.json
```

Rules, in evaluation order. The first that fires decides:

| # | Rule | Verdict |
|---|---|---|
| 1 | A changed path matches `review.policy.always-human` | `NEEDS_HUMAN` |
| 2 | The diff exceeds `max-reviewable-lines` | `NEEDS_HUMAN` |
| 3 | A blocking gate failed | `REQUEST_CHANGES` |
| 4 | Any surviving `critical` finding | `REQUEST_CHANGES` |
| 5 | Surviving `major` findings reach `major-count-blocks` | `REQUEST_CHANGES` |
| 6 | Otherwise | `APPROVE` |

The two escalation rules run **before** any finding is weighed, because a reserved path and
an unreviewable diff both say the same thing: this change is not the engine's to settle,
however clean or filthy the council's report was.

**Risk** (`LOW` / `MEDIUM` / `HIGH`) is graded from what was found and what was touched,
never from diff size. A twelve-line migration dropping a column outranks a three-thousand-line
regeneration of a generated client.

**Coverage is reported, never decisive.** A verdict driven by a coverage number rewards
padding the number, which is exactly what the `test-adequacy` lens is there to catch.

Report the verdict the engine returned. You may not override it. If you believe it is wrong,
say so alongside it with the finding that should have changed it.

## Report format

### Verdict
- **Verdict:** `APPROVE` / `REQUEST_CHANGES` / `NEEDS_HUMAN`
- **Risk:** `LOW` / `MEDIUM` / `HIGH`
- **Rule:** the `KW-REVIEW-*` rule that decided it, and why it fired.

### Gates
One line per gate: id, pass or fail, duration, and for a failure the exact command and the
failing output. If no gates were declared or run, say that here in one line rather than
omitting the section.

### Findings
Most severe first:

- **[Severity] [id] — Title**
  - **Location:** `path/to/file.ext:line`
  - **Claim:** what is wrong.
  - **Evidence:** how it is known — a path and line read, or a gate result id.
  - **Failure:** the concrete scenario.
  - **Suggestion:** the specific fix.

### Council coverage
Every lens, and its outcome: findings, `NO FINDINGS`, or `SKIPPED` with the reason. This
section is not optional. It is the difference between a dimension that was reviewed and one
that was quietly never looked at.

### Dropped
What was reported and removed, with the `KW-REVIEW-*` rule that removed it. A review that
hides its own false positives cannot be tuned.

## Configuration

Everything host-specific lives in one place — `review:` in `.kyber-weave/kyber-weave.yml`:

```yaml
review:
  gates:
    - id: test
      run: [dotnet, test, -c, Release]     # argv, never a command line
      blocking: true
    - id: inspectcode                      # .NET static analysis; see the resharper-clt skill
      run: [dotnet, jb, inspectcode, MySolution.sln, --output=.scratch/inspectcode.xml, --format=Xml]
      blocking: true
  coverage:
    file-line-percent: 85
    class-line-percent: 85
  policy:
    always-human: ["**/auth/**", "**/*secret*"]
    max-reviewable-lines: 10000
    major-count-blocks: 3
    min-confidence: 7
    suppressions:
      - id: correctness/generated-nullable
        reason: Regenerated client, tracked separately.
        expires: 2026-11-18                # required; re-justify or it returns
```

`run` is argv because the gate runner refuses a shell. A pipeline or a redirect cannot be
expressed here, and neither can an injection — that is the trade, and it is the right way
round.
