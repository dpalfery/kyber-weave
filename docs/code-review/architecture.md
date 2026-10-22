---
id: code-review/architecture
title: Review council architecture
doc-type: architecture
status: current
component: ReviewCouncil
source-root: src/KyberWeave.Core/Review
owner: dpalfery
last-reviewed: 2026-10-16
decided-by:
  - adr/0002-three-layer-review-council-verdict-engine
  - adr/0003-cross-file-duplication-and-prior-art-lenses
  - adr/0005-task-level-fast-review
code-refs:
  - VerdictEngine
  - GateRunner
  - PathGlob
  - ReviewConfig
  - ReviewConfigLoader
  - ReviewTaskGate
  - TaskGateSelection
---

# Review council architecture

Code review here is not one agent reading a diff. It is three layers with different failure
modes, deliberately kept apart:

| Layer | What it is | Who decides | Fails by |
|---|---|---|---|
| **Gates** | Repeatable commands — build, tests, coverage, analyzers | Nobody. They measure. | Not being run |
| **Council** | Specialist lenses reading the diff in parallel | A model, per lens, within its remit | Being confidently wrong |
| **Verdict** | Fixed rules over findings and gate results | Unit-tested code, no model | Being mis-specified |

The separation is the design. The judgement in a review is the council's; **the gate that
judgement passes through is not a model**. A verdict produced by the same model that produced
the findings cannot be audited, cannot be regression-tested, and can legitimately differ
between two runs over one diff. So the last step is arithmetic instead.

## The whole system

```mermaid
flowchart TD
    Cfg["review: section of .kyber-weave/kyber-weave.yml<br/>gates · coverage · policy · suppressions"]
    Gate["developer exit gate — task-gate skill<br/>review.task-gates · kyber-weave review gates --agent<br/>fix, then verify · deterministic, no model"]
    Caller["conductor"] --> Worker["dev worker"]
    Worker --> Gate
    Gate -->|"TASK_GATE: PASS · NONE_DECLARED · NONE_APPLICABLE"| TaskDone["task done to standard"]
    Gate -->|"FAIL or line missing · reject to the same worker"| Worker
    Gate -->|"2 reworks per task-gate key · circuit-breaker"| Findings["per-objective findings collection"]
    TaskDone --> Join["run join<br/>every task has cleared its gate<br/>findings collection drained"]
    Findings --> Drain["collection drain"]
    Drain -->|"residual findings"| Architect["architect — solution and plan"]
    Architect --> Approval{"plan approval"}
    Approval -->|"granted"| Worker
    Approval -->|"denied · stop"| PlanDenied["stop and ask — Draft plan not executable"]
    Drain -->|"collection empty"| Join
    Join -->|"end of run · the only automatic council run"| Scope
    HumanAsk["human asks for a review"] --> Scope

    subgraph CR["code-reviewer — orchestrator and adjudicator"]
        Scope["1. Scope<br/>diff · technologies · stated intent · touched paths"]
    end

    Scope --> GR
    Scope --> DG
    Scope --> Council

    subgraph Gates["LAYER 1 — Deterministic gates (no model)"]
        direction LR
        GR["kyber-weave review gates"] --> G1["build"] & G2["tests + coverage"] & G3["analyzers · scanners"]
        DG["kyber-weave review duplicates"] --> G4["CodeGraph body clusters"]
    end

    subgraph Council["LAYER 2 — The council (parallel, auto-skipping)"]
        direction LR
        RL["review-lens ×12<br/>model-profile: general"]
        RT["review-triage ×3<br/>model-profile: fast"]
    end

    Council -->|"findings"| Confirm["3. Adversarial confirm<br/>one refuter per major or critical finding"]
    Confirm --> Adjudicate
    Gates -->|"gates.json"| Adjudicate

    Adjudicate["4. Adjudication<br/>quote verified · duplicates reconciled<br/>claims checked against gate output"] --> VE

    subgraph VE["LAYER 3 — Verdict engine (unit-tested code)"]
        direction LR
        Drop["Drop findings:<br/>no evidence · low confidence · suppressed"] --> Rules["Apply rules 1-6<br/>in fixed order"]
    end

    VE --> Out["APPROVE · REQUEST_CHANGES · NEEDS_HUMAN<br/>+ risk grade + KW-REVIEW-* diagnostics"]

    Out -->|"NEEDS_HUMAN · stop"| Human["terminal human handoff"]
    Out -->|"APPROVE"| RunComplete["run complete"]
    Out -->|"REQUEST_CHANGES"| ObjRemed["remediation loop — workers then verifier re-review"]
    ObjRemed --> Worker
    ObjRemed -->|"three-cycle cap · terminal failure"| ObjFailed["terminal failure — stop and report"]
    Join --> ObjReview["objective-level code-reviewer<br/>once per objective"]
    ObjReview --> Scope

    Cfg -.-> Gates
    Cfg -.-> VE
```

Gates and council start **together** — they are independent and neither waits on the other.
Three lenses are the exception: `test-adequacy` consumes the coverage report,
`static-analysis-triage` consumes the analyzer output, and `duplicate-implementation` consumes
the duplicates report, so each is issued when its own gate completes rather than behind a
barrier on all gates.

`review duplicates` is a second command rather than a declared gate entry because its input is
not a host command at all: it reads the repository's CodeGraph index and clusters symbols whose
normalized bodies are identical. Hosts still declare it under `review.gates` so its execution
is recorded like any other evidence, and it exits 0 whether or not it finds anything — what
"blocking" buys there is that it must have *run*.

## The parts, and where they live

| Part | Kind | Location |
|---|---|---|
| `task-gate` | Canonical skill — the developer exit gate, ahead of the council | `products/kyber-squad/skills/task-gate/SKILL.md` |
| `code-reviewer` | Canonical agent — orchestrates, adjudicates, holds the skeptic | `products/kyber-squad/agents/code-reviewer.md` |
| `review-lens` | Canonical agent — one judgement seat, spawned N times | `products/kyber-squad/agents/review-lens.md` |
| `review-triage` | Canonical agent — one triage seat, `fast` model profile | `products/kyber-squad/agents/review-triage.md` |
| `code-review` | Skill — the procedure, the lens catalogue, the report format | `products/kyber-squad/skills/code-review/SKILL.md` |
| Lens files | 15 reference files, one per concern | `products/kyber-squad/skills/code-review/references/lenses/` |
| Technology checklists | 7 reference files, loaded as lens *modifiers*; each points at the host's `<technology>-coding-standard` and states no rule of its own | `products/kyber-squad/skills/code-review/references/` |
| `dp-code-reviewer` | Skill — modes and the re-review loop | `products/kyber-squad/skills/dp-code-reviewer/SKILL.md` |
| `security-review` | Skill — invoked by the security lens, not duplicated | `products/kyber-squad/skills/security-review/SKILL.md` |
| `GateRunner` | Runs declared gates, normalizes results | `src/KyberWeave.Core/Review/GateRunner.cs` |
| `DuplicateDetector` | Clusters symbols by normalized body, from the CodeGraph index | `src/KyberWeave.Core/Review/DuplicateDetector.cs` |
| `ICodeGraphSymbolEnumerator` | Optional CodeGraph port: every indexed symbol of one kind | `src/KyberWeave.Core/CodeGraph/ICodeGraphSymbolEnumerator.cs` |
| `VerdictEngine` | The rules. Pure — no clock, no filesystem, no process | `src/KyberWeave.Core/Review/VerdictEngine.cs` |
| `PathGlob` | Matches changed paths against reserved patterns | `src/KyberWeave.Core/Review/PathGlob.cs` |
| `ReviewConfig` | The `review:` host configuration | `src/KyberWeave.Core/Configuration/ReviewConfig.cs` |

## Why lenses are files, not agents

Fifteen concerns, but only **two** canonical agents carry them. The lens is a reference file;
the agent is the seat that reads it.

Fifteen agents would push the canonical roster from 22 to 37, clutter every harness's agent
picker, and produce fifteen near-identical descriptions that would collide under
`KW-AGENT-LINT-001` and `KW-SKILL-LINT-011`. As files, adding a lens is a Markdown file rather
than a deployment — which is exactly what adding `prior-art` cost.

The two seats exist because they do different work, not because one is cheaper:

```mermaid
flowchart LR
    subgraph J["review-lens — judgement · general"]
        direction TB
        J1["intent-alignment"]
        J2["correctness"]
        J3["security"]
        J4["authz-tenancy"]
        J5["di-composition"]
        J6["model-placement"]
        J7["test-adequacy"]
        J8["performance"]
        J9["blast-radius-revertibility"]
        J10["supportability"]
        J11["infra-workflow"]
        J12["prior-art"]
    end

    subgraph T["review-triage — attribution · fast"]
        direction TB
        T1["static-analysis-triage"]
        T2["dependency-supply-chain"]
        T3["duplicate-implementation"]
    end

    Input1["Reads source code<br/>and forms a judgement"] --> J
    Input2["Reads a machine artifact<br/>and attributes it to the change"] --> T
```

A triage seat's input is something a tool already produced. Its job — deciding which of that
output *this change* is responsible for — is bounded and checkable, which is what makes it
safe to run on the `fast` model profile. Twelve lenses are not that, and would produce worse
findings cheaply, which costs more than it saves.

`prior-art` and `duplicate-implementation` sit on opposite sides of that line while holding one
concern between them, and the split is by evidence rather than by topic. A body duplicated
verbatim is a fact a gate established, so attributing it is triage. A *type* that duplicates
another is a judgement about responsibility that needs both types read, so it is not.

**Every lens auto-skips.** Each lens file opens with an applicability predicate and returns
`SKIPPED: <reason>` when the diff holds nothing it owns. That is what makes fifteen lenses
affordable — and the skip is *recorded*, because a dimension that was deliberately skipped and
one that was quietly never looked at must not read the same in a report.

## The skeptic, as a schema

The reviewer's standing demand — *show me the logs or it didn't happen* — is enforced by data
shape rather than by tone. Every finding must carry:

```yaml
excerpt: |
  _client = new HttpClient();      # verbatim source — required
evidence: "src/Foo/Bar.cs:42"      # how the reporter knows — required
failure_scenario: "Socket exhaustion under load; no handler rotation."   # required
```

`VerdictEngine` drops any finding missing one of the three, under `KW-REVIEW-001`, and names
the lens that produced it — because a lens doing this repeatedly is the real defect. A finding
the engine would have to complete on the reporter's behalf is one the engine would be
inventing.

The same rule applies upward. Any claim that something was built, tested, or verified with no
corresponding entry in `gates.json` is itself a `critical` finding. The agent whose purpose is
refusing unverified claims is not permitted to make one.

## How a finding survives

```mermaid
flowchart TD
    F["Finding reported by a lens"] --> E{"excerpt + evidence<br/>+ failure_scenario?"}
    E -->|"missing any"| D1["DROPPED · KW-REVIEW-001"]
    E -->|"complete"| C{"confidence ≥ min-confidence?"}
    C -->|"below floor"| D2["DROPPED · KW-REVIEW-002"]
    C -->|"at or above"| S{"active suppression?"}
    S -->|"suppressed"| D3["DROPPED · KW-REVIEW-003"]
    S -->|"none"| R{"refuted by the<br/>adversarial pass?"}
    R -->|"refuted"| D4["DROPPED — never downgraded"]
    R -->|"survives"| A["ACCEPTED · counts toward the verdict"]
```

Suppressions carry a **mandatory expiry date**. There is no "never" value: a permanent
suppression is how a review system quietly stops reviewing, the reason ageing out of memory
while the exemption stays. On the day after it lapses the finding returns on its own, and
`KW-REVIEW-004` says why.

## How the verdict is computed

`VerdictEngine.Evaluate` is a pure function of (scope, findings, gates, config, date). Same
inputs, same verdict, every time. The order is the contract:

```mermaid
flowchart TD
    Start["Accepted findings + gate results"] --> R1{"1 · A changed path matches<br/>policy.always-human?"}
    R1 -->|"yes"| NH["NEEDS_HUMAN · exit 2<br/>KW-REVIEW-008"]
    R1 -->|"no"| R2{"2 · Diff exceeds<br/>max-reviewable-lines?"}
    R2 -->|"yes"| NH2["NEEDS_HUMAN · exit 2<br/>KW-REVIEW-009"]
    R2 -->|"no"| R3{"3 · A blocking gate failed?"}
    R3 -->|"yes"| RC["REQUEST_CHANGES · exit 1<br/>KW-REVIEW-005"]
    R3 -->|"no"| R4{"4 · Any critical finding?"}
    R4 -->|"yes"| RC2["REQUEST_CHANGES · exit 1<br/>KW-REVIEW-006"]
    R4 -->|"no"| R5{"5 · Majors ≥ major-count-blocks?"}
    R5 -->|"yes"| RC3["REQUEST_CHANGES · exit 1<br/>KW-REVIEW-007"]
    R5 -->|"no"| AP["APPROVE · exit 0"]
```

Three properties of that order are load-bearing:

**The escalation rules run first.** A reserved path and an unreviewable diff both say the same
thing — *this change is not the engine's to settle* — and that is true regardless of how clean
or how filthy the council's report was. Neither can be overridden by the engine; the policy
outranks it.

**`NEEDS_HUMAN` does not share an exit code with `REQUEST_CHANGES`.** Collapsing them would
make a change that merely touches a protected path indistinguishable, to any caller reading
the exit code, from one the review actually rejected.

**Risk is graded from findings, never from diff size.** A twelve-line migration dropping a
column outranks a three-thousand-line regeneration of a generated client. Size appears only as
an attention ceiling, and a large clean diff stays `LOW`.

Coverage is reported (`KW-REVIEW-010`) and **never decisive**. A verdict driven by a coverage
number rewards padding that number — which is precisely the defect the `test-adequacy` lens
exists to catch.

## Permissions

The council's shape is enforced by the capability lattice, not requested by prose. See
[Kyber-Squad architecture](../kyber-squad/architecture.md) for how profiles lower onto each
harness.

| Role | Profile | read | search | write | execute | delegate |
|---|---|---|---|---|---|---|
| `code-reviewer` | `reviewer` | allow | allow | **ask** | **allow** | **allow** |
| `review-lens` | `read-only` | allow | allow | deny | deny | deny |
| `review-triage` | `read-only` | allow | allow | deny | deny | deny |

`process.execute` is what makes the gate real; before it, the blocking pre-merge gate the
skill declared was unexecutable, and the one agent whose job is refusing unverified claims was
structurally forced to make one. It is exercised through a single declared entry point,
`kyber-weave review gates`.

`filesystem.write` stops at `ask` rather than following execute to `allow`. The reviewer writes
one artifact — its findings — and on harnesses that cannot express `ask` the lattice safely
narrows it to `deny` and the findings come back in the response instead. `network.publish`
stays `deny`. **The role that judges a change never ships it.**

`delegate` on the reviewer is what lets it fan out the council, and it is also what gives
`delegate: deny` on the other subagent profiles a meaning it previously lacked: delegation is a
per-role grant, not a property of being a subagent. The lens seats hold `deny`, so a council
seat cannot re-enter the council.

## Configuration

Everything host-specific is one section of `.kyber-weave/kyber-weave.yml`:

```yaml
review:
  gates:
    - id: test
      run: [dotnet, test, -c, Release]     # argv, never a command line
      blocking: true
  coverage:
    file-line-percent: 85
  policy:
    always-human: ["**/auth/**", "**/*secret*"]
    max-reviewable-lines: 10000
    major-count-blocks: 3
    min-confidence: 7
    suppressions:
      - id: correctness/generated-nullable
        reason: Regenerated client, tracked separately.
        expires: 2026-11-18                # required
```

`run` is **argv, not a command line**, because `ProcessRunner` refuses a shell and refuses a
concatenated argument string. A host cannot express a pipeline or a redirect here — and neither
can anything that later edits this file, nor any value arriving from a diff, a finding, or a
lens prompt. That is the injection surface closed at the point the value is written.

This repository reserves its own governance artifacts, which is the case worth copying:

```yaml
always-human:
  - "products/kyber-squad/profiles/capabilities.yml"
  - "products/kyber-squad/agents/**"
  - ".kyber-weave/kyber-weave.yml"
```

The artifacts that decide what agents may do are not approvable by an agent, and a change to
the review configuration escalates through the review configuration.

### Task gates

`review.task-gates` is the developer exit gate's counterpart to `review.gates`: a map from an
agent slug to that agent's ordered gates, run by `kyber-weave review gates --agent <slug>`
against the files that agent changed. It is optional; absent, no agent has task gates and
`--agent` reports `KW-REVIEW-027`.

```yaml
review:
  task-gates:
    csharp-dev:
      - id: format-whitespace
        include: ["**/*.cs", "**/*.csproj"]
        run: [dotnet, format, KyberWeave.sln, whitespace, --verify-no-changes, --include, "{changed}"]
        fix: [dotnet, format, KyberWeave.sln, whitespace, --include, "{changed}"]
      - id: dotnet-build
        include: ["**/*.cs", "**/*.csproj"]
        run: [dotnet, build, KyberWeave.sln, -c, Release, --artifacts-path, ".agent_scratchpad/{task}/artifacts"]
```

| Key | Required | Default | Meaning |
|---|---|---|---|
| `id` | yes | none | Lookup-name slug, unique within the agent |
| `include` | no | `[]`, which matches every changed file | Globs selecting the changed files the gate applies to |
| `os` | no | every platform | `windows` or `unix`; a gate on another platform is skipped (`KW-REVIEW-026`) |
| `run` | yes | none | The verifying command, argv |
| `fix` | no | none | The command `--fix` runs before `run`; when written it must not be empty |
| `blocking` | no | `true` | Whether a failure fails the run |
| `timeout` | no | `900` seconds | After this the runner kills the process tree; must be positive |

Both `run` and `fix` are argv for the same reason `review.gates` is. Two tokens expand, and only
in `TaskGateSelection`: `{changed}` must be an entire list element and becomes one argument per
changed file the gate's `include` matches; `{task}` is the `--task` slug and may sit inside an
argument. The program, the first element of `run` or `fix`, is always a literal: a token there is
refused, so a changed file or a task slug can never choose what executes. A gate whose `include`
matches no changed file is skipped, not passed. The loader refuses an unknown or embedded token,
a token in the program, an empty list, an empty argument, a duplicate gate id, a non-slug agent
or gate id, and an unrecognised `os`, at the point the operator wrote it. At run time a changed
path that is absolute, leaves the repository, begins with `-`, or passes through a symbolic link
whose target lies outside the repository, and a `--task` that is not a slug, refuse the whole run
(`KW-REVIEW-028`) before any process starts.

A YAML anchor is the way to share a gate between agents: define it on the first agent and alias
it under the others. This repository does so for the .NET format gates and the build.

## Modes

The two principles this area answers to are stated in the
[review index](README.md#principles); this section is how they are implemented.

**One reviewer, and a deterministic gate ahead of it.** `code-reviewer` reviews the whole run,
once, at the end — and one task only when a human explicitly asks. There is no condition under
which a task summons the council: not a failed gate, not a reserved path, not a concern that
looks serious. Each of those would be a per-task council bill, and avoiding that bill is the
entire point of the split.

**Ahead of the council sits a layer with no model in it at all.** Each developer agent ends its
task with the `task-gate` skill: it runs the `review.task-gates` entry for that agent (formatting,
analyzer fixes, `cleanupcode` scoped to the changed files, build and tests), fixes what a machine
can fix, and verifies. The worker reports a `TASK_GATE:` line in its digest. The conductor rejects
a missing or `FAIL` line back to the same worker; after two reworks for the same
`task-gate/<gate-id>` key the circuit-breaker trips and the failure enters the conductor's
per-objective findings collection, which `architect` plans against before the end-of-run review
runs. See [ADR 0005](../adr/0005-task-level-fast-review.md).

The council's own modes are owned by the `dp-code-reviewer` skill, which wraps the review
rather than performing it.

| Mode | Behaviour | When |
|---|---|---|
| `shadow` | Full review, verdict recorded, **gates nothing** | First rollout on a repository |
| `enabled` | The verdict gates | Normal operation |
| `verifier` | Re-checks only the prior findings | After a fix push |
| `full` | Forces a complete council re-scan | Escalation, or a stale prior review |

`verifier` is the cost lever: gates re-run every cycle because their result *is* the evidence,
but only lenses owning an outstanding finding re-run. The loop escalates after three cycles
without the surviving-finding count falling — a loop that is not converging is not going to,
and the usual cause needs a person.

## Current limitations

All twelve declared targets have a registered renderer. On the eleven native targets these permissions lower to their native representations and are enforced; `warp` is a fallback target whose deployed skills carry no capability enforcement, so there the council and the narrowing are instruction-only. On `devin` a subagent cannot delegate, so `code-reviewer` applies each lens itself, one at a time, runs the refutation pass itself, and says in its report that the council ran in-process, which is weaker evidence than independent seats — see
[rendering](../kyber-squad/architecture.md#8-rendering).

There is no cost measurement and no per-repository ceiling yet. The adversarial confirmation
pass in particular is unmeasured and may exceed the council itself on a findings-heavy diff.

## Related

- [Rule reference](../ci-pipelines/rule-reference.md) — every `KW-REVIEW-*` id
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — how agents and skills deploy
- [Agent harness governance](../context-hygiene/agents.md) — capability profiles and parity
- [The review council plan](../archive/plans/2026-08-20-code-review-council.md) — why it is shaped this way
- [ADR 0002](../adr/0002-three-layer-review-council-verdict-engine.md) — three-layer review council and verdict engine
- [ADR 0003](../adr/0003-cross-file-duplication-and-prior-art-lenses.md) — cross-file duplication and prior art

