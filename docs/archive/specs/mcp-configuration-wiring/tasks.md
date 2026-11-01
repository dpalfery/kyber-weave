---
id: archive/specs/mcp-configuration-wiring/tasks
title: MCP configuration wiring tasks
doc-type: spec
status: archived
component: DocGraph
owner: dpalfery
last-reviewed: 2026-11-01
documentation-impact: extends-component
impact-components:
  - DocGraph
  - Distribution
keywords:
  - MCP wiring
  - harness configuration
  - docs init
  - mcp-wire
  - wiring receipt
  - task waves
---

# Implementation Tasks

**Status:** Archived
**Archive Date:** 2026-11-01
**Phase status:** Delivered and verified — every leaf below is ticked against delivered work, not against this list. The tasks-phase approval (2026-10-31, conductor `AUTONOMOUS_APPROVAL: tasks-phase-approval`, provenance `autonomous: recommendation accepted`) is recorded as TK7, and the execution authorization as AP1, both taken at the declared scale `extends-component`, components `DocGraph` and `Distribution`. Durable content is mapped in [Closeout mapping](#closeout-mapping).
**Development mode:** test-first
**Autonomy:** autonomous

These tasks build the approved [requirements](requirements.md) (approved 2026-10-31, D15)
and [design](design.md) (approved 2026-10-31, DD17). Where a task says "per design §N",
that section is the specification. The tasks add only file names, test names, and
sequencing; no task makes a design decision.

## How every task runs

- **Test-first.** `test-dev` writes the test and records a failing run (RED) with the
  reason. Then the implementer named on the task makes it pass (GREEN) and tidies with the
  tests still green (REFACTOR). No production file is written before RED evidence exists.
- **Runner.**
  `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter-class KyberWeave.Tests.<Class>`.
  `global.json` selects Microsoft Testing Platform, so filters use `--filter-class`.
  `scripts/fast-loop.sh` is for iteration only; its output is never evidence.
- **Exit gate.** Before handing off, each implementer runs the `task-gate` skill
  (`kyber-weave review gates --agent <slug> --task <task-id>`). A failing gate returns to
  that worker.
- **File ownership.** Each task owns the files it lists. Tasks in the same wave touch
  disjoint files and run in parallel. A task that needs a file another task owns waits on
  that task.
- **Rule ids are permanent.** This feature adds `KW-MCP-WIRE-001` through
  `KW-MCP-WIRE-006`, declared as `public const string` next to the class that emits them
  (tasks 3 and 4), and documented in
  [`docs/ci-pipelines/rule-reference.md`](../../../ci-pipelines/rule-reference.md) (task 9).
  `KW-*` ids are added, never renumbered, never reused — the repository rule suppressions
  and SARIF baselines key on. `KW-MCP-ROOT-001/002` already belong to the MCP server's
  stderr startup diagnostics and are not touched.
- **Always-human paths.** Task 8 edits `.kyber-weave/kyber-weave.yml`, which this
  repository lists under `review.policy.always-human`: that change escalates for human
  review **by path alone**, before any finding is weighed, and the expected reserved-path
  `NEEDS_HUMAN` verdict is acceptable and is not a failure (D17). No task edits any
  `products/kyber-squad/**` file; a worker who finds one is needed stops and reports to
  the conductor (R7).
- **Squad untouched.** Beyond the one cross-reference sentence in task 9, nothing Squad
  changes: no renderer, receipt, install path, roster read, or `squad doctor` edit (R7,
  D18). The existing Squad contract suites must pass unchanged in task 10.
- **Platform evidence.** Unit tests run on any OS; `LaunchCommandResolver` and the
  splices take injected paths so no test depends on a real harness or a real `PATH`.

## Order and parallelism

- **Wave A** (independent, disjoint): 1, 5.
- **Wave B:** 2 needs 1. 3 needs 1. Tasks 2 and 3 touch disjoint files.
- **Wave C:** 4 needs 1, 3, and 5 — the ADR records the boundary before the write-path
  implementation lands (R8.3).
- **Wave D:** 6 needs 3 and 4. 7 needs 4. Tasks 6 and 7 touch disjoint files (6 owns
  `DocsInit*`, 7 owns the new command files and `Program.cs`).
- **Wave E:** 8 needs 2 and 6. 9 needs 6 and 7. Tasks 8 and 9 touch disjoint files.
- **Then:** 10 (whole-change review) needs 1–9; 11 (closeout) needs 10 and the owner's
  approve-and-execute authorization.

## Tasks

- [x] 1. **Declaration config model and loader** · R1.1, R1.2, R1.4, R1.5 · design §1
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`.
  - **Files:**
    - new `src/KyberWeave.Core/Configuration/McpConfig.cs`
    - new `src/KyberWeave.Core/Configuration/McpConfigLoader.cs`
    - new `src/KyberWeave.Core/Configuration/McpYamlSection.cs`
    - `src/KyberWeave.Core/Configuration/KyberWeaveConfigLoader.cs` (merges the new
      section in `FromDocument`)
    - `src/KyberWeave.Core/Configuration/KyberWeaveYamlDocument.cs` (the `Mcp` section)
  - **Work:** `RequiredMcpServer` (name, `Command` as argv, `Provenance`
    `first-party`/`third-party`, `Level` `required`/`recommended`, optional `Install`);
    `McpConfig` with the `required-servers` map; `ProductDefaults` with **no servers**, so
    an absent or empty section changes nothing (R1.4); merge by replacement, null falls
    back (the repository's configuration rule); a misspelled `provenance` or `level`
    value fails the load through the existing `KW-CONFIG-001` path (R3.6).
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/McpConfigTests.cs`, class `McpConfigTests`.
    - **Behaviours:** full section parses; `level` defaults to `required`; a misspelled
      `provenance` or `level` yields a `KW-CONFIG-001` message; an empty section and an
      absent section both yield zero servers and product defaults; the host's map replaces
      rather than merges; argv order is preserved; `install` is optional.
    - **RED reason:** the types do not exist (compile failure).
    - **GREEN:** the class passes, and the existing configuration loader tests pass
      unchanged.

- [x] 2. **Scaffold writes — and never rewrites — the declaration section** · R1.3,
  R1.5 · design §1 (DD3, D17)
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** 1.
  - **Files:**
    - new `src/KyberWeave.Core/Docs/Scaffolding/McpDeclarationBlockWriter.cs`
    - `src/KyberWeave.Core/Docs/Scaffolding/DocsScaffolder.cs` (fresh host config includes
      the section; append-when-absent; `.kyber-weave/.gitignore` gains `receipts/`)
  - **Work:** the rendered block is exactly design §1's YAML — `kyber-weave` declared,
    first-party, required; the codegraph example commented; the always-human sentence in
    the comment. An existing host config without an `mcp:` section gains it with every
    other byte unchanged; an existing `mcp:` section is never touched, with or without
    `--force` (host-owned after write). The scaffold's gitignore write adds `receipts/`
    beside `cache/` (DD13).
  - **Modernisation — implementer `test-dev`, dispatched alongside the implementation:**
    the existing fixtures in `tests/KyberWeave.Tests/DocsScaffolderTests.cs` and
    `tests/KyberWeave.Tests/DocsInitReviewGatesTests.cs` pin the **absence** of what R1.3
    adds — the exact-content gitignore pins (`"cache/\n"` and its merge results), the
    byte-identical host-config pins, the config-prefix pins, and the review-section
    extraction that runs to end of file. This step modernises those assertions in the same
    change as the writer, not after it: byte-exact pins of the old output become property
    assertions that still pin what the fixtures were protecting — the operator's own bytes
    preserved, the appended content exactly the new block (and, for the ignore file, the
    `receipts/` line joining `cache/` without regenerating it), and `Updated` reported only
    when something was appended. Tests in those two files that pin behaviour R1.3 does not
    change stay unchanged. This is the recorded TK8 correction, a deliberate change to the
    fixtures, not a weakening of the suite. It runs alongside the implementation and must
    land before this task is accepted.
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/McpDeclarationBlockWriterTests.cs`, class
      `McpDeclarationBlockWriterTests`.
    - **Behaviours:** a fresh host config contains the section declaring exactly
      `kyber-weave`; an existing config without it gains it, byte-identical outside the
      appended block; an existing config with it is unchanged, with and without `--force`;
      `.kyber-weave/.gitignore` gains `receipts/` and keeps `cache/`.
    - **RED reason:** the writer does not exist (compile failure).
    - **GREEN:** the `McpDeclarationBlockWriterTests` class passes, and the existing
      assertions in `tests/KyberWeave.Tests/DocsScaffolderTests.cs` and
      `tests/KyberWeave.Tests/DocsInitReviewGatesTests.cs` that pin the **absence** of the
      `mcp:` declaration and of `receipts/` are modernised in the same change — converted
      from byte-exact pins of the old output into property assertions that still pin the
      behaviour that matters: the operator's own bytes survive as an exact prefix of the
      written file, the appended content is exactly the rendered `mcp:` block (and, for the
      ignore file, the `receipts/` line joining `cache/` without regenerating it), and the
      scaffold outcome reports `Updated` when something was appended and `Preserved` when
      nothing was. Assertions in those two files that pin behaviour R1.3 does not change
      must still pass unchanged. The modernisation is a deliberate, recorded change (TK8),
      not a weakening: the requirement and design are untouched, and the old fixtures
      encoded the behaviour R1.3 changes.

- [x] 3. **Harness catalog, inspector, resolver, TOML scanner — the check engine**
  · R2.1–R2.8, R3.1–R3.3, R6.1 (check side) · design §2, §3, §6 (DD4, DD5, DD6, DD7)
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** 1.
  - **Files:**
    - new `src/KyberWeave.Core/Docs/Mcp/HarnessMcpCatalog.cs`
    - new `src/KyberWeave.Core/Docs/Mcp/McpWiringInspector.cs` (emits
      `KW-MCP-WIRE-001..004` as consts)
    - new `src/KyberWeave.Core/Docs/Mcp/LaunchCommandResolver.cs`
    - new `src/KyberWeave.Core/Docs/Mcp/TomlTableScanner.cs`
  - **Work:** the eight catalog rows per design §2 — repository-scope path(s), shape, key
    path, in-play signal, writability (Codex, ZCode, and the Copilot coding agent are
    snippet-only per DD18/DD19/R2.5), entry templates for the writable five. The inspector
    reads only the repository tree (R2.6), orders findings by harness (catalog order) then
    server (ordinal) for determinism (R2.8), and reports `NotWired`, `CommandDiffers`,
    `BinaryAbsent`, `NotDetectable` with the design's severities (`required` warns,
    `recommended` informs). `LaunchCommandResolver` resolves a command on `PATH` (and
    `PATHEXT` on Windows) with injectable search paths — a file-existence check, no
    process launch, no Squad dependency. `TomlTableScanner` is string-region-aware,
    classifies `[mcp_servers.<name>]` headers, and resolves every uncertainty toward
    refusal (DD10). ZCode's project-tier walk mirrors the verified
    `ZCodeMcpConfiguration` semantics (working directory to worktree root, `zcode.json`
    and `.zcode/config.json`, `mcp.servers`) implemented standalone — the user tier is
    never read.
  - **Test contract:**
    - **New files:** `tests/KyberWeave.Tests/McpWiringInspectorTests.cs`,
      `tests/KyberWeave.Tests/TomlTableScannerTests.cs`.
    - **Behaviours:** per shape — wired, not wired, command differs (both values named),
      binary absent via injected `PATH`; undetectable harness reports `info` naming its
      marker; Claude Code with no `.mcp.json` reports `info` and never a warning; an empty
      or absent declaration produces no findings; the same tree yields the same report
      twice; `recommended` informs; ZCode: a sentinel file placed at the user-tier path is
      never opened; Codex: a header inside a `"""` region is not found, an unbalanced
      delimiter classifies as refusal.
    - **RED reason:** the types do not exist (compile failure).
    - **GREEN:** both classes pass; no `src/KyberWeave.Core/Squad/**` file changed.

- [x] 4. **JSON splice, repairer, receipt — the bounded writer** · R5.4–R5.11, R6.2, R6.3
  · design §5, §6 (DD9, DD10, DD11, DD12, DD13)
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** 1, 3, 5.
  - **Files:**
    - new `src/KyberWeave.Core/Docs/Mcp/JsonServerSplice.cs`
    - new `src/KyberWeave.Core/Docs/Mcp/McpWiringRepairer.cs` (emits `KW-MCP-WIRE-005`)
    - new `src/KyberWeave.Core/Docs/Mcp/McpWiringReceipt.cs` (emits `KW-MCP-WIRE-006`)
  - **Work:** the splice computes the **full plan before the first byte is written**
    (no partial writes); `JsonServerSplice` reads leniently (comments, trailing commas)
    and locates byte offsets — existing-name refusal, empty/non-empty/absent container
    cases, BOM preserved (DD9). The repairer respects catalog writability: a requested
    snippet-only harness is refused non-writable with its snippet (DD11, DD12, DD18,
    DD19); a server whose binary does not resolve is refused for every harness (R6.2);
    the first-party binary absent points at `kyber-weave update` (R6.3). A requested
    target is written whether or not its harness is in play: the explicit selection is the
    consent, and an absent target file is created (R5.4, D19) — for Claude Code and
    opencode, whose only repository-scope artefact is the file itself, "not in play" and
    "file absent" are the same condition, and creation is the outcome. The receipt lives
    at `.kyber-weave/receipts/mcp-wiring.json` (version 1, one record per file, SHA-256
    pre/post digests, mode created/inserted); `mcp-unwire` restores `preSha256` content or
    deletes a `created` file, only when the current digest equals `postSha256` — any
    mismatch is drift and refuses (DD13).
  - **Test contract:**
    - **New files:** `tests/KyberWeave.Tests/JsonServerSpliceTests.cs`,
      `tests/KyberWeave.Tests/McpWiringRepairerTests.cs`,
      `tests/KyberWeave.Tests/McpWiringReceiptTests.cs`.
    - **Behaviours:** byte preservation outside the insert (byte-compare), comments and
      BOM preserved; empty, non-empty, and absent container splices; existing server name
      → `KW-MCP-WIRE-005` and zero bytes written; malformed or foreign file → `005`;
      absent binary → server refused for every harness; dry-run writes nothing, not even
      the receipt, and reports the same outcome the real run would; the receipt records
      mode and both digests; unwire restores `inserted` and deletes `created`; drift →
      `KW-MCP-WIRE-006` and no restore; a snippet-only harness requested → non-writable
      refusal with snippet; a requested harness with no target file is created even though
      it is not in play — a named claude-code with no `.mcp.json` and a named opencode
      with no `opencode.json` get their canonical file (R5.4, D19).
    - **RED reason:** the types do not exist (compile failure).
    - **GREEN:** all three classes pass.

- [x] 5. **ADR 0051 — the ownership boundary** · R8.1–R8.3 · design §8 (D16, DD15)
  - **Implementer:** `docs-dev`. **No-test task**; replacement validation below.
  **Depends on:** none (Wave A); **predecessor of 4** — the boundary is recorded before the
  write-path implementation lands, and before the shipped remedy writes any file outside
  this repository (R8.3).
  - **Files:**
    - new `docs/adr/0051-docs-branch-writes-repository-scope-mcp-configuration.md`
    - `docs/adr/README.md` (index row)
  - **Work:** the ADR records the D16 decision — the docs-branch repair may write
    repository-scope harness MCP configuration under the R5 bounds; the
    repository-scope/user-scope distinction ADR 0026 never draws, with user-scope never
    written or read; ADR 0026 intact for Kyber Utilities; D-no-settings intact for Squad;
    the rejected alternatives (snippet-only remedy, extending Squad or Kyber Utilities, a
    general harness-settings writer). Cross-references ADR 0026 and
    `docs/kyber-squad/architecture.md`.
  - **Validation:** `docs validate .` and `docs drift .` exit 0 (no `--merge-ready`, this
    specification active); the ADR is in the corpus and names every element above.

- [x] 6. **`docs init`: the check step, `--no-mcp-check`, and the terminal-gated prompt**
  · R2.1, R3.3–R3.6, R4.1–R4.8 · design §3, §4 (DD7, DD8)
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** 3, 4.
  - **Files:**
    - `src/KyberWeave.Cli/Commands/Docs/DocsInitCommand.cs`
    - `src/KyberWeave.Cli/Commands/Docs/DocsInitSettings.cs` (`--no-mcp-check`)
  - **Work:** after the skill deploy and retired-skill warnings, before the `Next:` block:
    run the inspector and print the section unfolded (`WriteUnfolded`), with each
    finding's rule id and the copy-ready remedy command. `--no-mcp-check` skips the check,
    the prompt, and any repair, and says so. No declaration → the section prints nothing.
    The exit code never moves off 0 when the scaffold succeeded (R3.4). The prompt is two
    injectable seams: `InteractiveCheckOverride` (default
    `!Console.IsInputRedirected && !Console.IsOutputRedirected`) and `ConfirmFixOverride`
    (default `AnsiConsole.Confirm`, naming every file and server it would write).
    Non-interactive → no prompt, no write, the printed manual command stands (R4.4).
    Confirm → the same repair path as `docs mcp-wire`, reporting each write and the
    receipt (R4.2). Decline → nothing (R4.3). The prompt is unreachable on a failed
    scaffold (R4.7). `docs init` gains no consent flag (R4.5).
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/DocsInitMcpCheckTests.cs`, class
      `DocsInitMcpCheckTests`.
    - **Behaviours:** findings print with ids and the remedy command; exit 0 with
      findings; `--no-mcp-check` prints its one line and no findings; seam-driven:
      interactive `false` → no prompt and nothing written; interactive `true` +
      confirm `true` → fixture file written and receipt recorded; confirm `false` →
      nothing written; a failed scaffold never reaches the prompt; an all-wired tree
      prints the `ok` confirmation line (R3.5); output survives a redirected-width
      console (unfolded).
    - **RED reason:** no check step exists (no output, no seams, no flag).
    - **GREEN:** the class passes, and the existing `DocsInit` tests pass unchanged.

- [x] 7. **`docs mcp-wire` and `docs mcp-unwire`** · R5.1–R5.3, R5.10, R6.2 · design §5
  (DD1, DD14)
  - **Implementer:** `csharp-dev`. **Tests:** `test-dev`. **Depends on:** 4.
  - **Files:**
    - new `src/KyberWeave.Cli/Commands/Docs/DocsMcpWireSettings.cs`
    - new `src/KyberWeave.Cli/Commands/Docs/DocsMcpWireCommand.cs`
    - new `src/KyberWeave.Cli/Commands/Docs/DocsMcpUnwireCommand.cs`
    - `src/KyberWeave.Cli/Program.cs` (docs-branch registration, descriptions, examples)
  - **Work:** the two verbs per design §5 — `[path]` default `.`, `--target` comma list,
    `--dry-run` on wire. No target → list harnesses in play and fixable gaps, name each
    writable harness no marker can detect, write nothing, exit 0. A requested target
    refused (malformed, existing entry, non-writable, absent binary) → exit 1 naming the
    file and reason; a requested target with nothing to write — its file already holds
    every declared server — is reported and skipped, not an error. A named harness is
    written whether or not it is in play, its absent target file created (R5.4, D19).
    `--dry-run` writes nothing including the receipt and exits what the real run would.
    Unwire restores per receipt
    with drift refusal per task 4. Human output through `AnsiConsole` with `Markup.Escape`
    on every interpolated path; configuration failures through the `KW-CONFIG-001` path.
  - **Test contract:**
    - **New file:** `tests/KyberWeave.Tests/DocsMcpWireCommandTests.cs`, class
      `DocsMcpWireCommandTests`.
    - **Behaviours:** no-target preview writes nothing and exits 0; a targeted write
      produces the file and the receipt; a refusal exits 1 with the rule id; dry-run writes
      nothing (no receipt) and mirrors the real run's exit; unwire restores; unwire on
      drift exits 1; a requested snippet-only harness exits 1 with the snippet; a
      requested harness whose file already holds every declared entry is reported and
      skipped with exit 0; a requested harness whose file is absent is created and exits 0
      — claude-code and opencode included, where file absence is the not-in-play condition
      (R5.4, D19); the no-target preview names each writable harness no marker can detect.
    - **RED reason:** the verbs are not registered.
    - **GREEN:** the class passes; the docs branch remains symmetric
      (one `WithDescription`, at least one `WithExample` per verb).

- [x] 8. **Dogfood: the declaration in this repository** · R1.3, R1.6 · design §1 (D17)
  - **Implementer:** `csharp-dev`. **No-test task**; replacement validation below.
  **Depends on:** 2, 6.
  - **Files:** `.kyber-weave/kyber-weave.yml` (only).
  - **Work:** run the built `kyber-weave docs init .` on this repository and verify it
    appended the `mcp:` section (declaring `kyber-weave`, first-party, required) leaving
    every other byte unchanged, and that the check section prints for this repository's
    harnesses in play. This is the feature's own host adoption, and it exercises the
    append-when-absent path on a real host configuration.
  - **Always-human:** this repository lists `.kyber-weave/kyber-weave.yml` under
    `review.policy.always-human`, so this change escalates for human review **by path
    alone**; the expected reserved-path `NEEDS_HUMAN` verdict is acceptable and is not a
    failure.
  - **Validation:** `docs validate .` and `docs drift .` exit 0 after the edit; `git diff`
    shows only the appended block; a `docs init .` re-run is a no-op for the section.

- [x] 9. **Documentation harvest** · R9.1–R9.5, H1–H7 · design §9
  - **Implementer:** `docs-dev`. **No-test task**; replacement validation below.
  **Depends on:** 6, 7 (the documented surface — flags, exit codes, prompt contract — is
  final).
  - **Files:**
    - `docs/docgraph/mcp-runbook.md` — the ZCode row in the wiring table (file paths and
      `mcp.servers` cited from the verified source, entry body marked unpinned per DD18)
      and a "Checking and wiring" section: the `docs init` check, `--no-mcp-check`, the
      prompt's non-interactive contract, both verbs with flags and exit codes, the receipt
      and its location, the snippet-only boundary for Codex, ZCode, and the Copilot coding
      agent with each one's declared widening path and unblocking source (DD18, DD19)
    - `docs/docgraph/onboarding.md` — the check among `docs init`'s steps; the
      always-human consequence of editing the declaration (R1.6)
    - `docs/cli-command-surface.md` — both verbs, `docs init`'s check step and
      `--no-mcp-check`, the exit-code table
    - `docs/configuration.md` — the `mcp.required-servers` schema
    - `docs/ci-pipelines/rule-reference.md` — the six `KW-MCP-WIRE-*` ids with severities
      and hints
    - `docs/kyber-squad/architecture.md` — one sentence naming the split (H7): Squad owns
      droid-level visibility, the docs branch owns harness-level wiring, Squad writes
      nothing
    - `docs/install.md` — one line on what an adopting repository's `docs init` now
      checks and offers
    - `last-reviewed` bumped on every file edited
  - **Validation:** `docs validate .` and `docs drift .` exit 0; every harvest item H1–H7
    lands at its declared target with the design's wording; no page claims Squad installs
    or owns harness MCP configuration (R9.5).

- [x] 10. **Whole-change review (conductor-run)** · R7, R10.2
  - **Not a worker task.** The conductor runs the declared gate suite
    (`dotnet run --project src/KyberWeave.Cli -c Release -- review gates . --out
    artifacts/gates.json`) and the council over the whole change. **Depends on:** 1–9.
  - **Acceptance:** all gates pass; the diff contains no `products/kyber-squad/**` file;
    the existing Squad contract suites pass unchanged (R7.1–R7.4 verified by absence and a
    green suite); the `KW-MCP-WIRE-*` ids match `rule-reference.md`; task 8's
    `.kyber-weave/kyber-weave.yml` change carries its expected reserved-path human review.
  - **Test contract (verification, not new tests):** the declared suite above is the
    evidence a review cites.

- [x] 11. **Specification closeout** · final task
  - **Implementer:** `docs-dev`, assigned through the conductor. **Depends on:** 10 and
    the owner's approve-and-execute authorization.
  - **Files:** this specification's three files,
    `docs/specs/README.md` (row moves to Archived with the archive date),
    the archive path `docs/archive/specs/mcp-configuration-wiring/`.
  - **Work:** verify every requirement against the implementation and review evidence;
    fill the Closeout mapping in each artifact, mapping the decision ids (requirements
    D15–D19, design DD17–DD20, tasks TK1–TK8 and the execution approval AP1 — every row
    this artifact's Decisions table holds at closeout) and harvest items H1–H7 to their
    destinations with the ADR disposition (ADR 0051, task 5); move the specification to
    the archive; update the index row. Live acceptance — a real harness session reaching
    the server after the repair — stays the owner's check and remains
    `[NOT LIVE-VERIFIED]` in the delivery notes until performed (design DG5).
  - **Validation:** after the archive move, `docs validate . --merge-ready` and
    `docs drift .` both exit 0 (KW-DOC-LIFECYCLE-003 clears only when no specification
    remains active).

## Requirement traceability

| Requirement | Tasks |
|---|---|
| R1 (declaration) | 1 (schema, defaults), 2 (scaffold), 8 (dogfood + always-human), 9 (docs) |
| R2 (detection surface) | 3 (engine), 6 (docs init step, opt-out) |
| R3 (report, exit codes) | 3 (ids, severities), 6 (output, unchanged exit) |
| R4 (prompt, non-interactive) | 6 |
| R5 (repair command, write bounds) | 4 (splice, receipt, refusals), 7 (verbs, exits) |
| R6 (absent third-party binary) | 3 (check side), 4 (repair refusals), 9 (docs) |
| R7 (Squad untouched) | 10 (review verifies), 9 (one cross-reference sentence) |
| R8 (ADR boundary) | 5 (predecessor of 4) |
| R9 (documentation) | 9 |
| R10 (testability) | 1–7 (RED/GREEN per task), 10 (declared suite), 11 (live note) |

## Design harvest

The authoritative harvest table is [design.md's](design.md#design-harvest) (H1–H7, each
with its target component and doc type); task 9 lands every row at its declared target.
The design changed no target after approval, so the table was carried forward unrefined.
The ids are repeated here so this artifact's own closeout stands alone:

| ID | Design item | Target component and doc type | Landed by |
|---|---|---|---|
| H1 | The per-harness shape catalog: files, keys, in-play markers, writability, substitution semantics; ZCode cited from `ZCodeMcpConfiguration.cs`; ZCode row added to the runbook | DocGraph · runbook | Task 9 |
| H2 | The `mcp.required-servers` declaration schema, scaffold default, and the always-human escalation consequence | DocGraph · reference | Task 9 |
| H3 | The `docs init` check step: `KW-MCP-WIRE-001..004` ids, severities, unchanged exit code, `--no-mcp-check` | DocGraph · CI Pipelines · onboarding and reference | Task 9 |
| H4 | The prompt and non-interactive contract: terminal-gated, decline-on-redirect, no consent flag, satisfiable inside `docs init` alone | DocGraph · reference | Task 9 |
| H5 | `docs mcp-wire` / `docs mcp-unwire`: target selection, insertion without re-serialization, receipt and drift-refusing removal, dry run, exit codes | DocGraph · runbook and reference | Task 9 |
| H6 | ADR 0051 — the D16 repository-scope write exception versus ADR 0026 | DocGraph · adr | Task 5 |
| H7 | The docs-init / Squad split: Squad owns droid-level visibility, the docs branch owns harness-level wiring, Squad writes nothing | KyberSquad · architecture | Task 9 |

## Decisions

The specification's decision lineage lives across the three artifacts — requirements.md
holds D1–D19 (the owner's four decisions as D1–D4, the requirements-phase decisions, the
conductor's approvals as D15–D18, and the mid-execution conflict settlement as D19) and
design.md holds DD1–DD20 (the design decisions,
approved as DD17, with DG3 and DG4 decided as DD18 and DD19, and the ADR-number correction
recorded as DD20). Both tables are permanent and intact. The tasks-phase decisions are
appended here:

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| TK1 | How are the tasks sequenced, and what runs in parallel? | Five waves by file ownership: A: 1, 5. B: 2, 3. C: 4. D: 6, 7. E: 8, 9. Then review (10) and closeout (11). Same-wave tasks touch disjoint files. | Mirrors the archived ship-gate-catalogue task structure; every dependency edge is stated in "Order and parallelism". | autonomous: recommendation accepted |
| TK2 | When does ADR 0051 land relative to the write path? | Task 5 precedes task 4: the recorded boundary lands before any code that writes a harness file, satisfying R8.3's "before the repair command writes its first file outside this repository". | The conductor's instruction makes the ADR a predecessor of any task that writes outside this repository; task 4 implements that writer. The number is 0051 by design.md's DD20, renumbered from 0050 after PR #489 landed that number on `main` for the universal grant while this specification executed. | autonomous: recommendation accepted |
| TK3 | Does any task edit `.kyber-weave/kyber-weave.yml`, given the always-human policy? | Yes — task 8, deliberately: the dogfood run appends this repository's own declaration. The change escalates for human review by path alone under `review.policy.always-human`; the expected `NEEDS_HUMAN` verdict is acceptable and is not a failure. | D17 accepted the escalation consequence; task 8 is also the one real-host exercise of the append-when-absent path. No other task touches the file. | autonomous: recommendation accepted |
| TK4 | How is the prompt's terminal-gating made testable? | Two injectable internal seams on `DocsInitCommand`: `InteractiveCheckOverride` (default: stdin and stdout both not redirected) and `ConfirmFixOverride` (default: `AnsiConsole.Confirm`). Tests inject both; the shipped default is the design's §4 contract. | Design named `ConfirmFixOverride`; the tasks add the interactive-check seam because `Console.IsInputRedirected` cannot be driven from a test process, and the non-interactive behaviour (R4.4) is a contract that must be tested, not assumed. | autonomous: low-confidence, no authoritative source |
| TK5 | Is the DG3/DG4 pinning work scheduled as tasks? | No. ZCode's entry body and Codex's repository-scope semantics are snippet-only in v1 by decision (DD18, DD19); the pinning work is each row's declared widening path, not v1 scope. No `docs/todo` entry is created: deferred work enters the todo index only when the user accepts it (repository rule). | The conductor approved both decisions as deliberate, temporary narrowing with named unblocking sources; scheduling them would exceed the approved design. | autonomous: recommendation accepted |
| TK6 | Is live acceptance automated? | No. A real harness session reaching the server after the repair stays the owner's live check (design DG5); delivery notes remain `[NOT LIVE-VERIFIED]` until it is performed. Task 11 carries the note into closeout. | Consistent with prior renderer plans; the deterministic tests (R10.1) cover everything a fixture can. | autonomous: recommendation accepted |
| TK7 | Are these tasks approved? | Yes: tasks phase approved; with it, all three phases are approved and the specification is Ready. | Conductor gate `AUTONOMOUS_APPROVAL: tasks-phase-approval` (2026-10-31). Gate evidence, from the conductor's run against this checkout (2026-10-31): `docs_status` reported `provenance: root=/Users/dave/git/personal/factory/kyber-weave rev=8943b6a dirty=yes documents=142`, a root identical to `git rev-parse --show-toplevel` for this checkout (branch `feat/squad-response-format`, HEAD `8943b6a`), so the provenance root matches; `docs validate .` (no `--merge-ready`, this specification active) — `result: pass`, 0 errors, 0 warnings; `docs drift .` — `result: pass`, 0 errors, 0 warnings. TG1 is closed by this evidence. | autonomous: recommendation accepted |
| AP1 | Approve and execute this specification at the declared scale? | Approved. Execute at the declared scale `extends-component`, components `DocGraph` and `Distribution`. The conductor dispatches the tasks; this recording authorizes execution and does not start it. | Consolidated conductor gate `AUTONOMOUS_APPROVAL: approve-and-execute` (2026-10-31), presented together with the tasks approval (TK7) because the tasks digest reported `GAPS: none` and `OPEN_QUESTIONS: none`. Readiness verified: all three phases approved (requirements D15, design DD17, tasks TK7); this artifact records `development-mode: test-first` and its Test contract; the always-human consequence on `.kyber-weave/kyber-weave.yml` (task 8) and the rule-id permanence rule (`KW-MCP-WIRE-001..006`, added, never renumbered or reused) are recorded in the task preamble; the declared scale and the harvest H1–H7 with their target components are present and consistent across the artifacts. | autonomous: recommendation accepted |
| TK8 | Task 2's GREEN line demanded the existing `DocsScaffolder` tests pass unchanged, while R1.3 requires the scaffold to write exactly what those tests pin the absence of — which governs? | The requirement governs; the acceptance wording was stale. The criterion now requires the `McpDeclarationBlockWriterTests` class to pass **and** the modernisation, in the same change, of the existing assertions in `DocsScaffolderTests.cs`, `DocsInitReviewGatesTests.cs`, and `MultipleDocsRootTests.cs` that pin the absence of the `mcp:` declaration and of `receipts/` — byte-exact pins of the old output become property assertions (the operator's own bytes preserved as an exact prefix, the appended content exactly the new block, `Updated` reported when something was appended). An explicit `test-dev` step inside task 2 performs it alongside the implementation, before the task is accepted. The three-file list is the complete verified set, not a provisional one: a whole-test-tree sweep for `.gitignore` text pins, for byte-equality pins against the pre-scaffold configuration, and for any assertion that the scaffold output does not contain `mcp:` found no fourth file, so the modernisation's scope is closed. | Nothing is weakened and no requirement moves: R1.3 (a fresh host configuration gains the `mcp:` declaration) and DD13 (`.kyber-weave/.gitignore` gains `receipts/` beside `cache/`) are unchanged and deliberate — the old fixtures encoded the behaviour the requirement changes. The modernisation preserves exactly what those fixtures were protecting — the operator's own bytes, the exact appended content, the honest `Updated`/`Preserved` outcome — while tests pinning behaviour R1.3 does not change still pass unchanged. The task 2 RED test-dev worker found the contradiction and escalated rather than weakening its own tests; the pinned assertions were verified by reading them (`FreshInitCreatesOnlyTheNarrowCacheIgnoreAndNoEmptyGlossary`, the cache-ignore merge and encoding-prefix pins, `ForceDoesNotOverwriteTheHostConfig`, `AnUnchangedDocsRootLeavesTheConfigByteForByteIdentical`, `AnExistingReviewSectionSurvivesDocsInitUnchanged`, and the review-section-to-EOF extraction behind `DocsInitWritesTheApprovedReviewBlock`). A third file, `MultipleDocsRootTests.cs`, carries the same byte pins — three assertions holding a multi-root host configuration byte-for-byte identical after a scaffold — and is added to the correction's scope; the whole-test-tree sweep found no fourth file, so the three-file list is complete and the scope is closed. The modernisation has landed: 16 assertions converted across the three files, none deleted, two outcome pins inverted rather than removed, four fixtures renamed where the old name would have become false, and the full suite green — 4638 tests, 0 failed, 27 skipped. | autonomous: recommendation accepted (conductor-directed correction, 2026-10-31; conductor-directed scope and outcome amendment, 2026-10-31) |

## Closeout mapping

Complete as of 2026-11-01. The stub that stood here named "D15–D19, DD17–DD20, TK1–TK8,
AP1"; this artifact's own table holds **TK1–TK8 and AP1**, so those are mapped here, and the
requirements' D1–D19 and design's DD1–DD20 are mapped in their own artifacts (the closeout
range was corrected twice during execution, from TK1–TK6 to TK1–TK8 plus AP1, so the table
as it stands is what governs). ADR disposition: **ADR 0051**, task 5 — no ADR waiver.

| ID | Canonical destination |
|---|---|
| TK1 | Documentation waiver — delivery sequencing is a plan-time arrangement with no surviving architectural, operational, or behavioural invariant; the ordering it imposed is realised in the landed commits and in TK2's row. |
| TK2 | [ADR 0051](../../../adr/0051-docs-branch-writes-repository-scope-mcp-configuration.md) — the boundary record landed before the write path, which is the ordering R8.3 fixes |
| TK3 | [DocGraph onboarding](../../../docgraph/onboarding.md#mcp-wiring) — the always-human consequence of the dogfood edit, stated for the operator |
| TK4 | [MCP runbook: the prompt and no terminal](../../../docgraph/mcp-runbook.md#the-prompt-and-what-happens-without-a-terminal) — the tested contract the seams exist to pin |
| TK5 | [MCP runbook: what the repair writes](../../../docgraph/mcp-runbook.md#what-the-repair-writes-and-what-it-will-not) — each snippet-only harness and its declared widening path |
| TK6 | [MCP runbook: the check](../../../docgraph/mcp-runbook.md#the-check) — what the check proves, and the live step it cannot perform |
| TK7, AP1 | Documentation waiver — the tasks-phase approval and the execution authorization are historical records, not surviving invariants; both are preserved permanently in this document's Decisions table. |
| TK8 | [Configuration: MCP servers](../../../configuration.md#mcp-servers) — the append-only behaviour the modernised fixtures now pin as properties |
| H1 | [MCP runbook](../../../docgraph/mcp-runbook.md) — the shape catalog, the ZCode row, and the writable/snippet-only table |
| H2 | [Configuration: MCP servers](../../../configuration.md#mcp-servers), [DocGraph onboarding](../../../docgraph/onboarding.md#mcp-wiring) |
| H3 | [Install](../../../install.md) — what an adopting repository's `docs init` now checks and offers; [DocGraph onboarding](../../../docgraph/onboarding.md#mcp-wiring), [CLI command surface](../../../cli-command-surface.md), [Rule reference](../../../ci-pipelines/rule-reference.md) |
| H4 | [MCP runbook: the prompt and no terminal](../../../docgraph/mcp-runbook.md#the-prompt-and-what-happens-without-a-terminal), [CLI command surface](../../../cli-command-surface.md) |
| H5 | [MCP runbook: `docs mcp-wire`](../../../docgraph/mcp-runbook.md#docs-mcp-wire), [MCP runbook: `docs mcp-unwire`](../../../docgraph/mcp-runbook.md#docs-mcp-unwire), [CLI command surface](../../../cli-command-surface.md) |
| H6 | [ADR 0051](../../../adr/0051-docs-branch-writes-repository-scope-mcp-configuration.md), [ADR index](../../../adr/README.md) |
| H7 | [Kyber-Squad architecture](../../../kyber-squad/architecture.md) |

## Gaps carried forward

- **TG1 (closed 2026-10-31):** `docs validate .` and `docs drift .` could not be run by the
  authoring session — the harness has neither the kyber-weave docs MCP tools nor a shell
  (the same limitation recorded as G1 in requirements and DG1 in design, both closed by
  the conductor's runs). **Closed by the conductor's run against this checkout
  (2026-10-31):** `docs_status` reported
  `provenance: root=/Users/dave/git/personal/factory/kyber-weave rev=8943b6a dirty=yes documents=142`,
  a root identical to `git rev-parse --show-toplevel` for this checkout (branch
  `feat/squad-response-format`, HEAD `8943b6a`), so the provenance root matches;
  `docs validate .` (no `--merge-ready`, this specification active) — `result: pass`,
  0 errors, 0 warnings; `docs drift .` — `result: pass`, 0 errors, 0 warnings. The
  results stand for this checkout and are the gate evidence recorded in TK7.
- **TG2 (updated at closeout, 2026-11-01 — still open):** Live acceptance (design DG5) remains
  the owner's check, and delivery notes remain `[NOT LIVE-VERIFIED]`. What the deterministic
  suite covers is every behaviour a fixture can drive (R10.1); what no test in this bundle
  covers is a real harness session that, after the repair, lists the declared server's tools —
  the check reads configuration bytes and never starts a harness, which is now stated to the
  operator in [the runbook](../../../docgraph/mcp-runbook.md#the-check). The row closes when
  the owner records that observation, not when the suite is green.
- **TG4 (closed 2026-11-01): one `DocsApp` builder copied three times.** Accepted as Minor by the
  council's re-review: `tests/KyberWeave.Tests/DocsInitMcpCheckTests.cs` adds a third copy of the
  six-line `DocsApp` command-app builder, identical to the copies in `DocsInitReviewGatesTests.cs`
  and `GateCatalogueToolchainTests.cs`. The copies can diverge, so a `docs init` registration
  defect could pass one suite and fail another. The suggestion on record is to move `DocsApp` into
  `McpWiringFixture` and have all three suites call it. No `docs/todo` entry is created — a review
  finding does not by itself authorize a repository change. **Closed by the owner's decision to
  fix it, executed on branch `feat/mcp-configuration-wiring` (2026-11-01):** the six copies — five
  already in the test project, and the sixth this specification added — are now one,
  `tests/KyberWeave.Tests/CommandAppFixture.cs`, which registers the `docs` branch and its `init`
  command once for the whole test project and offers four named compositions for the four shapes
  the suites need (`DocsInit`, `ReviewGates`, `ReviewCatalogue`, `DocsInitWithReviewGates`). Two
  further copies the finding had not named turned up and were verbatim copies of the strict
  review-gates app, so the strict cluster is one implementation with three presets rather than
  four copies; no assertion changed and no test was deleted. Commit `a3263a91`. No `docs/todo`
  entry follows the decision either: the owner asked for the fix, so the work is done rather than
  tracked.
- **TG5 (closed 2026-11-01): the atomic-write idiom has a third copy.** Accepted as Minor:
  `src/KyberWeave.Core/Docs/Mcp/McpWiringRepairer.cs` is a third implementation of the
  temp-file-then-rename idiom that `ManagedGlossaryService.AtomicWrite` and
  `StatusLineReceiptStore.Write` already provide — and the three already disagree, the receipt
  store using a predictable `<path>.tmp` with no cleanup. A durability fix landing in one copy
  leaves the others behind. **Closed by the owner's decision to fix it, executed on branch
  `feat/mcp-configuration-wiring` (2026-11-01):** the four copies — the three this finding names
  and the docs review export's own — are now one,
  `src/KyberWeave.Core/FileSystem/AtomicFile.cs`, used by the MCP repair, the glossary service,
  the status-line receipt store and the docs review export. The disagreements the finding named
  are resolved with the copies: the receipt store no longer writes a predictable `<path>.tmp`,
  and the cleanup is guarded so a failure to sweep cannot replace the reason the write failed.
  Squad's eight near-copies are left alone — they gate a disk flush on a global test toggle,
  take an exclusive create, and name their temporaries without the leading dot, so consolidating
  them is a change of its own, and this specification's Squad-untouched bound (R7) is why that
  change is not this one. Commit `46c6240b`.
- **TG6 (observed at closeout, carried for the owner's decision, 2026-11-01):
  `KW-MCP-WIRE-002` cannot separate "wired, differently spelled" from "wired wrong".** The
  finding compares the declared launch command with the wired one as text, allowing exactly one
  substitution: the declaration's `.` spelled as that harness's own repository-root token. A
  wired entry that names the binary by **absolute path** — or by a path the harness expands,
  such as `${userHome}/.local/bin/kyber-weave-mcp` — is therefore still reported as a
  difference even though it launches. This repository's own `.cursor/mcp.json` is in that
  state, so `docs init` here reports a `KW-MCP-WIRE-002` for Cursor. The finding is accurate and
  the repair correctly refuses to touch an existing entry, so nothing is broken — but an
  operator cannot distinguish the two cases, and the remedy line asks for a by-hand edit.
- **TG3:** The DG3/DG4 widening work (ZCode entry body; Codex relative-path semantics) is
  deliberately not scheduled (TK5); each is a catalog flip plus a runbook row once its
  named source is consulted.
