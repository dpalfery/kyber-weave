---
id: plans/2026-10-23-pi-renderer-network-read-mcp
title: "Pi: restore read-only MCP access via a canonical per-target agent-file overlay (pi-extensions / pi-tools)"
doc-type: plan
status: archived
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-25
documentation-impact: extends-component
impact-components: [KyberSquad]
development-mode: test-first
autonomy: autonomous
---

# Pi: restore read-only MCP access via a canonical per-target agent-file overlay (`pi-extensions` / `pi-tools`)

**Status: Archived — T12 backfill closeout 2026-10-25: scale declared (`extends-component`, [KyberSquad]), design harvested into the KyberSquad canonical docs + ADR 0019 (see `## Design harvest` and `## Closeout mapping` below). The Decisions table and every original section are preserved intact; the original status paragraph follows, labelled as history.**

**Pre-closeout status (preserved as history):** Ready — decision-complete. Revision 2 (2026-10-24): D1/Option A is REVERSED on
verified live evidence (not a guess — see the D1 row); the fix moved one layer up from the
renderer vocabulary to the canonical Squad agent file, per D7–D11, decided under
`autonomy: autonomous` with provenance recorded per row. `docs validate` / `docs drift`
could not be run in the authoring harness (no process tool; G3, same disclosure as revision
1) — the conductor runs both before execution.** Frontmatter `status: draft` per closed
ontology — body Ready is authoritative.

**Development mode:** `test-first` (default; no opt-out supplied).
**Autonomy:** `autonomous` (resolved from repository root `AGENTS.md` Config Reg `<autonomy>`, confirmed by the packet).

**Branch context (COM-B preserved):** `feat/pi-renderer-network-read` holds UNCOMMITTED
revision-1 work: T1 (`PiRendererContractTests.cs`, now asserting the wrong eight-tool
expectation), T2 (`PiRenderer.cs`, now emitting the invalid bare `mcp` token), T3
(ADR 0019 amendment + `requirements.md` + `onboarding.md`, all stating the wrong mechanism),
the plan-index row, and this plan file. This revision reworks those changes in place; it
does not restart from a clean tree. Prior GREEN evidence (full suite 3126/3126 pre-revert)
is **void for every changed file** — all gates re-run (§10).

## 1. Problem and goal

### 1.1 Defect

The real defect is unchanged from revision 1: `research-agent` — whose canonical profile
grants `network.read: allow` and whose body tells it to prefer MCP tools — renders on Pi
with `tools: read, grep, find, ls` and no web access, while the renderer honestly records
`permission-not-expressible`.

Revision 1's fix premise is now **provably wrong**. Verified live on 2026-10-24 (probe
sequence cited in §3, item 6):

- `~/.pi/agent/settings.json` carries `extensions: ["-builtin:mcp", ...]` — a documented
  global opt-out of Pi's **built-in** MCP, inherited by every subagent. The main session
  still has `mcp` because `npm:pi-mcp-adapter` v6.0.0 provides its own `mcp` proxy there;
  a subagent has no such proxy.
- Plain names in `tools:` must be **built-ins**. Bare `mcp` is not a built-in available to
  a subagent, so the loader rejects it under the unknown-name (typo) rule → `tools-error`.
  Revision 1's `("network.read", ["mcp"])` therefore emits invalid frontmatter.
- `extensions: false` loads nothing. The **working combination**, proven live twice: agent
  frontmatter `extensions: [pi-mcp-adapter]` + `tools:` carrying `ext:pi-mcp-adapter/mcp`
  — and, crucially, the nested `mcp__<server>__<tool>` names (as
  `ext:pi-mcp-adapter/mcp__context7__resolve_library_id`) must ALSO be allowlisted, or the
  subagent scope gate refuses the call. With
  `ext:pi-mcp-adapter/mcp__context7__resolve_library_id` and
  `ext:pi-mcp-adapter/mcp__context7__query_docs` added, a subagent started the context7
  server, resolved a library ID, and returned sourced docs. No warnings; `settings.json`
  untouched (`-builtin:mcp` stays).
- All components are latest (pi 1.4.0, pi-subagents 0.22.0, adapter 6.0.0) — there is no
  update path that would make the revision-1 mechanism work.

### 1.2 Goal

Revert the renderer to the pre-plan seven-tool vocabulary (revision 1's T2 edit is undone),
and move the durable fix one layer up: a **per-target overlay in the canonical Squad agent
file** — two optional frontmatter keys, `pi-extensions` and `pi-tools` (D7) — that the Pi
renderer lowers onto the verified wiring: `extensions: [<declared ids>]` plus the declared
`ext:` tool names appended to the capability-derived `tools` CSV (D9). `research-agent`
declares exactly the live-verified overlay (D10). Everything else about the Q1-A envelope
is preserved: `process.execute: deny` stays withheld for read-only roles (**no shell for
read-only** — and `pi-tools` cannot smuggle one in, per D8), `network.publish` stays
unmapped and still records `permission-not-expressible`, the `network.read`
not-expressible record **stays** (it is TRUE for the renderer alone — no built-in expresses
it), `extensions: false` remains the default emission for every agent without an overlay,
and the `tools` key is still always emitted.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Fix approach for the dropped `network.read` grant on Pi | **REVERSED 2026-10-24 — Option A is provably wrong and is not implemented.** Bare `mcp` in `tools:` is treated by the loader as an unknown built-in (plain names must be built-ins; subagents inherit the `-builtin:mcp` global opt-out), so revision 1's mapping emits invalid frontmatter. The operative fix is D7's canonical agent-file overlay. The Option-A renderer edit (uncommitted) is reverted in T2. | The reversal is evidence-based, not a change of preference: 2026-10-24 live probes (owner-supplied, reproduced twice) established the refusal of bare `mcp` and the working `extensions: [pi-mcp-adapter]` + `ext:` combination. Option B (`network.read → bash`) stays rejected on the same least-privilege grounds as revision 1 — a read-only role must not gain a shell. | REVERSED — owner-supplied verified evidence 2026-10-24 (probe sequence, §3 item 6); superseded by D7. Original approval: owner decision relayed by conductor 2026-10-23. |
| D2 | Revisit of the Q1-A precedent (2026-09-14, R8 vocabulary fact) | **Q1-A now stands entirely intact — R8 is re-confirmed.** `network.read` still has no expressible built-in Pi tool; the revision-1 belief that "`mcp` is a first-class built-in" was an artifact of the main session's pi-mcp-adapter proxy. The overlay is an additive per-agent exception declared in canonical source, not a revision of Q1-A. | `extensions: false` unconditionally (default), always-emitted `tools`, `ask`/`deny` both withheld, `network.publish` unmapped — all stand exactly as decided 2026-09-14. The renderer's `network.read` not-expressible record stays because it remains true of the renderer alone. | ANSWERED — autonomous: recommendation accepted; basis is the 2026-10-24 verified evidence (supersedes the revision-1 split-revision reading). |
| D3 | Record for the durable decision change | Amend [ADR 0019](../adr/0019-pi-native-subagents-and-primary-lowering.md) in place — but the **uncommitted 2026-10-24 amendment text is wrong** (it records the reverted `network.read → mcp` lowering) and is reworked by T3 to state the corrected mechanism: canonical `pi-extensions`/`pi-tools` overlay, the verified-evidence basis including the bare-`mcp` refusal, the `network.publish` residual, and `extensions: false` as untouched default. No new ADR. | Repo precedent for refinements of an existing decision: ADRs 0005, 0016, 0031 amended in place. The amendment must record the reversal explicitly so no future reader re-derives the provably-wrong mechanism from it. T3's files are uncommitted, so the rework loses nothing. | ANSWERED — autonomous: recommendation accepted; established amend-in-place pattern; revision reworked per packet direction. |
| D4 | Placement of `mcp` in the fixed `ToolOrder` CSV | **VOID — superseded by the D1 reversal.** `mcp` never enters `ToolOrder`; the order returns to the seven-tool `read, grep, find, ls, edit, write, bash` (byte-identical to the pre-plan state). | Recorded for the permanent record so the placement debate is not re-opened: the ordering question was moot the moment bare `mcp` was proven invalid. | ANSWERED — reversed 2026-10-24 (no longer operative); provenance D1. |
| D5 | Deployed `~/.pi/agent/agents/*.md` renders in the wild | The overlay changes **only `research-agent`'s render** relative to the pre-plan baseline; every other agent's rendered file is byte-identical to it. The next successful `squad install`/`squad update` re-renders and overwrites the receipt-managed `research-agent.md` with the overlay wiring, superseding the hand-patched local file. **Deployment caveat:** `squad install/update --global` currently fails ("No Kyber-Squad release exists at version '0.1.0'" — no published Squad release; §3 item 7), so re-deployment on this machine is deferred until that separate defect is fixed; the rendered output itself is pinned by T1's contract assertion. | ADR 0037 receipt-managed deployment. The deployment gap is a finding recorded for a future plan, not fixed here (out of scope, §8). | ANSWERED — autonomous: recommendation accepted; revised 2026-10-24 with the verified deployment-gap evidence. |
| D6 | MCP-grant enforcement inside a subagent carrying the wiring | **VERIFIED GREEN 2026-10-24** — the probe subagent carrying `extensions: [pi-mcp-adapter]` + the `ext:` allowlist started the context7 server, resolved a library ID, and returned sourced docs; the subagent scope gate's refusal without the nested `mcp__<server>__<tool>` allowlisting was also observed, proving the gate is real and the allowlist entries are load-bearing. T4 records this evidence; no re-run is required unless the implementation deviates from the verified mechanism (T4 row). | Revision 1 sequenced this as a fail-closed unknown; the 2026-10-24 probe sequence closed it. The probe file was hand-authored and carried `bash`; the canonical render differs only by `bash`'s absence (lattice-withheld), and frontmatter equivalence is pinned by T1's rendered-output assertion. | ANSWERED — verified live 2026-10-24 (owner-supplied probe evidence); T4 GREEN. |
| D7 | Canonical vehicle: where the verified wiring lives | **Two optional flat frontmatter keys on `kyber-squad.agent/v1`: `pi-extensions`** (array of Pi extension ids) **and `pi-tools`** (array of additional Pi tool names), mirroring the flat per-target precedent already in the schema (`copilot-tools`, `copilot-capability-profile`, `devin-capability-profile`). Loader, validator, and `agent.schema.json` all grow the two keys; `SquadAgent` grows the two fields. | Rejected: (a) a nested `pi:` overlay object — the schema has no nested-object frontmatter key; flat keys are the established shape; (b) a `pi-capability-profile` string naming a target-only profile — the capability vocabulary expresses capability→decision only and cannot express the two halves of the verified wiring (load extension X; allowlist `ext:` tool Y); (c) an `ext:pi-mcp-adapter/mcp` entry in the renderer's `CapabilityTools` — a vocabulary change of the reverted class, and it would assume the adapter is installed on every deployment machine, whereas the canonical file can opt in per agent. **Why adapter-specific `ext:pi-mcp-adapter/*` strings are acceptable in canonical source:** they are deployment-harness-namespaced identifiers of exactly the same standing as the existing `copilot-tools` entries (`codegraph/*`, `context7/*`, `kyber-weave/*`) — the canonical file already declares target-namespaced tool identifiers, and `pi-mcp-adapter` is the published npm package name, stable and drift-managed in source (a rename is a one-line edit) like the toolchain roster. | ANSWERED — autonomous: recommendation accepted; packet-directed decision point ("decide exactly where the verified wiring lives"); schema/loader/validator precedent read at `agent.schema.json`, `SquadSourceLoader.ParseAgent`, `SquadSourceValidator`. |
| D8 | Validation rules for the overlay keys (loader + `SquadSourceValidator`) | `pi-tools` entries are restricted to the **`ext:<extension>/<tool>` form only — bare names are rejected**: built-in tool names derive solely from the capability lattice, so a canonical file cannot bypass `process.execute: deny` (or any other withheld capability) with `pi-tools: [bash]`. Every `ext:` entry's `<extension>` must be declared in `pi-extensions` (cross-check modeled on `ValidateCopilotToolCapabilities`). Both keys optional; when present, non-empty and distinct. Extension ids: non-empty, portable charset, no `/` (it is the `ext:` separator). The tool part is checked with the loader's existing `IsPortableMcpNamePart` charset — the qualified `mcp__server__tool` naming rule already established for `required-mcp-tools`. | Least-privilege by construction: the overlay can grant extension tools (the verified MCP-gateway use case) and nothing else. Keeping validation in loader/validator (not the renderer) follows the established pattern: `CopilotToolCatalog` validation and the copilot/devin profile cross-checks all live in source validation. | ANSWERED — autonomous: recommendation accepted; modeled on the existing copilot-key validation pattern (§3 items 3–4). |
| D9 | Renderer emission of the overlay | `tools` CSV = the seven capability-derived built-ins in the fixed `ToolOrder`, followed by the declared `pi-tools` entries in declared canonical order; agents without an overlay render **byte-identical to the pre-plan state**. `extensions` = the declared list, emitted as a **YAML sequence** (`extensions: [pi-mcp-adapter]` — the exact form the live probe verified; the CSV-scalar form is unverified and is not guessed), or `false` for every agent without an overlay (Q1-A default untouched). Frontmatter key order unchanged (`name, description, [model], [thinking], tools, extensions, [allowed_subagents]`). The `network.read` not-expressible record is unchanged; the renderer remarks point at the agent-file overlay as the mechanism that supplies web access. | Matching the verified emission form exactly is the cheapest correctness guarantee available: the probe proved the sequence form end-to-end. Byte-stability for all non-overlay agents keeps the blast radius at one file. | ANSWERED — autonomous: recommendation accepted; verified probe form; cheap to reverse. |
| D10 | Overlay scope: which agents carry it and with which entries | `research-agent` **only**, declaring exactly the live-verified wiring: `pi-extensions: [pi-mcp-adapter]`; `pi-tools: [ext:pi-mcp-adapter/mcp, ext:pi-mcp-adapter/mcp__context7__resolve_library_id, ext:pi-mcp-adapter/mcp__context7__query_docs]`. Other `network.read: allow` agents get no overlay. Other toolchain-roster servers (codegraph, kyber-weave) are deferred: their qualified `ext:` tool names are unverified — the hyphen→underscore transformation was live-verified only for context7's pair. | Least-privilege: only the agent whose canonical body says "Prefer the dedicated MCP tool … (Microsoft Learn, Terraform, GitHub, Context7)" gets the gateway; its peers' bodies do not reference MCP tools. Extension later is a one-line source edit per agent per verified server, exactly the drift-management pattern the toolchain roster comment describes. | ANSWERED — autonomous: recommendation accepted; verified-only entries; cheap to reverse and extend. |
| D11 | Assertion location for the research-agent deployed-wiring pin | **`PiRendererContractTests` (pi-rendered output tests).** That suite renders the real canonical source (`products/kyber-squad`) through the registry, so a rendered-output pin on `research-agent` transitively guards both the canonical declaration and the renderer emission — the strongest single location. `SquadCanonicalContentTests` is untouched (it carries no per-agent frontmatter pins today; its migration digests cover bodies only). The new-key validation contract (D8) is asserted in `SquadSourceTests`, the established loader/validation suite. | SquadCanonicalContentTests inspected: per-agent pins are roster/migration-digest only (§3 item 5); duplicating the wiring pin there would assert the same fact twice with no drift guard between the copies. | ANSWERED — autonomous: recommendation accepted; existing suite responsibilities read before deciding. |

## 3. Investigation findings

**Discovery report:** `Discovery: codegraph 0, docs 0, direct reads 13 (self-gathered)`.
The harness exposes no `codegraph_explore` and no Kyber-Weave docs MCP tools (consistent
with revision 1's disclosure, and now explained: the authoring subagent inherits the
`-builtin:mcp` global opt-out). Per the fallback rule, discovery used narrow direct reads,
all labeled self-gathered, each with a reason: `agent.schema.json` + `agents/research-agent.md`
(packet-named decision sites for D7); `PiRenderer.cs` regions :1–160, :300–440, :440–520
(the uncommitted T2 edit to revert, the `tools`/`extensions` emission site, `ResolveTools`)
— reason: the rework sites; `SquadSource.cs` + `SquadSourceLoader.cs` regions :560–670,
:400–510 (the `EnsureOnlyFields` key allowlist at :578, `ParseAgent`, `ParseToolchain`,
`IsPortableMcpNamePart`) — reason: where the overlay keys must be parsed and validated;
`SquadSourceValidator.cs` :55–135 (copilot/devin cross-check pattern) — reason: D8's
validation model; `toolchain.yml` (the MCP roster and its drift rationale) — reason: D10
scope evidence; `SquadCanonicalContentTests.cs` :1–120 (no per-agent frontmatter pins) and
`PiRendererContractTests.cs` :1–120, :370–450, :600–660 (the uncommitted T1 state) — reason:
re-point sites and D11; `SquadSourceTests.cs` grep (copilot-tools validation tests :377–491)
— reason: where the D8 validation tests belong; `onboarding.md` :240–280, ADR 0019
amendment grep, corpus `eight-tool` grep — reason: the exact T3 rework list.

Key facts established:

1. **Verified harness facts (2026-10-24, live, owner-supplied; supersede the plan's
   revision-1 T4 premise):** (a) `-builtin:mcp` global opt-out in `settings.json`, inherited
   by every subagent; (b) `extensions: false` loads nothing, and plain `tools:` names must
   be built-ins — bare `mcp` is a `tools-error`; (c) the working combination
   `extensions: [pi-mcp-adapter]` + `ext:pi-mcp-adapter/mcp` + nested
   `ext:pi-mcp-adapter/mcp__<server>__<tool>` allowlisting, proven twice via context7
   (`mcp__context7__resolve_library_id`, `mcp__context7__query_docs`); without the nested
   names the subagent scope gate refuses (`Tool "mcp__context7__resolve_library_id" is not
   available to this subagent`); (d) all components latest — pi 1.4.0, pi-subagents 0.22.0,
   adapter 6.0.0, no update path.
2. **Revert site (uncommitted T2)** — `src/KyberWeave.Core/Squad/Rendering/PiRenderer.cs`:
   `CapabilityTools` currently carries `("network.read", ["mcp"])` (:117–128), `ToolOrder`
   has eight entries (:133), and the vocabulary remarks (:59–69) state the eight-tool
   lowering — all revert to the pre-plan seven-tool state. The emission site is
   `RenderSubagentAgent` (:326–331): `tools` CSV then `extensions = false` unconditionally —
   the overlay branches here (D9). `ResolveTools` (:469–494) and
   `BuildSubagentDegradations` keep the `network.read` not-expressible branch (revision 1
   deleted it in the uncommitted edit — T2 restores it).
3. **Canonical parse/validate sites** — `SquadSourceLoader.ParseAgent`: `EnsureOnlyFields`
   allowlist at :578 (add `pi-extensions`, `pi-tools`), sequence parsing after the
   `copilot-tools` block (:590–632); `SquadSource.cs` `SquadAgent` record (add the two
   fields); `SquadSourceValidator` (D8 cross-checks, after the copilot/devin profile
   checks); `products/kyber-squad/schemas/agent.schema.json` (`additionalProperties: false`
   — add the two optional keys; flat-key precedent: `copilot-tools`,
   `copilot-capability-profile`, `devin-capability-profile`).
4. **Portable-name precedent** — `SquadSourceLoader.IsPortableMcpNamePart` (:489–495) already
   defines the charset a qualified `mcp__server__tool` name survives, enforced for
   `required-mcp-tools`; D8 reuses it for `pi-tools`' tool parts. The hyphen→underscore
   qualified-name transformation was live-verified only for context7's pair.
5. **Test suites** — `PiRendererContractTests` renders the real canonical source and pins
   `CapabilityToolContract` (:45–57, currently carrying the wrong `("network.read",
   ["mcp"])`), `ToolOrder` (:61, eight entries), the repurposable research-agent regression
   pin (:378–400), and the not-expressible predicate (:614–654, currently excluding
   `network.read`) — all re-pointed by T1. `SquadSourceTests` carries the loader validation
   contract (copilot-tools accept/reject battery :377–491) — the home for the D8 tests.
   `SquadCanonicalContentTests` has no per-agent frontmatter pins (D11). Canonical grant
   unchanged: `read-only` grants `network.read: allow`, `process.execute: deny`,
   `delegate: deny`; `network.publish` is `allow` on no profile.
6. **Live evidence (D6/T4 GREEN)** — probe sequence 2026-10-24, run twice: a subagent with
   the overlay wiring started the context7 server, resolved a library ID, and returned
   sourced docs; the scope-gate refusal without nested allowlisting was observed. The probe
   file was hand-authored and carried `bash`; the canonical render differs only by
   `bash`'s absence.
7. **Deployment gap (finding, out of scope)** — `squad install/update --global` fails with
   "No Kyber-Squad release exists at version '0.1.0'": no published Squad release exists.
   Recorded for a future plan; not fixed here (§8). It defers the in-the-wild re-deploy (D5)
   but does not gate this plan: the rendered output is contract-pinned (T1) and the wiring
   is live-verified (T4).
8. **Doc surface (T3 rework list)** — `docs/adr/0019-…md` uncommitted amendment :76–86
   (wrong mechanism), `docs/kyber-squad/requirements.md` :104 (Pi row, eight-tool claim),
   `docs/kyber-squad/onboarding.md` :245–249 (tool-vocabulary paragraph). Corpus grep for
   `eight-tool`/bare-`mcp`-lowering claims covers exactly these plus this plan and its
   index row; ADR 0025's `network.read` text is Devin's and is untouched.

## 4. Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/PiRendererContractTests.cs` and `tests/KyberWeave.Tests/SquadSourceTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PiRendererContractTests\|FullyQualifiedName~SquadSourceTests"` | (a) Renderer revert pins: `CapabilityToolContract` is the four-entry map (no `network.read` row), `ToolOrder` is the seven-tool order, and a `network.read: allow` profile yields **no** `mcp` in any rendered `tools` CSV. (b) The not-expressible predicate **re-includes** `network.read: allow` subagents (`research-agent` and its read-only peers return to the expected set); the combined set assertion stays non-empty. (c) The research-agent deployed-wiring pin (repurposed from the uncommitted `…IncludeMcpWithoutBash` test): rendered `tools` CSV is exactly `read, grep, find, ls, ext:pi-mcp-adapter/mcp, ext:pi-mcp-adapter/mcp__context7__resolve_library_id, ext:pi-mcp-adapter/mcp__context7__query_docs` with `bash` absent, and `extensions` is the sequence `[pi-mcp-adapter]`. (d) Every other agent's `extensions` is the scalar `false` (default untouched); key order unchanged. (e) `SquadSourceTests`: valid `pi-extensions`/`pi-tools` parse onto `SquadAgent`; rejections — bare (non-`ext:`) name in `pi-tools`, `ext:` entry whose extension is absent from `pi-extensions`, empty or duplicate entries, non-portable name parts; the two keys are accepted by the unknown-field gate. | The updated suites fail against the current uncommitted code: the renderer still emits `read, grep, find, ls, mcp` for research-agent (pin (c) and revert pins (a) fail), the not-expressible predicate excludes `network.read` where the new expectation includes it, and the loader rejects the overlay keys as unknown fields (T1(e) parse test fails). Recorded in the task digest before T2 starts. | Both suites pass with no weakened expectation: revert pins hold, the wiring pin holds against the implemented overlay, the D8 validation battery passes, and every prior assertion (key order, `extensions: false`, `none` literal, degradations, `Assert.NotEmpty`) still holds. |
| T2 | No separate test file — T1's suites are the test surface; the whole .NET suite is the regression net. | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --no-build` | Renderer reverts to the seven-tool vocabulary and lowers the overlay per D8/D9; loader/validator/schema accept and enforce the two keys; `research-agent` declares the D10 overlay. | RED already established by T1 (the revert expectations and the overlay pin fail against the uncommitted code). | Full suite green (T1 suites plus all Squad renderer/canonical-content/packaging suites), `TreatWarningsAsErrors` + `AnalysisMode=all` build clean. |
| T3 | Documentation edits — verification is `docs validate . --merge-ready` and `docs drift .` (read-only checks over the edited corpus). | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready` and `... docs drift .` | The reworked ADR 0019 amendment and the requirements/onboarding rows state the corrected mechanism (overlay, bare-`mcp` refusal, `network.publish` residual, `extensions: false` default) — no doc still claims the eight-tool vocabulary or a bare-`mcp` lowering. | Genuinely no-unit-test task (documentation only): the read-only verification that replaces it is both docs checks returning zero findings, plus the corpus grep proving no surviving `eight-tool`/bare-`mcp`-lowering claim. Recorded in the task digest. | Both checks zero findings; grep clean. |
| T4 | Live harness — **pre-satisfied, no dispatch required.** | n/a (evidence recording only) | n/a | n/a | GREEN evidence recorded in the completion summary and closeout: probe sequence 2026-10-24 (run twice) — a subagent with `extensions: [pi-mcp-adapter]` + the `ext:` allowlist started the context7 server, resolved a library ID, returned sourced docs; the scope-gate refusal without nested `mcp__<server>__<tool>` allowlisting was observed. Caveat recorded: the probe file was hand-authored with `bash` present; the canonical render differs only by `bash`'s absence, and frontmatter equivalence is pinned by T1(c). **Re-run trigger:** if T2's implementation deviates from the verified mechanism (different extension id, different emission form for `extensions`, or a different allowlist shape), the deviation is a new mechanism and T4 re-runs fail-closed in a live session before closeout. |

## 5. Tasks

### T1 — Contract test re-pointing (RED)

- **Objective:** Re-point the uncommitted contract tests at the corrected expectation: no
  `mcp` token from the renderer, the overlay wiring pinned on the rendered output, and the
  D8 validation battery — before any implementation rework, per the Test contract row above.
- **Files/symbols:** `tests/KyberWeave.Tests/PiRendererContractTests.cs` — `CapabilityToolContract`
  (:45–57, drop `("network.read", ["mcp"])`, rewrite the comment: R8 re-confirmed 2026-10-24
  — bare `mcp` is not a built-in; web access arrives via the canonical agent-file overlay),
  `ToolOrder` (:61, back to seven), remarks (:31–37, seven-tool vocabulary restored + overlay
  pointer), the repurposed research-agent pin (:378–400, assert the D10 wiring CSV +
  `extensions: [pi-mcp-adapter]` + `bash` absent), the not-expressible predicate (:614–654,
  re-add the `network.read` allowed condition; comment names `network.read` + `network.publish`
  + empty-roster as the R9/R10 cases), and a new assertion that every non-overlay agent's
  `extensions` scalar is `false`. `tests/KyberWeave.Tests/SquadSourceTests.cs` — the
  `pi-extensions`/`pi-tools` accept/reject battery per T1(e), modeled on the copilot-tools
  battery (:377–491).
- **Acceptance:** RED evidence captured per the Test contract row (suites fail against the
  current uncommitted renderer/loader); no existing assertion deleted or weakened (the
  `Assert.NotEmpty` on the combined not-expressible set stays).
- **Dependencies:** none (disjoint from T2's file scope; sequenced RED-first).
- **Required skills:** `test-dev`; the repo's test coding standard at **<test-coding-standard>**.

### T2 — Renderer revert + overlay mechanism (GREEN)

- **Objective:** Revert the uncommitted renderer edit and implement the canonical overlay
  vehicle end to end per D7/D8/D9/D10.
- **Files/symbols:** `src/KyberWeave.Core/Squad/Rendering/PiRenderer.cs` — revert
  `CapabilityTools` (:117–128, drop the `network.read` row), `ToolOrder` (:133, seven
  entries), and the vocabulary remarks (:59–69, seven-tool; point at the agent-file overlay
  as the web-access mechanism; record the 2026-10-24 bare-`mcp` refusal); restore the
  `network.read` not-expressible branch in `BuildSubagentDegradations`; in
  `RenderSubagentAgent` (:326–331) emit `extensions` as the declared sequence or `false`,
  and append the declared `pi-tools` after the capability-derived CSV (D9).
  `src/KyberWeave.Core/Squad/Model/SquadSource.cs` — `SquadAgent` gains
  `PiExtensions`/`PiTools` fields. `src/KyberWeave.Core/Squad/Parsing/SquadSourceLoader.cs`
  — `EnsureOnlyFields` (:578) + parsing + the D8 name rules (reuse
  `IsPortableMcpNamePart`). `src/KyberWeave.Core/Squad/Validation/SquadSourceValidator.cs`
  — the D8 cross-checks (ext: form only; extension declared in `pi-extensions`).
  `products/kyber-squad/schemas/agent.schema.json` — the two optional keys.
  `products/kyber-squad/agents/research-agent.md` — the D10 overlay in frontmatter
  (nothing else in `products/` changes).
- **Acceptance:** T1's suites green; full .NET suite green; Release build clean under
  `TreatWarningsAsErrors` + `AnalysisMode=all`; house-style `<remarks>` record the reversal
  and the mechanism, not just the new fact; every non-overlay agent's render byte-identical
  to the pre-plan baseline.
- **Dependencies:** T1 (consumes its RED contract).
- **Required skills:** `csharp-dev`; **<csharp-coding-standard>**.

### T3 — Canonical documentation and ADR amendment rework

- **Objective:** Rework the uncommitted T3 docs to state the corrected mechanism per D3.
- **Files/symbols:** `docs/adr/0019-pi-native-subagents-and-primary-lowering.md` (rewrite
  the uncommitted 2026-10-24 amendment :76–86: record the D1 reversal explicitly — bare
  `mcp` proven invalid — then the overlay mechanism, the probe-evidence basis, the
  `network.publish` residual, and `extensions: false` as the untouched default);
  `docs/kyber-squad/requirements.md` (:104, Pi row: seven-tool vocabulary; read-only MCP
  access via the canonical per-target overlay on `research-agent`; `network.publish` and
  lowered-primary residuals); `docs/kyber-squad/onboarding.md` (:245–249, tool-vocabulary
  paragraph: same correction; one sentence that an agent carrying the overlay requires the
  `pi-mcp-adapter` extension in the deployment harness). Update `last-reviewed` dates per
  the corpus convention.
- **Acceptance:** `docs validate . --merge-ready` and `docs drift .` zero findings
  (conductor runs them if the executing harness lacks the CLI build); corpus grep shows no
  surviving `eight-tool` or bare-`mcp`-lowering claim.
- **Dependencies:** T2 (docs describe the final emitted behavior).
- **Required skills:** `kyber-weave-docs` (frontmatter/validate contract), `app-docs-standard`
  conventions for the edited app docs.

### T4 — Live MCP-grant verification (G2) — pre-satisfied GREEN

- **Objective:** Record, not re-run. The 2026-10-24 probe sequence (§3 item 6, D6) verified
  exactly the mechanism T2 implements. Evidence (tool invoked, subagent, wiring form) goes
  into the completion summary. The re-run trigger in the Test contract row is the only path
  back to a live check.
- **Dependencies:** T2 only insofar as T2 must not deviate from the verified mechanism.
- **Required skills:** none (evidence recording; a re-run, if triggered, is any live Pi
  session able to spawn the subagent).

### Closeout (`docs-dev`)

Standard corpus closeout per the plan-index policy: decision harvest mapping in the
completion summary (D1 reversal, D2, D7–D11 → the ADR 0019 amendment +
requirements/onboarding rows; this plan's Decisions table is permanent and preserved);
archive per `KW-DOC-LIFECYCLE-003` when the finishing PR merges. The closeout summary also
records the deployment-gap finding (§3 item 7) as an open item for a future plan.

## 6. Dependency graph

```text
T1 (RED, tests) ──► T2 (GREEN, revert + overlay) ──► T3 (docs + ADR rework) ──► closeout
                                                  └► T4 (GREEN evidence, no dispatch)
```

T4 is pre-satisfied and gates nothing unless the re-run trigger fires. T1's RED evidence
must exist before T2 starts (test-first sequencing). T3 follows T2 because the docs state
the final emitted behavior.

## 7. Risks

| Risk | Mitigation |
|---|---|
| The overlay emission deviates from the verified probe form (e.g., CSV-scalar `extensions`, reordered keys) and silently repeats the accepted-but-dead failure mode. | D9 pins the emission to the exact verified form (YAML sequence); T1(c) pins it in the rendered output; the T4 re-run trigger fires a fail-closed live re-check on any deviation. |
| A canonical `pi-tools` entry smuggles a built-in and bypasses the capability lattice. | D8: `ext:`-form-only validation in loader + `SquadSourceValidator`, with a dedicated `SquadSourceTests` rejection battery (T1(e)). |
| The overlay keys widen `kyber-squad.agent/v1` in a way other targets must eventually honor. | The keys are Pi-prefixed and additive-optional; no other renderer reads them; every other target's output is untouched (D7 blast radius: Pi renderer + squad source only). |
| `pi-mcp-adapter` is absent on a deployment machine, so the rendered overlay references a missing extension. | The overlay is opt-in per agent (D7); `research-agent` is the only carrier (D10). Onboarding gains one sentence stating the extension prerequisite (T3). The unverified loader behavior for a missing extension is a recorded caveat in the ADR amendment, not an assumption. |
| Rework of uncommitted files loses prior GREEN coverage silently. | COM-B honored: §10 re-gates everything; the prior 3126/3126 GREEN is explicitly void for changed files. |
| Squad release-path breakage from the canonical-source/schema edits (pack includes `products/kyber-squad`). | Full suite includes the Squad packaging/release suites; `./scripts/update-loop.sh` is a required gate (§10.4). The pre-existing `--global` deployment failure is out of scope (§8). |
| `docs validate` / `docs drift` cannot be run in the authoring harness. | Conductor runs both before execution and again at closeout (G3, same disclosure as revision 1). |

## 8. Out of scope

- **Deployment gap:** `squad install/update --global` failing with "No Kyber-Squad release
  exists at version '0.1.0'" (no published Squad release). Recorded as a finding (§3
  item 7); fixed by a future plan, not here.
- `network.publish` mapping — still no built-in Pi tool; still records
  `permission-not-expressible` (D2).
- No overlay for any agent other than `research-agent`, and no `ext:` entries for servers
  other than the verified context7 pair (D10) — extension is one source edit per verified
  server later.
- No changes to `capabilities.yml`, model profiles, other renderers, or the
  deployment/transaction machinery; no `squad doctor` addition; no new dependencies.
- No `KW-*` rule-id changes; no settings.json changes (`-builtin:mcp` stays).
- No handling of Option B (`network.read → bash`), rejected on least-privilege grounds in
  D1 and not re-weighed.

## 9. In-the-wild deployment consequence (owner call-out, per D5)

Relative to the pre-plan baseline, only `research-agent`'s rendered Pi file changes: it
gains `extensions: [pi-mcp-adapter]` and the three `ext:` tool entries; `bash` stays absent
(read-only, no shell — the boundary this plan keeps). Every other agent's render is
byte-identical to before this branch started.

The hand-patched `~/.pi/agent/agents/research-agent.md` (carrying `bash, mcp`) is
superseded at the next successful deploy: the patch's purpose (web access) is served
canonically, and `bash` disappears again by design. **However**, `squad install/update
--global` currently fails (§3 item 7), so the overwrite is deferred until that separate
defect is fixed; until then the manual patch remains the only wired file on this machine.
Machines that never re-run a successful update keep their old renders; there is no remote
push mechanism, and none is added by this plan.

## 10. Verification gates

1. `dotnet format KyberWeave.sln whitespace --verify-no-changes --no-restore -v minimal` and the style pass.
2. `dotnet build KyberWeave.sln -c Release` (warnings-as-errors, `AnalysisMode=all`) and the full `dotnet test` suite — **re-run in full**: the prior 3126/3126 GREEN predates the revert and is void for every changed file.
3. `docs validate . --merge-ready` and `docs drift .` (editing `docs/` and ADR 0019 makes both mandatory; conductor runs them if the executing harness cannot).
4. **`./scripts/update-loop.sh`** — the Squad release path is touched (`PiRenderer` ships in the CLI; the canonical agent source and `agent.schema.json` ship in the pack), so the local release verification loop is required per root `AGENTS.md` / distribution.md. **Note: it needs `node` and `npm` on `PATH`** (it builds `kyberdash` too). It installs no tray and does not run `install.sh`.
5. G2/T4 — the live MCP-grant check is pre-satisfied GREEN with recorded evidence; the re-run trigger (Test contract, T4 row) is the only live-gate path back.
6. Per-task `task-gate` run by each developer task before handoff (repo convention).
7. Whole-change review (`review gates . --out artifacts/gates.json`) before the finishing PR; the PR that finishes this plan archives it (`docs validate --merge-ready` fails on open plans per `KW-DOC-LIFECYCLE-003`).

## 11. Review and closeout

Whole-change review per repo policy (deterministic gate suite plus council). Closeout
(`docs-dev`): harvest mapping — the D1 reversal, D2 re-confirmation, and D7–D11 → the ADR
0019 amendment (rewritten by T3); the corrected vocabulary facts → the
`requirements.md`/`onboarding.md` rows; D5 + the deployment-gap finding → §9 above and the
closeout summary's open item. This plan's `## Decisions` table survives archival intact.

## Design harvest

Targets are component + doc type. The closeout mapping below maps every H id and every
decision ID to the resolved docs.

| ID | Design item (section of this plan) | Target component | Target doc type |
|---|---|---|---|
| H1 | Canonical overlay keys `pi-extensions` / `pi-tools` (D7) | KyberSquad | architecture; onboarding; requirements |
| H2 | `ext:`-form-only validation, no built-in smuggling (D8) | KyberSquad | architecture |
| H3 | Renderer emission: YAML sequence, CSV append, byte-stability (D9) | KyberSquad | architecture |
| H4 | `research-agent`-only scope with verified entries (D10) | KyberSquad | requirements; onboarding |
| H5 | Reversal record: bare `mcp` invalid, Option A reversed, Q1-A intact (D1, D2) | KyberSquad | adr |

## Completion summary (T12 2026-10-25)

Revision 2 reverted the renderer to the pre-plan seven-tool vocabulary and implemented
the canonical overlay end to end (T1 RED contract re-pointing → T2 GREEN revert +
overlay → T3 docs + ADR 0019 amendment rework; T4 live gate pre-satisfied GREEN via the
2026-10-24 context7 adapter-path probes, run twice). Merged as `0351a45c`
(`feat(squad): pi per-agent MCP overlay via pi-extensions/pi-tools`). This T12 backfill
adds the scale frontmatter, the archived status line, this Design harvest, and the
Closeout mapping below — the Decisions table and every original section are untouched.
The T12 pass also wrote the missing overlay design into
`docs/kyber-squad/architecture.md` (Pi per-target overlay bullet: keys, validation,
emission, scope).

Evidence note for reviewer (T10): historical RED was observed 1× `003` on the still-open
current plan and 0× `004` on #425/#426 with `--base 8beb97ae~1`. Recorded as an evidence
mismatch; it does not block this backfill (T12 still meets C1–C9 in scope).

Open owner items (not residuals): the deployment gap (`squad install/update --global`
fails — no published Squad release — fixed by a future plan, out of scope here); the T4
re-run trigger is conditional on implementation deviating from the verified mechanism,
not an open gate.

## Closeout mapping

| ID | Destination |
|---|---|
| H1 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) — Pi per-target overlay bullet (added T12); [kyber-squad/requirements.md](../../kyber-squad/requirements.md), Pi row; [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md), tool-vocabulary paragraph |
| H2 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) — same bullet (validation rules); [adr/0019](../../adr/0019-pi-native-subagents-and-primary-lowering.md) amendment |
| H3 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) — same bullet (emission form); [adr/0019](../../adr/0019-pi-native-subagents-and-primary-lowering.md) amendment |
| H4 | [kyber-squad/requirements.md](../../kyber-squad/requirements.md), Pi row; [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md), tool-vocabulary paragraph |
| H5 | [adr/0019](../../adr/0019-pi-native-subagents-and-primary-lowering.md) amendment 2026-10-24 (reversal + intact Q1-A) |
| D1 | [adr/0019](../../adr/0019-pi-native-subagents-and-primary-lowering.md) amendment (reversal on verified live evidence; renderer edit reverted in T2) |
| D2 | [adr/0019](../../adr/0019-pi-native-subagents-and-primary-lowering.md) amendment (Q1-A intact) + [kyber-squad/requirements.md](../../kyber-squad/requirements.md), Pi row |
| D3 | [adr/0019](../../adr/0019-pi-native-subagents-and-primary-lowering.md) amendment itself (amend-in-place, no new ADR) |
| D4 | **Documentation waiver — voided design:** the `mcp` placement debate is moot after the D1 reversal (VOID); no surviving invariant; the record stays in this plan. |
| D5 | [kyber-squad/requirements.md](../../kyber-squad/requirements.md), Pi row + [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md), tool-vocabulary paragraph (overlay scope + byte-stability); the deployment gap is an open owner item, see Completion summary |
| D6 | [adr/0019](../../adr/0019-pi-native-subagents-and-primary-lowering.md) amendment (live-probe basis cited) |
| D7 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) — Pi per-target overlay bullet; [kyber-squad/requirements.md](../../kyber-squad/requirements.md), Pi row |
| D8 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) — same bullet (validation rules); [adr/0019](../../adr/0019-pi-native-subagents-and-primary-lowering.md) amendment |
| D9 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md) — same bullet (emission form); [adr/0019](../../adr/0019-pi-native-subagents-and-primary-lowering.md) amendment |
| D10 | [kyber-squad/requirements.md](../../kyber-squad/requirements.md), Pi row; [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md), tool-vocabulary paragraph |
| D11 | **Documentation waiver — transient test placement:** the assertion-location decision pins no doc invariant; the pins live in `PiRendererContractTests`/`SquadSourceTests` per the plan record. |
