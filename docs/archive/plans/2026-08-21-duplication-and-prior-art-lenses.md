---
id: archive/plans/2026-08-21-duplication-and-prior-art-lenses
title: Duplication lenses — prior art, duplicate implementations, and the redundancy tier the gate was dropping
doc-type: plan
status: archived
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-08-22
---

# Duplication lenses

**Status:** Archived  
**Archive Date:** 2026-08-22  
**Date:** 2026-08-21 (revised 2026-08-22 against what shipped)  
**Outcome / Closeout:** Completed. Phases 0, 1, 2, and 4 implemented (InspectCode redundancy promotions, prior-art and duplicate-implementation review lenses, and `review duplicates` gate command). Phase 3 deferred by precondition. Decisions harvested into [ADR 0003](../../adr/0003-cross-file-duplication-and-prior-art-lenses.md).  
**Goal:** Give the review council the three concerns it does not currently hold — code that
duplicates code, types that duplicate types, and generality nothing asked for. One of the
three is a settings change and is already done. The other two are new lenses: `prior-art`,
which asks whether the repository already contains the thing this diff is adding, and
`duplicate-implementation`, which asks the CodeGraph index whether this diff's method bodies
already exist somewhere else.

---

## 1. Problem

### 1.1 — No seat owns it

Thirteen lenses, and the catalogue has no entry for simplicity, YAGNI, or duplication.
What exists is adjacent, and each piece stops short in a way that is worth stating precisely:

| Lens | What it does cover | Where it stops |
|---|---|---|
| `static-analysis-triage` | ReSharper's redundancy families, by rule id | Whatever the gate's severity filter lets through — see §1.2 |
| `dependency-supply-chain` | "a dependency that duplicates something already present in the tree" | Packages, not code |
| `intent-alignment` | "Implemented but not described", "More than one change" | Correspondence to the description; explicitly not *"whether the change is a good idea"* |
| `model-placement` | Type classification against the declared standard | Silent where the standard is silent — a needless abstraction, correctly placed, passes |
| `blast-radius-revertibility` | Reach and revertibility | Bans "Speculative future consumers" outright |

The per-technology checklists carry a DRY line for
[Python](../../products/kyber-squad/skills/code-review/references/python.md) and a
duplicate-package line for
[React](../../products/kyber-squad/skills/code-review/references/react.md). C# has neither,
because [csharp.md](../../products/kyber-squad/skills/code-review/references/csharp.md)
delegates the whole mechanical half to the analyzer: *"Do not re-derive by eye what the tool
already reported by rule id."* That delegation is correct in principle and was broken in
practice — §1.2.

Pushing the other way, [`code-reviewer.md`](../../products/kyber-squad/agents/code-reviewer.md)
§89 makes *"a simplified implementation standing in for the real one"* a finding. The council
guards against under-building. Nothing guards against over-building.

### 1.2 — The gate was filtering out the tier it delegated to

`review.gates.inspectcode` in [`.kyber-weave/kyber-weave.yml`](../../.kyber-weave/kyber-weave.yml)
runs `--severity=WARNING`. ReSharper files most of `Redundancies in Symbol Declarations` and
the solution-wide dead-code rules at `SUGGESTION`. Measured on this repository, 2026-08-21:

- Gate output at `--severity=WARNING`: **35 diagnostics, exactly one from a redundancy rule**
  (`OutParameterValueIsAlwaysDiscarded.Local`).
- The same solution at the default `SUGGESTION` tier: **1280 diagnostics**, including 43
  `UnusedMember.Global` and 39 `ClassNeverInstantiated.Global`.

So `csharp.md` told the reviewer the analyzer owns redundancy, and the gate dropped the
analyzer's redundancy output before any lens could read it. Fixed in Phase 0.

### 1.3 — No clone detector exists to delegate to

JetBrains retired `dupfinder`. Verified against the pinned toolchain — JetBrains Inspect Code
2026.2.1 — `dotnet jb` exposes `inspectcode` and `cleanupcode` and nothing else. The nearest
inspections InspectCode still ships are `DuplicatedStatements` (already `WARNING`, and scoped
to branches of one statement) and `RedundantOverload.*` (scoped to overloads of one method).
Neither sees across files. There is no tool in this repository's chain that answers "does this
method body already exist somewhere else", and no lens can be told to delegate to one.

### 1.4 — Cross-file duplication is unreachable by construction

Every lens reads the diff. Only `dependency-supply-chain` is instructed to check what the
project already has before judging, and only for packages.
[`review-lens.md`](../../products/kyber-squad/agents/review-lens.md) §2 says to read
surrounding code — the file, the caller, the base class — which is the *local* neighbourhood.
A new helper that reimplements something three directories away passes all thirteen seats
cleanly, and will keep doing so however carefully each seat reads.

### 1.5 — The evidence rules would drop a naive finding anyway

`excerpt`, `evidence`, and `failure_scenario` are mandatory, and the adjudicating reviewer
drops any finding missing one. "This is more than we need" has no failure scenario. A lens
that reports taste produces findings the adjudicator deletes, which is worse than no lens:
it spends a seat and returns nothing. Any design here has to answer what the failure scenario
*is* before it earns a seat. §3 D3 answers it.

---

## 2. Phase 0 — already landed (2026-08-21)

[`KyberWeave.sln.DotSettings`](../../KyberWeave.sln.DotSettings) promotes 29 inspections from
`SUGGESTION` to `WARNING`, in four groups:

| Group | Rules |
|---|---|
| Duplicate implementation | `RedundantOverload.Global`, `RedundantOverload.Local` |
| Speculative generality | `ClassWithVirtualMembersNeverInherited.{Global,Local}`, `VirtualMemberNeverOverridden.{Global,Local}` |
| Dead code at solution scope | `UnusedMember.Global`, `UnusedMemberHierarchy.Global`, `UnusedMemberInSuper.Global`, `UnusedMethodReturnValue.Global`, `UnusedParameter.Global`, `UnusedType.Global`, `OutParameterValueIsAlwaysDiscarded.Global` |
| Redundant expressions left at suggestion by default | `DoubleNegationOperator`, `OverriddenWithSameValue`, `RedundantAlwaysMatchSubpattern`, `RedundantAttributeUsageProperty`, `RedundantConfigureAwait`, `RedundantDiscardDesignation`, `RedundantExplicitParamsArrayCreation`, `RedundantImmediateDelegateInvocation`, `RedundantPropertyPatternClause`, `RedundantRangeBound`, `RedundantStringInterpolation`, `RedundantStringType`, `RedundantTypeArgumentsInsideNameof`, `RedundantTypeDeclarationBody`, `RedundantVerbatimPrefix`, `RedundantVerbatimStringPrefix` |

**Measured effect on the gate: 35 → 83 diagnostics.** The entire increase comes from three
rules — `UnusedMember.Global` (43), `UnusedParameter.Global` (3), `UnusedMemberInSuper.Global`
(2). Every other promotion cost **zero** on the current tree: they are latent, and will fire
only on code written after this change, which is exactly the posture wanted from a gate.

**`ClassNeverInstantiated.{Global,Local}` was promoted, measured, and backed out.** All 39 hits
were Spectre.Console `Command<TSettings>` and `CommandSettings` types constructed by the type
registrar through reflection — the same false-positive class this file already suppresses via
`UnusedAutoPropertyAccessor.Global` and `CollectionNeverUpdated.Global`. Promoting it would
have added two permanent false positives to every new CLI command.

**What was already `WARNING` and needed nothing.** The constructor and default-value
redundancies were never the gap: `EmptyConstructor`, `RedundantBaseConstructorCall`,
`RedundantDefaultMemberInitializer`, `RedundantOverriddenMember`, `RedundantArgumentDefaultValue`,
`MeaninglessDefaultParameterValue`, and `DuplicatedStatements` all ship at `WARNING` and were
already flowing through the gate into `static-analysis-triage`.

**The 48 newly surfaced diagnostics were a backlog, not a regression, and Phase 4 cleared it.** The InspectCode gate
exits 0 regardless of findings — `blocking: true` there means *must have run* — and
`static-analysis-triage` reports only diagnostics on lines the diff touched. Pre-existing
hits do not flood any review. They did need clearing, and §7 Phase 4 records how. The
final split was 13 framework-constructed constructors (nine of them the exception surface
`CA1032` compels, four Spectre command constructors) and 31 genuine dead members across the
test fakes and `KyberWeave.Core`.

---

## 3. Decisions

**D1 — Two lenses, not one.** "Does this class already exist" and "does this method body
already exist" have different inputs, different failure modes, and different runners. A class
that duplicates another class is a judgement about responsibility that needs both types read.
A method body that duplicates another method body is a string comparison. Merging them would
put the mechanical half on a judgement model and the judgement half on a mechanical seat, which
is precisely the misrouting [`architecture.md`](../code-review/architecture.md) warns about.

**D2 — `duplicate-implementation` gets a gate, not a search prompt.** CodeGraph is a symbol and
call-graph index. It answers "where is X" and "who calls X"; it has no similarity query, and
`codegraph --help` on the installed build exposes no duplicate or clone command. A lens told to
"look around with CodeGraph for something similar" would have unbounded recall and no
reproducible evidence. Instead a new gate computes duplicate clusters deterministically from
the index and emits an artifact, and the lens attributes that artifact to the diff — the same
shape as `inspectcode` → `static-analysis-triage`.

**D3 — The failure scenario is the second edit site.** This is what makes these findings
survivable under the adjudication rules. A duplication finding's `failure_scenario` is never
"this is not DRY". It is: *"a change to the validation rule at `A.cs:40` must also be made at
`B.cs:120`; nothing links them, and the tests at `X` cover only the first."* That is concrete,
checkable, and states what goes wrong. A finding that cannot be written in that form is not
reportable, and both lens files will say so.

**D4 — `duplicate-implementation` runs on `review-triage`; `prior-art` runs on `review-lens`.**
Triage is a role, not a discount (D8 of the [council plan](2026-08-20-code-review-council.md)).
Exact normalized-body equality is a fact the gate established, so attributing it is bounded and
checkable work. If Phase 3 lowers the threshold to *near*-duplicate, the judgement content rises
and the lens re-homes to `review-lens`; that re-homing is part of Phase 3, not an afterthought.

**D5 — Exact matches first, near matches later.** Phase 2 ships exact normalized-body equality
only. Exact matching has a false-positive rate of zero by construction; a similarity threshold
has one that must be tuned against a corpus, and a gate that cries wolf stops being read.

*Revised 2026-08-22 against the shipped detector.* The scouting prototype reported 12 clusters,
but it counted braces and the signature toward its threshold and stripped trailing comments
with a regex. The shipped normalizer drops brace-only lines and the signature, and deliberately
does **not** strip trailing comments (§5.1), so its line counts are stricter and its matching is
more conservative. Measured on this repository through the real gate:

| `minimum-lines` | Bodies compared | Clusters |
|---|---|---|
| 3 | 1990 | 9 |
| **4 (default)** | **1846** | **6** |
| 5 | 1661 | 2 |

Every cluster at every threshold is a true duplicate — the tuning question is recall, not
precision. Four is the shipped default because three-statement bodies start to coincide in a
larger tree while four rarely do. The findings include `RequireCanonicalJsonEnum` duplicated
verbatim between `SquadStateStore.cs:621` and `SquadTransaction.cs:2496`, `AddNode` between
`ManagedGlossaryGraphContributor.cs:187` and `DocGraphProjection.cs:330`, and the constructors
of `SquadInstallCommand` and `SquadUpdateCommand`.

**D6 — `prior-art` is scoped to types the diff *adds*.** Not types it touches. The question is
"should this new thing exist", which is only askable about a new thing. Scoping it this way also
makes it skip most diffs, which is what keeps a fifteen-seat council affordable.

**D7 — The CodeGraph index is an input, and its absence is a skip, not a failure.**
[`AGENTS.md`](../../AGENTS.md) already states the rule: no `.codegraph/` directory means skip
CodeGraph entirely, because indexing is the user's decision. The gate reports "no index" and
the lens skips with that reason. A review that silently omitted duplicate detection would read
as one that checked and found nothing.

---

## 4. Lens A — `prior-art`

**Runner:** `review-lens`. **File:**
`products/kyber-squad/skills/code-review/references/lenses/prior-art.md`

**Applicability.** Applies when the diff adds a type — a class, record, interface, or a service
registration. Skips when the diff adds no types, and says so.

**What it owns.** Whether the repository already contains something that does this job, and if
it does, whether the diff duplicates it or diverges from it. Two distinct findings:

- **Duplication** — the new type re-implements an existing one. Report the existing type by path
  and line, and state the second edit site (D3).
- **Divergence** — the repository already solves this class of problem, and the new type solves
  it a different way. Two idioms for one job is a finding about the *codebase's* coherence, and
  it is reportable only when the existing idiom can be named and quoted.

**What to look for.**

- A new service whose collaborators, method shapes, and registration match an existing service.
- A new validator, loader, renderer, or store alongside a family of them that already share a
  base type or interface the new one does not implement.
- A helper that reimplements something in `KyberWeave.Core` that the diff's own project already
  references.
- Speculative generality: an interface with one implementation and no second caller, an abstract
  base introduced with a single derived type, a generic parameter used at one instantiation.
  This is the YAGNI half, and it is reportable only with D3's failure scenario — the maintenance
  site the indirection creates — never as an opinion about abstraction.

**What it must not report.**

- Preference between two acceptable idioms that share no behaviour.
- A second implementation whose reason is stated in the change description. Deliberate divergence
  is a decision; silent divergence is a finding.
- Pre-existing duplication in files the diff merely touches.
- Test doubles, fixtures, and fakes, which duplicate production shapes by design.
- Any finding whose existing counterpart it cannot quote. Without the other side, this lens is
  producing taste.

**Search discipline.** The lens holds `filesystem.search` and, on rendered harnesses,
`codegraph/*` — [`CopilotRendererTests`](../../tests/KyberWeave.Tests/Squad/CopilotRendererTests.cs)
already asserts `review-lens` renders with `'codegraph/*'` in its tool list. The lens file will
prescribe the query order — CodeGraph by the new type's name stem and by its collaborators
first, then `filesystem.search` for the naming family — so recall is a procedure rather than
improvisation.

---

## 5. Lens B — `duplicate-implementation`, and the gate under it

**Runner:** `review-triage`. **File:**
`products/kyber-squad/skills/code-review/references/lenses/duplicate-implementation.md`

### 5.1 The gate

New CLI verb, alongside `review gates` and `review verdict`:

```bash
kyber-weave review duplicates . --out duplicates.json
```

Declared by a host under `review.gates` like any other gate, so it is subject to the same
argv-only rule and the same "the reviewer never substitutes an ad-hoc command" discipline.

**Algorithm.** For every `method` and `function` node in `.codegraph/codegraph.db`, read the
source span `start_line..end_line`, normalize it, and cluster by SHA-256 of the normalized body.
Emit clusters of two or more that reach `review.duplicates.minimum-lines`.

Normalization drops blank lines, brace-only lines, and whole-line comments, collapses interior
whitespace, and discards the first surviving line — the signature — so that a body copied under
a new name still clusters. It deliberately does **not** strip trailing comments: doing that
safely means knowing whether a `//` sits inside a string literal, and a normalizer that gets
that wrong merges two bodies that differ, which is the one failure this gate must not have.
Missing a cluster costs recall; a false cluster costs trust.

**No exclusion globs.** Rejected rather than forgotten. `PathGlob` fails *towards matching* on a
regex timeout, which is right for a rule that escalates to a human and exactly wrong for an
exclusion — a pathological pattern would silently hide clusters. The gate reports facts and the
lens filters test fixtures and generated code, which needs no matcher at all.

**Cluster ids are content-derived** (`dup-` plus the first 8 hex of the body hash), not ordinal,
so the same duplication carries the same id across runs and across reorderings of the index.

**Reuse.** [`CodeGraphResolverAdapter`](../../src/KyberWeave.Core/CodeGraph/CodeGraphResolverAdapter.cs)
already batch-loads the node table through one `sqlite3` CLI invocation, which is the access path
[`AGENTS.md`](../../AGENTS.md) mandates over `Microsoft.Data.Sqlite` and its unresolved advisory.
The gate reuses it rather than opening its own connection.

**One schema change.** [`CodeGraphNode`](../../src/KyberWeave.Core/CodeGraph/CodeGraphNode.cs)
carries `StartLine` but not `EndLine`, and the adapter's `SELECT` does not read `end_line`. Both
gain it. The change is additive to a record with a defaulted position, so no caller breaks.

**One measured constraint on the design.** The index's `signature` column is populated for
**0 of 2462** C# method nodes, so the gate cannot key on signature and must read source spans.
`visibility` is populated for all 2462 and `return_type` for 770; neither is load-bearing here
but both are available for the Phase 3 ranking.

**Staleness is reported, not resolved.** The gate emits `indexModifiedUtc` and a
`symbolsUnreadable` count — symbols whose file or span the working tree no longer has — and the
lens is instructed to lower confidence and say so when that count is non-zero. Syncing the index
from inside a review gate was rejected: it would make an evidence-producing command mutate the
thing it reads from. This resolves the open question §9 carried.

### 5.2 The lens

**Applicability.** Applies when the duplicates gate produced output and the diff touches a file
named in at least one cluster. Skips with the reason when there is no `.codegraph/` index (D7),
when the gate did not run, or when no cluster intersects the diff.

**What it owns.** Attributing clusters to this change. A cluster whose members are all
pre-existing is not this change's finding. A cluster where the diff *added* one member is —
the change introduced a second copy of code that already existed.

**Reporting.** `id: duplicate-implementation/<method-name>`. The `excerpt` is the added body;
the `evidence` is the gate, the cluster id, and the path and line of every other member; the
`failure_scenario` is D3's second edit site, named concretely. Severity is `minor` by default and
`major` where the duplicated body encodes a rule — validation, authorization, parsing, a
serialization contract — because those are the bodies whose two copies drift into disagreeing.

**What it must not report.** Clusters with no member added by this diff. Test fixtures and
generated code. Any argument that the two copies *should* be merged into a specific shared
abstraction — the finding is that a second copy exists; how to resolve it is the author's call,
and `prior-art` is the seat that argues about structure.

---

## 6. Artifact inventory

| Artifact | Change | State |
|---|---|---|
| `KyberWeave.sln.DotSettings` | 29 promotions (§2) | ✅ |
| `…/lenses/prior-art.md` | New lens file (§4) | ✅ |
| `…/lenses/duplicate-implementation.md` | New lens file (§5.2) | ✅ |
| `…/skills/code-review/SKILL.md` | 15-row lens table, the second gate command, the seam between the two new lenses, `review.duplicates` in the config example, v4.1.0 | ✅ |
| `…/skills/code-review/references/csharp.md` | A "Duplication & Dead Code" section naming what the analyzer now covers and what it cannot | ✅ |
| `products/kyber-squad/agents/code-reviewer.md` | Three gate-consuming lenses, not two | ✅ |
| `products/kyber-squad/migration/code-reviewer.md` | `final-body-sha256` re-pinned to the edited body | ✅ |
| `docs/code-review/architecture.md` | Diagram, seat counts, part inventory, the evidence-split rationale between the two lenses | ✅ |
| `docs/code-review/README.md` | Fifteen lenses; three commands | ✅ |
| `docs/ci-pipelines/rule-reference.md` | `KW-REVIEW-030/031/032` under a `review duplicates` heading | ✅ |
| `src/KyberWeave.Core/CodeGraph/CodeGraphNode.cs` | `EndLine`, defaulted; `LineSpan` | ✅ |
| `src/KyberWeave.Core/CodeGraph/ICodeGraphSymbolEnumerator.cs` | New optional port — enumerate by kind | ✅ |
| `src/KyberWeave.Core/CodeGraph/CodeGraphResolverAdapter.cs` | `end_line` in the `SELECT`, kind index, port implementation | ✅ |
| `src/KyberWeave.Core/Review/DuplicateDetector.cs` | Normalization, hashing, clustering, the report record | ✅ |
| `src/KyberWeave.Core/Review/ReviewJson.cs` | Duplicates report read/write | ✅ |
| `src/KyberWeave.Core/Configuration/ReviewConfig.cs` | `ReviewDuplicates`, default 4 | ✅ |
| `…/Configuration/ReviewYamlSection.cs`, `ReviewConfigLoader.cs` | `review.duplicates`, with an out-of-range fallback | ✅ |
| `src/KyberWeave.Cli/Commands/Review/ReviewDuplicatesCommand.cs` | The gate command | ✅ |
| `…/Commands/Review/ReviewSettings.cs`, `Program.cs` | Settings and registration | ✅ |
| `.kyber-weave/kyber-weave.yml` | The duplicates gate, blocking; `minimum-lines: 4` | ✅ |
| `tests/KyberWeave.Tests/ReviewDuplicatesTests.cs` | 20 cases (§8) | ✅ |
| `tests/KyberWeave.Tests/CodeGraphPortTests.cs` | Adapter parity for `end_line` and the enumerator port | ✅ |
| `tests/KyberWeave.Tests/CodeGraphFixtureDb.cs`, `FakeCodeGraphResolver.cs` | `end_line` column, `IndexMethod`, the enumerator port | ✅ |
| `docs/plans/README.md` | This plan's row | ✅ |

## 7. Phases

- **Phase 0 — The redundancy tier. ✅ Done 2026-08-21.** §2. Settings only. The largest coverage
  gain per unit of effort in the plan, and it needed no code.
- **Phase 1 — `prior-art`. ✅ Done 2026-08-22.** One lens file, the SKILL.md row, the C# checklist
  section, the doc counts. Markdown only, exactly as scoped.
- **Phase 2 — The duplicates gate and `duplicate-implementation`. ✅ Done 2026-08-22.**
  `CodeGraphNode.EndLine`, `ICodeGraphSymbolEnumerator`, `DuplicateDetector`, the CLI verb, the
  lens file, this repository's gate declaration, 20 tests. Exact matches only (D5). Running it
  here finds 6 clusters, all true.
- **Phase 3 — Near-duplicates. ⏸ Deferred by its own precondition.** The plan gates this on
  "Phase 2 running for long enough to have a false-positive rate worth measuring", and Phase 2
  is hours old. Shipping a Jaccard threshold now would mean tuning it against the same six
  clusters it was derived from, which measures nothing. Revisit after the gate has run over real
  pull requests.
- **Phase 4 — Clear the backlog. ✅ Done 2026-08-22.** Two decisions the plan could not take for
  itself were put to the repository owner and answered: delete the unused public members in
  `KyberWeave.Core`, and take the `JetBrains.Annotations` dependency for `[UsedImplicitly]`.

  Cleared: the 18 unused public members in Core; every dead member in the test fakes
  (`FakeSquadRenderer`'s seven unused builders and the simulation branches only they could
  reach, `FakeSquadReleaseSource`'s five); a vestigial `IProcessExecutor` seam through
  `SquadInstallCommand`, `SquadUpdateCommand`, and `CreateLifecycleService` that four tests were
  injecting a configured fake into and which nothing had ever read; a `TryCreateLoader` overload
  chain with an `out` parameter no caller consumed; and `[UsedImplicitly]` on the 13
  framework-constructed constructors.

  Two of the findings were not dead code and were fixed rather than deleted.
  `IAgentParser` had two implementations and no consumer through the interface — `AgentLoader`
  now dispatches through `IAgentParser[]` instead of hardcoding both concretes, which is what
  the interface was for. `SquadLifecycleService.UninstallAsync` accepted a `CancellationToken`
  and ignored it; it now honours it at the boundary.

  One finding was neither, and became a todo:
  [`KW-AGENT-SPEC-004` is documented in the rule reference but never emitted](../todo/agent-spec-broken-reference-rule.md).
  Its constant is gone; whether the check should be implemented or the row withdrawn is a
  product decision, not a cleanup.

  **Gate: 86 → 34.** The promoted redundancy tier now reports **zero** findings on this
  repository.

## 8. Verification

Run 2026-08-22, all green:

| Gate | Result |
|---|---|
| `dotnet build -c Release` | 0 warnings, 0 errors (warnings are errors here) |
| `dotnet test -c Release` | **1548 passed**, 0 failed |
| `docs validate` · `docs drift` | 0 findings each |
| `skill validate` · `skill scan` · `skill lint` (`code-review`) | 0 findings, 0 findings, 2 info — no `KW-SKILL-LINT-011` overlap regression despite two new lenses in adjacent vocabulary |
| `agent validate` · `agent scan` (`products/kyber-squad`) | 0 findings each |
| `review duplicates .` | 6 clusters, 1846 symbols compared |
| `dotnet jb inspectcode --severity=WARNING` | **34**, down from 86 after Phase 4 |

**The analyzer trend, with the promotion isolated:**

| Point | Count | Of which from a promoted rule |
|---|---|---|
| Before Phase 0 | 35 | 0 — the tier was being filtered out |
| After the 29 promotions | 83 | 48 |
| After this plan's own new code | 86 | 51 |
| After Phase 4's cleanup | **34** | **0** |

The end state is one fewer finding than the starting state, with the entire redundancy tier now
switched on and reporting clean. All 34 that remain are pre-existing findings in test files from
rules this plan never touched — `AssignNullToNotNullAttribute` (30) and the closure rules (4).

Three of the 51 were on code this plan itself wrote, and the gate caught them: a symbol
enumerator port that was implemented and then bypassed, an unused fixture helper, and a test
helper returning a value nobody read. All three are fixed. That is the promotion earning its
keep on the first change after it landed.

**The duplicates gate is tested without an index.** 20 cases in `ReviewDuplicatesTests`, each a
source fixture on disk plus synthetic nodes: identical bodies cluster; different names over one
body still cluster; comment-only, blank-line, and whitespace differences do not prevent
clustering; a trailing comment *is* treated as a difference, and the test says why; a genuinely
different body does not cluster; below-threshold bodies are not compared; lowering the threshold
brings shorter bodies in; a missing file and a span past end-of-file are counted `unreadable`
rather than silently skipped or truncated; cluster ids are stable across input orderings; the
report round-trips through JSON; an out-of-range host threshold falls back; an unavailable index
returns a report rather than throwing, and `Analyze` never enumerates one.

`CodeGraphPortTests.AdapterReadsEndLinesAndEnumeratesByKind` is the parity test over the changed
`SELECT` — without it, losing the `end_line` column would silently empty the gate rather than
fail anything.

**Still outstanding:** the end-to-end `shadow`-mode run against a branch that copies a method
into a new file. The unit tests pin the detector; they do not pin the lens's attribution
behaviour, which is a model's.

## 9. Risks and open questions

### Two decisions Phase 4 surfaced and did not take

Both are about the same 31 `UnusedMember.Global` findings, and both turned out to rest on facts
the plan did not have when it was written.

**The 13 constructors are framework-constructed, and nine of them are compelled by another
analyzer.** Four are Spectre.Console command constructors the type registrar reaches
reflectively. The other nine are the conventional `()`, `(string)`, `(string, Exception)` set on
exception types — required by `CA1032`, which is enforced here (`AnalysisMode=all`,
`TreatWarningsAsErrors=true`, and `CA1032` is not in `NoWarn`). Deleting them breaks the build.
Two analyzers are in direct conflict, and the JetBrains-sanctioned resolution is `[UsedImplicitly]`,
which means a `JetBrains.Annotations` reference — a new dependency, and
[`AGENTS.md`](../../AGENTS.md) says new dependencies need justification. The alternatives are
inline `// ReSharper disable once` comments, which the
[previous InspectCode plan](2026-08-21-inspectcode-warning-fixes-and-suggestion-triage.md)
explicitly chose to avoid, or declaring the annotation attributes in our own source. Nobody
should pick between those silently.

**The 18 unused members in `KyberWeave.Core` are public API of a published package.**
`KyberWeave.Core` carries a `PackageId` and is pushed to GitHub Packages by
`.github/workflows/release.yml`. `UnusedMember.Global` means "unused *in this solution*", which
for a library is expected rather than defective — so deleting them is a breaking change to a
shipped surface, not a cleanup. Reading them, most look like genuine speculative generality
(`KyberWeaveConfig.WithReview`, `ReviewConfigLoader.LoadMerged`, `ReviewJson.Write(FindingsReport)`,
`DocumentIndexHost.RepoRoot`) rather than deliberate API, which argues for deleting them — but
that is a judgement about the package's contract, and the contract is the owner's.

The same fork decides whether `UnusedMember.Global` should stay promoted at all. Measured here it
is 42% signal: 13 of 31 were real and are now gone, and the remaining 18 will be joined by one
more every time a public member is added to Core. If the answer is "exempt the public surface",
the rule stays and gets `[PublicAPI]`; if it is "delete the dead members", the rule stays and the
backlog clears; if it is neither, the honest move is to back it out the way
`ClassNeverInstantiated.Global` was backed out in §2, for the same reason.

Two smaller ones in the same family: `IAgentParser`'s two `UnusedMemberInSuper.Global` hits
(interface members reached only through implementations — the same published-API question), and
`SquadLifecycleService.UninstallAsync`'s unused `CancellationToken` (public, conventional on an
`*Async` method, and arguably a real minor finding that the method ignores cancellation).

### Standing risks

- **`prior-art` is the highest false-positive risk lens in the catalogue.** "This looks like that"
  is exactly the shape of finding a model produces confidently and wrongly. Three mitigations are
  in the lens file: it may not report without quoting the existing counterpart, it is scoped to
  added types only (D6), and every `major` finding still faces the adversarial refutation pass.
  If it proves noisy, narrow it to the duplication half and drop the divergence half — do not
  lower the evidence bar.
- **Fifteen seats cost more than thirteen.** Both new lenses skip aggressively, so the marginal
  cost on a typical diff should be two skips. That is still a prediction: the council's cost
  measurement does not exist, carried over from the [council plan](2026-08-20-code-review-council.md) §14.
- **The duplicates gate adds a `dotnet run` to every gate suite.** On this repository it takes
  seconds and the index load dominates. On a tree with an order of magnitude more symbols,
  neither the sqlite3 batch load nor the per-body file reads have been profiled.
- **`prior-art` and `model-placement` will collide on some findings.** A new type that duplicates
  an existing one is often also misplaced. Adjudication reconciles duplicates and treats
  independent agreement as signal, so the collision is tolerable — but if the two seats report the
  same thing on most diffs, one is redundant and this plan should say which.
- **Resolved 2026-08-22 — index staleness.** The gate reports `indexModifiedUtc` and
  `symbolsUnreadable`, and the lens lowers confidence and says so when the count is non-zero.
  Syncing from inside the gate was rejected: an evidence-producing command should not mutate what
  it reads.
- **Open — should `prior-art` read the plan and specification indexes?** A type that duplicates
  another may be a deliberate step in an approved migration. `intent-alignment` already resolves
  **<plan-index>** and **<specification-index>**; teaching a second lens to do it duplicates that
  resolution, and not teaching it risks reporting an approved transitional duplicate as a defect.
