---
id: plans/2026-10-28-architect-docs-gates-mcp
title: "Architect obtains docs validate and docs drift through two read-only MCP tools"
doc-type: plan
status: archived
component: DocGraph
owner: dpalfery
last-reviewed: 2026-10-28
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [DocGraph, KyberSquad]
---

# Architect obtains docs validate and docs drift through two read-only MCP tools

**Status: Archived — complete 2026-10-28.** Closeout mapping is in §8. Approved for execution 2026-10-28 (AP1, `AUTONOMOUS_APPROVAL: approve-and-execute`, provenance `autonomous: recommendation accepted`). Frontmatter `status: archived` follows the closed status vocabulary (`KW-DOC-SPEC-002`). **Development mode:** `test-first` (packet value; no user opt-out). **Autonomy:** `autonomous` (packet value and repository Config Reg `<autonomy>`).

**Scale:** `extends-component`, components `DocGraph` and `KyberSquad`.

**Recommendation:** Add two read-only tools, `docs_validate` and `docs_drift`, to the existing kyber-weave docs MCP server. Each tool runs the same in-process Core gate as the matching CLI command and returns the findings, including failures. Do not grant the architect `process.execute: allow`.

> Discovery: codegraph 5, docs 14 (docs_explore 10, docs_status 1, docs_glossary 1, docs_for_symbol 2), direct reads 8.
> - CodeGraph: `DocsTools` and its callers; `DocsValidateCommand` and `DocsDriftCommand` blast radius; `SquadCapabilityProfile` / `SquadPermissionDecision`; `CapabilityDegradations` (shell-plus-withheld-write). The first two command explores named the command symbols and then returned other files' bodies, so the command bodies were read directly.
> - Docs: `docs_status` root is this checkout (`/Users/dave/git/kyber-weave2`, rev `49a05ad7`). Governance (`docs validate` / `docs drift` rule ids), MCP runbook, squad architecture and requirements (KS-002 lattice), catalog (`DocGraph`, `KyberSquad`), ADR 0028 was not in the explore hit and was read directly.
> - Direct reads of indexed code, each because the explore did not return that body: `DocsValidateCommand.cs`, `DocsDriftCommand.cs`, `CursorRenderer.cs` lines 1-120, `Program.cs` lines 1-140, `DocsServerInstructions.cs`, `DocDriftLinter.cs` lines 1-80, `Diagnostic.cs` lines 1-49, `DocumentIndexHost.cs` lines 70-109.
> - Non-indexed reads: `products/kyber-squad/agents/architect.md`, `profiles/capabilities.yml`, `toolchain.yml`, `docs/plans/README.md`, ADR 0028, archived plan frontmatter for the current plan shape.
> - Validation of this Draft: `docs validate .` reported no findings. The first `docs drift .` reported 58 `KW-DOC-DRIFT-001` on symbols that exist in this checkout (the local CodeGraph index had not picked up `SquadStateMigration.cs` and hundreds of other files); none of those findings were in this plan. After `codegraph sync`, `docs drift .` reported no findings. Neither check used `--merge-ready`, because this plan is still in the active plans folder.

## 1. Problem and goal

### 1.1 Problem

The architect must obtain `docs validate` and `docs drift` results, including failure output it can act on, for the checkout it is planning in, before a plan is `PLAN_READY`. Those two checks are how a plan edit keeps the corpus at zero findings.

The shared `architect` capability profile does not grant a shell. It sets `process.execute: ask`, and the profile comment states the intended grant is those two commands, not a shell (`products/kyber-squad/profiles/capabilities.yml`). The lattice is `deny < ask < allow` with no command or argv scope (same file; ADR 0028; the `documentation` profile comment). Targets that cannot express `ask` withhold the tool (ADR 0028 for Devin; the `documentation` profile records the same narrowing on Claude, Pi, and ZCode). On those targets the architect cannot run the checks. Cursor cannot express the split either: `readonly: true` is emitted only when both `filesystem.write` and `process.execute` are withheld, and there is no tools allow-list (`CursorRenderer`). The architect must be able to write a plan, so Cursor does not set `readonly`, and `process.execute: ask` is recorded as `permission-not-expressible` rather than as two allowed commands.

`architect-copilot` and `architect-devin` already set `process.execute: allow` for target limitations (no ask state; Devin file creation). Extending that allow to the shared profile, or adding a Cursor allow override, would be a general shell. That is the grant this work must not make.

The docs MCP server (`kyber-weave-mcp`) already serves this checkout read-only. Its tools are `docs_explore`, `docs_for_symbol`, `docs_analysis_candidates`, `docs_glossary`, `docs_status`, and `agent_coding_standards`. None of them runs the gates. The CLI commands do: `DocsValidateCommand` walks the working tree and runs `DocSpecValidator`, `ConfigRegValidator`, `PlanInventoryValidator`, `TodoInventoryValidator`, `ManagedGlossaryService.Validate`, and, only with `--merge-ready`, `OpenWorkValidator` plus archive closeout. `DocsDriftCommand` runs `DocDriftLinter`. A missing CodeGraph index is one critical `KW-DOC-DRIFT-001` ("Drift cannot be verified"), and `DiagnosticReport.HasErrors` treats Critical as a failure. The MCP project references Core only, and it must not write to stdout because stdout is the JSON-RPC transport.

### 1.2 Goal

An architect with the kyber-weave MCP server bound to its checkout can call `docs_validate` and `docs_drift`, read a pass or fail line, and read every finding it needs to fix the plan (rule id, severity, path, line, message, hint). The tools default to the planning gate: `docs validate` without `--merge-ready`, plus `docs drift`. The shared architect profile stays `process.execute: ask`. No new rule id, dependency, or ontology value.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | How does the architect obtain the two gate results without a general shell? | Two read-only MCP tools, `docs_validate` and `docs_drift`, on the existing kyber-weave docs server. Each calls the same Core gate as the matching CLI command and returns findings for the server's resolved repository root. | The user's two-tool shape matches the CLI and the `PLAN_READY` digest, which names the checks separately. In-process Core calls are required because the MCP process cannot host Spectre stdout (MCP `Program.cs`, MCP `AGENTS.md`). Precedent: `agent_coding_standards` plus `toolchain.yml` `required-mcp-tools` (archived plan `2026-10-14-mcp-agent-coding-standards-tool`). | `autonomous: recommendation accepted` |
| D2 | Should the shared architect profile become `process.execute: allow`? | No. Leave `architect` at `process.execute: ask`. Do not add a target override that allows execute. | The lattice has no argv scope, so allow is a shell (`capabilities.yml` architect and documentation comments; ADR 0028). That is the grant the role is denied in order to keep an expensive model from implementing. Copilot and Devin already allow execute for other target limits; those profiles stay as they are (D5). | `autonomous: recommendation accepted` |
| D3 | Should `process.execute` move from `ask` to `deny` once the tools exist? | No. Keep `ask`. | `ask` is what targets that can prompt still use for the authorized shell fallbacks (`codegraph explore`, and `codegraph init` when `.codegraph/` is absent). The tools remove the need to approve a shell for the two gates. Flipping to deny does not make Cursor withhold the shell while writes stay allowed (`CursorRenderer`). | `autonomous: recommendation accepted` |
| D4 | One combined tool instead of two? | No. Two tools. | `--merge-ready` applies only to validate. Drift fails closed on a missing index (`KW-DOC-DRIFT-001` critical) even when validate is clean. A single boolean hides which gate failed. `PLAN_READY` records `DOCS_VALIDATE` and `DOCS_DRIFT` separately. | `autonomous: recommendation accepted` |
| D5 | Should `architect-copilot` or `architect-devin` lose `process.execute: allow`? | No. Leave both overrides. | ADR 0028: Devin's allow exists so the architect can create a plan file that does not yet exist (`touch`), because Devin `edit` fails on a missing file. Copilot has no ask state. The tools do not replace file creation. Narrowing those profiles would re-break plan writes on those targets. | `autonomous: recommendation accepted` |
| D6 | What do the tools accept and return? | No repository-path argument. Root is the MCP server's resolved root (`DocumentIndexHost.RepositoryRoot`), disclosed by the existing provenance line. `docs_validate` takes `mergeReady` (default `false`) and `base` (used only when `mergeReady` is true; default `origin/HEAD`, same as the CLI). Both take `charBudget` (default 12000, clamp 1000–120000). Response order: provenance line, then `result: pass` or `result: fail` with error and warning counts, then findings. Errors and criticals before warnings. The verdict is not truncated. A truncated body ends with an omitted count. `ReadOnly = true`, `OpenWorld = false`. | A path argument could validate a different tree than the checkout. Planning `PLAN_READY` must not pass `mergeReady`: an active plan fails `KW-DOC-LIFECYCLE-003` under `--merge-ready` (governance). Untracked plan files are on disk; `DocumentLoader` walks the docs tree, not `git ls-files`, and the tools must keep that. Warnings (`KW-DOC-DRIFT-003`) do not fail the gate, matching `HasErrors`, and must still be printed. | `autonomous: recommendation accepted` |
| D7 | Where does the validate sequence live so the CLI and the tool cannot diverge? | A new Core type `DocsSchemaGate` in `src/KyberWeave.Core/Docs/Validation/DocsSchemaGate.cs`. `DocsValidateCommand` and `docs_validate` both call it. Drift stays `DocDriftLinter` called from both `DocsDriftCommand` and `docs_drift`. The linter's `ICodeGraphResolver` comes from the factory `DocumentIndexHost` already stores; add a public method that invokes that factory. The gate returns a `DiagnosticReport` and writes nothing to stdout. | MCP references Core only. Core must not construct `CodeGraphResolverAdapter` (Core `AGENTS.md`). Closeout scope resolution stays on `ChangedFiles` in the caller; the gate takes the resolved changes or the scope-failure diagnostic so the `KW-DOC-LIFECYCLE-004` C0 text is built once. No new `KW-*` id. No new package. Markdig and YamlDotNet remain Core's only dependencies. | `autonomous: recommendation accepted` |
| D8 | Who may call the tools, and what does the architect's body say? | The tools are on the shared server, so any agent whose harness lists the kyber-weave roster can call them. They are read-only. The architect body and `plan-authoring.md` name `docs_validate` and `docs_drift` as the way to satisfy the two checks, tell the architect to leave `mergeReady` false while the plan is in the active folder, and tell it to return `STATUS: BLOCKED` when a tool is absent or the provenance root is a different checkout. A shell is not a substitute for the tools and is not permission to implement. `DocsServerInstructions` and `toolchain.yml` `required-mcp-tools.kyber-weave` list both names. | `SquadToolchainRosterGuardTests` fails when an `[McpServerTool]` name is missing from the roster. The roster is per server, not per agent (`toolchain.yml`). Restricting the tools to the architect would be a new permission mechanism (rejected with D2). | `autonomous: recommendation accepted` |
| AP1 | Approve and execute this plan at the declared scale? | Approved. Scale `extends-component`. Components `DocGraph` and `KyberSquad`. | Conductor gate `AUTONOMOUS_APPROVAL: approve-and-execute` on 2026-10-28. D1–D8 are unchanged. Frontmatter ontology `status` is `current` because that vocabulary has no `ready` value; the lifecycle Ready is the body status line and the plan index. | `autonomous: recommendation accepted` |

Rejected alternatives, recorded above: MCP tools that shell out to the `kyber-weave` CLI (D1); shared or Cursor `process.execute: allow` (D2); `process.execute: deny` (D3); one combined tool (D4); narrowing the Copilot or Devin overrides (D5); a new capability or argv allow-list (D2, disproportionate across twelve renderers); the conductor running the gates for the architect (D8 — ADR 0028 already records that this leaves `PLAN_READY` unreachable on a target that withholds exec).

## Design harvest

| ID | Durable item | Target component | Target doc type | Target document |
|---|---|---|---|---|
| H1 | `docs_validate` and `docs_drift` contracts: names, defaults, no path argument, provenance, verdict line, finding shape, caps, `mergeReady` false for an active plan | DocGraph | runbook | `docs/docgraph/mcp-runbook.md` (The tools) |
| H2 | An agent obtains the same two gates through those tools. The CLI remains the human and CI path. No new rule id. A missing index is still critical `KW-DOC-DRIFT-001`, not a pass. | DocGraph | governance | `docs/docgraph/governance.md` |
| H3 | The architect's two checks are the MCP tools. Shared `process.execute` stays `ask`. The tools are not a shell grant. | KyberSquad | architecture | `docs/kyber-squad/architecture.md` §2 |
| H4 | Amend ADR 0028 in place: the two gates no longer depend on `process.execute`. The Devin allow remains for creating a plan file that does not yet exist. | KyberSquad | adr | `docs/adr/0028-devin-target-scoped-authoring-capability-profiles.md` |

No new ADR. ADR waiver: this extends the existing DocGraph MCP server with two read-only tools and does not change the capability lattice, the permission vocabulary, a dependency, or a schema. ADR 0028 is amended in place (H4), not replaced.

No documentation waiver. The CLI command surface is unchanged (same flags, same exit codes), so `docs/cli-command-surface.md` is not a harvest target.

## 4. Test contract

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/McpDocsGateToolsTests.cs` (new). Follow the `[McpServerTool]` lookup idiom in `McpDocsProvenanceTests`. | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~McpDocsGateToolsTests` | See T1 scenarios below. | The new test class exists and fails because `docs_validate` and `docs_drift` are not registered `[McpServerTool]` names, and because `DocsServerInstructions.Text`, `toolchain.yml`, `architect.md`, and `plan-authoring.md` do not yet name both tools. The capability-profile pin passes on the current `ask` value; it is a guard, not the RED. | The same filter passes with no assertion weakened. Missing-tool failures are gone because the tools and the named instruction lines exist. The `ask` pin still passes. |
| T2 | Same class, plus existing `SquadToolchainRosterGuardTests`, `McpPackagingTests`, `OpenWorkValidatorTests`, `ManagedGlossaryTests`, `TodoInventoryValidatorTests`. | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~McpDocsGateToolsTests\|FullyQualifiedName~SquadToolchainRosterGuardTests\|FullyQualifiedName~McpPackagingTests\|FullyQualifiedName~OpenWorkValidatorTests\|FullyQualifiedName~ManagedGlossaryTests\|FullyQualifiedName~TodoInventoryValidatorTests"` | CLI validate findings and `docs_validate` findings are the same rule ids on one fixture. Drift tool findings match `DocDriftLinter` for an unresolved `code-refs` entry and for an unavailable resolver. Packaging and roster tests list both new tool names. | T1's RED is already recorded. T2 does not start until that RED exists. | The filter passes. `KyberWeave.Mcp.csproj` and `KyberWeave.Core.csproj` gain no package reference. `capabilities.yml` still has `architect.process.execute: ask`. |
| T3 | No new test. The governed pages are the contract. | `dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .` and `dotnet run --project src/KyberWeave.Cli -c Release -- docs drift .` (no `--merge-ready` while this plan is in the active folder). | H1–H4 state the D6 contract and the D2/D3/D5 lattice facts. ADR 0028's Devin allow decision is still the file-creation grant. | No-test task: documentation. RED does not apply. The pages are wrong until T3 edits them; T3's verification is the two docs commands plus reading the four targets against H1–H4. | Both commands exit 0. Each harvest target contains the durable item in the Design harvest table. |
| T4 | No new test. Closeout is the archive mapping. | Same two docs commands, then `docs validate . --merge-ready` after the plan has been archived. | Closeout mapping names every decision id and H1–H4. The plan file is no longer under `docs/plans/`. | No-test task: closeout. Verification is the mapping table and the merge-ready validate after archive. | `docs validate . --merge-ready` and `docs drift .` exit 0. Index lists the plan under Archived Plans. |

### T1 scenarios

Assert observable results, not wiring:

1. `docs_validate` and `docs_drift` resolve by `[McpServerTool]` name. Each has `ReadOnly` true and `OpenWorld` false. Neither method has a repository-path parameter.
2. On a fixture corpus with one ungoverned markdown file, `docs_validate` returns `result: fail`, contains `KW-DOC-SPEC-001`, and the provenance root is that fixture's absolute root. The same fixture through `DocsSchemaGate` (or, until that type exists, the failure is the missing tool) yields the same rule id once both exist. Until the tool exists this scenario fails at tool lookup.
3. On a conformant fixture, `docs_validate` with `mergeReady` false returns `result: pass`. The same fixture with a plan document still in the active plans folder returns `KW-DOC-LIFECYCLE-003` only when `mergeReady` is true, and does not return it when `mergeReady` is false.
4. `docs_drift` against a document whose `code-refs` symbol the injected resolver cannot resolve returns `result: fail`, `KW-DOC-DRIFT-001`, the path, and a hint. An unavailable resolver returns `result: fail` and the existing "Drift cannot be verified" diagnostic, not `result: pass`. A warning-only `KW-DOC-DRIFT-003` returns `result: pass` and still includes that rule id.
5. A response that exceeds `charBudget` still begins with the provenance line and the `result:` line, lists error-severity findings before warnings, and states how many findings were omitted.
6. `DocsServerInstructions.Text`, `products/kyber-squad/toolchain.yml`, `products/kyber-squad/agents/architect.md`, and `products/kyber-squad/agents/architect/references/plan-authoring.md` each contain the names `docs_validate` and `docs_drift`.
7. Guard, expected to pass before and after implementation: under the shared `architect` profile in `capabilities.yml`, `process.execute` is `ask`. It is not `allow`.

The drift tests supply the resolver through `DocumentIndexHost`'s existing resolver factory (a fake). They must not require `sqlite3` or a real `.codegraph/` index.

## 5. Tasks

### T1 — RED: gate-tool contract tests

- **Objective:** Add `McpDocsGateToolsTests` covering the T1 scenarios. Do not add the tools, the gate type, or the instruction edits.
- **Files:** `tests/KyberWeave.Tests/McpDocsGateToolsTests.cs` (new only).
- **Skill:** `test-dev`.
- **Depends on:** none.
- **Acceptance:** The runner command fails on scenarios 1–6 for the missing tools and the missing names. Scenario 7 passes. No production file changes. The worker's `task-gate` is run on this task's files; a failing gate returns to this worker.

### T2 — GREEN: tools, shared validate gate, architect instructions

- **Objective:** Make T1 pass without weakening an assertion. Implement D6, D7, and D8. Leave the capability decisions at D2, D3, and D5.
- **Files:**
  - `src/KyberWeave.Core/Docs/Validation/DocsSchemaGate.cs` (new)
  - `src/KyberWeave.Core/Docs/Search/DocumentIndexHost.cs` (public method that invokes the stored resolver factory)
  - `src/KyberWeave.Cli/Commands/Docs/DocsValidateCommand.cs` (delegate the validator sequence to `DocsSchemaGate`; keep CLI flags, exit codes, and `CommandHelpers.Finish`)
  - `src/KyberWeave.Mcp/DocsTools.cs` (`docs_validate`, `docs_drift`)
  - `src/KyberWeave.Mcp/DocsServerInstructions.cs`
  - `products/kyber-squad/toolchain.yml` (both names under `kyber-weave`)
  - `products/kyber-squad/agents/architect.md` and `products/kyber-squad/agents/architect/references/plan-authoring.md` (the D8 wording)
  - `products/kyber-squad/profiles/capabilities.yml` (comments only: the two gates are the MCP tools; `ask` is unchanged; the Devin allow remains for creating a missing plan file)
  - `tests/KyberWeave.Tests/McpPackagingTests.cs` (add both names everywhere that file hardcodes the kyber-weave tool roster)
  - `src/KyberWeave.Mcp/Program.cs` only if the resolver factory cannot be reached from `DocumentIndexHost`
- **Skill:** `csharp-dev`.
- **Depends on:** T1.
- **Acceptance:** T2's runner command passes. `docs_validate` / `docs_drift` format text from a `DiagnosticReport` and do not write to stdout. `mergeReady` false does not run open-work or archive closeout. `mergeReady` true uses `ChangedFiles` the way `DocsValidateCommand` does today, including the C0 failure when the base cannot be resolved. A config or IO failure is `result: fail` with the diagnostic, not an exception that escapes the tool. No new `KW-*` id. No package reference added. `architect` `process.execute` remains `ask`. Architect instructions use this wording: after plan writes, obtain the two checks by calling `docs_validate` and `docs_drift`; leave `mergeReady` false while the plan is in the active plans folder; compare the provenance root to this checkout; if a tool is absent or the root differs, return `STATUS: BLOCKED` and name the gap; a shell is not a substitute and is not permission to implement. The worker's `task-gate` is run on this task's files; a failing gate returns to this worker.

### T3 — Canonical docs for the tool contract and the lattice fact

- **Objective:** Write H1–H4. Do not widen the ontology. Do not add a rule id. Do not change CLI flags.
- **Files:** `docs/docgraph/mcp-runbook.md`, `docs/docgraph/governance.md`, `docs/kyber-squad/architecture.md`, `docs/adr/0028-devin-target-scoped-authoring-capability-profiles.md`. Update `last-reviewed` on each file edited. If the ADR index row needs a pointer to the in-place amendment, edit `docs/adr/README.md` and its `last-reviewed` as well.
- **Skill:** `docs-dev`.
- **Depends on:** none. File set does not overlap T1 or T2. The wording is fixed by D6, D2, D3, D5, and H1–H4.
- **Acceptance:** T3's two docs commands exit 0. The four targets state the harvest items. ADR 0028 still grants Devin `process.execute: allow` for file creation. The worker's `task-gate` is run on this task's files; a failing gate returns to this worker.

### T4 — Closeout

- **Objective:** After the whole-change review is approved, map every decision id and H1–H4 into the Closeout mapping, archive this plan, and move the index row.
- **Files:** this plan, `docs/plans/README.md`, and the archive path `docs/archive/plans/2026-10-28-architect-docs-gates-mcp.md`.
- **Skill:** `docs-dev`.
- **Depends on:** T2, T3, and an approved whole-change review.
- **Acceptance:** Closeout mapping is complete. `docs validate . --merge-ready` and `docs drift .` exit 0. No documentation waiver is introduced.

## 6. Dependency graph

```text
T1 ──► T2 ──► T4
T3 ──────► T4
```

T1 and T3 may run together. T2 waits for T1 (RED before GREEN). T4 waits for T2, T3, and the whole-change review. No other edges. `review.max-concurrent-workers` is 4; this graph is ready for two workers at the start (T1, T3) and one afterwards.

## 7. Risks, scope, verification, review

### Risks

- A server bound to another checkout answers for the wrong tree. Mitigation: provenance line first, and the architect instructions require a root match or `STATUS: BLOCKED` (D6, D8). The existing `--expect-root` / `KYBER_WEAVE_EXPECT_ROOT` refusal (`KW-MCP-ROOT-002`) is unchanged.
- A missing or stale CodeGraph index. Mitigation: the existing critical `KW-DOC-DRIFT-001` is `result: fail`, not a pass (D6 scenario 4).
- Calling `mergeReady` true on this Draft fails `KW-DOC-LIFECYCLE-003`. Mitigation: default false, and the architect body forbids the flag while the plan is active (D6, D8).
- A char budget hides the finding the architect must fix. Mitigation: verdict outside the cut, errors before warnings, omitted count (D6).
- The tool and the CLI drift. Mitigation: one `DocsSchemaGate` and one `DocDriftLinter` (D7), pinned by T1 scenarios 2 and 4.
- A tool writes to stdout and breaks the MCP transport. Mitigation: the gate returns a report; formatting stays in `DocsTools` (D7, MCP `AGENTS.md`).

### Out of scope

- Implementing this plan in the planning session.
- Changing `process.execute` on `architect`, `architect-copilot`, or `architect-devin`.
- A new capability, a command allow-list, or a Cursor `readonly` change.
- New `KW-*` ids, new dependencies, or an ontology change.
- Replacing the CLI, changing its flags, or adding SARIF/JSON to the MCP tools.
- Giving `product-owner` a new profile. The tools are available to any client of this server; that is not a new grant of execute.
- Editing `docs/plans/2026-10-07-kyberdash-context-capture.md`.

### Verification gates

Per task, the runner in the Test contract, then that worker's `task-gate`. Before the whole-change review, the declared suite the review cites:

```bash
dotnet run --project src/KyberWeave.Cli -c Release -- review gates . --out artifacts/gates.json
```

While this plan remains under `docs/plans/`, documentation checks are `docs validate .` and `docs drift .` without `--merge-ready`. `--merge-ready` is the closeout command in T4, after the archive move.

### Review and closeout

The conductor runs `code-reviewer` over the whole change after T2 and T3, before T4. T4 is the `docs-dev` closeout. Closeout fills the mapping below, archives the plan, and does not delete this Decisions section.

## Closeout mapping

**Closeout mapping, completed 2026-10-28.** Every durable decision in Decisions and harvest item in Design harvest has a named destination in canonical documentation. The Decisions section is preserved intact.

**ADR disposition:** no ADR — ADR waiver: extends the existing DocGraph MCP server with two read-only tools; does not change the capability lattice, permission vocabulary, dependency, or schema. ADR 0028 is amended in place (H4), not replaced.

**Documentation waiver:** none for harvest items; AP1 approval provenance is transient (see AP1 row).

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D1 | [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md) — `docs_validate` and `docs_drift`, in-process with the CLI gates | extends-component | DocGraph | 2026-10-28 | archived |
| D2 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) §2 — shared `architect` keeps `process.execute: ask` | extends-component | KyberSquad | 2026-10-28 | archived |
| D3 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) §2 — keep `process.execute` at `ask`, not `deny` | extends-component | KyberSquad | 2026-10-28 | archived |
| D4 | [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md) — two tools, not one combined gate | extends-component | DocGraph | 2026-10-28 | archived |
| D5 | [adr/0028-devin-target-scoped-authoring-capability-profiles.md](../../adr/0028-devin-target-scoped-authoring-capability-profiles.md) — Copilot and Devin `process.execute: allow` unchanged | extends-component | KyberSquad | 2026-10-28 | archived |
| D6 | [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md) — tool parameters, provenance, verdict, caps | extends-component | DocGraph | 2026-10-28 | archived |
| D7 | [docgraph/governance.md](../../docgraph/governance.md) — `DocsSchemaGate`, `DocDriftLinter`; no new `KW-*` id | extends-component | DocGraph | 2026-10-28 | archived |
| D8 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) §2 — MCP tools, provenance root, `mergeReady` false while active | extends-component | KyberSquad | 2026-10-28 | archived |
| AP1 | Documentation waiver — execution approval provenance is the AP1 row in Decisions; no surviving invariant beyond that record | extends-component | DocGraph, KyberSquad | 2026-10-28 | archived |
| H1 | [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md) — `docs_validate` and `docs_drift` contracts (D1, D4, D6) | extends-component | DocGraph | 2026-10-28 | archived |
| H2 | [docgraph/governance.md](../../docgraph/governance.md) — MCP gate surface; missing index still critical `KW-DOC-DRIFT-001` (D1, D7) | extends-component | DocGraph | 2026-10-28 | archived |
| H3 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) §2 — architect checks via MCP; not a shell (D2, D3, D8) | extends-component | KyberSquad | 2026-10-28 | archived |
| H4 | [adr/0028-devin-target-scoped-authoring-capability-profiles.md](../../adr/0028-devin-target-scoped-authoring-capability-profiles.md) — gates independent of execute; Devin file-creation allow (D2, D5) | extends-component | KyberSquad | 2026-10-28 | archived |

### Documentation-impact review (DOCS_REVIEW)

Run record for T4 closeout (implementation and T3 harvest already landed; this task confirms canonical docs and archives the plan).

| Asked | Decided and why | Files changed (run) |
|---|---|---|
| How can an architect denied a general shell still obtain `docs validate` and `docs drift`? | Two read-only MCP tools calling the same in-process Core gates as the CLI (D1); shared architect stays `process.execute: ask` (D2–D3); Copilot/Devin allows unchanged (D5); no repository-path argument; provenance root; separate tools (D4, D6). Capability lattice has no command scope, so allow is a general shell; MCP stdout is JSON-RPC. | Core/MCP/CLI/squad product files listed in T2–T3 (out of closeout edit scope) |
| [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md) | **update — confirmed.** H1: `docs_validate` and `docs_drift` contracts match D6. | Harvested T3; closeout verifies |
| [docgraph/governance.md](../../docgraph/governance.md) | **update — confirmed.** H2: MCP gate surface, no new rule id; missing index still critical `KW-DOC-DRIFT-001`. | Harvested T3; closeout verifies |
| [kyber-squad/architecture.md](../../kyber-squad/architecture.md) §2 | **update — confirmed.** H3: architect checks via MCP; tools are not a shell grant. | Harvested T3; closeout verifies |
| [adr/0028-devin-target-scoped-authoring-capability-profiles.md](../../adr/0028-devin-target-scoped-authoring-capability-profiles.md) | **update — confirmed.** H4: gates independent of `process.execute`; Devin file-creation allow preserved. | Harvested T3; closeout verifies |

### Closeout evidence

- **Council:** terminal `NEEDS_HUMAN` under KW-REVIEW-008 because the diff touches `products/kyber-squad/agents/**` and `capabilities.yml`; 27/27 declared gates passed. Owner review before merge; not cleared in T4.
- **Parked for human (out of scope):** `InvalidOperationException` escaping `docs_validate`; budget truncation dropping the first finding. Do not edit `DocsTools.cs` in closeout.
- **Docs gates (T4):** `docs validate . --merge-ready` and `docs drift .` recorded in the completion digest after archive.
