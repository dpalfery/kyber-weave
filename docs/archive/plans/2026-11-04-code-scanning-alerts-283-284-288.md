---
id: plans/2026-11-04-code-scanning-alerts-283-284-288
title: Close code-scanning alerts 283, 284 and 288 — skill provenance and the gate's evidence write
doc-type: plan
status: complete
component: ContextHygiene
owner: dpalfery
created: 2026-11-04
last-reviewed: 2026-11-05
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [ContextHygiene, Distribution]
---

# Close code-scanning alerts 283, 284 and 288 — skill provenance and the gate's evidence write

**Status: Complete.** Archived 2026-11-05. Every decision below is answered; the permanent `## Decisions` table records the
autonomous recommendations accepted during planning (D1–D8) and the approve-and-execute gate (AP1).
Frontmatter `status` is `complete` and the plan is archived. **Development mode:** `test-first`
(packet default; the owner did not opt out). **Autonomy:** `autonomous` (packet value and the
repository Config Reg **<autonomy>**). **Scale:** `extends-component`, components
`ContextHygiene`, `Distribution`.

## Problem and goal

**Problem.** Three GitHub code-scanning alerts are open on `main` at `af4f70d`:

| Alert | Tool | Rule | Where | What it says |
|---|---|---|---|---|
| 283 | Kyber-Weave `skill scan` (dogfooded in CI) | `KW-SKILL-SEC-030` | `.apm/skills/kyber-docs-onboarding-and-assessment/SKILL.md` | no `author` provenance |
| 284 | same | `KW-SKILL-SEC-031` | same file | no `version` provenance |
| 288 | CodeQL (`security-extended`) | `js/http-to-file-access`, medium | `dash/scripts/opencode-live-gate/run.mjs:472` | "Write to file system depends on Untrusted data" |

Alerts 283/284 are a **regression**: `5b91f7f9` (#474) added exactly the missing block and cited
these two alerts; `046ca6a6` (#476, a one-parent squash) removed those lines one commit later.
Nothing failed, because no test pins skill provenance and CI runs `skill scan --fail-on critical`
while these two codes are `Info` — the alert is only visible in the Security tab. So the defect is
not "a line is missing"; it is "a line can go missing again silently".

Alert 288 is **accepted risk, not a vulnerability**: the live gate deliberately records raw
kyberDash and OpenCode response bodies as its evidence, and an inline comment at `run.mjs:468-471`
already says so for human readers. The file name is a literal at every call site and
`evidenceFileName` (`run.mjs:482-487`) rejects `''`, `.`, `..`, NUL and separators; the traffic is
loopback-only, redacted (`run.mjs:182-186`) and bounded to 5 MB (`run.mjs:490-498`). CodeQL's
`js/http-to-file-access` has no default sanitizer subclass, so no guard in that file can break the
taint flow: a code-only fix would have to stop writing the response bodies, which deletes the gate's
purpose. The missing artifact is therefore a **reviewable decision record inside the analysis
configuration**, not a code guard and not a test of the write.

**Goal.**

1. `.apm/skills/kyber-docs-onboarding-and-assessment/SKILL.md` declares provenance in the nested
   `metadata:` map the gate actually reads, so alerts 283 and 284 close with zero findings from
   `skill scan` and `skill validate`.
2. A repository test pins provenance across **every** `.apm/skills/*/SKILL.md`, so the fix that was
   reverted once cannot be reverted silently again.
3. Alert 288 closes by excluding exactly one rule in `.github/codeql/codeql-config.yml`, with the
   reason, owner and review date stated in the file — the closest existing precedent in this
   repository (the Semgrep rule exclusion at `.github/workflows/ci.yml:558-570`, `:583`) — and a
   guard test that keeps the exclusion rule-scoped instead of letting it drift into the path-scoped
   blanket that ADR 0020 retired.
4. No `dash/` source changes, no change to what the live gate records, and the governed-docs corpus
   stays at zero findings.

## Investigation findings

Two read-only investigations were supplied with the dispatch and are not re-derived here. What this
authoring pass added:

**283/284 — the gate reads only the nested map.** `InstructionSurfaceScanner.ScanProvenance`
(`src/KyberWeave.Core/Security/InstructionSurfaceScanner.cs:92-119`) reports `codes.MissingAuthor`
at `:99-104` and `codes.MissingVersion` at `:106-111`; both branch on `metadata is null ||
!HasKey(metadata, "author"|"version")`, and `HasKey` (`:121-123`) accepts only `author` or
`metadata.author` **inside that dictionary**. `SkillScanner.Scan` (`src/KyberWeave.Core/Skills/Security/SkillScanner.cs:36-42`)
passes `skill.Frontmatter.Metadata` — the nested `metadata:` map only
(`src/KyberWeave.Core/Skills/Model/SkillFrontmatter.cs:30-32`), with `UnknownKeys` (`:52-53`) the
place a top-level `author:` would land instead. Codes: `InstructionSurfaceRuleCodes.cs:46-48`
(`KW-SKILL-SEC-030/031/032`), all `Severity.Info` (`InstructionSurfaceScanner.cs:101`, `:108`).
Confirmed trap: a top-level `author:`/`version:` does not satisfy the gate and adds two
`KW-SKILL-SPEC-009` unknown-key warnings from `SpecValidator.cs:89-95`.

**283/284 — the tree today.** `.apm/skills/` holds seven skill directories (`codegraph`,
`kyber-docs-authoring`, `kyber-docs-integrity-review`, `kyber-docs-onboarding-and-assessment`,
`kyber-weave-cli`, `kyber-weave-model-profile`, `spec-conductor`). Six carry
`license: MIT` followed by a `metadata:` block with `author:` and `version:`; **only
`kyber-docs-onboarding-and-assessment` does not**. So one repository-wide assertion fails on exactly
two findings today and on none after the fix — a clean RED/GREEN with no collateral.

**283/284 — existing coverage is blind to it.** `ScannerAndRoutingTests.cs:10-42` covers injection,
HTML comments, secrets; `CleanSkillHasNoCriticalFindings` (`:36-41`) asserts only
`Severity.Critical`, so `Info` provenance findings pass. The agent analogue to model the guard on is
`AgentGovernanceTests.cs:174-192`, which asserts `KW-AGENT-SEC-030/031/032` from an empty metadata
map. The repo-tree guard shape to model on is `KyberDocsSkillSetTests.cs` (`ToolRoot` +
`.apm/skills` enumeration). The skill file is hand-authored in place, not generated from a template
(`src/KyberWeave.Core/Squad/Packaging/SquadCompanionSkills.cs:10-12`, `:60-83`;
`products/kyber-squad/README.md:163-165`; `apm.yml:14-15`), so editing `SKILL.md` directly is
correct and no regeneration step exists.

**288 — the only HTTP-response sink in the tree.** Swept every `writeFileSync` / `writeFile` /
`appendFileSync` / `createWriteStream` sink under `dash/` and `npm/` and read what feeds each one:

| Sink | Data written | HTTP response body? |
|---|---|---|
| `dash/scripts/opencode-live-gate/run.mjs:472`, `:476` (`writeText`, `writeJson`) | kyberDash/OpenCode response text, redacted and bounded | **yes** — the alert |
| `dash/scripts/opencode-live-gate/run.mjs:939-940` | literal `SKILL_MARKDOWN`, literal `NOTES_TEXT` | no (literals) |
| `dash/scripts/release-acceptance/run.mjs:124`, `:145`, `:178`, `:186` | locally built JSON, subprocess stdout/stderr, CSV | no (subprocess output) |
| `dash/scripts/{pack-sea-web,bundle-litellm,bundle-factory-plugin,bundle-opencode-plugin,upgrade-path/gen-corpus,upgrade-path/run}.mjs`, `dash/src/tools/capture-content-fixture.mjs` | generated or on-disk data | no |
| `npm/lib/download.js:106` | release asset bytes piped from `https.get` to an install path | adjacent, not the same flow: no request-scoped response text, destination fixed by the installer, and **no alert is open against it** |

Conclusion for D1's condition: alert 288 is the only open CodeQL alert of this rule and
`run.mjs:472`/`:476` is the only HTTP-response-to-file sink in the tree. The conductor supplied the
open-alert set (this authoring harness has no shell, so `gh api` was not run here); T4 re-checks it
against the API before the config change is committed. Honest blast radius: a **rule**-scoped
exclusion would also cover `npm/lib/download.js:106` if CodeQL ever modelled that stream — recorded
as an accepted residual under Risks, which is exactly why the exclusion is rule-scoped with a stated
review date rather than silent.

**288 — governance, and what actually governs the config.** `docs/code-review/architecture.md:312-315`
("Suppressions carry a **mandatory expiry date** … `KW-REVIEW-004` says why") governs the **review
council's** suppression mechanism — `KW-REVIEW-003`/`KW-REVIEW-004` in
`docs/ci-pipelines/rule-reference.md:175` — and no gate reads `.github/codeql/`. So the expiry there
is not mechanically binding on a CodeQL query filter; this plan adopts it voluntarily as a stated
owner plus review date in the config comment, and says in the file that it is a review convention
rather than a gate. `products/kyber-squad/skills/code-review/references/lenses/static-analysis-triage.md:43-47`
is binding on the decision: every added suppression needs a justification that survives reading the
code, and "a blanket suppression at file or project scope is a finding regardless" — a
**rule**-scoped exclusion with a stated reason is the form the lens permits; a `paths-ignore` for
`dash/scripts` is the form it forbids, and ADR 0020 (`docs/adr/0020-kyberdash-one-time-fork.md:52-58`)
already retired the old `dash/scripts` paths-ignore list.

**288 — CI mechanics.** `.github/codeql/codeql-config.yml` is seven lines: a comment block, then
`name: KyberWeave CodeQL`. No `paths`, no `paths-ignore`, no `query-filters`. CI passes
`queries: security-extended` (`ci.yml:453`) and `config-file: ./.github/codeql/codeql-config.yml`
(`:455`), uploads under `category: /language:<lang>` (`:474`), and `.github/workflows/ci.yml:79` +
`:433-435` force the `changes` job's `all()` fallback for any edit under `.github/codeql/`, so the
PR is fully re-analysed (both CodeQL languages, dash, dotnet, sbom). No test anywhere in
`tests/` mentions CodeQL, so nothing pins the current config shape. Existing behaviour of the gate is
asserted and passing by `dash/scripts/opencode-live-gate/run.test.ts:37-55` and `:204-213`; that
stays green because this plan does not touch `run.mjs`.

**Discovery:** codegraph 0 (no `codegraph_explore` tool in this harness — reported, fallback below);
docs 3 (`docs_explore` × 3: CodeQL config exclusion, `KW-REVIEW-004`, skill provenance);
direct reads 26, each a line-exact confirmation of a dispatch-supplied finding or a file this plan
edits: `AGENTS.md`, `<plan-index>`, `plan-authoring.md`, both `SKILL.md` frontmatters,
`codeql-config.yml`, an archived plan's frontmatter (template), `ci.yml` × 3 slices,
`ScannerAndRoutingTests.cs`, `AgentGovernanceTests.cs` × 2, `KyberDocsSkillSetTests.cs`,
`InstructionSurfaceScanner.cs`, `SkillScanner.cs`, `SkillFrontmatter.cs`,
`InstructionSurfaceRuleCodes.cs`, `run.mjs` (evidence writer), `run.test.ts`,
`npm/lib/download.js`, `docs/code-review/architecture.md`, `docs/distribution.md`,
`docs/ci-pipelines/workflows-runbook.md`, `docs/context-hygiene/skills.md`,
`docs/context-hygiene/security-scanning.md`, `lenses/static-analysis-triage.md`, `docs/catalog.md`.
Self-gathered (no sub-agent tool): the `dash/`+`npm/` sink sweep in the table above, labelled as such
per the dispatch fallback. Live GitHub alert state: not read — no shell in this harness; delegated to
T4's `gh api` step.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | How is alert 288 closed, given no code guard can break the taint flow? | Rule-scoped `query-filters` exclusion of id `js/http-to-file-access` in `.github/codeql/codeql-config.yml`, with the rationale, owner and review date in the file, and the "Everything is analysed" comment amended so it is not contradicted. | Options: (a) code fix — off the table, the query has no default sanitizer subclass and only stopping the response-body write would clear it, which destroys the gate; (b) dismiss the alert in the Security tab — invisible to review, not in the repository, re-raised by the next analysis; (c) `paths-ignore: dash/scripts` — rejected, the weakest option, a path-scoped blanket the triage lens calls a finding and ADR 0020 retired; (d) inline `// codeql[...]` annotation at the sink — not chosen, it depends on the `AlertSuppression` query pack being part of the analysis rather than on this repository's configuration, and the existing `run.mjs:468-471` comments already record the acceptance for humans; (e) **selected**: one rule, whole repo, decision visible in review, matching the Semgrep precedent at `ci.yml:558-570` + `:583`. Condition satisfied in investigation: 288 is the only open alert of this rule and `run.mjs:472`/`:476` is the only HTTP-response-to-file sink. Re-checked in T4 before commit. | `autonomous: recommendation accepted` (2026-11-04) |
| D2 | Is a rule-scoped CodeQL exclusion a "blanket suppression" under `static-analysis-triage.md:43-47`? | No. The lens forbids a blanket at **file or project scope**; this is one rule id with a justification that survives reading the code, stated in the file that review reads. Its cost is disclosed: a rule-wide exclusion also covers `npm/lib/download.js:106` if CodeQL ever models that stream. | The alternative that would be a finding — `paths-ignore: dash/scripts` — is also the one ADR 0020 already retired, so choosing the rule scope is the narrow option this repository permits. | `autonomous: recommendation accepted` (2026-11-04) |
| D3 | Does `docs/code-review/architecture.md:312-315` (mandatory expiry, no "never") govern a CodeQL query filter? | It governs the **council's** suppression mechanism (`KW-REVIEW-003`/`KW-REVIEW-004`, `rule-reference.md:175`); no gate reads `.github/codeql/`. The convention is adopted voluntarily: owner `dpalfery` plus a review date (`2027-11-04`) written into the config comment, and the file says plainly that this is a review convention, not a gate. | Writing a date into the file is what makes a future reader ask the question again; claiming a mechanical gate that does not exist would be the worse failure. `docs/distribution.md:604` and `docs/ci-pipelines/workflows-runbook.md:274-276` are the canonical CI surfaces the fact is harvested into. | `autonomous: recommendation accepted` (2026-11-04) |
| D4 | How are alerts 283/284 fixed? | Insert a nested block after `license: MIT` in `.apm/skills/kyber-docs-onboarding-and-assessment/SKILL.md`: `metadata:` / `  author: dpalfery` / `  version: 0.1.0`. Edit the hand-authored file in place; no template or regeneration. | A **top-level** `author:`/`version:` is the trap the investigation measured: it satisfies nothing (`HasKey` reads the nested map) and adds two `KW-SKILL-SPEC-009` unknown-key warnings. `author: dpalfery` matches five of six siblings (`spec-conductor` uses `David R Palfery`); `version: 0.1.0` matches `codegraph`, `kyber-docs-integrity-review`, `kyber-weave-cli` as a first declared version — no changelog derives a different number, and inventing one would be a fabricated fact. | `autonomous: recommendation accepted` (2026-11-04) |
| D5 | What regression guard pins the provenance metadata, in which file? | New `tests/KyberWeave.Tests/SkillProvenanceTests.cs`: enumerate every `.apm/skills/*/SKILL.md` (recursive, ordered), parse each with the real `SkillParser.Parse`, scan with the real `SkillScanner`, collect any diagnostic whose code is `KW-SKILL-SEC-030`/`-031`/`-032`, and `Assert.Empty` the collection; `Assert.NotEmpty` on the discovered file set so a mistyped root cannot pass vacuously. | Modelled on `AgentGovernanceTests.cs:174-192` (same three codes for agents) in the repo-tree shape of `KyberDocsSkillSetTests.cs`. It dogfoods the exact engine the alert came from instead of asserting string presence, so it also catches the top-level-`author` trap. Scope is `.apm/skills` only: `products/kyber-squad/skills` holds 28 Squad skills of which only three declare `metadata:`, so including them would fail for 25 unrelated reasons and widen this change past its authorization — a Squad-wide provenance sweep is a separate, owner-authorized change. | `autonomous: recommendation accepted` (2026-11-04) |
| D6 | Does 288 ship without a test, or with one? | With one: new `tests/KyberWeave.Tests/CodeQlConfigTests.cs` parses `.github/codeql/codeql-config.yml` and asserts the file declares exactly one exclusion and that it is `js/http-to-file-access`, declares no `paths`/`paths-ignore` key at all, and carries the owner + review date in its comment. | The packet's finding that "the missing artifact is a decision record, not a test" is right about behaviour — no test can reproduce the alert locally (no bundled QL packs; installing them is out of scope). But a config-only change with nothing pinning it is how a rule-scoped decision later becomes a path-scoped one; the pin is cheap and keeps 288 test-first rather than exempt. Precedent for repo-file assertions: `UpstreamSeveranceTests`, `DistributionManifestTests`. | `autonomous: recommendation accepted` (2026-11-04) |
| D7 | Development mode. | `test-first`. | Packet value; the owner did not opt out, and both changes carry a RED observation (T2 before T1, T3 before T4). | `autonomous: recommendation accepted` (2026-11-04) |
| D8 | New `KW-*` rule ids? | None. The tests assert existing ids (`KW-SKILL-SEC-030/031/032`); the CodeQL rule id is CodeQL's, not this repository's. | Rule ids are permanent; nothing here needs a new one, and adding one to describe a config exclusion would duplicate the CodeQL id. | `autonomous: recommendation accepted` (2026-11-04) |
| G1 | Scope boundary. | No change to `dash/scripts/opencode-live-gate/run.mjs` behaviour or text beyond what already exists, no claim of a sanitizer, no `dash/` or `npm/` source edits, no new dependency, no `NoWarn`. | The alert closes by configuration; the gate's evidence content stays exactly as it is, which is why `run.test.ts:37-55`, `:204-213` stay green without modification. | `autonomous: recommendation accepted` (2026-11-04) |
| AP1 | Approve and execute. | Approved for execution. | Plan is decision-complete, mode-complete, scale-declared, with `docs_validate` and `docs_drift` passing at Ready. | `autonomous: recommendation accepted` (2026-11-04) |

## Test contract (test-first)

Mode `test-first`. Every behavioural change arrives as a test observed failing for the right reason
before the fix lands, and no test may be weakened to pass.

| Task | RED (must be observed and quoted in the task digest) | GREEN |
|---|---|---|
| T2 — skill provenance guard | `SkillProvenanceTests` fails with **exactly two** entries, both `KW-SKILL-SEC-030` and `KW-SKILL-SEC-031` on `kyber-docs-onboarding-and-assessment/SKILL.md`, and none for the other six skills. A run that reports zero, or that reports a third skill, is a bug in the test, not a pass. | After T1: `SkillProvenanceTests` green; the discovered-file count is 7 and non-zero. |
| T3 — CodeQL config guard | `CodeQlConfigTests` fails: the file declares no `query-filters` at all (and the other two assertions — one exclusion for `js/http-to-file-access`, no `paths`/`paths-ignore` — have nothing to hold). | After T4: `CodeQlConfigTests` green; the parsed document carries exactly one excluded id. |
| T1, T4 — fixes | No test of their own; they exist to turn T2/T3 green, and are re-verified by the CLI in T1's case (`skill scan`, `skill validate`). | See Verification gates. |
| T5 — docs harvest | Not a test task. Its contract is the two documentation gates reporting zero findings with the harvested sentences in place. | `docs validate --merge-ready` and `docs drift` both zero findings. |

**Alert 288's RED evidence is different in kind and is stated plainly:** no local reproduction exists
— the CodeQL CLI ships no QL packs in this checkout and installing them is out of scope — so the RED
evidence for 288 is (i) the open alert itself (medium, `js/http-to-file-access`, `run.mjs:472`,
observed on `main` at `af4f70d`) and (ii) the recorded inability to reproduce it locally, which is
why the closure evidence is the post-merge `CodeQL Analysis (javascript-typescript)` job plus the
alert's state, not a local run. T3's RED is the only locally observable one for 288's change.

## Tasks

| ID | Task | Owner role | Required skills | Files / symbols | Acceptance criteria | Depends on |
|---|---|---|---|---|---|---|
| T2 | Pin skill provenance across `.apm/skills` (RED first) | `test-dev` | **<test-coding-standard>**, **<csharp-coding-standard>** | `tests/KyberWeave.Tests/SkillProvenanceTests.cs` (new) | Test enumerates `.apm/skills/**/SKILL.md` from `KyberWeaveTestPaths.ToolRoot`, parses with `SkillParser.Parse(File.ReadAllText(...), path, dir)`, scans with `new SkillScanner().Scan(skill)`, collects `KW-SKILL-SEC-030/031/032`, `Assert.Empty` the collection, `Assert.NotEmpty` the file set. RED observed and quoted (two findings, one skill). Filter run reports a non-zero total. | — |
| T3 | Pin the CodeQL config's shape (RED first) | `test-dev` | **<test-coding-standard>**, **<csharp-coding-standard>** | `tests/KyberWeave.Tests/CodeQlConfigTests.cs` (new) | Parses `.github/codeql/codeql-config.yml` with YamlDotNet (already referenced); asserts (1) exactly one `query-filters` entry, (2) it is `exclude.id: js/http-to-file-access`, (3) no `paths` or `paths-ignore` key exists, (4) the comment block names an owner and a review date. Assertions read the parsed structure, not a byte-for-byte file match. RED observed and quoted. | — |
| T1 | Declare provenance in the onboarding skill | `docs-dev` | **<csharp-coding-standard>** not applicable; governed-instruction authoring: `kyber-docs-authoring` skill, `docs/context-hygiene/security-scanning.md` | `.apm/skills/kyber-docs-onboarding-and-assessment/SKILL.md:4` (insert after `license: MIT`) | Literal insertion (see Exact fixes). `SkillProvenanceTests` green; `skill validate` and `skill scan` on the directory report 0 findings; no `KW-SKILL-SPEC-009` warning appears (nothing added at top level). | T2 |
| T4 | Exclude one CodeQL rule, with its reason | `github-devops` | `static-analysis-triage` lens rules; GitHub Actions / CodeQL config schema | `.github/codeql/codeql-config.yml` (whole file) | `gh api` pre-flight confirms 288 is the only open `js/http-to-file-access` alert; file contains the literal block in Exact fixes; `CodeQlConfigTests` green; no other file touched; `run.mjs` byte-identical. | T3 |
| T5 | Harvest the two durable facts into canonical docs | `docs-dev` | `kyber-docs-authoring`, `docs/documentation-ontology.md` | `docs/context-hygiene/security-scanning.md` §"Provenance — `-030`…`-032`" (currently lines 54-57); `docs/distribution.md` §"Continuous integration security" CodeQL row (currently line 604) | Each harvested sentence is true of the shipped files (line-comparable to the config and the skill frontmatter); `last-reviewed` bumped on both; both documentation gates at zero findings. | T1, T2, T4 |
| T6 | Verify, then close out | `docs-dev` | `docs validate`/`docs drift`, `plan-closeout` | plan file, `<plan-index>` | Every verification command below run and pasted into the plan; the plan `git mv`'d to `docs/archive/plans/`, index row moved to Archived Plans with harvest destinations, Active Plans returned to its empty state; alert states 283/284/288 recorded (closed post-merge, `[NOT LIVE-VERIFIED]` if the merge has not happened when closeout runs). | T5 |

## Exact fixes

### 283 + 284 — `.apm/skills/kyber-docs-onboarding-and-assessment/SKILL.md`

Current lines 1-5:

```yaml
---
name: kyber-docs-onboarding-and-assessment
description: "Bootstraps or audits a repository's documentation architecture — … Not for authoring a single governed document — that is kyber-docs-authoring."
license: MIT
---
```

Replace the single line `license: MIT` (line 4) with these three lines; nothing else in the file
changes:

```yaml
license: MIT
metadata:
  author: dpalfery
  version: 0.1.0
```

### 288 — `.github/codeql/codeql-config.yml` (whole file)

```yaml
# CodeQL scope for KyberWeave.
#
# Everything is analysed, `dash/**` included. `dash/` was a vendored subtree of
# the upstream CodeBurn project whose directories this config once excluded;
# since the one-time fork (ADR 0020) all of it is first-party code under the
# same analysis as the rest of the repository. One rule is turned off below, and
# it is turned off by rule id, never by path: no file or directory is excluded.
#
# `js/http-to-file-access` is excluded because its one sink is the evidence
# writer in `dash/scripts/opencode-live-gate/run.mjs`: the live gate records
# kyberDash and OpenCode responses into evidence files, which is the thing the
# gate exists to prove, so "network response data reaches a file" is the feature
# and not a defect. No code guard can clear it: the query has no default
# sanitizer, the file names are plain names inside a gate-owned directory, and
# redaction and a byte cap do not break the flow, so the only "fix" would be to
# stop recording the responses. This is the same shape as the Semgrep rule
# exclusion in `.github/workflows/ci.yml`, and keeping the reason here keeps it
# visible to review instead of in a source comment.
#
# Cost, stated so review can weigh it: a rule-wide exclusion also covers any
# future sink of the same rule, including `npm/lib/download.js`, whose streamed
# release download raises no alert today.
#
# Owner: dpalfery. Review date: 2027-11-04 - reopen the rule if the gate stops
# writing response bodies, and do not let this entry outlive its reason. This
# date is a review convention, not a gate: no rule reads this file except the
# CodeQL action.
name: KyberWeave CodeQL
query-filters:
  - exclude:
      id: js/http-to-file-access
```

The workflow's `queries: security-extended` (`ci.yml:453`) still applies; a config-file
`query-filters` entry narrows the suite the workflow requests and does not replace it.

## Dependency graph

```
T2 (test, RED) ──► T1 (skill fix, GREEN) ─┐
                                          ├──► T5 (docs harvest) ──► T6 (verify + closeout)
T3 (test, RED) ──► T4 (config fix, GREEN) ┘
```

T2 and T3 are file-independent and run in parallel; T1 and T4 are file-independent and run in
parallel once their tests are RED. Four small edits across four files: within
`review.max-concurrent-workers` (default 4) and sized for one worker per task.

## Risks

| Risk | Mitigation |
|---|---|
| The exclusion is rule-wide, so a future sink of `js/http-to-file-access` (e.g. `npm/lib/download.js`) would be silently excluded too. | Stated in the config file itself and in the plan; owner + review date so the scope is re-argued rather than inherited. `CodeQlConfigTests` forbids the drift that would matter more — a path-scoped exclusion. |
| Someone "fixes" the provenance finding by hoisting `author:` to the top level, which passes nothing and adds two `KW-SKILL-SPEC-009` warnings. | `SkillProvenanceTests` dogfoods the scanner, so the hoist goes RED; the harvest sentence states the nested requirement explicitly. |
| Adding `metadata:` changes the `description` score or the `skill lint` outcome. | `metadata` is a spec field (`SkillFrontmatter.Metadata`, `:30-32`), not an unknown key; T1 re-runs `skill validate` and `skill scan` on the directory, and the description text is untouched. |
| The PR is fully re-analysed by both CodeQL languages and every other job because `.github/codeql/` changes (`ci.yml:79`, `:433-435`). | Expected, and the reason the config change must be correct on the first push: T3's test and a local YAML parse run before the push. |
| Adding a test file could trip `AnalysisMode=all` / `TreatWarningsAsErrors`. | The test lands through `test-dev`'s own `task-gate` run plus the filtered test build; no `NoWarn` is added (constraint). |
| Alert closure cannot be observed locally. | T6 records the post-merge alert state, or records `[NOT LIVE-VERIFIED]` plus the check to run — an unobserved closure is never claimed. |

## Out of scope

- Any change to `dash/scripts/opencode-live-gate/run.mjs` — text, behaviour, or an inline suppression
  comment.
- Any sanitizer claim: no code path here is asserted to break the taint flow.
- `paths` / `paths-ignore` in any CodeQL, Semgrep, Trivy or gitleaks configuration.
- Provenance for the 28 `products/kyber-squad/skills` skills (25 of them declare no `metadata:`):
  a separate, owner-authorized change.
- Installing CodeQL QL packs for a local reproduction.
- Any new `KW-*` rule id, any `NoWarn` entry, any dependency.
- Widening the ontology, the rule set, or the analysis scope to make a failure disappear.

## Verification gates

Run from the repository root; every test/build command goes through the watchdog.

```bash
# 1. Build once (T1/T4 changed no C#, but the CLI is the checker for T1).
scripts/proc-guard.sh run -- dotnet build src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release

# 2. The two new tests (expect a non-zero total in the summary).
scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release \
  --no-build --filter "FullyQualifiedName~SkillProvenanceTests|FullyQualifiedName~CodeQlConfigTests"

# 3. The alert's own tool, before and after T1 (2 info findings -> 0).
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli --no-build -c Release -- \
  skill scan .apm/skills/kyber-docs-onboarding-and-assessment --fail-on critical --format markdown
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli --no-build -c Release -- \
  skill validate .apm/skills/kyber-docs-onboarding-and-assessment --format table

# 4. Configuration parses and carries exactly one rule filter.
python3 -c "import yaml,pathlib;d=yaml.safe_load(pathlib.Path('.github/codeql/codeql-config.yml').read_text());print(d['query-filters'])"

# 5. Pre-flight for D1's condition: 288 is the only open alert of this rule.
gh api "repos/dpalfery/kyber-weave/code-scanning/alerts?state=open" \
  --jq '[.[] | select(.rule.id=="js/http-to-file-access") | .number]'   # expect [288]

# 6. Governed docs (zero findings each).
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- \
  docs validate . --merge-ready
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .

# 7. Gate suite scoped to the changed files; --all because the change touches CI config.
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- review gates . --all --out artifacts/gates.json
```

**Closure evidence.** 283/284: step 3 reports 0 findings and step 5 no longer lists them after the
merge (`main`'s `skill-docs-gate` re-uploads the SARIF). 288: the PR's
`CodeQL Analysis (javascript-typescript)` job runs because of the `.github/codeql/` fallback; after
merge, `gh api .../code-scanning/alerts?state=open` lists none of the three and each shows `fixed`.
Anything not observed is recorded as `[NOT LIVE-VERIFIED]` with the command that would close it.

## Review and closeout

- Whole-change review by the council over a diff that touches one skill frontmatter, one CodeQL
  config, two test files and two documents. The static-analysis-triage lens applies to the config
  change by construction (D2); the C# lens is limited to the two test files.
- Closeout (`docs-dev`): harvest the two durable facts (below), archive this plan with `git mv` to
  `docs/archive/plans/`, move the index row to Archived Plans with its harvest destinations and
  evidence, return **Active Plans** to its empty state, and re-run `docs validate . --merge-ready`
  plus `docs drift .` after the archive.

## Design harvest

| ID | Target component | Target doc type | Durable item | Destination |
|---|---|---|---|---|
| H1 | ContextHygiene | reference | Skill provenance (`KW-SKILL-SEC-030`…`-032`) is read from the **nested** `metadata:` map only; a top-level `author:`/`version:` satisfies nothing and adds `KW-SKILL-SPEC-009`; every `.apm/skills/*/SKILL.md` ships `metadata.author` and `metadata.version`, pinned by `SkillProvenanceTests` | `docs/context-hygiene/security-scanning.md` §"Provenance — `-030`…`-032`" |
| H2 | Distribution | operations | CodeQL analysis excludes exactly one rule, `js/http-to-file-access`, by id and never by path; the reason is the live gate's evidence writer; owner and review date live in the config; no file or directory is excluded | `docs/distribution.md` §"Continuous integration security" (CodeQL row plus one short paragraph) |

## Closeout mapping

| ID | Destination |
|---|---|
| H1 | [context-hygiene/security-scanning.md](../../context-hygiene/security-scanning.md) — nested `metadata:` provenance form and the repository-wide guard |
| H2 | [distribution.md](../../distribution.md#continuous-integration-security) — the single rule-scoped CodeQL exclusion, its reason, owner and review date |
| D1, D2, D3, D6 | [distribution.md](../../distribution.md#continuous-integration-security) — rule-scoped CodeQL exclusion, its rationale, and the config guard |
| D4, D5 | [context-hygiene/security-scanning.md](../../context-hygiene/security-scanning.md) — nested provenance fields and the repository-wide pin |
| D7, D8, G1, AP1 | Documentation waiver: transient process and scope decisions (mode selection, no new rule id, unchanged `run.mjs`/`run.test.ts` scope boundary, approve-and-execute), recorded in `## Decisions` and carrying no lasting architectural record |

## Completion summary

- **T2 (RED):** `SkillProvenanceTests` failed with exactly two findings — `KW-SKILL-SEC-030` and
  `KW-SKILL-SEC-031`, both on `kyber-docs-onboarding-and-assessment/SKILL.md` — and none for the
  other six skills.
- **T3 (RED):** `CodeQlConfigTests` failed: `query-filters` absent and no `Owner:` line.
- **T1 (283/284 GREEN):** nested `metadata:` added; `skill scan` and `skill validate` on the directory
  report **0 findings**.
- **T4 (288 GREEN):** rule-scoped `query-filters` added; the YAML parses to exactly
  `[{'exclude': {'id': 'js/http-to-file-access'}}]`; the open-alert pre-flight returned `[288]` (the
  only open alert of that rule).
- **T5 (docs):** harvested to `docs/context-hygiene/security-scanning.md` and `docs/distribution.md`.
- **T6 (verify):** filtered `dotnet test` 4/4 passed; test-project build 0 warnings; `docs validate
  --merge-ready` and `docs drift .` at 0 findings after the archive.
- **Alert closure:** 283/284/288 `[NOT LIVE-VERIFIED]` — they close when `main`'s analysis jobs
  re-upload after merge. Check:
  `gh api "repos/dpalfery/kyber-weave/code-scanning/alerts?state=open" --jq '[.[] | select(.number==283 or .number==284 or .number==288)]'`
  → expect `[]`.
