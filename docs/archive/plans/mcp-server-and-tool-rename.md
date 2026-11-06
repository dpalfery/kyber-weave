---
id: plans/mcp-server-and-tool-rename
title: Rename the three MCP servers and their 19 tools by purpose
doc-type: plan
status: archived
component: DocGraph
owner: dpalfery
created: 2026-11-05
last-reviewed: 2026-11-06
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [DocGraph, KyberSquad, KyberDash, KyberArbiter, Distribution, CI Pipelines]
---

# Rename the three MCP servers and their 19 tools by purpose

**Status: Archived — complete 2026-11-06.** Every task in [Tasks](#tasks) shipped and the closeout
ran at T16: the [Design harvest](#design-harvest) items are mapped to their destinations in
[Closeout mapping](#closeout-mapping), and the evidence is in [Completion summary](#completion-summary).
Frontmatter `status` is `archived` and the plan now lives in `docs/archive/plans/`. The
`AUTONOMOUS_APPROVAL: approve-and-execute` gate that approved execution on 2026-11-05 is recorded in
`## Decisions` as AP1, and that table is preserved intact.
**Development mode:** `test-first` (packet value; the owner did not opt out).
**Autonomy:** `autonomous` (packet value and the repository Config Reg **<autonomy>**). **Scale:**
`extends-component`, components `DocGraph`, `KyberSquad`, `KyberDash`, `KyberArbiter`,
`Distribution`, `CI Pipelines`. **Delivery:** a draft pull request from
`refactor/mcp-server-tool-rename`; never merged by this run.

## Problem and goal

**Problem.** An agent chooses an MCP tool from its name first and its description second. The current
names are short labels (`docs_status`, `dash_query`, `arbiter_rules`) under server keys that repeat
the product name (`kyber-weave`), so a model cannot tell from the tool list what a tool is for.

**Goal.**

1. The three MCP configuration keys and all 19 tool names change to the owner's exact names (see
   [Name contract](#name-contract)), everywhere they are used as an MCP name.
2. Executables, CLI commands, dash analytics operation strings, the product name, the config
   directory and third-party servers do not change.
3. An upgraded install ends with one server per purpose, not old and new side by side, and no entry
   the tool cannot prove is its own is ever modified or removed.
4. A future tool cannot exceed the known name limits without a failing test.

## Name contract

The owner chose these names. They are requirements; a change needs a new row in `## Decisions`.

### Server keys (the key in MCP configuration)

| Old key | New key | Length | Executable (unchanged) |
|---|---|---|---|
| `kyber-weave` | `kw-docs` | 7 | `kyber-weave-mcp` |
| `kyberdash` | `kw-dash` | 7 | `kyberdash mcp` |
| `kyber-weave-arbiter` | `kw-arbiter` | 10 | `kyber-weave-arbiter serve` |

### Tools

"Qualified" is `mcp__<server>__<tool>`; "server + tool" is the two lengths added, which is how Cursor
is reported to count.

| Server | Old tool | New tool | Tool | Server + tool | Qualified |
|---|---|---|---|---|---|
| `kw-docs` | `docs_explore` | `docs_answer_question_from_repo_documentation` | 44 | 51 | 58 |
| `kw-docs` | `docs_for_symbol` | `docs_find_documents_citing_code_symbol` | 38 | 45 | 52 |
| `kw-docs` | `docs_glossary` | `docs_look_up_glossary_term_definition` | 37 | 44 | 51 |
| `kw-docs` | `docs_analysis_candidates` | `docs_find_duplicates_conflicts_ambiguous_terms` | 46 | 53 | 60 |
| `kw-docs` | `docs_validate` | `docs_validate_governed_document_schema` | 38 | 45 | 52 |
| `kw-docs` | `docs_drift` | `docs_detect_drift_between_documents_and_code` | 44 | 51 | 58 |
| `kw-docs` | `docs_status` | `docs_get_source_repository_and_revision` | 39 | 46 | 53 |
| `kw-docs` | `agent_coding_standards` | `docs_get_coding_standard_for_technology` | 39 | 46 | 53 |
| `kw-dash` | `dash_status` | `dash_list_available_analytics_operations` | 40 | 47 | 54 |
| `kw-dash` | `dash_catalog` | `dash_list_queryable_metrics_dimensions_limits` | 45 | 52 | 59 |
| `kw-dash` | `dash_query` | `dash_query_session_cost_tokens_and_duration` | 43 | 50 | 57 |
| `kw-dash` | `dash_search_sessions` | `dash_search_sessions_by_text_and_scope` | 38 | 45 | 52 |
| `kw-dash` | `dash_inspect` | `dash_read_captured_run_session_or_turn_context` | 46 | 53 | 60 |
| `kw-dash` | `dash_findings` | `dash_list_detected_context_and_tool_problems` | 44 | 51 | 58 |
| `kw-dash` | `dash_compare` | `dash_compare_metrics_between_two_scopes` | 39 | 46 | 53 |
| `kw-dash` | `dash_outcomes` | `dash_get_run_success_or_failure_outcomes` | 40 | 47 | 54 |
| `kw-arbiter` | `arbiter_evaluate` | `arbiter_evaluate_agent_dispatch_or_return` | 41 | 51 | 58 |
| `kw-arbiter` | `arbiter_rules` | `arbiter_list_effective_rules_by_trigger` | 39 | 49 | 56 |
| `kw-arbiter` | `arbiter_status` | `arbiter_check_configuration_and_key_readiness` | 45 | 55 | 62 |

Longest: tool 46, server + tool 55, qualified 62. Recomputed by hand for this plan and matches the
packet; `McpToolNameContractTests` and `tool-names.test.ts` recompute them from the code.

Pi's adapter spelling is `mcp__kw_docs__<tool>` (the renderer maps the hyphen), the same length.
Kilo and OpenCode use `{server}_{tool}`, at most 56.

## Investigation findings

Every packet fact was re-checked. Where the code differs from the packet, this section wins.

**Tool definitions.** `src/KyberWeave.Mcp/DocsTools.cs` (8 `[McpServerTool]` at `:78`, `:162`, `:201`,
`:312`, `:394`, `:450`, `:495`, `:530`), `src/KyberWeave.Arbiter/Mcp/ArbiterTools.cs` (`:107`, `:262`,
`:309`), `dash/src/mcp/tools.ts:3-12` and `:80-89`, `dash/src/mcp/client.ts:67`, `:78`, `:90`, `:103`.
Descriptions and text that cite another tool by name: `DocsTools.cs:128`, `:140`, `:208`, `:444`,
`:453`, `:485`; `DocsServerInstructions.cs:18`, `:23-29`; `ArbiterTools.cs:264`, `:311`;
`src/KyberWeave.Core.Docs/Validation/ArchiveCloseoutValidator.cs:108`.

**One caller outside the servers, not in the packet.** `src/KyberWeave.Core/Doctor/McpLiveProbe.cs:142`
sends `tools/call docs_explore`. Its check id `mcp.docs_explore` is a stable contract:
`DoctorCheck.cs:4` says "Agents branch on it", and `doctor --json` exposes it (D5).

**Host declaration, not in the packet, and the main migration trap.** `.kyber-weave/kyber-weave.yml:426`
and the scaffold constant `McpDeclarationBlockWriter.cs:45` key the host's `mcp.required-servers` by
server name. `docs init` "never edits it again" (`:17-21`). So an upgraded host keeps declaring
`kyber-weave`: the check stays satisfied by the old entry, and `docs mcp-wire` would re-insert the
old key. Migration has to cover the declaration as well as the harness files (D10).

**Repair never touches an existing entry, and that is a recorded boundary.**
`McpWiringRepairer.cs:528-541` refuses a name already present. ADR 0052 (`docs/adr/0052-…`) is
binding: the writers are a closed list (§1); user-scope files are "never written and never read"
for check, repair and dry run alike (§2); "insertion-only"; and "weakening one … is a new decision".
A rename therefore needs an ADR 0052 amendment, and the owner's `~/.claude.json` is out of reach for
any tool (D7, D11, D14).

**No Kilo row.** `HarnessMcpCatalog.Rows` has eight rows: factory, claude-code, cursor, vscode,
copilot-coding-agent, codex, opencode, zcode. Kilo is not one, yet the repository tracks a `kilo.json`
holding the old key. Only factory, claude-code, cursor, vscode and opencode are writable.

**Checked-in configuration, corrected.** Old key present: `.mcp.json:3`, `.cursor/mcp.json:3`,
`.factory/mcp.json:3`, `kilo.json:4`, `products/kyber-squad/mcp.json:3,11`. **`.vscode/mcp.json` holds
only `github`, so it does not change.** `.cursor/mcp.json:4` launches by absolute-style path
(`${userHome}/.local/bin/kyber-weave-mcp`), so the proof must compare a file name, not the argv text.
`docs/docgraph/mcp-runbook.md:1429-1434` forbids rewriting a tracked MCP config in place from a
worktree setup; a key rename in a normal commit is not that, and no path changes (D22).

**Constants, classified.**

- Server keys (change): `CapabilityDegradations.UniversalMcpServer:58`, `ClaudeRenderer.UniversalMcpTool:121`
  and `:158`, `:522`, `OpenCodeRenderer:80`, `KiloRenderer:104`, `PiRenderer:203`,
  `ZCodeRenderer.ArbiterServer:199`, `DevinRenderer:174`, `FactoryRenderer:98`, `AntigravityRenderer:93`,
  `SquadDoctorCommand:21`, `CopilotToolCatalog:29,65`, `products/kyber-squad/schemas/agent.schema.json:50`.
- Binaries (keep): `ArbiterHookWiring.CommandName:46`, `ArbiterPluginShim:186`, `PiRenderer:427`,
  `ProcessProbes:51`, `ArbiterDoctorCommand:188`, `SelfUpdater:23-26`, keychain `Service` constants,
  `RequiredMcpServer.ShippedBinary`, `UtilitiesStatusLinePrerequisites:14`.
- Transport name, not config key (keep): `src/KyberWeave.Arbiter/Program.cs:90`,
  `src/KyberWeave.Mcp/Program.cs:111`, `dash/src/mcp/server.ts:9` (`kyberdash-analytics`) (D4).

**Order changes as a side effect.** `CapabilityDegradations.QualifiedMcpToolNames:293-298` sorts by
server then tool. `kw-arbiter` now sorts before `kw-docs`, and `docs_get_coding_standard_for_technology`
no longer sorts first. Rendered enumerations change order; tests compute or restate them.

**Update re-renders, so it flips grants before the operator acts.** `SelfUpdater.RefreshArbiterHooks`
runs `ArbiterHookRefresher`, which runs `SquadLifecycleService.UpdateAsync` for each Arbiter-enabled
project (`ArbiterHookRefresher.cs:58-59`). Right after `kyber-weave update`, those projects' agents
grant `mcp__kw-docs__*` while their harness files still say `kyber-weave`. This is the moment an
agent starts returning `STATUS: BLOCKED`, so the notice belongs there (D14).

**Dashboard history.** No code special-cases an old name. `mcp__` handling is generic:
`dash/src/metrics/classifier.ts:46`, `providers/session-message.ts:70-82`,
`providers/codex.ts:70-78`, `providers/copilot.ts:142-154`. `dash/src/` outside `dash/src/mcp/` and
comments in `analysis/query/catalog.ts` names no tool on these servers; the one match,
`canon/adapters/content.test.ts:92`, is a synthetic fixture. New names split correctly in both
normalisers: `kw-docs_docs_answer_…` to server `kw-docs`, and Copilot's `kw-docs-docs_answer_…` to
`mcp__kw_docs__docs_answer_…`, because no tool name holds a hyphen (D17).

**Workflows.** `.github/` names no old tool. `release.yml` runs `dash/scripts/sea-mcp-smoke.mjs`,
which holds its own list and changes in T4. No workflow edit, so no `github-devops` task (D23).

**Links break when a heading renames.** `KW-DOC-LINK-001` (`DocSpecValidator.cs:35`, `:770-812`)
errors on a fragment naming no heading. `OntologyConfig.ArchiveSegment` keeps `docs/archive` out of the
set, so archived fragments are not checked. Live fragments that will break: `docs/plans/README.md:54`,
`docs/docgraph/mcp-runbook.md:1126`, `:1280`, `docs/docgraph/governance.md:39-40` (D21).

**Release mechanics.** No changelog exists. `release.yml:23` and `:1168-1179` build notes from the
`generate-notes` API; `scripts/next-release-version.sh:21-22` auto-bumps patch and says "Minor and
major cuts stay a tag push". Precedent for a rename: `docs/install.md:524` ("Retired skills") plus
`SquadDoctorRetiredSkillTests` and `ReportRetiredSkills` (D19).

**Name limits, researched** (`research-agent`, 2026-11-05; the packet's figures are partly stale).

| Surface | Limit | Source | 62 / 55 / 46 fits |
|---|---|---|---|
| MCP specification (2026-07-28) | 1-128, letters, digits, `_`, `-`, `.` | modelcontextprotocol.io/specification/2026-07-28/server/tools | yes |
| Anthropic Messages API | `^[a-zA-Z0-9_-]{1,128}$` | platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools | yes |
| Claude Code | Full name over 128 is left out, not truncated | code.claude.com/docs/en/errors | yes |
| OpenAI (namespaced function) | `^[a-zA-Z0-9_.-]+$`, max 128; the 64 rule found only in third-party docs | developers.openai.com/api/reference/resources/responses/methods/create | yes either way |
| Cursor | 60 on server + tool (staff forum replies, not official docs) | forum.cursor.com/t/google-gws-cli-tool-names-too-long/153918 | yes (55) |
| Copilot CLI | 1-128 quoted in an issue, low confidence | github.com/github/copilot-cli/issues/1879 | yes |
| OpenCode | No official limit; open PR proposes 64 truncation | research-agent | yes if 64 (56 by its own shape) |
| Gemini | No authoritative limit; 64 only in third-party SDK pages | aistudio.google.com/docs/function-calling | yes if 64 |
| Kilo Code | 60 is inferred from Cursor, not sourced | none | yes (56 by its `{server}_{tool}` shape, per ADR 0050) |
| Antigravity, Command Code, Devin, Factory, Pi, Warp, ZCode, Codex CLI | No authoritative limit found | none | **unverified** |

No harness limit below 62 was verified, so nothing here changes the owner's names (D18).

**Discovery:** codegraph 6, docs 10 (`docs_explore` 5, `agent_coding_standards` 1, `docs_for_symbol` 2,
`docs_analysis_candidates` 1, `docs_status` 1), direct reads of indexed code 31, delegated 1
(`research-agent`, external limits and the `claude mcp` commands). Direct reads, by reason: **whole-file
because a rename needs every occurrence and codegraph capped its output** — `DocsTools.cs`,
`DocsServerInstructions.cs`, `ArbiterTools.cs`, `tools.ts`, `client.ts`, `McpLiveProbe.cs`,
`McpWiringRepairer.cs`, `McpDeclarationBlockWriter.cs`, `HarnessMcpCatalog.cs` (rows), `McpConfig.cs`,
`JsonServerSplice.cs`, `DocsMcpWireSettings.cs`, `SquadToolchainRosterGuardTests.cs`; **line-exact slice
codegraph's budget cut off** — `McpWiringInspector.cs` (twice), `McpWiringReceipt.cs`, `McpWiringFix.cs`,
`CopilotMcpServerProbe.cs`, `ClaudeRenderer.cs`, `CopilotToolCatalog.cs`, `SelfUpdater.cs`,
`ArbiterHookRefresher.cs`, `DocSpecValidator.cs`, `ArchiveCloseoutValidator.cs`,
`KyberWeave.Arbiter/Program.cs`; **test or provider shape to model** — `copilot.ts`, `session-message.ts`,
`codex.ts`, `ReleaseNotesTests.cs`, `UpstreamSeveranceTests.cs`, `opencode-live-gate/run.mjs` (to rule out
one match). Text search was used for occurrence counts and classification: an index answers "where is
this symbol", not "which of 597 string matches are MCP names". Provenance on every docs call:
`root=/Users/dave/git/personal/Devin/kyber-weave`, equal to this checkout. The run-start doctor's
`mcp.root` failure is a false alarm whose cause is recorded in D24.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Which names? | The exact table in [Name contract](#name-contract). | Owner requirement. Lengths recomputed; all fit every verified limit. | ANSWERED: owner requirement, packet 2026-11-05 |
| D2 | How is `kyber-weave` classified, given it is also the product? | By the rule table in [Classification rule](#classification-rule). Only an MCP configuration key, a grant built from one, or prose naming the server changes. | Blanket replace rejected by the owner. T10's guard enforces the outcome on shipped surfaces. | ANSWERED: owner constraint, packet 2026-11-05 |
| D3 | Where do the new key strings live? | One new `McpServerNames` class in `src/KyberWeave.Core/Configuration/`, with `Docs`, `Dash`, `Arbiter`. The five `ArbiterServer` constants and `UniversalMcpServer` point at it. Tool-permission spellings stay local constants built from it. | Both `KyberWeave.Core.Squad` and `KyberWeave.Core.Docs` reference Core (ADR 0057), so Core is the one place both can read. Alternative: rename each literal in place, which keeps five copies that can disagree. Cost of wrong: local, cheap. | autonomous: low-confidence, no authoritative source |
| D4 | Do the MCP `serverInfo` names change? | No: `kyber-weave-mcp`, `kyber-weave-arbiter`, `kyberdash-analytics` stay. | Owner: "Only the MCP configuration key and the MCP tool names change". Harnesses prefix tools from the configuration key. These strings name the program. | ANSWERED: owner constraint, packet 2026-11-05 |
| D5 | Does the doctor check id `mcp.docs_explore` change? | No. The id stays; the tool it calls and its reason and fix text use the new name. The runbook says the id predates the rename. | `DoctorCheck.cs:4`: "Agents branch on it"; `doctor --json` exposes it; the owner excluded CLI surfaces. Alternative: rename the id, breaking a stable contract for no agent benefit. | ANSWERED: autonomous, source `DoctorCheck.cs:4` |
| D6 | Keep aliases for old tool names? | No aliases on any server. An alias doubles the list that this work exists to make readable, and no consumer needs one, since every first-party caller changes in this pull request. | Same for the probe: no fallback to the old name (D28). | ANSWERED: owner goal, packet 2026-11-05 |
| D7 | How are existing installs migrated? | A new explicit verb `kyber-weave docs mcp-migrate [path] [--target <slugs>] [--dry-run]`. It renames the key in place rather than adding and removing. See [Migration design](#migration-design). | Rejected: (a) running inside `kyber-weave update`, because ADR 0052 §3 forbids ambient repair and update would dirty committed files in every registered repository; (b) extending `mcp-wire`, whose insertion-only bound is the contract; (c) documentation only, which leaves grants broken. ADR 0052 is amended in place (D20). Verb name is cheap to change before release. | ANSWERED: autonomous, source ADR 0052 §1, §3 |
| D8 | How is an entry recognised as ours? | Exact old key AND launch proof: `argv[0]`'s file name equals the shipped binary (any directory, case-insensitive, optional `.exe`, same rule as `RequiredMcpServer.NamesShippedBinary`) AND the required next argument matches. A key alone is never proof. | Table in [Migration design](#migration-design). `kyberdash` and `kyber-weave` are plausible names for someone's own server, and the owner forbids removing what cannot be proven. A wrapper such as `npx kyber-weave-mcp` is unproven, so reported and left alone: the safe direction. | ANSWERED: autonomous, source owner constraint and `McpConfig.cs:150-164` |
| D9 | What if old and new are both present? | Old proven and launch identical to new (allowing the harness root token): remove the old member, byte-exact. Launch differs: refuse with both shown, zero bytes. Old unproven: never touched. | This is the side-by-side case the packet names. A differing launch may be a deliberate edit, so it goes to a person. | ANSWERED: autonomous, source owner constraint |
| D10 | Does migration edit the host's `mcp.required-servers` key? | Yes, one line, when a target is named or `--target declaration` is passed, and only when the declared command passes the same proof. Written by text, then re-parsed by `KyberWeaveConfigLoader`; a mismatch refuses. A flow-style mapping refuses with the edit to make. | Without it, the check is satisfied by the old key and `mcp-wire` re-inserts it. `McpDeclarationBlockWriter:17-21` says `docs init` never edits it; running this verb is the consent, and `docs init` still does not. | ANSWERED: autonomous, source `McpWiringRepairer` and `McpDeclarationBlockWriter` behaviour |
| D11 | How does the owner's hand-added `kyberdash` entry in `~/.claude.json` change? | By hand, with a documented command. No tool reads or writes it. Steps: `claude mcp get kyberdash` to see its scope, `claude mcp remove kyberdash --scope user`, `claude mcp add --scope user kw-dash -- kyberdash mcp`. `mcp-migrate` prints the rename table and a pointer on every run because it cannot see those files. | ADR 0052 §2 ("never written and never read") and `docs/dash/onboarding.md:235` ("KyberDash never writes a client's configuration"). Widening to user scope "is a new decision" and is not worth it for one known entry. Commands per code.claude.com/docs/en/mcp, read 2026-11-05 by `research-agent`. Listed as a post-merge step in the pull request body. | ANSWERED: autonomous, source ADR 0052 §2 |
| D12 | What about harnesses the repair may not write? | Report only, with the exact manual edit. Codex (no TOML writer; rename `[mcp_servers.kyber-weave]` and any `.env` sub-table), ZCode (entry body unpinned, so no proof), Copilot coding agent (repository settings), Kilo and everything else outside the catalog (`docs/install.md` lists them). | ADR 0052 §6: "reported but never written"; a new harness "is a new decision". | ANSWERED: autonomous, source ADR 0052 §6 |
| D13 | How do generated harness files update? | `kyber-weave squad update` (and `--global`) re-renders them through the existing receipt path; no new code. `update` already does it for Arbiter-enabled projects. | Squad owns rendered agents and no settings file (ADR 0052 §5, D-no-settings). Upgrade order documented in [Upgrade steps](#upgrade-steps). | ANSWERED: autonomous, source existing lifecycle |
| D14 | Where is a leftover old key reported, and how loudly? | Read-only scan on `docs init`, `docs mcp-wire` preview, `squad doctor`, and per project in `ArbiterHookRefresher`. New permanent ids: `KW-MCP-WIRE-007` (Warning: proven old key present, remedy is the migrate command), `-008` (Info: old key present but not provably ours, left alone), `-009` (Error, `mcp-migrate` only: target refused, zero bytes). Never changes `docs init` or `squad doctor` exit. | Existing 001 to 006 keep their meaning; ids are never reused. Exit behaviour matches `rule-reference.md:84-85` and the retired-skills precedent ("a warning, not a failure"). The scan must not depend on `Inspect`, which returns empty when nothing is declared. Severity is cheap to raise later. | ANSWERED: autonomous, source AGENTS.md and rule-reference |
| D15 | Can `mcp-wire` create the side-by-side state? | No. `RepairTarget` refuses (`KW-MCP-WIRE-005`, zero bytes) when the file holds a proven old key for a server it would add, and the hint names `docs mcp-migrate`. | Otherwise a host that migrated its declaration first, then ran `mcp-wire`, gets both. | ANSWERED: autonomous, source owner constraint |
| D16 | How does migration interact with the receipt and `mcp-unwire`? | No schema change. If the file's digest equals the current record's post-write digest, rebase that record: same mode and pre-write bytes, new post-write digest and server names, so `mcp-unwire` still undoes the original wire. Otherwise write a new `Inserted`-mode record whose pre-write bytes are the pre-migrate file. Record persists before the write. | Keeps the rule "a change this tool cannot record is a change it does not make" (`McpWiringRepairer.cs:601-618`). A new enum value would make older binaries read the receipt as empty, which is safe but pointless. | autonomous: low-confidence, no authoritative source |
| D17 | Does the dashboard need anything for history? | Documentation plus one pinning test. Past sessions keep old names; per-tool stats split at the rename. No rewrite, no alias map. T3 adds a characterization test that new names normalise correctly. | Investigation found no special-casing. Stored records are evidence (ADR 0018 keeps `records.raw`), so rewriting them would falsify what the harness sent. `docs/dash/runbook.md` states the split. | ANSWERED: autonomous, source investigation |
| D18 | Should a test pin the limits? | Yes. Tool matches `^[a-zA-Z0-9_-]{1,64}$`, holds no `__` so `mcp__a__b` stays unambiguous, qualified name at most 64, server + tool at most 60. C# reflects `DocsTools` and `ArbiterTools`; a vitest covers the dash list. Both also pin the exact 19-name map. | 64 and 60 are the tightest figures anyone reports, not all vendor-verified; see the table in findings. Stated in the test so a later reader can raise them with evidence. Antigravity, Command Code, Devin, Factory, Pi, Warp, ZCode and Codex are recorded as unverified: the only remaining risk is a limit under 62 nobody has reported, which would surface as a harness error, and research found no such limit. | autonomous: low-confidence, no authoritative source |
| D19 | How does the release communicate the break? | Pull request title `refactor(mcp)!: rename MCP servers and tools by purpose`, because `generate-notes` builds release notes from titles. Body carries [Upgrade steps](#upgrade-steps). `docs/install.md` gains a "Renamed MCP servers and tools" section beside "Retired skills". Recommended, not done here: the owner cuts a minor tag, since the auto-bump is patch-only. Nothing is published. | `release.yml:23`, `:1168-1179`; `next-release-version.sh:21-22`; `install.md:524`. No changelog exists to update, and adding one is out of scope. | ANSWERED: autonomous, source release.yml and install.md |
| D20 | ADRs? | **New ADR 0058** for the naming convention (key shape `kw-<area>`, tool shape `<area>_<verb>_<object…>`, no aliases, the limit guard): it constrains every future tool, had a rejected alternative, and this plan shows it is costly to revisit. **ADR 0052 amended in place** for the fourth writer and the key-only bound. **ADRs naming old names (0017, 0019, 0028, 0034, 0043, 0046, 0050, and any `T14` finds) are edited in place**, not left as history. File names and `id`s do not change; titles may. | `docs/adr/README.md:12-17`: "An ADR states only the decision in force … the ADR is edited in place … because git holds the history." So "leave as history" is not available. Probe evidence in ADR 0050 stays honest: the amendment note says those probes ran against the old spelling and the rules they established are name-independent. | ANSWERED: autonomous, source `docs/adr/README.md:12-17` |
| D21 | What about `docs/archive` and archived index rows? | `docs/archive/` is untouched. Archived rows in `docs/plans/README.md` are untouched except the two link fragments on the 2026-10-30 row, updated to the new heading anchors. | Owner: archive is history. `KW-DOC-LINK-001` still validates that row because the index is live; archived documents themselves are excluded (`OntologyConfig.ArchiveSegment`). | ANSWERED: owner constraint, source `DocSpecValidator.cs:35` |
| D22 | How do this repository's own config files change? | T11 runs the new verb on `.mcp.json`, `.cursor/mcp.json`, `.factory/mcp.json` and `.kyber-weave/kyber-weave.yml`, and hand-edits `kilo.json` (no catalog row). The diff is key-only. `.vscode/mcp.json` is unchanged. No machine-specific path is added. It runs last among code tasks. | Dogfooding is the best available proof the verb works on real files, including `.cursor/mcp.json`'s path form. Last because a session started after this edit reads `kw-docs` while deployed agents still grant the old key. | ANSWERED: autonomous, source mcp-runbook worktree section |
| D23 | Who owns JSON and YAML data files, and is `github-devops` needed? | Data read by C# goes with the C# task (`csharp-dev`). No workflow changes, so no `github-devops` task. | `.github/` holds no old name. The packet routes by file type; these files have no named delegate, and the C# worker is the one proving them. | autonomous: low-confidence, no authoritative source |
| D24 | Fix the false `mcp.root` doctor failure here? | No; out of scope and reported to the conductor. Cause: `McpLiveProbe.CheckRoot:188` takes the whole rest of the provenance line, so `served` is `<path> rev=… dirty=… documents=…` and never equals the root. `DoctorHarness.cs:72` fakes `provenance: root=ROOT` alone, so tests pass. Live checks in this plan read the `mcp.docs_explore` row, not the exit code. | Scope is the owner's call and this is a rename. It is a small fix in a file T2 touches, so a separate fix after this merges avoids a conflict. Recommended as its own `bug-crusher` run. | autonomous: low-confidence, no authoritative source |
| D25 | Does `kw-dash` join the Squad roster? | No. It stays out of `toolchain.yml`, `products/kyber-squad/mcp.json` and every grant. | Owner non-goal. | ANSWERED: owner constraint, packet 2026-11-05 |
| D26 | Do C# and TypeScript symbols rename? | No: `DocsTools.Explore`, `ArbiterTools.Evaluate`, `analyticsToolNames`, operation strings and `/api/kyber/analytics/*` stay. Only `Name =` values and the name strings. | Owner: only the key and tool names change. Keeps `code-refs` stable (`docs_for_symbol` shows `mcp-runbook.md` claims these classes). | ANSWERED: owner constraint, packet 2026-11-05 |
| D27 | How does RED work for a command that does not exist yet? | T8's tests reference `DocsMcpMigrateCommand` and do not compile against head. T8 proves RED with a throwaway no-op stub in its own worktree, never committed, and quotes each behaviour failing. T8's commit lands with T9's, not before. | A compile break on the shared head would stop every other worker. Zero-stash rule is honoured (`docs/standards/test/README.md`). T1, T3, T5 and T10 use literals and compile on their own. | ANSWERED: autonomous, source test standard |
| D28 | What happens with a mismatched install (new CLI, old server)? | `mcp.docs_explore` fails with the existing fix line `kyber-weave update`. No fallback call. `update --no-mcp` documents this skew. | Correct remedy already exists; a fallback would be an alias by another name. | ANSWERED: autonomous, source `McpLiveProbe.cs:158-171` |
| D29 | Development mode and autonomy. | `test-first`, `autonomous`. | Packet values. | ANSWERED: packet 2026-11-05 |
| AP1 | Approve and execute. | Approved for execution at the declared scale: `extends-component`, components DocGraph, KyberSquad, KyberDash, KyberArbiter, Distribution, CI Pipelines. | The owner's request was "create and execute a plan to make the above renames"; autonomy resolved to `autonomous` from the repository Config Reg; the conductor returned `AUTONOMOUS_APPROVAL: approve-and-execute`. The plan was decision-complete, mode-complete and scale-declared, with `docs_validate` and `docs_drift` passing. Rows D3, D16, D18, D23 and D24 stay `autonomous: low-confidence, no authoritative source` and are for pull request review to catch. | autonomous: recommendation accepted (2026-11-05) |

NO_QUESTIONS: no decision needed the owner. Every open item had an owner constraint, a repository
source, or a recommendation that a pull request review can catch.

## Classification rule

Workers apply this to every match. When in doubt, it is not an MCP name.

| Use | Examples | Action |
|---|---|---|
| MCP configuration key | `"kyber-weave": {` under `mcpServers`, `servers` or `mcp`; `required-servers.kyber-weave`; `[mcp_servers.kyber-weave]`; toolchain roster keys | Rename |
| Grant built from a key | `mcp__kyber-weave__*`, `kyber-weave_*`, `kyber-weave/*`, `mcp__kyber_weave__<tool>`, `mcp__kyber-weave-arbiter__…` | Rename |
| Prose naming the server | "the kyber-weave documentation server", "the `kyber-weave` server" | Rename to `kw-docs` |
| An old tool name | `docs_explore`, `dash_query`, `arbiter_evaluate`, `agent_coding_standards` | Rename |
| Executable or argv | `kyber-weave-mcp`, `kyber-weave-arbiter serve`, `kyberdash mcp`, `"command": …`, `spawnSync("kyber-weave-arbiter", …)` | **Keep** |
| CLI command | `kyber-weave docs validate`, `kyber-weave squad update`, `kyberdash web` | **Keep** |
| Product, repo, directory, env var | Kyber-Weave, `.kyber-weave/`, `kyber-weave.yml`, `KYBER_WEAVE_REPO_ROOT`, `~/.kyberdash`, GitHub repo, keychain service, user agent | **Keep** |
| Dash internals | `operation: 'status'`, `/api/kyber/analytics/*`, `kyberdash-analytics` | **Keep** |
| Rule ids, doctor check ids | `KW-*`, `mcp.docs_explore`, `mcp.root` | **Keep** |
| Test fixtures of generic behaviour | `McpConfigTests.FirstPartyServer`, `content.test.ts:92`, `TomlTableScannerTests` | **Keep** unless the test pins a shipped default |
| `docs/archive/**` | Anything | **Never edit** |
| Third-party servers | `codegraph`, `context7` | **Keep** |

## Migration design

`kyber-weave docs mcp-migrate [path] [--target <slugs>] [--dry-run]`, mirroring `mcp-wire`.

- **No target:** preview, nothing written. Lists every old key found, with its proof result, and the
  writable targets. **Named target:** writes that file and migrates the declaration. **`declaration`**
  is a valid slug for a host with no harness files. **`--dry-run`:** same report, no bytes, no receipt.
- **Exit:** 0 for preview, success or nothing to do; 1 if any named target was refused.
- **Bounds inherited from ADR 0052:** catalog-only file set; repository scope only; link and permission
  guard before any read; plan fully before writing; receipt before bytes; byte preservation outside the
  key; idempotent, so a second run reports nothing to do.

### Proof table

| Old key | New key | `argv[0]` file name | Required next argument |
|---|---|---|---|
| `kyber-weave` | `kw-docs` | `kyber-weave-mcp` | none |
| `kyber-weave-arbiter` | `kw-arbiter` | `kyber-weave-arbiter` | `serve` |
| `kyberdash` | `kw-dash` | `kyberdash` | `mcp` |

### States, per file and per old key

| File state | Launch proven | Action | Reported as |
|---|---|---|---|
| Old key absent | n/a | Nothing | Nothing |
| Old present, new absent | Yes | Rewrite the key's bytes only | `KW-MCP-WIRE-007` in checks; "renamed" here |
| Old and new present, same launch | Yes | Remove the old member, byte-exact | `KW-MCP-WIRE-007`; "removed duplicate" |
| Old and new present, different launch | Yes | Refuse, zero bytes, show both | `KW-MCP-WIRE-009`, exit 1 |
| Old present | No | **Never touched** | `KW-MCP-WIRE-008`, with the manual edit |
| Symlink, denied, malformed, foreign shape | n/a | Refuse, zero bytes | `KW-MCP-WIRE-009`, exit 1 |
| Codex, ZCode, Copilot settings | n/a | Report only | `KW-MCP-WIRE-008`, with the snippet |

### Splice

`JsonServerSplice` gains `PlanRename` and `PlanRemoval`, on bytes as `Plan` is. Rename replaces only the
property-name token. A key written with an escape refuses. Removal takes the member, its one separating
comma, and the whitespace back to the previous line. Both verify by re-parsing: the result must equal
the input's model minus the change, or the plan refuses and returns the input by identity. BOM, comments,
trailing commas and key order survive.

### Upgrade steps

Copied verbatim into `docs/install.md` and the pull request body.

1. `kyber-weave update`. Binaries now serve the new names. Arbiter-enabled projects re-render and warn.
2. In each repository: `kyber-weave docs mcp-migrate .` to preview, then
   `kyber-weave docs mcp-migrate . --target <harnesses>`. Commit the diff; it is key-only.
3. `kyber-weave squad update` (add `--global` if you deployed globally).
4. By hand, for files no tool reads: user-scope files (`~/.claude.json` through `claude mcp`,
   `~/.cursor/mcp.json`, `~/.codex/config.toml`, `~/.config/opencode/opencode.json`,
   `~/.factory/mcp.json`, `~/.copilot/mcp-config.json`, Kilo's and ZCode's), `kilo.json`, Copilot
   repository settings, and any harness permission allow-list naming an old tool.
5. Restart the harness. Claude Code will likely ask to approve the renamed `.mcp.json` server
   (`claude mcp reset-project-choices` resets; the docs do not say approval is keyed by name, so this
   is an inference). Cursor's per-server toggles are worth a look; Factory and OpenCode document no
   per-server state.

## Test contract

Mode `test-first`. Every RED is observed and quoted in the task digest before its GREEN starts, in a
clean worktree, with no `git stash`. Changing an assertion to reach green is a scope change.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T2 (RED T1) | `tests/KyberWeave.Tests`: new `McpToolNameContractTests`, updated `McpPackagingTests` and siblings | CMD-T1 | Exposed docs and Arbiter tool names equal the 11-row map; limits hold; server instructions and descriptions cite only new names; doctor probe calls the new name, keeps the id | New names not found; `McpToolNameContractTests` lists all 11 as missing | CMD-T1 green, non-zero total |
| T4 (RED T3) | `dash/src/mcp/*.test.ts`, new `tool-names.test.ts`, `sea-mcp-smoke.test.ts` | CMD-T3 | `tools/list` returns the 8 new names; limits hold; engine argument mapping still applies to the same tools | Vitest fails naming missing tools; zero-count run invalid | CMD-T3 green, non-zero count |
| T7, T6 (RED T5) | `tests/KyberWeave.Tests`: renderer and Squad classes | CMD-T5 | Every target renders the new grant spelling; the roster guard passes; Squad source loads; the universal and orchestrator partition is unchanged in meaning | Renderer tests fail on old spellings; roster guard reports no `kw-docs` roster | CMD-T5 green, non-zero total |
| T9 (RED T8) | `tests/KyberWeave.Tests`: new `DocsMcpMigrateCommandTests`, `SquadDoctorMcpRenameTests`, `ArbiterHookRefresherRenameNoticeTests`; updated wiring classes | CMD-T8 | Each row of the states table; proof table including wrong subcommand and path forms; byte preservation; dry run; receipt rebase through `mcp-unwire`; declaration edit; no user-scope access; `mcp-wire` guard; findings on `docs init` and `squad doctor` with exit unchanged; scaffold writes `kw-docs` | With a throwaway no-op stub (D27), each behaviour fails on its assertion; quote the failures and the compile error without the stub | CMD-T8 green, non-zero total |
| T11, T12-T14, T6 (RED T10) | `tests/KyberWeave.RepoPolicy.Tests`: new `McpRenameRepoGuardTests` | CMD-T10 | Tracked config uses new keys; no old tool name or old-key form on shipped surfaces outside a reasoned allow-list | Lists the current offenders, more than zero | `CheckedInConfig*` facts green at T11; all green after T14 |
| T12, T13, T14 | Governed docs | CMD-DOCS plus CMD-T10 | No broken fragment; drift clean; legacy scan clean | Not a separate RED: T10 already failing is the evidence | Zero findings; guard green |
| T15 | Whole change | CMD-FINAL | Gates pass | Not a test task (read-only verification) | Verdict recorded |
| T16 | Closeout | CMD-DOCS with `--merge-ready` | Plan archived | Not a test task (read-only verification) | Zero findings |

The `T3` normaliser test (D17) is a characterization test, green before and after, and is exempt from RED
by this statement; it is not counted as RED evidence for T4.

### Pre-computed commands

Run from the repository root. Build once per task, then re-run with `--no-build`. Check the summary shows a
non-zero total: a mistyped filter matches nothing and exits 0. Dash paths are relative to `dash/`.

```bash
# CMD-T1
scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~McpToolNameContractTests|FullyQualifiedName~McpPackagingTests|FullyQualifiedName~McpAgentCodingStandardsTests|FullyQualifiedName~McpDocsGateToolsTests|FullyQualifiedName~McpAnalysisToolsTests|FullyQualifiedName~McpDocsProvenanceTests|FullyQualifiedName~McpLiveProbeTests|FullyQualifiedName~DoctorCommandTests|FullyQualifiedName~RepositoryDoctorTests|FullyQualifiedName~DoctorReportRendererTests|FullyQualifiedName~ExploreAnswerTests|FullyQualifiedName~RetrievalRegressionTests|FullyQualifiedName~ArbiterMcpPackagingTests|FullyQualifiedName~ArbiterServeCallerSourceTests|FullyQualifiedName~ArbiterServeToolResponseTests|FullyQualifiedName~ArbiterServeStdioTransportTests"

# CMD-T3 (sea-mcp-smoke.test.ts spawns the script rather than importing it, so name the test file too;
# confirm it appears in the run)
scripts/proc-guard.sh run -- npm --prefix dash run test:related -- src/mcp/tools.ts src/mcp/client.ts src/mcp/server.ts src/providers/session-message.ts src/providers/copilot.ts src/mcp/tool-names.test.ts scripts/sea-mcp-smoke.test.ts

# CMD-T5
scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~ClaudeRendererContractTests|FullyQualifiedName~OpenCodeRendererContractTests|FullyQualifiedName~KiloRendererContractTests|FullyQualifiedName~PiRendererContractTests|FullyQualifiedName~AntigravityRendererContractTests|FullyQualifiedName~FactoryRendererContractTests|FullyQualifiedName~ZCodeRendererContractTests|FullyQualifiedName~DevinRendererContractTests|FullyQualifiedName~ArbiterDevinRenderingTests|FullyQualifiedName~CommandCodeRendererContractTests|FullyQualifiedName~CodexRendererContractTests|FullyQualifiedName~CursorRendererContractTests|FullyQualifiedName~WarpRendererContractTests|FullyQualifiedName~CopilotRendererTests|FullyQualifiedName~SquadUniversalMcpGrantContractTests|FullyQualifiedName~SquadToolchainRosterGuardTests|FullyQualifiedName~SquadSourceTests|FullyQualifiedName~SquadCanonicalContentTests|FullyQualifiedName~ArbiterMcpGrantRenderingTests|FullyQualifiedName~ArbiterCanonicalSquadTests|FullyQualifiedName~ArbiterConductorContractTests|FullyQualifiedName~ArbiterFallbackContractTests|FullyQualifiedName~SquadArbiterCliTests|FullyQualifiedName~CapabilityDegradationsTests|FullyQualifiedName~ZCodeMcpConfigurationTests"

# CMD-T8
scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~DocsMcpMigrateCommandTests|FullyQualifiedName~SquadDoctorMcpRenameTests|FullyQualifiedName~ArbiterHookRefresherRenameNoticeTests|FullyQualifiedName~McpWiringRepairerTests|FullyQualifiedName~McpWiringHardeningTests|FullyQualifiedName~McpWiringInspectorTests|FullyQualifiedName~McpWiringReceiptTests|FullyQualifiedName~JsonServerSpliceTests|FullyQualifiedName~McpDeclarationBlockWriterTests|FullyQualifiedName~McpDeclarationSurfaceTests|FullyQualifiedName~DocsInitMcpCheckTests|FullyQualifiedName~DocsMcpWireCommandTests|FullyQualifiedName~SquadDoctorRetiredSkillTests"

# CMD-T10
scripts/proc-guard.sh run -- dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build \
  --filter "FullyQualifiedName~McpRenameRepoGuardTests"

# CMD-DOCS
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate .
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .

# CMD-FINAL (--all: this change touches .kyber-weave/kyber-weave.yml and Core shared types. No --slot here:
# the declared `test` gate takes the slot itself, and an outer slot would make it wait on its own parent.)
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- review gates . --all --out artifacts/gates.json
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .
```

## Tasks

Every developer task ends with the worker's own `task-gate` run. No file is in two tasks unless a
dependency orders them. Scopes are entry files: a worker may touch a sibling only for a match this plan's
classification rule requires, and says so in its digest.

### T1: RED for docs and Arbiter tool names and the limit guard

- **Delegate:** `test-dev`. **Skills:** `test-dev`, **<test-coding-standard>**. **Depends on:** none.
- **Entry files:** new `tests/KyberWeave.Tests/McpToolNameContractTests.cs`; `McpPackagingTests.cs`,
  `McpAgentCodingStandardsTests.cs`, `McpDocsGateToolsTests.cs`, `McpAnalysisToolsTests.cs`,
  `McpDocsProvenanceTests.cs`, `McpLiveProbeTests.cs`, `DoctorHarness.cs`, `ExploreAnswerTests.cs`,
  `RetrievalRegressionTests.cs`, `Arbiter/ArbiterMcpPackagingTests.cs`,
  `Arbiter/ArbiterServeCallerSourceTests.cs`, `Arbiter/ArbiterServeToolResponseTests.cs`,
  `ArbiterServeStdioTransportTests.cs`.
- **Acceptance:** the new class pins the exact 11-name map by reflection over `[McpServerTool]`, plus D18's
  limits, with the limits' sources in a comment. A new `McpLiveProbeTests` case proves the probe asks for
  `docs_answer_question_from_repo_documentation` and still reports check id `mcp.docs_explore`
  (`DoctorHarness` fakes must assert the requested name). `DoctorReportRendererTests` stays unchanged.
  No test is deleted. RED quoted. **Commands:** CMD-T1.

### T2: GREEN for docs and Arbiter tool names

- **Delegate:** `csharp-dev`. **Skills:** `csharp-dev`, **<csharp-coding-standard>**, `task-gate`.
  **Depends on:** T1.
- **Entry files:** `src/KyberWeave.Mcp/DocsTools.cs`, `src/KyberWeave.Mcp/DocsServerInstructions.cs`;
  `src/KyberWeave.Arbiter/Mcp/ArbiterTools.cs`, `src/KyberWeave.Arbiter/Mcp/ArbiterRootResolver.cs` (comment),
  `src/KyberWeave.Arbiter/ArbiterServeToken.cs` (comments); `src/KyberWeave.Core/Doctor/McpLiveProbe.cs`;
  `src/KyberWeave.Core.Docs/Validation/ArchiveCloseoutValidator.cs:108`;
  `src/KyberWeave.Core.Docs/Search/ExploreAnswer.cs:14`.
- **Acceptance:** CMD-T1 green with T1 unweakened. Only `Name =` values and text citing a renamed tool
  change; no description is improved beyond that; method names stay (D26). `McpLiveProbe` keeps its id
  (D5). No `NoWarn`. **Commands:** CMD-T1.

### T3: RED for KyberDash tool names and the limit guard

- **Delegate:** `test-dev`. **Skills:** `test-dev`, **<test-coding-standard>**. **Depends on:** none.
- **Entry files:** new `dash/src/mcp/tool-names.test.ts`; `dash/src/mcp/server.test.ts`, `client.test.ts`,
  `transport.test.ts`, `stdio.test.ts`, `stdio-attach.test.ts`, `context-inspect.test.ts`;
  `dash/scripts/sea-mcp-smoke.test.ts`. Leave `dash/src/canon/adapters/content.test.ts:92`.
- **Acceptance:** pins the 8-name map and D18's limits for server `kw-dash`. Adds the characterization test
  (D17): `normalizeToolName('kw-docs_docs_answer_question_from_repo_documentation')` from
  `providers/session-message.ts` and `copilot.toolDisplayName('kw-docs-docs_answer_question_from_repo_documentation')`
  split on the right server. RED quoted for the map; the characterization test is labelled as exempt.
  **Commands:** CMD-T3.

### T4: GREEN for KyberDash tool names

- **Delegate:** `typescript-dev`. **Skills:** `typescript-dev`, **<typescript-coding-standard>**, `task-gate`.
  **Depends on:** T3.
- **Entry files:** `dash/src/mcp/tools.ts`, `client.ts`, `server.ts` (comment);
  `dash/src/analysis/query/catalog.ts` (comments); `dash/scripts/sea-mcp-smoke.mjs`.
- **Acceptance:** CMD-T3 green. `operation` values, HTTP paths, `kyberdash-analytics` and the
  `analyticsToolNames` symbol are unchanged. `toEngineBody` keeps its behaviour under the new names.
  **Commands:** CMD-T3, then `scripts/proc-guard.sh run -- npm --prefix dash run typecheck`.

### T5: RED for Squad roster and renderers

- **Delegate:** `test-dev`. **Skills:** `test-dev`, **<test-coding-standard>**. **Depends on:** none.
- **Entry files:** `ClaudeRendererContractTests.cs`, `OpenCodeRendererContractTests.cs`,
  `KiloRendererContractTests.cs`, `PiRendererContractTests.cs`, `AntigravityRendererContractTests.cs`,
  `FactoryRendererContractTests.cs`, `ZCodeRendererContractTests.cs`, `DevinRendererContractTests.cs`,
  `ArbiterDevinRenderingTests.cs`, `CommandCodeRendererContractTests.cs`, `CodexRendererContractTests.cs`,
  `Squad/CopilotRendererTests.cs`, `SquadUniversalMcpGrantContractTests.cs`,
  `Fakes/SquadMcpGrantProjection.cs`, `SquadToolchainRosterGuardTests.cs`, `SquadSourceTests.cs`,
  `SquadCanonicalContentTests.cs`, `ArbiterMcpGrantRenderingTests.cs`, `ArbiterCanonicalSquadTests.cs`,
  `ArbiterConductorContractTests.cs`, `ArbiterFallbackContractTests.cs`, `SquadArbiterCliTests.cs`.
- **Acceptance:** expected spellings per target: Claude `mcp__kw-docs__*`; OpenCode and Kilo `kw-docs_*`;
  Copilot `kw-docs/*`; Pi `ext:pi-mcp-adapter/mcp__kw_docs__<tool>`; enumerating targets
  `mcp__kw-docs__<tool>` in the new ordinal order; Arbiter `mcp__kw-arbiter__<tool>`; Factory and
  Antigravity `kw-docs` with `command: kyber-weave-mcp` unchanged. Tests asserting the binary name keep it.
  Roster guard reads key `kw-docs`. RED quoted. **Commands:** CMD-T5.

### T6: Product Markdown

- **Delegate:** `docs-dev`. **Skills:** `kyber-docs-authoring`, `app-docs-standard`. **Depends on:** T5.
- **Entry files:** `products/kyber-squad/agents/**` (`copilot-tools` frontmatter entry and body),
  `products/kyber-squad/skills/**` (`code-review`, `product-owner`, `task-gate`),
  `products/kyber-squad/migration/code-reviewer.md`, `.apm/skills/codegraph/SKILL.md`,
  `.apm/skills/kyber-docs-integrity-review/SKILL.md`, `.apm/skills/kyber-weave-cli/SKILL.md` (add the
  `mcp-migrate` verb).
- **Acceptance:** the classification rule is applied. `mcp.docs_explore` stays. Frontmatter emission order
  is unchanged (`kw-docs/*` takes the old slot). `arbiter_evaluate` in the literal sentences that
  `ArbiterFallbackContractTests` and `ArbiterConductorContractTests` pin becomes the new name, as T5 states.
  **Commands:** none own; T7's CMD-T5 proves it. Head is red for Squad classes between T6 and T7 by design.

### T7: GREEN for Squad roster and renderers

- **Delegate:** `csharp-dev`. **Skills:** `csharp-dev`, **<csharp-coding-standard>**, `task-gate`.
  **Depends on:** T2 (roster guard reflects `DocsTools`), T5, T6.
- **Entry files:** new `src/KyberWeave.Core/Configuration/McpServerNames.cs`;
  `src/KyberWeave.Core.Squad/Rendering/` `CapabilityDegradations`, `ClaudeRenderer`, `OpenCodeRenderer`,
  `KiloRenderer`, `PiRenderer`, `ZCodeRenderer`, `DevinRenderer`, `FactoryRenderer`, `AntigravityRenderer`,
  `CommandCodeRenderer`, `CopilotRenderer`; `src/KyberWeave.Core.Squad/Model/CopilotToolCatalog.cs`;
  `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs:21`; `products/kyber-squad/toolchain.yml`,
  `mcp.json`, `schemas/agent.schema.json`, `profiles/capabilities.yml` (comments).
- **Acceptance:** CMD-T5 green with T5 unweakened. Binary constants listed in findings are unchanged:
  `mcp.json` keeps `"command": "kyber-weave-mcp"` and `"command": "kyber-weave-arbiter"`. `kw-dash` appears
  in no roster (D25). `McpServerNames` holds keys only, no binary. `CoreAssemblySplitTests` still passes.
  **Commands:** CMD-T5, plus `FullyQualifiedName~CoreAssemblySplitTests` on CMD-T10's project.

### T8: RED for migration

- **Delegate:** `test-dev`. **Skills:** `test-dev`, **<test-coding-standard>**. **Depends on:** none.
- **Entry files:** new `tests/KyberWeave.Tests/DocsMcpMigrateCommandTests.cs`,
  `SquadDoctorMcpRenameTests.cs` (model: `SquadDoctorRetiredSkillTests`),
  `ArbiterHookRefresherRenameNoticeTests.cs`; updated `McpWiringRepairerTests.cs`,
  `McpDeclarationBlockWriterTests.cs`, `DocsInitMcpCheckTests.cs`, `DocsMcpWireCommandTests.cs`.
- **Acceptance:** assert observable files and output through the command, in real temp directories, for: all
  seven state rows; all three proof rows, plus wrong subcommand (`kyberdash web`, `kyber-weave-arbiter hook`),
  `${userHome}/.local/bin/kyber-weave-mcp`, `.exe`, and `npx kyber-weave-mcp` (unproven); byte equality outside
  the key for a file with BOM, comments and trailing commas; opencode's single-array command; dry run and
  preview write nothing, including no receipt; receipt rebase then `mcp-unwire` restores pre-wire bytes, and
  migrate with no prior record then `mcp-unwire` restores pre-migrate bytes; declaration key edit with comments
  intact and reload giving `kw-docs`; flow-style declaration refused; idempotent second run; a fake home holding
  `.claude.json` with a `kyberdash` entry stays byte-identical and its content never appears in output; `mcp-wire`
  refuses beside a proven old key and names the verb; `docs init` prints `KW-MCP-WIRE-007`, offers no wire fix for
  it, exit unchanged; `squad doctor` warns, exit unchanged; the refresher logs per project; a fresh scaffold writes
  `kw-docs`. RED per D27. **Commit lands with T9.** **Commands:** CMD-T8.

### T9: GREEN for migration

- **Delegate:** `csharp-dev`. **Skills:** `csharp-dev`, **<csharp-coding-standard>**, `task-gate`.
  **Depends on:** T7 (`McpServerNames`, `SquadDoctorCommand`), T8.
- **Entry files:** new `src/KyberWeave.Core.Docs/Mcp/McpServerRenamer.cs` (proof table, scan, plan, apply);
  `JsonServerSplice.cs` (`PlanRename`, `PlanRemoval`); `McpWiringInspector.cs` (expose the entry-body reader
  as `internal`); `McpWiringRepairer.cs` (D15 guard); `McpWiringReceipt.cs` if the rebase needs it;
  `HarnessMcpCatalog.cs` (five example keys); `Scaffolding/McpDeclarationBlockWriter.cs:45`;
  new `src/KyberWeave.Cli/Commands/Docs/DocsMcpMigrateCommand.cs`; `DocsMcpWireSettings.cs` (settings);
  `src/KyberWeave.Cli/Program.cs:117-126`; `DocsInitCommand.cs` (`ReportMcpWiring`, before its no-declaration
  return); `DocsMcpWireCommand.cs` (`Preview`); `SquadDoctorCommand.cs`; `src/KyberWeave.Cli/Update/ArbiterHookRefresher.cs`.
- **Acceptance:** CMD-T8 green with T8 unweakened. The three new ids are constants. No user-scope path is opened
  in any code path. The scan never writes. `McpWiringFixes.Derive` does not offer a wire fix for an old key.
  Output goes through `TerminalText`. No dependency added; no TOML writer. **Commands:** CMD-T8.

### T10: RED for repository guards

- **Delegate:** `test-dev`. **Skills:** `test-dev`, **<test-coding-standard>**. **Depends on:** T9 (allow-list
  names its files).
- **Entry files:** new `tests/KyberWeave.RepoPolicy.Tests/McpRenameRepoGuardTests.cs` (model:
  `UpstreamSeveranceTests`, walking `git ls-files`).
- **Acceptance:** `CheckedInConfig*` facts: `.mcp.json`, `.cursor/mcp.json`, `.factory/mcp.json`, `kilo.json`,
  `products/kyber-squad/mcp.json` and `.kyber-weave/kyber-weave.yml` hold `kw-docs` and no old key; commands
  are unchanged; `.vscode/mcp.json` is unchanged in intent (no first-party entry). Legacy scan: word-bounded old
  tool names (so `docs_validate_governed…` does not match), and `mcp__kyber`, `kyber-weave_*`, `kyber-weave/*`,
  JSON and YAML old keys, across tracked `src/**`, `dash/src/**`, `dash/scripts/**`, `products/**`, `.apm/**`,
  `docs/**` and root `*.md`, excluding `tests/**`, `*.test.ts`, `docs/archive/**`, `docs/plans/**`. The allow-list
  is exact file plus reason: `McpServerRenamer.cs`, the `mcp.docs_explore` id uses, the rename tables in
  `docs/install.md`, `docs/docgraph/mcp-runbook.md`, `docs/dash/onboarding.md` and ADR 0058. RED quoted with a
  non-zero offender count. **Commands:** CMD-T10.

### T11: GREEN for checked-in configuration (dogfood)

- **Delegate:** `csharp-dev`. **Skills:** `csharp-dev`, `task-gate`. **Depends on:** T2, T7, T9, T10.
- **Entry files:** `.mcp.json`, `.cursor/mcp.json`, `.factory/mcp.json`, `.kyber-weave/kyber-weave.yml`
  (through the verb); `kilo.json` (by hand).
- **Acceptance:** run from a branch build: `docs mcp-migrate .` (preview), then
  `--target claude-code,cursor,factory`, and quote the output. `git diff` shows key lines only. A second preview
  reports nothing to do. `CheckedInConfig*` green. The gitignored receipt is not committed. Sequenced after every
  other code task (D22). **Commands:** the verb, then CMD-T10 for `CheckedInConfig`.

### T12: DocGraph, CLI, distribution and root documents

- **Delegate:** `docs-dev`. **Skills:** `kyber-docs-authoring`, `app-docs-standard`. **Depends on:** T2, T9.
- **Entry files:** `docs/docgraph/mcp-runbook.md` (headings, tool contract, every wiring example, a new migration
  section, `code-refs` for `McpServerRenamer` and `McpServerNames`, the note on the `mcp.docs_explore` id, the
  limits), `retrieval.md`, `governance.md`, `onboarding.md`, `README.md`; `docs/documentation-ontology.md`,
  `docs/README.md`, `docs/configuration.md`, `docs/cli-command-surface.md` (new verb, flags, exit codes),
  `docs/install.md` (new section with the rename table and [Upgrade steps](#upgrade-steps)),
  `docs/distribution.md`, `docs/ci-pipelines/rule-reference.md` (`KW-MCP-WIRE-007` to `-009`);
  `docs/plans/README.md` (D21's two fragments only); root `README.md`, `AGENTS.md`;
  `src/KyberWeave.Mcp/AGENTS.md`, `src/KyberWeave.Core/AGENTS.md`, `src/KyberWeave.Cli/AGENTS.md`.
- **Acceptance:** classification rule applied; `KW-DOC-LINK-001` clean, with the live fragments in findings
  repaired; `AGENTS.md` stays hand-authored and the Config Reg block untouched; `RootAgentsOperationalPointerTests`
  passes. **Commands:** CMD-DOCS, then CMD-T10 with `FullyQualifiedName~RootAgentsOperationalPointerTests`.

### T13: Squad, Arbiter and KyberDash documents

- **Delegate:** `docs-dev`. **Skills:** `kyber-docs-authoring`, `app-docs-standard`. **Depends on:** T4, T7.
- **Entry files:** `docs/kyber-squad/architecture.md`, `onboarding.md`, `requirements.md`;
  `docs/kyber-arbiter/architecture.md`, `runbook.md`; `src/KyberWeave.Arbiter/AGENTS.md`;
  `docs/dash/README.md`, `architecture.md`, `onboarding.md` (server key, the eight names, the manual rename),
  `requirements.md`, `runbook.md` (per-tool stats split at the rename, D17); `dash/AGENTS.md` if matched.
- **Acceptance:** classification rule applied; `docs/catalog.md`'s "eight `dash_*` tools" stays true and is
  unchanged; D11's commands appear exactly. **Commands:** CMD-DOCS.

### T14: ADRs

- **Delegate:** `docs-dev`. **Skills:** `architecture-decision-record`, `kyber-docs-authoring`.
  **Depends on:** T9.
- **Entry files:** new `docs/adr/0058-mcp-servers-and-tools-are-named-by-purpose.md`; amended
  `docs/adr/0052-…` (fourth writer, key-only bound, §1's table, §2 stands); in place `0050-…`, `0046-…`,
  `0043-…`, `0034-…`, `0019-…`, `0028-…`, `0017-…`; any other ADR the classification rule catches in prose,
  which the CMD-T10 scan cannot see (candidates: 0016, 0031, 0036, 0053, most of which name a binary and stay);
  `docs/adr/README.md`.
- **Acceptance:** per D20. ADR 0050 keeps its evidence truthful with an `Amended 2026-11-05` note. File names and
  `id`s unchanged. ADR 0052's "Weakening one … is a new decision" sentence is honoured by saying this amendment
  is that decision. **Commands:** CMD-DOCS, then CMD-T10, whose whole class is now green.

### T15: Whole-run review

- **Delegate:** `code-reviewer`. **Skills:** `code-review`. **Depends on:** T1 to T14, and the conductor pushing
  the branch and opening the draft pull request first (`create-pull-request`), with D19's title and
  [Upgrade steps](#upgrade-steps) in the body.
- **Entry files:** the whole diff against `main`.
- **Acceptance:** CMD-FINAL run once. A live check from branch builds:
  `dotnet build src/KyberWeave.Mcp -c Release`, then with that output directory first on `PATH`,
  `kyber-weave doctor --check --json` must show `mcp.docs_explore` passing. Its `mcp.root` row is a known false
  failure (D24) and is not this run's defect. Verdict recorded in the digest.

### T16: Closeout

- **Delegate:** `docs-dev`. **Skills:** `kyber-docs-authoring`. **Depends on:** T15.
- **Entry files:** this plan, `docs/plans/README.md`.
- **Acceptance:** harvest verified against [Design harvest](#design-harvest), `## Closeout mapping` and a completion
  summary added, `git mv` to `docs/archive/plans/`, row moved to Archived Plans, Active Plans empty, then
  `docs validate . --merge-ready` and `docs drift .` zero. Non-verified items stay marked `[NOT LIVE-VERIFIED]`.
  The closeout commit goes on the same draft pull request; nothing is merged or published.

## Dependency graph

```text
T1 RED ───► T2 GREEN ───────────┐
T3 RED ───► T4 GREEN ─────────┐ │
T5 RED ─► T6 product docs ─► T7 GREEN ◄─ T2
                              │
T8 RED ─────────────────────► T9 GREEN ◄─ T7   (T8 and T9 land together)
                              │
                              ├─► T10 RED ─► T11 config (also needs T2, T7)
                              ├─► T12 docs (also needs T2)
                              └─► T14 ADRs
T4, T7 ─► T13 docs
T11, T12, T13, T14 ─► T15 review ─► T16 closeout
```

First ready set, within `review.max-concurrent-workers` of 4: T1, T3, T5, T8. Then T2, T4, T6. T12, T13 and T14
can run together. All scopes are disjoint except the ordered overlaps: `SquadDoctorCommand.cs` (T7 then T9),
`docs/docgraph/*` only in T12, ADRs only in T14, the two product and repository `mcp.json` sets in T7 and T11.

## Risks

| Risk | Mitigation |
|---|---|
| A session started after T11 reads `kw-docs` while deployed agents still grant `mcp__kyber-weave__*`, so agents lose the docs tools mid-run. | T11 runs last among code tasks. Running sessions keep their connection. If a session must restart, use the CLI (`kyber-weave docs validate`). The conductor should not restart between T11 and review. |
| After `kyber-weave update`, an Arbiter-enabled project's agents flip to new grants before its harness files migrate. | Per-project warning in `ArbiterHookRefresher`, `KW-MCP-WIRE-007`, and step order in [Upgrade steps](#upgrade-steps). Accepted: consent to write committed files is the ADR 0052 boundary. |
| A wrong entry is rewritten or removed. | Proof needs key and launch identity; unproven never touched; pinned by T8's negative cases; rename preserves the entry; removal requires an identical launch; receipt gives undo. |
| A user-scope entry is left behind, giving duplicate tools or old grants. | No tool can see it (ADR 0052 §2). Printed on every run, in `docs/install.md`, and in the pull request body. The owner's own entry is a named post-merge step. |
| Kilo, Antigravity, Command Code, Devin, Factory, Pi, Warp, ZCode or Codex enforces a limit under 62. | Research found none; recorded unverified. Failure would be a visible harness error. The guard can be tightened with evidence, and the owner decides any name change. |
| ADR 0050's probes ran on the old spelling. Kilo's and Pi's rules are name-independent by their stated logic, but no probe re-ran. | Tests pin emitted bytes. Amendment says so. New names are `[NOT LIVE-VERIFIED]` on Kilo, Pi and Antigravity until someone runs them. |
| Heading renames break live links. | `KW-DOC-LINK-001` catches them; T12 lists the known ones; the validator's hint names the closest anchor. |
| A worker blanket-replaces `kyber-weave`. | Classification rule in every packet; T7 acceptance pins binaries; `ReleaseTests` and `DistributionManifestTests` still pin artefact names at CMD-FINAL. |
| The T5 and T8 test surface is large, and a fixture is renamed needlessly. | Rule: generic fixtures stay unless they pin a shipped default. |
| `docs_explore`'s retrieval scores shift as headings rename. | `RetrievalRegressionTests` is in CMD-T1 and re-runs at CMD-FINAL. |

## Out of scope

- Renaming any executable, CLI command, dash operation string, config directory, environment variable or repository.
- `codegraph` and `context7`.
- Editing `docs/archive/**`.
- Adding KyberDash tools to the Squad toolchain roster.
- Tool description improvements beyond citing a renamed tool.
- Adding a Kilo row, a TOML writer, or any user-scope read or write.
- Fixing `McpLiveProbe.CheckRoot` (D24).
- Publishing a release, cutting a tag, or merging.
- Rewriting stored dashboard history or adding an alias map.

## Verification gates

CMD-FINAL at T15, CMD-DOCS with `--merge-ready` after the archive at T16, and T15's live check.
`[NOT LIVE-VERIFIED]` until an owner runs them after a release: the new names in a real session on each of the 13
harnesses, and the `claude mcp` steps on the owner's machine.

## Review and closeout

- Draft pull request only, opened before review, with title
  `refactor(mcp)!: rename MCP servers and tools by purpose`.
- One whole-run council review (T15). The `.kyber-weave/kyber-weave.yml` and Core changes make `--all` the right
  scope per `AGENTS.md`.
- `docs-dev` closeout (T16), then the post-merge steps below for the owner.

**Post-merge, owner only:** cut a minor tag if desired (D19), run [Upgrade steps](#upgrade-steps) on your machine,
including D11's `claude mcp` commands, and re-run `docs mcp-migrate` in any other repository you use this in.

## Design harvest

| ID | Target component | Target doc type | Durable item | Destination |
|---|---|---|---|---|
| H1 | DocGraph | adr | Naming convention: `kw-<area>` keys, `<area>_<verb>_<object…>` tools, no aliases, the limit guard and its sources | `docs/adr/0058-mcp-servers-and-tools-are-named-by-purpose.md` |
| H2 | DocGraph | adr | The fourth writer, its key-only bound and proof rule; user scope still never read or written | `docs/adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md` |
| H3 | DocGraph | runbook | `docs mcp-migrate` behaviour, state table, proof table; renamed tool contract; `mcp.docs_explore` id note | `docs/docgraph/mcp-runbook.md`, `docs/cli-command-surface.md` |
| H4 | CI Pipelines | reference | `KW-MCP-WIRE-007`, `-008`, `-009` | `docs/ci-pipelines/rule-reference.md` |
| H5 | KyberSquad | architecture | Roster keys, grant spellings per target, `McpServerNames`, `squad doctor` notice | `docs/kyber-squad/architecture.md`, `docs/kyber-squad/onboarding.md`, `docs/kyber-squad/requirements.md` |
| H6 | KyberDash | operations | Server key, tool names, manual client rename, per-tool stats split at the rename | `docs/dash/onboarding.md`, `docs/dash/runbook.md`, `docs/dash/architecture.md` |
| H7 | KyberArbiter | architecture | Server key and tool names for the fallback path | `docs/kyber-arbiter/architecture.md`, `docs/kyber-arbiter/runbook.md` |
| H8 | Distribution | operations | Upgrade steps and how the release communicates the break | `docs/install.md`, `docs/distribution.md` |

## Closeout mapping

Checked at T16 on 2026-11-06 by opening each destination named in [Design harvest](#design-harvest)
and confirming the durable item it was harvested to is present and states what the harvest row says.
"Verified" here is a documentation check: the item landed and says what was promised. It is not a
live harness check; the surfaces nobody has exercised in a real session keep their
`[NOT LIVE-VERIFIED]` marker in [Verification gates](#verification-gates).

| ID | Destination | Verified |
|---|---|---|
| H1 | [adr/0058-mcp-servers-and-tools-are-named-by-purpose.md](../../adr/0058-mcp-servers-and-tools-are-named-by-purpose.md) | **Verified.** The ADR states the `kw-<area>` key shape, the `<area>_<verb>_<object…>` tool shape, the no-alias rule, and the name limits with the sources each figure came from |
| H2 | [adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md](../../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md) | **Verified.** Amended in place: the fourth writer, its key-only bound and its launch-proof rule are recorded, and §2's "never written and never read" for user scope stands |
| H3 | [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md#docs-mcp-migrate), [cli-command-surface.md](../../cli-command-surface.md) | **Verified.** `### docs mcp-migrate` carries the behaviour, the state table and the launch-proof paragraph; the state table holds the seven rows designed in [States, per file and per old key](#states-per-file-and-per-old-key) plus the home-directory refusal the review added after implementation. The runbook's renamed tool contract and its note that the check id `mcp.docs_explore` predates the rename are in place; the CLI surface table carries the verb's synopsis, flags and exit codes |
| H4 | [ci-pipelines/rule-reference.md](../../ci-pipelines/rule-reference.md) | **Verified.** `KW-MCP-WIRE-007`, `-008` and `-009` each have a row with severity, cause list and remedy; T16 added the home-directory cause to `-009` after the review found it missing |
| H5 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md), [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md), [kyber-squad/requirements.md](../../kyber-squad/requirements.md) | **Verified.** The roster key `kw-docs`, the grant spelling per target, `McpServerNames` as the one place the keys live, and the `squad doctor` `KW-MCP-WIRE-007`/`-008` notice with the exit code unchanged |
| H6 | [dash/onboarding.md](../../dash/onboarding.md), [dash/runbook.md](../../dash/runbook.md), [dash/architecture.md](../../dash/architecture.md) | **Verified as documentation.** The `kw-dash` key, the eight names, D11's `claude mcp` commands for the user-scope entry, and the per-tool stats split at the rename. Running those commands on the owner's machine stays `[NOT LIVE-VERIFIED]` |
| H7 | [kyber-arbiter/architecture.md](../../kyber-arbiter/architecture.md), [kyber-arbiter/runbook.md](../../kyber-arbiter/runbook.md) | **Verified.** `kw-arbiter` as the fallback path's server key, taken from `McpServerNames.Arbiter`, with the three tool names and the unchanged `kyber-weave-arbiter serve` launch |
| H8 | [install.md](../../install.md#renamed-mcp-servers-and-tools), [distribution.md](../../distribution.md) | **Verified.** The "Renamed MCP servers and tools" section carries the verbatim [Upgrade steps](#upgrade-steps), including every surface no tool reads; distribution.md states that a rename is not a new asset and points at that section for the operator-side work |

Every row in the permanent [`## Decisions`](#decisions) table maps to the live document that now
carries it. A decision whose substance is a process step or an approval leaves no lasting record and
says so.

| Decisions | Lasting record | What landed there |
|---|---|---|
| D1, D2, D6, D18, D26 | [adr/0058-mcp-servers-and-tools-are-named-by-purpose.md](../../adr/0058-mcp-servers-and-tools-are-named-by-purpose.md) | The name contract, the classification rule, the no-alias rule, the name limits with their sources, and the boundary that keeps C# and TypeScript symbols unchanged |
| D3, D13, D25 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) | `McpServerNames` as the one place the keys live, the renderer and receipt path generated harness files take, and `kw-dash` staying outside the roster |
| D4, D5, D28 | [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md) | The transport and `serverInfo` names that did not change, the note that the check id `mcp.docs_explore` predates the rename, and the mismatched-install remedy |
| D7, D9, D10, D12, D16, D22 | [docgraph/mcp-runbook.md#docs-mcp-migrate](../../docgraph/mcp-runbook.md#docs-mcp-migrate) | The verb, the [states](#states-per-file-and-per-old-key) and proof tables, the declaration edit, the snippet-only harnesses, the receipt rebase, and this repository's own migrated files |
| D8 | [adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md](../../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md) | The launch proof an entry has to satisfy and the repository-scope bound that proof is held inside |
| D14, D15 | [ci-pipelines/rule-reference.md](../../ci-pipelines/rule-reference.md) | `KW-MCP-WIRE-007`, `-008` and `-009` with their severities, causes and remedies, and the `-005` guard that stops `mcp-wire` creating the side-by-side state |
| D11 | [dash/onboarding.md](../../dash/onboarding.md) | The manual user-scope rename: the `claude mcp remove` and `claude mcp add` commands no tool can run for the operator |
| D17 | [dash/runbook.md](../../dash/runbook.md) | The per-tool statistics split at the rename, with past sessions left as they were captured |
| D19 | [install.md#renamed-mcp-servers-and-tools](../../install.md#renamed-mcp-servers-and-tools), [distribution.md](../../distribution.md) | The upgrade steps and the release's statement that a rename adds no asset |
| D20 | [adr/0058-mcp-servers-and-tools-are-named-by-purpose.md](../../adr/0058-mcp-servers-and-tools-are-named-by-purpose.md), [adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md](../../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md) | The ADR disposition: 0058 created for the convention, 0052 amended in place, and the ADRs naming the old names edited in place |
| D21 | [docgraph/governance.md](../../docgraph/governance.md) | The plan inventory lifecycle: a plan is closed once archived, and it is the index row and the links a live row carries that move — the archived documents themselves are history and stay untouched |
| D27 | [standards/test/README.md](../../standards/test/README.md) | The zero-stash RED proof rule the throwaway-stub proof of T8 follows |
| D24 | [docgraph/mcp-runbook.md](../../docgraph/mcp-runbook.md) | The `mcp.root` row and its fix line are documented as they behave today. The false failure stays an **open, out-of-scope follow-up, not a pass**; it is not fixed by this change |
| D23, D29, AP1 | Documentation waiver: plan-execution routing, the declared mode and autonomy, and the approve-and-execute gate are process provenance with no surviving architectural, operational or behavioural invariant. Each is stated in full in [`## Decisions`](#decisions): D23 changes no workflow, D29 carries the packet's `test-first` and `autonomous` values, and AP1 records the 2026-11-05 approval |

## Completion summary

**What shipped.**

- The three MCP configuration keys and all 19 tool names, exactly as the [Name contract](#name-contract)
  fixes them: `kyber-weave` → `kw-docs`, `kyberdash` → `kw-dash`, `kyber-weave-arbiter` → `kw-arbiter`,
  and the tool table unchanged in spelling and order. Executables and argv, CLI verbs, dash operation
  strings, the product name, `.kyber-weave/`, `kyber-weave.yml`, environment variables, every rule id
  and the doctor check id `mcp.docs_explore` did not change (D2, D4, D5, D26).
- `McpServerNames` in `src/KyberWeave.Core/Configuration/` is the one place the keys live, and the five
  `ArbiterServer` constants plus `UniversalMcpServer` point at it (D3). Every renderer emits the new
  grant spellings for its target; `kw-dash` appears in no roster (D25).
- A new `kyber-weave docs mcp-migrate` verb (D7): proof-gated key rename, byte-exact duplicate removal,
  host-declaration migration, receipt rebase so `mcp-unwire` still undoes the original wire (D16), the
  `mcp-wire` guard (D15), and the permanent `KW-MCP-WIRE-007`/`-008`/`-009` ids with the finding
  reports on `docs init` and `squad doctor` leaving both exit codes unchanged (D14).
- Name-limit guards on both sides (D18): `McpToolNameContractTests` for the docs and Arbiter servers,
  `tool-names.test.ts` for the eight dash tools, each pinning the exact map and the tightest figures
  with their sources.
- Documentation harvest H1 to H8, all mapped above; ADR 0058 created, ADR 0052 amended in place, and
  the ADRs naming the old names edited in place rather than left as history (D20).
- This repository's own configuration migrated through the verb, key lines only (T11, D22), and
  `docs/install.md` carries the "Renamed MCP servers and tools" section with the upgrade steps (D19).

**Verification evidence.**

- Full unscoped gate suite at T15: **34 gates, 0 failed** (`review gates . --all`).
- `McpRenameRepoGuardTests` **23/23** (T16 re-ran the class with
  `RootAgentsOperationalPointerTests`; both green).
- `DocsMcpMigrateCommandTests` **47/47** (T9's class, green through T15's CMD-FINAL).
- `docs validate . --merge-ready` **0 findings** and `docs drift .` **0 findings**, re-run at T16 after
  this plan moved to `docs/archive/plans/`.
- Live `mcp.docs_explore` probe from branch builds: **passing** — `kyber-weave doctor --check --json`
  reports the row for the renamed tool under the unchanged check id.
- Whole-run review verdict: **APPROVE**.

**What is not claimed.**

- `[NOT LIVE-VERIFIED]` — the new names in a real session on each of the 13 harnesses, and D11's
  `claude mcp` steps on the owner's machine. Nobody has exercised them, so they stay marked in
  [Verification gates](#verification-gates); Kilo, Pi and Antigravity carry the same marker for their
  name limits.
- The run-start doctor's `mcp.root` failure is still a **false failure**, recorded as the out-of-scope
  follow-up D24 and fixed in its own change. It is not a pass and is not counted as one here.
