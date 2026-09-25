---
id: plans/agent-spec-broken-reference-rule
title: Implement KW-AGENT-SPEC-004 (broken file reference) check
doc-type: plan
status: Draft
development-mode: test-first
component: ContextHygiene
owner: dpalfery
created: 2026-09-24
last-reviewed: 2026-09-24
---

# Implement KW-AGENT-SPEC-004 (broken file reference) check

## Goal

Implement the `KW-AGENT-SPEC-004` validation rule that is already documented in [rule-reference.md](../ci-pipelines/rule-reference.md) but never emitted. `AgentSpecValidator.Validate` must verify that file references inside an agent's instruction body and description resolve to existing files, and raise `KW-AGENT-SPEC-004` when they do not. In test-first mode with a full rule-id audit of all `KW-AGENT-SPEC-*` and `KW-AGENT-SEC-*` ids to ensure every rule is actually emitted.

## Context

**Finding source**: [todo/agent-spec-broken-reference-rule.md](../todo/agent-spec-broken-reference-rule.md), identified 2026-08-22.

**Current state** (from worktree):
- `AgentSpecValidator` class: [src/KyberWeave.Core/Agents/Validation/AgentSpecValidator.cs](../../src/KyberWeave.Core/Agents/Validation/AgentSpecValidator.cs:9)
- Currently checks:
  - KW-AGENT-SPEC-001: Missing name
  - KW-AGENT-SPEC-002: Missing description
  - KW-AGENT-SPEC-003: Missing instructions
- Does NOT check: KW-AGENT-SPEC-004 (broken file reference)
- Rule is documented at: [docs/ci-pipelines/rule-reference.md](../ci-pipelines/rule-reference.md:125)

**Precedent for reference extraction**: `SkillParser.ExtractReferenceLinks` in [src/KyberWeave.Core/Skills/Parsing/SkillParser.cs](../../src/KyberWeave.Core/Skills/Parsing/SkillParser.cs:125) shows the pattern already established in the codebase:
- Extracts Markdown `LinkInline` elements from document AST
- Extracts inline path patterns using regex `@"(?<![A-Za-z0-9._\-/])(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)"`
- Normalizes paths (strips `./` prefix)
- Resolves relative to skill directory
- Validates existence on disk
- Skips URLs, anchors (#), mailto:, and paths containing `..`

## Definition of "file reference"

A file reference in an agent's instruction body or description is:

1. **Markdown links**: `[text](path/to/file)` — extracted from `LinkInline` AST nodes via Markdig parser
2. **Inline backtick paths**: `` `relative/path/to/file` `` — matching the inline path regex (to be scoped for agents)
3. **Excluded patterns** (treated as non-references):
   - HTTP/HTTPS URLs: `http://`, `https://`
   - Anchor-only links: starting with `#`
   - Mailto links: `mailto:`
   - Path traversal attempts: containing `..`
   - Config Reg tokens: `<property-name>` (e.g., `<docs-root>`, `<plan-index>`)
   - Absolute filesystem paths: `/absolute/path` (not resolved, warnings deferred to user convention)

4. **Resolution**: Relative to `AgentModel.DirectoryPath` (the folder containing the agent definition file). Paths beginning with `./` are normalized to strip the prefix before resolution.

5. **Decision D1** (below): Scope of inline patterns — whether to extract arbitrary bare paths or restrict to conventional subdirectories like skill parser does (scripts/, references/, assets/).

## Severity and hint text

- **Rule id**: `KW-AGENT-SPEC-004`
- **Severity**: Error (from [rule-reference.md](../ci-pipelines/rule-reference.md:125): "Broken file reference")
- **Hint text** (observable if nearest match is computable): Offer the nearest existing file/directory as a candidate suggestion, e.g.:
  - `"File not found: 'references/guide.md'. Nearest match: 'references/guides.md' in the agent's directory."`
  - Fallback (no candidates): `"Relative file path does not exist. Check the path spelling relative to the agent definition directory."`

## Decisions

**Q1: Inline path extraction scope** | user choice affecting spec
- **A) Restrict to conventional subdirectories** (recommended) — Extract patterns like `scripts/script.sh`, `references/doc.md`, `assets/image.png`. Narrower, matches skill parser precedent, reduces false positives in prose.
- **B) Extract all relative paths** — Use a broad regex that captures any `relative/path/with/slashes`. Higher recall but noisier; arbitrary prose containing path-like tokens triggers false warnings.
- Recommendation: **A**. The skill parser's `@"(?<![A-Za-z0-9._\-/])(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)"` is proven, maintainable, and aligns with repository conventions for portable instruction artifacts. **This decision affects what diagnostics agents see.**

## Decided (implementation details, no user visibility)

**Nearest-match suggestion algorithm**: Compute from agent's directory tree (one level up search using edit distance or basename matching). Fast, bounded, no external dependency on CodeGraph. Lightweight and available always, consistent with the "file is missing" finding. _Rationale: Specification mirrors precedent from skill parser's suggestion behavior; local discovery is more actionable than repository-wide suggestions._

**Test coverage for nearest-match suggestions**: Use temporary file fixtures in test setup (not real repository tree). Faster, isolated, repeatable; deterministic across branches and clones. _Rationale: Fixtures are sufficient to exercise the algorithm; real-tree tests would couple to filesystem state and degrade robustness._

## Tasks

### RED1: Test the broken file reference check (test-dev)

**id**: T1-RED-agent-spec-004-broken-ref  
**specialist**: test-dev  
**scope**: tests/KyberWeave.Tests/AgentGovernanceTests.cs  
**depends-on**: None  
**concurrency**: Independent; can run in parallel with other RED/GREEN pairs.

**Acceptance criteria**:
1. Three new test methods that all fail before GREEN1 is implemented:
   - `ValidateWhenMarkdownLinkReferencesNonexistentFileEmitsKwAgentSpec004` — agent description contains `[guide](docs/missing.md)`; file does not exist; diagnostic raised with code `KW-AGENT-SPEC-004` and severity Error.
   - `ValidateWhenInlineLinkReferencesNonexistentFileEmitsKwAgentSpec004` — agent instructions contain `` `references/missing.md` ``; file does not exist; diagnostic raised.
   - `ValidateWhenFileReferenceResolvesSuccessfullyEmitsNoKwAgentSpec004` — agent instructions contain `` `references/existing.md` `` with the file physically present in the agent's directory; no diagnostic for code `KW-AGENT-SPEC-004`.
2. Test fixtures: Create temporary agent definition and file structure for each test so the resolved path can be validated without relying on real repository files.
3. Tests exercise both InstructionsBody and Description fields.

**Test contract**:
- Runner: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.AgentGovernanceTests.ValidateWhen.*Kw.*Spec004" -v normal`
- Observable behavior: Three new tests FAIL with message like `"Expected diagnostic with code 'KW-AGENT-SPEC-004' but found none"` (or similar) before GREEN1.
- After GREEN1, same runner command returns all tests PASS.

### GREEN1: Implement the broken file reference check (csharp-dev)

**id**: T2-GREEN-agent-spec-004-broken-ref  
**specialist**: csharp-dev  
**scope**: src/KyberWeave.Core/Agents/Validation/AgentSpecValidator.cs  
**depends-on**: T1-RED-agent-spec-004-broken-ref  
**concurrency**: Must follow T1; blocks T3 (rule-id audit).

**Acceptance criteria**:
1. Add `public const string RuleBrokenFileReference = "KW-AGENT-SPEC-004"` to AgentSpecValidator (or reuse if one exists; the todo notes it was deleted from a duplicate).
2. Extend `Validate(AgentModel agent)` to:
   - Parse `agent.Description` and `agent.InstructionsBody` as Markdown (using Markdig pipeline)
   - Extract `LinkInline` elements (links like `[text](path)`) from the AST
   - Extract inline path patterns matching `@"(?<![A-Za-z0-9._\-/])(?<path>(?:\./)?(?:scripts|references|assets)/[A-Za-z0-9._\-/]+)"` from raw body text
   - For each extracted path:
     - Skip if it starts with `http://`, `https://`, `#`, `mailto:`, or contains `..`
     - Normalize (strip leading `./`)
     - Resolve relative to `agent.DirectoryPath`
     - Check if file or directory exists at resolved path
   - If file does not exist: Add a diagnostic with code `RuleBrokenFileReference`, severity Error, message `"File reference '{path}' does not resolve — no file at '{resolvedPath}'."`, and a hint suggesting the nearest candidate file (if computable) or a fallback message.
3. Nearest-match algorithm:
   - Search `agent.DirectoryPath` for files/directories with name matching the broken reference's basename (case-insensitive, using edit distance or simple string matching)
   - Include one level of parent directory in search if no candidates found locally
   - Return the best match or null
4. All three tests from T1-RED must pass.
5. No changes to signature of `Validate(AgentModel agent)` — output is `DiagnosticReport`, callers unchanged.

**Deliverable**:
- Updated AgentSpecValidator.cs with new check and nearest-match helper method
- No new public types or interfaces required

**Test contract**:
- Runner: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.AgentGovernanceTests.ValidateWhen.*Kw.*Spec004" -v normal`
- Observable behavior: All three tests PASS; no new warnings introduced by implementation.

### T3: Audit all KW-AGENT-SPEC-* and KW-AGENT-SEC-* rule ids (docs-dev)

**id**: T3-audit-agent-rules  
**specialist**: docs-dev  
**scope**:
- Code: src/KyberWeave.Core/Agents/Validation/AgentSpecValidator.cs (current emitters)
- Docs: docs/ci-pipelines/rule-reference.md (documented rules)
- Reference: rule-reference.md section on agent security codes; src/KyberWeave.Core/Agents/Security/ for any additional emitters
**depends-on**: T2-GREEN-agent-spec-004-broken-ref  
**concurrency**: After GREEN1; blocks T4 and verification.

**Acceptance criteria**:
1. Audit output (to be saved in plan artifacts or attached comment):
   - For each `KW-AGENT-SPEC-*` id (001–004): state source file:line, emitter method, confirmation that it is emitted
   - For each `KW-AGENT-SEC-*` id: state that it is emitted from `InstructionSurfaceRuleCodes` (shared with skill-sec codes per rule-reference.md line 133–136)
   - Any gap: documented id not found in code → raise as a finding for user review (do not withdraw unilaterally)

2. No code changes in T3 (code changes go to T2 if they become necessary). If an audit finds a dead id, document it as a finding and let T4 decide whether to withdraw it (based on user input to the plan).

3. Confirm that rule-reference.md is already up-to-date after T2 (it should be, since 004 was already documented).

**Test contract**:
- Runner: Code review + grep verification. Grep for `const string Rule` in AgentSpecValidator.cs and InstructionSurfaceRuleCodes; grep for `KW-AGENT-SPEC` and `KW-AGENT-SEC` in rule-reference.md; verify each documented id appears in code.
- Observable behavior: All ids accounted for; no dead constants; no silent mismatches between code and docs.

### T4: Confirm rule-reference.md is current (docs-dev)

**id**: T4-confirm-docs-rule-ref  
**specialist**: docs-dev  
**scope**: docs/ci-pipelines/rule-reference.md  
**depends-on**: T3-audit-agent-rules (to learn if any updates are needed)  
**concurrency**: After T3; blocks verification.

**Acceptance criteria**:
1. Review T3 audit output. If T3 found no gaps, this task is a no-op: documentation is current and no changes are needed.
2. If T3 found a documented-but-not-emitted id: Do not withdraw it unilaterally. Instead, document the finding in the plan as a decision for the user (e.g., "Withdraw X?" or "Implement X as a new task?") and block plan finalization.
3. KW-AGENT-SPEC-004 is already documented at rule-reference.md:125; no change needed.
4. No unilateral documentation edits: all changes require user approval or closure of the audit finding.

**Test contract**:
- Runner: `dotnet run --project src/KyberWeave.Cli -- docs validate .` (no --merge-ready; this is docs-only, not a gate).
- Observable behavior: Zero findings in docs corpus; rule-reference.md passes validation.

## Verification

After all tasks complete, verify the full gate list via AGENTS.md' declared gates (from kyber-weave.yml):

```bash
cd /Users/dave/git/personal/kyber-weave/.claude/worktrees/agent-spec-broken-reference-rule
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- review gates . --out artifacts/gates.json
```

This runs all declared gates atomically: format, build, test, skill validation, docs validation, and drift. All gates must return exit code 0 (PASS).

Local equivalent (for contributor verification before pushing):

```bash
dotnet restore KyberWeave.sln
dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal
dotnet format KyberWeave.sln style --verify-no-changes --severity warn --no-restore -v minimal
dotnet build KyberWeave.sln -c Release --no-restore
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill validate .apm/skills/kyber-weave-docs
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill lint .apm/skills/kyber-weave-docs --min-desc-score 70
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- skill scan .apm/skills/kyber-weave-docs --fail-on critical
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

Expected result: **All gates PASS** (exit code 0, zero errors, zero blocking failures).

Note: CodeGraph index required for docs drift gate. See OPEN_QUESTIONS below.

## Closeout

After plan finalization and successful review:

1. **docs-dev** archives this plan to [docs/archive/plans/2026-09-24-agent-spec-broken-reference-rule.md](../archive/plans/) (new file).
2. **docs-dev** updates [docs/plans/README.md](../README.md) index: Move the plan row from Active to Archived, record completion date and any harvested ADRs (none anticipated for this implementation).
3. **docs-dev** closes the todo: Move [docs/todo/agent-spec-broken-reference-rule.md](../todo/agent-spec-broken-reference-rule.md) to [docs/archive/todo/agent-spec-broken-reference-rule.md](../archive/todo/) and update [docs/todo/README.md](../todo/README.md) index.

Durable facts to record in rule documentation (not the archived plan, which is read-only):
- **Decision Q1 (inline path scope)**  recorded in [docs/context-hygiene/agents.md](../../docs/context-hygiene/agents.md) (reference section on instruction-body validation, where the check is documented)
- **Rule-id audit results (T3 output)**: Attach final audit to plan closure comment or inline as verified in [docs/ci-pipelines/rule-reference.md](../ci-pipelines/rule-reference.md)

## File-scope overlaps and concurrency

**No overlaps**:
- T1 (RED): Tests in AgentGovernanceTests.cs
- T2 (GREEN): Code in AgentSpecValidator.cs
- T3 (Audit): Grep/search, produces report artifact (no source changes yet)
- T4 (Docs): Updates rule-reference.md only if T3 found dead rules to withdraw

**Concurrency**:
- T1 and T2 form a RED-GREEN pair; T2 blocks T3
- T3 determines whether T4 is needed; both block verification
- Verification runs after all code tasks complete

**No file conflicts**: Each task writes to independent files.

## Open Questions

**CodeGraph index availability for docs drift gate**:
The `docs drift` gate (declared in kyber-weave.yml, line 63–64) requires a CodeGraph index. Running it in this worktree returns:

```
KW-DOC-DRIFT-001 Critical: Drift cannot be verified: No CodeGraph index at /.codegraph/codegraph.db. 
→ Run 'codegraph index' at the repository root (or restore the cached index in CI), 
and ensure the 'sqlite3' CLI is on PATH.
```

Per [AGENTS.md](AGENTS.md), CodeGraph indexing is the user's decision. This plan assumes the user will either:
- Create a `.codegraph/` index locally via `codegraph index` (cost: one-time setup), or
- Restore the cached index from CI (cost: zero, requires cloning from origin)

**Decision for user** (not blocking plan finalization): Will you create a CodeGraph index, or should this plan run verification without the drift gate? The plan's current Verification section assumes the index exists. If it does not, drift will fail and the verification will be incomplete.

## Notes

- The unused `RuleBrokenReference` constant mentioned in the todo was already deleted on this branch; no cleanup needed.
- The todo also noted `KW-AGENT-SEC-003` as an example of a dead constant in `AgentPromptScanner` — but the rule *is* emitted (from `InstructionSurfaceRuleCodes`), so only the duplicate constant was dead. T3 will verify this.
- Decision Q1 (inline pattern scope) mirrors the skill parser's established convention, reducing future maintenance burden and aligning host expectations.
