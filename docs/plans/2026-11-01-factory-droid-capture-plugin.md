---
id: plans/2026-11-01-factory-droid-capture-plugin
title: "Factory (Droid) capture — a hooks-based Factory plugin and a KyberDash Settings row for Factory"
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-11-01
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberDash, KyberSquad]
---

# Factory (Droid) capture — a hooks-based Factory plugin and a KyberDash Settings row for Factory

**Status: Ready — approved for execution 2026-11-01 (D22).** Execution authority is the task
list in §5: T1–T21, plus the rework tasks T22–T33 added by the D28–D37 amendments below, each
with its RED/GREEN split, acceptance criteria and test command. The
conductor dispatches them; no task runs before it does. The reconciliation of the external
evidence digest and the local-environment digest (D12, D23–D26, A1–A10) is folded into this
version; the two documentation checks must be re-run by the conductor after these edits, because
the architect session cannot run them (§2.1). **Amendment 5 (D38–D42) adds no task**: it reconciles
this plan's decision text with the code that shipped, and closes the one open question the
earlier amendments left.

**Lifecycle note (D27).** The frontmatter `status` is `current`, not `ready`: the ontology's
status vocabulary is closed (`current`, `draft`, `needs-review`, `superseded` —
`src/KyberWeave.Core/Configuration/OntologyConfig.cs:30-31`), and any other value is a
`docs validate` error. The body status is authoritative when the two disagree
(`products/kyber-squad/agents/architect/references/plan-authoring.md`, "Start or resume"), so
**Ready** is this plan's lifecycle state, and the `## Active Plans` row in
`docs/plans/README.md` states it.

**Amendment (D28–D30, T22–T26).** The pairing test written under T13
(`dash/src/canon/sessions.droid-pairing.test.ts`) proved three gaps in this plan's accounting
rules, and this amendment decides them. **D28** states the Factory source-precedence rule — in a
`droid` session holding both plugin rows and file rows, the plugin rows are the session's turns
and the file rows are its counters, and neither source is counted twice. **D29** moves the child
session's token source from the parent's `childInclusiveTokenUsageBySessionId` map to the child's
**own** companion file, which changes D25's meaning. **D30** requires the plugin adapter's four
`not_measurable` reasons to name the file-source pairing, which D8 and D9 already required and the
shipped wording did not. D21's scope boundary is amended by the same stroke: a droid-scoped change
to the shared projection's session derivation is now in scope, and nothing else is. The plan's
lifecycle state is unchanged (**Ready**): this is the scope decision T13's acceptance criteria
required before a projection change was made, recorded as a decision and dispatched as ordinary
tasks. The `## Design harvest` table keeps every row it had and gains two (H13, H14). Provenance:
**autonomous: recommendation accepted**.

**Amendment 2 (D31, D32, T27–T30).** The native-OpenTelemetry group (T15/T16) is implemented and
green, and the implementer flagged two seams where the production path differs from the tested
path. **D31** decides where Factory's native OTLP metrics GO: a new canon store table,
`otel_metric` — **D38 corrects the shipped name to `metric_point`**, and **D42** the point's
identity — holding the decoded metric points, written by the production sink through a new
canon-layer module; the canon projection does **not** consume metrics in this branch (a todo); and
the writer's "never accepted-and-dropped" rule does **not** change. **D32** decides the status
rule at the normalization seam: an error status is never downgraded, so the `droid` adapter's
`error.type` status survives into the stored record — a rule that is inert for every other
harness, because the `droid` adapter is the only adapter that assigns a status. The amendment also
records two further divergences the implementer did not flag (the persisted-fact hook omits
metrics, so a metrics-only commit never moves the receiver's last-persisted fact; and the metrics
path has no test below the receiver's decode seam), and the harvest table gains two rows (H15,
H16). The plan's lifecycle state is unchanged (**Ready**). Provenance:
**autonomous: recommendation accepted**.

**Amendment 3 (D33, D34, T31).** The `csharp-dev` worker for T18 escalated with
`CIRCUIT_BREAKER_TRIGGER: INVARIANT_CONTRADICTION`: three legacy fixtures in
`tests/KyberWeave.Tests/UpdateCommandTests.cs` pin the reconciliation sequence as
`["engine","tray","plugin"]`, `["engine","tray","plugin"]` and `["plugin"]`, and the Factory design
adds a second plugin call. **D33** resolves it: the escalation is a **legacy detail in a coupled
fixture, not an architectural contradiction** — those fixtures pin WHICH reconciliation kinds run
and in what ORDER, and the count of plugin entries was incidental to what they were written to
prove. The step is harness-aware and runs one plugin call per capture-plugin harness in a fixed
order, and the three fixtures are updated **in place** to state that substance. **D34** answers
the flag question: the updater's Factory call **carries `--preserve`** (D11 already mandates
preserve mode for `kyber-weave update`, and the OpenCode call has carried it all along), and the
RED test pins the flag's **presence**; D18's and T18's wording is amended to be unambiguous, because
"the same with `--if-installed`" never said whether the mode travelled with it. **No production
change is required — the worker's implementation stands.** The plan's lifecycle state is unchanged
(**Ready**). Provenance: **autonomous: recommendation accepted**.

**Amendment 4 (D35–D37, D14 amended, T32, T33).** The documentation harvest surfaced a real
inconsistency and three smaller ones. **D35** decides the `--config-dir` flag on the Factory
plugin install: the flag is the **OpenCode** config root, the Factory path takes **no root** from
it, the Squad step stops passing it for Factory, and the Factory verb **refuses** it with a usage
error instead of ignoring it; the Factory installer's root stays `$FACTORY_DIR` else `~/.factory`
— the resolution the Factory file provider uses — so no precedence rule is added and the plugin
and the file source always pair from the same root. **D36** requires the harness-collection rule's
Factory row to state the D28 split (the plugin rows are the session's turns, the file rows are its
counters, neither is counted twice) — the row as written claims only the exact-id join. **D37**
keeps the plan's native-availability reporting as a **requirement** (D26 stands) while saying
plainly that §3.3 describes a design the shipped row does not yet implement. **D14 is amended in
place**: the shared-transport extraction did **not** keep the OpenCode bundle byte-stable — the
bundle's module paths changed and the shared body builder gained an identity argument — while the
OpenCode **wire** stayed unchanged and the drift guard still holds in its real sense (the committed
artefact is current, not unchanged). The plan's lifecycle state is unchanged (**Ready**).
Provenance: **autonomous: recommendation accepted**.

**Amendment 5 (D38–D42, no new tasks).** The second documentation harvest (commit `54b31d6`)
compared this plan's decision text against the shipped code and reported four places where the
plan no longer matched what was built, plus one question that had never been decided. Every
claim below was checked against the code as it stands on `feat/factory-droid-capture-plugin`;
where the harvest's report was itself imprecise, the plan records the code's truth. **D38**
corrects the metric destination's **table name**: the shipped table is `metric_point`, created by
the store's own `CREATE ... IF NOT EXISTS` schema and written through the production sink's
`upsertMetrics` and the `ingestMetricBatch` seam — not the `otel_metric` this plan named, which
no code ever carried. **D39** corrects the **model chain**: the plugin reads the hook payload
only, so a plugin turn names a model only when the payload carries one; the FILE SOURCE reads
the session's `model` from the companion `<session-id>.settings.json` beside the transcript and
publishes it wrapper-stripped; and the session publishes the model its turn records name, else
the counter record's model, and none where no source named one — never a default, never a guess.
`~/.factory/settings.json` is never a model source. **D40** records that the literal `unknown`
the file source publishes for a companion file that names no model is the **file source's own**
placeholder — pre-existing behaviour outside this plan's scope (D21), which this plan does not
change and which a reader must not mistake for a measured model. **D41** removes a **stale
marker**: D37's "not shipped yet" note was right when it was written and is wrong now — the
native-export availability reporting ships on every status surface, and the status row ships it
(commit `e140472`). **D42** settles the open question D31 left open: a metric point's identity
is its **content digest**, so the plan's "one row per decoded point" is corrected to "one row per
DISTINCT decoded point", and the duplicate case — reachable, and always the same point delivered
twice — is stated plainly. **No implementation change is required or made by this amendment**:
the shipped code is right, and it is this plan's text that was wrong. The plan's lifecycle state
is unchanged (**Ready**). Provenance: **autonomous: recommendation accepted**.

**Development mode:** `test-first` (supplied in the packet). **Autonomy:** `autonomous` (supplied
in the packet; matches the repository root `AGENTS.md` Config Reg `<autonomy>`).

Issue: [#485](https://github.com/dpalfery/kyber-weave/issues/485) — a hooks-based capture plugin
and a Settings row for Factory (Droid) in KyberDash.

Branch: not yet cut. Suggested: `feat/factory-droid-capture-plugin`, from `origin/main`
`7b307703`.

## 1. Problem and goal

### 1.1 Problem, as it stands in this checkout

Factory (`droid`) is a declared Kyber-Squad target (`docs/kyber-squad/onboarding.md:109`, target
`factory`) and KyberDash already ingests it — but only as a **file** source, and it has no
Settings row and no capture plugin:

| Gap | Evidence in this checkout |
|---|---|
| No Settings row: `CAPTURE_HARNESS_REGISTRY` has seven writers and Factory is not one of them | `dash/src/capture/harnesses/registry.ts:205-213` — `copilotHarness`, `claudeCodeHarness`, `codexHarness`, `opencodeHarness`, `piHarness`, `antigravityHarness`, `kiloHarness` |
| No plugin, no OTLP path for Factory | `dash/src/capture/harnesses/` has no `factory-plugin/`; the receiver serves `POST /v1/traces` and `POST /v1/logs` only (`dash/src/otel/receiver.ts:62-63`, `:1379-1386`) |
| The file path loses turn and tool timing, and the model attribution is a session-level guess | `dash/src/providers/droid.ts:113-290` — tokens come from the companion `*.settings.json` `tokenUsage` and are **split evenly** across assistant calls (`:196-232`); the model is the session's `settings.model`, never the per-turn model |
| The harness id the file source stores is `droid` | `dash/src/providers/droid.ts:384` (`name: 'droid'`), `dash/src/refresh/registry.ts:153,406-410` (`harnessId: 'droid'`, `sourceRootLabel: '~/.factory/sessions'`) |

The precedent to follow is the OpenCode capture plugin: one Kyber-owned plugin file, one receipt,
one consent file, one managed writer row, and a plugin pipeline from the host's hooks to
contract-3 OTLP spans to a canon adapter to the shared projection
(`dash/src/capture/harnesses/opencode-plugin/`, `dash/src/canon/adapters/opencode.ts`,
[ADR 0048](../../adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline.md)).

### 1.2 Goal

Make KyberDash fully operational for the Factory harness:

1. A Kyber-owned Factory **hooks plugin** (`kyberdash-factory`) that emits turn, tool and session
   spans as OTLP/HTTP to the loopback receiver, with content off by default behind a consent
   file, and that never fails the host and never writes the owner's Factory settings.
2. A **Settings row** for Factory (`displayName: 'Factory (Droid)'`) whose Enable installs the
   plugin and whose Disable removes it, reporting plugin state, receipt and consent state.
3. A canon **adapter** that claims the plugin's spans, so a live Droid turn produces a
   dashboard-visible session with model and tool attribution.
4. Honest token accounting: paired from the existing file source when the session ids agree,
   otherwise `not_measurable` with a stated reason — never a fabricated zero.
5. An **authorized, additive** native-OpenTelemetry branch (`POST /v1/metrics` and a
   `gen_ai.agent.name=droid` claim), because Factory's own documentation confirms its endpoint is
   user-configurable (`OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"` is Factory's own example).
   It complements the plugin rows — it carries no tokens and no subagent lineage — and never
   replaces them (D12, D23).

### 1.3 Explicitly not the end state

A status-only row (the same gap in another shape), native Factory OpenTelemetry as the primary
path, merging hooks into `~/.factory/hooks.json`, and file-only ingest are rejected for the
reasons recorded in D2 and in the issue.

## 2. Investigation findings

### 2.1 Discovery tooling — unavailable in the architect session, checks run by the conductor

The repository asks for `codegraph_explore` and the Kyber-Weave MCP documentation tools
(`docs_explore`, `docs_for_symbol`, `docs_glossary`) before text search, and for
`docs_validate` / `docs_drift` after a plan write (root `AGENTS.md`, "Exploration order" and
"Non-negotiables"). **Neither tool family is loadable in the architect session**: the only
deferred tools are `WebSearch` and `FetchUrl`, and every `ToolSearch` query — including the
exact-name form `select:docs_validate,docs_drift,docs_explore,codegraph_explore` — returned
"Every tool this session can load is named in the Deferred tools reminder … the capability is
unavailable in this session". The plan was therefore authored from **direct reads and targeted
greps** of the named files, and that discovery record stands.

**The documentation checks were run, by the conductor, and both passed.** Against this saved plan
and its index registration, over a CodeGraph-indexed checkout:

- `docs validate .` (mergeReady false, the planning gate while a plan sits in the active folder):
  **pass — 0 errors, 0 warnings**, 150 documents.
- `docs drift .`: **pass — 0 errors, 0 warnings**, 150 documents.

So the plan and its index row are schema-conformant and drift-free. The two checks must be re-run
after every further change to this plan or to the index, because the architect session cannot
run them itself. **The D28–D30, D31–D32, D33–D34, D35–D37 and D38–D42 amendments are such changes**: the two checks
must be re-run by the conductor against this amended plan, for the same reason and with the same
`--merge-ready` gate (the plan still sits in the active plans folder, so `mergeReady` stays false).

### 2.2 What the repository already provides (the reusable seams)

| Fact | Where |
|---|---|
| The registry holds one writer per harness; a managed writer may own a whole plugin file through `filePlugin` | `dash/src/capture/harnesses/registry.ts:151-203` (`ManagedHarnessWriter`, `ManagedFilePlugin`) |
| A plugin-only managed writer is the OpenCode shape: `kind: 'managed'`, `desiredKeys: () => ({})`, a `filePlugin`, no config file | `dash/src/capture/harnesses/opencode.ts:239-246`; `dash/src/capture/index.ts:268-280` (`isPluginOnly`) |
| Enable = strict install then consent; disable = consent off then uninstall | `dash/src/capture/index.ts:927-979` (`enableFilePlugin` / `disableFilePlugin`) |
| The Settings drawer is generic: it renders whatever the registry reports, so a new row needs **no web change** | `dash/web/src/components/settings/HarnessCollectionList.tsx:292`; `dash/web/src/lib/kyberApi.ts:1694,1947` (`CAPTURE_MAX_HARNESSES = 64`) |
| The capture route accepts any writer with a `filePlugin` | `dash/src/server/capture-routes.ts:81` (`MANAGED_IDS`) |
| Install modes: strict refuses a foreign or edited file (exit 1); preserve keeps it with a warning (exit 0) and creates the consent file with content off only when absent | `dash/src/capture/harnesses/opencode-plugin/install.ts:239-334` |
| The receipt is `{ version: 1, path, sha256 }`; the consent file is `{ version: 1, content: <boolean> }` | `install.ts:79,221-227`; `consent-write.ts:16-40` |
| A one-shot installer verb exists for out-of-drawer callers | `dash/src/capture/harnesses/opencode-plugin/install-command.ts`; registered at `dash/src/cli/register.ts:460-486` |
| Squad and `kyber-weave update` install the OpenCode plugin in preserve mode through that verb | `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs:161-169`; `src/KyberWeave.Cli/Update/SelfUpdater.cs:425-460` |
| The receiver decodes OTLP spans and hands them to a store port; adding a path means touching the route table and the pause/error-window predicates | `dash/src/otel/receiver.ts:62-63,1119-1121,1370-1386` |
| An adapter claims a harness by attribute fingerprint only, never by source name | `dash/src/canon/adapters/base.ts` (`HarnessAdapter`), `dash/src/canon/adapters/registry.ts:106-141` |
| The OpenCode plugin envelope is claimed only under a contract version; an unknown version quarantines | `dash/src/canon/adapters/opencode-envelope.ts:47-79`; `dash/src/canon/adapters/opencode.ts:281-300` |
| The plugin bundle is committed text, regenerated by esbuild and drift-checked | `dash/scripts/bundle-opencode-plugin.mjs`; `dash/src/capture/harnesses/opencode-plugin/bundle.test.ts`; `dash/src/architecture/opencode-leaves.test.ts` (leaf rule) |
| The file source for Factory and its token pairing already exist | `dash/src/providers/droid.ts:68-69` (`FACTORY_DIR` → `~/.factory`), `:129-135` (companion `*.settings.json`), `:196-232` (even split of `tokenUsage`), `:295-299` (internal-session skip) |
| The collection standard's mechanism hierarchy: plugin hooks are a PRIMARY mechanism; file ingest is fallback where a PRIMARY mechanism is declared | `docs/rules/kyberdash-harness-collection.md` ("Mechanism hierarchy") |

### 2.3 The display-layer constraint

`dash/web` and `dash/tray` are display layers: no feature logic, no schedulers, no jobs, one
datastore, one `/api/kyber/*` API (`dash/AGENTS.md`, `docs/rules/kyberdash-display-layer.md`,
[ADR 0033](../../adr/0033-kyberdash-surfaces-are-display-layers.md)). This plan therefore adds
**zero** web code: the Factory row appears because the engine's registry reports it. The
`dash/src/architecture/display-layer.test.ts` scan stays green.

### 2.4 The ownership constraint

ADR 0026 ("Kyber Utilities deploys owned files and never owns harness settings") and
[ADR 0031](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) (as amended
for the OpenCode and Kilo file plugins) set the bar: KyberDash may own a **whole file or file
tree** of its own, with a receipt and a digest, but it never edits a harness's shared settings
file. A Kyber-owned plugin directory under `~/.factory/plugins/` is the same shape as the OpenCode
plugin file: Kyber owns the tree, the receipt records every file's digest, and the owner's
`~/.factory/settings.json` and `~/.factory/hooks.json` are never read, written, merged or
restored.

### 2.5 Local-environment evidence (read-only inspection of this machine)

Empirical facts about the Factory installation this work runs against, from the local-environment
digest. They correct two premises of the issue text and they sharpen the model, token and
child-session rules.

| Fact | Consequence in this plan |
|---|---|
| `~/.factory/plugins/` **exists** and holds only `sync_stamp.json`; no Factory plugin is installed. Squad owns `~/.factory/droids/` and `~/.factory/skills/`, so the plugin subtree is a third, distinct, collision-free subtree | A1's discovery path is confirmed on disk (the live check remains the detector). D18's no-collision claim is evidenced, not assumed |
| **No `~/.factory/hooks.json`** exists on this machine, and there is no `~/.factory/plugins/hooks` | The byte-identity criterion must still be proven for an owner who *does* have one: the test **creates** `~/.factory/hooks.json` (and a project `.factory/hooks.json`) and proves both are byte-identical after install, enable, disable and uninstall (T7) |
| `~/.factory/settings.json` exists and its keys are `compactionModel`, `compactionTokenLimit`, `keepAwakeEnabled`, `logoAnimation`, `missionModelSettings`, `sessionDefaultSettings`, `subagentModelSettings`, `trustedFolders`. **There is no top-level `model` key**; the model lives in nested blocks (`sessionDefaultSettings.model`, `missionModelSettings.workerModel`, `subagentModelSettings.{light,medium,heavy}Model`, `compactionModel`) | **The issue's premise is wrong about this file's shape.** The model-resolution chain is corrected in D24; no nested settings block is ever read as "the model" |
| Every session directory holds a `<session-id>.settings.json` beside `<session-id>.jsonl`, with keys `model` (`minimax-m3.1-flash`), `tokenUsage` (`inputTokens`, `outputTokens`, `cacheCreationTokens`, `cacheReadTokens`, `thinkingTokens`, `factoryCredits`), `lastCallTokenUsage`, `inclusiveTokenUsage`, `childInclusiveTokenUsageBySessionId`, `providerLock`, `apiProviderLock`, `toolExecutionModeModelId`, `reasoningEffort`, `interactionMode`, `autonomyLevel`, `artifacts`, `allowSubagentsInScripts`, `compactionThresholdCheckEnabled`, `specModeModel` | The companion file — **not** `~/.factory/settings.json` — is the token and model source, which is what `dash/src/providers/droid.ts:129-135` already reads. D9 stands and is strengthened. `childInclusiveTokenUsageBySessionId` gives child-session tokens keyed by child session id (D25) |
| A Factory **child (subagent) session is a first-class session on disk**: the parent session `5f50fd96-…`'s companion file names the child `bfde2f1a-…` with input 368047 / output 41735 / cacheRead 1617280, and the child's **own** `<child-id>.settings.json` beside its own `<child-id>.jsonl` carries exactly those counters. The child's transcript's `session_start` names the child's id and its `callingSessionId` names the parent; the parent's `inclusiveTokenUsage` (369300 / 47551 / 1938106) equals the parent's own `tokenUsage` (1253 / 5816 / 320826) plus every child's entry | The child session is discovered by the file source as its own session, so a child's tokens come from **its own** companion file through the ordinary exact-id pairing (D29) and **no file-import change is needed**; the parent's `childInclusiveTokenUsageBySessionId` map is the parent's aggregate view of its children, never a token source, and a child's usage is never folded into the parent's totals. The characterization test that pins the map as ignored stays correct and unchanged (A9, A10) |
| Each transcript record carries `type` (`session_start`), `id`, `timestamp`, `message`, `seq`, `parentId`, `messageIndex`, `title`, `cwd`, `hostId`, `turnId`, `reason`, `resultKind`; an assistant `message` carries `modelId` (`minimax-m3.1-flash`), `apiProvider` (`fireworks`), `reasoningEffort`, `userMessageSource`, `openaiMessageId`. **No `usage` field exists on any message** | The per-message model is `message.modelId`, which D24 listed as a link of the chain. **AMENDED (D39): no shipped reader reads it** — the file source names the session's model from the companion file, and nothing reads the transcript's per-assistant model. `parentId` gives parent/child turn linkage that the native `genai` OTLP format does not carry (D2) |
| `~/.factory/org-managed-settings.cache.json` holds `factoryTier` (`team_annual`) and a `managedSettings` object with **only** `modelPolicy` and `routedModelVisibilityPolicy` — **no `telemetry` block** | For this user no organisation pins an endpoint, so a machine environment variable would take effect. The org-override caveat (D12, D26) is real but not universal |
| The installed Factory CLI reports version **1.1.0** | The version is recorded in the status surface and the live check so a future Factory change is diagnosable |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D0 | Development mode | `test-first`. Every unit below has a RED task and a GREEN task, separated adversarially. | Supplied in the packet; repository default. | ANSWERED — packet |
| D1 | Autonomy | `autonomous`. Open items are decided and recorded here; only a hard block would escalate, and none did. | Supplied in the packet; matches Config Reg `<autonomy>` in the root `AGENTS.md`. | ANSWERED — packet |
| D2 | Which collection path is primary? | **The hooks-based Factory plugin.** Not a status-only row, not native Factory OpenTelemetry, not file-only ingest, and not a merge into `~/.factory/hooks.json`. | It is the only path that yields turn, tool and session spans with per-event content gating and no dependency on org telemetry settings. The OpenCode plugin is the established precedent for a hook-based harness (`dash/src/capture/harnesses/opencode-plugin/`). The file source stays as the token source (D9). The external evidence strengthens this: the native `genai` format carries **no subagent lineage** — Factory's data reference states that subagent turns are separate trace roots and that the `parent_session.id` linkage "has no equivalent in the GenAI conventions", so a subagent's trace cannot be joined to its parent's (§2.5) — whereas the transcript's own `parentId` does carry that linkage, and the plugin's `SubagentStop` event carries the child session id. Native export is also **tokenless by documentation** (D12), so it can never be the primary path. | ANSWERED — autonomous: decided from the issue's rejected alternatives, the repository precedent, and Factory's own data reference |
| D3 | What exactly is the owned unit? | **A Kyber-owned plugin directory** `~/.factory/plugins/kyberdash-factory/` holding `.factory-plugin/plugin.json`, `hooks/hooks.json` and `hooks/kyberdash-factory.mjs`, installed and removed as one unit with one receipt. Project scope (`.factory/plugins/`) is **not** used: the capture is per-user and the owner may have many projects. | A directory is required because Factory's plugin shape is a manifest plus a hooks directory (issue body, confirmed by Factory's own documentation). The unit is Kyber-owned, so it is reversible; the owner's hook file is never touched (D4). The discovery root `~/.factory/plugins/` exists on this machine and holds only `sync_stamp.json` (§2.5), so the subtree is free of collision; the live check (T21) confirms the loader picks the installed plugin up, and it remains the detector for that residual (A1). | ANSWERED — autonomous: decided from Factory's documented plugin layout and the observed local plugin root; the residual live confirmation is A1's |
| D4 | Does anything write the owner's Factory files? | **No.** `~/.factory/settings.json`, `~/.factory/hooks.json`, `.factory/settings.json` and `.factory/hooks.json` are never created, read, merged, rewritten or restored — not by `enable`, not by `disable`, not by `squad`, not by `update`. They stay byte-identical. **AMENDED (D39): there is no read-only exception to state.** This row once excepted the companion per-session file `<session-id>.settings.json`, on the ground that the plugin reads it for one key — the session's `model` (D24). The shipped plugin reads **no** file at all: the committed bundle takes the hook payload from stdin and nothing else, so the exception described a reader that does not exist. The companion file is read by the **file source** (`dash/src/providers/droid.ts:122-134`), which is a different reader of the same file and never writes it. | ADR 0026 (never own harness settings) and ADR 0031's file-plugin amendments (own a whole file, never a shared settings file). `resolvePath` points at `~/.factory/settings.json` **only** as install evidence (D16), never as a write target. The byte-identity acceptance criterion is pinned by a test that creates an owner `~/.factory/hooks.json` and a project `.factory/hooks.json` first, because this machine has neither (§2.5, T7). | ANSWERED — autonomous: decided from ADR 0026 / ADR 0031; **amended** by D39 |
| D5 | Wire contract: reuse the OpenCode contract-3 keys? | **No. A new Factory contract, version `1`, with `factory.*` attribute keys and its own span names**, in its own leaf module `dash/src/capture/harnesses/factory-plugin/contract.ts`. | The OpenCode adapter claims any span carrying the contract-3 markers (`dash/src/canon/adapters/opencode-envelope.ts:47-79`), so reusing `opencode.*` keys would attribute Factory rows to OpenCode — a cross-harness mis-attribution, which the fingerprint rule (R6.2) exists to prevent. A separate contract also lets the two plugins evolve independently. | ANSWERED — autonomous: decided from the adapter fingerprint rule |
| D6 | Which canonical harness id do the plugin's rows carry? | **`droid`** — the id the file source already stores (`dash/src/providers/droid.ts:384`, `dash/src/refresh/registry.ts:406`). The plugin adapter is therefore named `droid`. | One harness bucket for one harness. The dashboard shows Factory once, with the plugin's turns and the file source's tokens in the same bucket. Display name: `Factory (Droid)`. | ANSWERED — autonomous: decided from the existing file-source id |
| D7 | One adapter or two? | **One adapter, named `droid`**, claiming (a) the Factory plugin envelope (`factory.contract_version = 1`) and (b) native `gen_ai.*` spans carrying `gen_ai.agent.name=droid` — the native claim is authorized (D12 branch A) and governed by D23's precedence rule. The plugin envelope is checked first; a span is claimed by one rule only. | `AdapterRegistry.register` throws on a duplicate name (`dash/src/canon/adapters/registry.ts:73-79`), so two adapters named `droid` are impossible. Precedence is explicit so the plugin path's rows never depend on the native claim, and so a native span and a plugin row for one session are never summed (D23). | ANSWERED — autonomous: decided from the registry's uniqueness rule |
| D8 | How do plugin turns and file tokens reach one session? | **By exact session-id equality only.** The plugin stamps the hook's `session_id`; the file source stores the `session_start` entry's id (`dash/src/providers/droid.ts:150-153`). When they are equal the shared projection groups them into one session and the file row carries the tokens; when they differ, the plugin records carry `not_measurable` with a reason naming the file-source pairing. **No fuzzy join, no time-window join, no token fabrication.** | **Assumption A2** (below): the hook's `session_id` equals the jsonl `session_start` id. The rule is exact-match, so a wrong assumption degrades to `not_measurable`, never to a wrong number. | ANSWERED — autonomous: low-confidence, no authoritative source (cost of wrong: sessions split in two; mitigated by the exact-match rule and the live check) |
| D9 | Where do tokens come from? | **Only from the file source**, through D8's exact-id pairing. The plugin **does not** read `transcript_path` and does not read `~/.factory/settings.json`; it never writes a zero. A plugin record with no paired file row declares `token_usage` (and the counter classes) `not_measurable` with a reason that names the file-source pairing, the way the OpenCode adapter declares an absent counter. | The hook payload carries no counters (issue body, and Factory's own data reference: token and cost data "are not exported as customer OTEL metrics"). The local evidence confirms where the counters actually live: the **companion per-session file** `<session-id>.settings.json` beside the transcript, whose `tokenUsage` carries `inputTokens`, `outputTokens`, `cacheCreationTokens`, `cacheReadTokens`, `thinkingTokens` and `factoryCredits` — which is exactly what `dash/src/providers/droid.ts:129-135` already reads, and **not** `~/.factory/settings.json`. The plugin never reads the transcript, which would be a second, ungated content reader. Honest unobservability is the rule (`docs/rules/honest-unobservability.md`). | ANSWERED — autonomous: decided from the issue's token rule, the local environment evidence, and the honest-unobservability rule |
| D10 | Content consent | **Off by default and never turned on by enabling the plugin.** The consent file is `~/.kyberdash/factory-plugin.json` (`{ "version": 1, "content": <boolean> }`). The install creates it with `content: false` only when it is absent. `capture enable` (the Settings switch) installs the plugin and **does not** change consent. The owner turns content on explicitly, through a new verb `kyberdash kyber capture plugin consent --harness factory --on|--off`, or by editing the file. `KYBERDASH_FACTORY_CONTEXT=1|0` overrides it, mirroring the OpenCode override. | The issue's acceptance criterion: "enabling the plugin never turns content on". This **differs** from OpenCode, whose Settings switch also sets consent (`dash/src/capture/harnesses/opencode.ts:63-66`), and the difference is deliberate: for Factory the switch is "install the capture", not "start recording my prompts". The verb is additive and cheap to drop if the owner prefers editing the file. | ANSWERED — autonomous: decided from the issue's acceptance criterion |
| D11 | Install modes and refusal | **Strict** for `capture enable` (a foreign or owner-edited plugin directory is refused with exit 1 and nothing is written) and **preserve** for `squad` / `kyber-weave update` (kept with a warning, exit 0). The receipt records every installed file's path and SHA-256; removal requires every digest to match, so an edited file is left in place. An absent plugin with `--if-installed` writes nothing. | The OpenCode installer's exact semantics (`dash/src/capture/harnesses/opencode-plugin/install.ts:239-334`), extended from one file to a tree. The receipt shape grows from `{version,path,sha256}` to `{version, root, files:[{path,sha256}]}` — a superset, not a replacement. | ANSWERED — autonomous: decided from the OpenCode installer |
| D12 | Is Factory's native OpenTelemetry a second path? | **ANSWERED — branch A: the endpoint IS user-configurable to an arbitrary local address, so the native group (T15/T16) is AUTHORIZED.** The endpoint is set by **machine environment variables** or by an **org-managed `telemetry` block**; there is **no personal `settings.json` telemetry key** (a `telemetry` block in a user or project settings file is ignored with a warning, "read from the organization level only"). The documented machine variables are `OTEL_TELEMETRY_ENDPOINT` (base URL; metrics → `{endpoint}/v1/metrics`, spans → `{endpoint}/v1/traces`; falls back to `OTEL_EXPORTER_OTLP_ENDPOINT`), `OTEL_TELEMETRY_HEADERS` (falls back to `OTEL_EXPORTER_OTLP_HEADERS`), `OTEL_LOG_MESSAGE_CONTENT`, `OTEL_METRIC_EXPORT_INTERVAL` (default 60000), `OTEL_CUSTOMER_ENABLED`, `OTEL_TELEMETRY_FORMAT` (`legacy` \| `genai`), `OTEL_TELEMETRY_GRANULARITY` (`user` \| `aggregate`), `OTEL_RESOURCE_ATTRIBUTES` and `DROID_PARENT_SESSION_ID`. **Factory's own documented example is `export OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"`** — the KyberDash receiver address. The endpoint must be `http`/`https` with no query string or fragment. **No collector is bundled or required**: Droid pushes OTLP/HTTP directly to the endpoint, so KyberDash is simply the customer's own collector. Only OTLP/HTTP is documented — port 4318, `/v1/metrics` and `/v1/traces`; no gRPC path, no `OTEL_EXPORTER_OTLP_PROTOCOL`, no port 4317. **Signals:** default is metrics only, delta temporality, 60-second flush. Under `legacy`, spans exist only as the content spans `droid.message.user`, `droid.message.assistant`, `droid.tool.call`, `droid.tool.result`, and only when `OTEL_LOG_MESSAGE_CONTENT=true`; `aggregate` + `legacy` is metrics only, zero spans. Under `genai`, the **trace structure is emitted whenever a customer endpoint is configured** and the content flag gates only the four content attributes; `aggregate` + `genai` keeps the full trace with the content attributes stripped. **Logs are never exported to a customer endpoint**, so KyberDash's `POST /v1/logs` receives nothing from Droid. **Tokens and cost are never exported** — the documentation states affirmatively that they "are not exported as customer OTEL metrics" and directs users to Factory's hosted org-level Analytics API; there is no `gen_ai.usage.*` metric and no token attribute. | Consequences of branch A, all now binding: the receiver grows a third signal path (`POST /v1/metrics`, T15/T16); the `droid` adapter gains a native claim with the exact vocabulary in §3.4; native spans are **tokenless**, so they can never replace the plugin or the file pairing (D9); the `genai` format carries **no subagent lineage**, which the plugin path does carry (D2); and the native path is **advisory** on the developer machine but **silently unavailable** where an organisation pins an endpoint (D26). The plugin path is unaffected by every one of these facts. | ANSWERED — autonomous: accepted from the research digest (Factory's own documentation, verified twice); the digest's branch A is confirmed by Factory's documented `OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"` example |
| D13 | How is the plugin script delivered? | **One self-contained ESM file**, `hooks/kyberdash-factory.mjs`, bundled by esbuild from `dash/src/capture/harnesses/factory-plugin/entry.ts` and committed as generated text (`factory-plugin/generated/plugin-source.ts`), exactly like the OpenCode bundle. Its first line is the ownership marker `// kyberdash-factory-plugin v1`. | A plugin tree cannot resolve `node_modules`, so the script must be self-contained. The committed text keeps npm and SEA builds on the same bytes, and the existing drift-test pattern (`bundle.test.ts`) proves it. | ANSWERED — autonomous: decided from the OpenCode bundle contract |
| D14 | Shared code: transport and consent writes | **Extract the generic parts into shared leaves and keep the old paths as re-export shims.** `dash/src/capture/harnesses/transport/{outbox.ts,otlp-sender.ts}` and `dash/src/capture/consent-file.ts` **became** the shared modules; `opencode-plugin/transport/*.ts` and `opencode-plugin/consent-write.ts` **became** thin re-exports, so the OpenCode entry's imports and its tests are unchanged. **AMENDED: the shims did NOT keep the committed bundle byte-stable** — the shared modules' module paths changed inside the bundle, and the shared OTLP body builder gained an **identity argument** (`OtlpTracesIdentity`: `serviceName`, `scopeName`, `scopeVersion`) whose defaults are the OpenCode ones. The committed artefact was regenerated; the **OpenCode wire is unchanged** (the emitted body still names `kyberdash-opencode-plugin` and the OpenCode scope), the OpenCode suite is green, and the drift guard still holds **in its real sense**: it asserts the committed source EQUALS a fresh bundle of the entry, so it proves the committed bytes are CURRENT, not that they never changed. | Copying the outbox would fork a bounded, security-relevant component; the repository's review lenses flag duplication. **AMENDED:** the original claim — "the shim pattern keeps the OpenCode bundle byte-stable (its drift test proves it)" — was **wrong**: the drift test proves the committed artefact is current, not unchanged, and the extraction regenerated it. What the shim pattern did deliver is the intent: no duplicated transport or consent code, the OpenCode entry's imports unchanged, the wire unchanged, and the suite green. The cost was a regenerated committed artefact — caught and re-committed by the drift guard rather than shipped silently, which is what the guard exists for. The Factory plugin adds a **file-backed** spool (D15) rather than reusing the in-memory outbox, so the shared module contributes the OTLP body builder, the delivery error type and the retry/429 policy, not the queue. | ANSWERED — autonomous: decided from the duplication rule; **amended** — the byte-stability claim was wrong and is corrected here; the cost of wrong was a regenerated committed artefact, caught by the drift guard |
| D15 | How does a one-shot hook process deliver a span? | **A bounded synchronous POST plus a bounded file-backed spool.** The hook script reads stdin, maps the event to at most one span, POSTs it to `http://127.0.0.1:4318/v1/traces` with a ≤ 2 s abort deadline, and on failure appends it to `~/.kyberdash/capture/factory/outbox.jsonl` (bounded: 500 events / 4 MB, drop-oldest). Each run drains the spool first. Every error is swallowed, stdout carries only the hook protocol, diagnostics go to a bounded 64 KB tail file, and the script always exits 0. | The OpenCode plugin is a long-lived process with an in-memory outbox (`transport/outbox.ts:1-20`); a Factory hook script is one process per event, so an in-memory queue would lose the span when the process exits. The file spool is the file-backed analogue of the same policy (bounded, drop-oldest, retry with backoff, 429 halves the batch). Never fail the host is the issue's hard requirement. | ANSWERED — autonomous: decided (cost of wrong: a lost span on a transient receiver outage; mitigated by the spool) |
| D16 | The registry row's shape | **`kind: 'managed'`, `id: 'factory'`, `displayName: 'Factory (Droid)'`, `desiredKeys: () => ({})`, a `filePlugin`, `resolvePath` → `~/.factory/settings.json` honouring `FACTORY_DIR`.** Because it declares no config key and no `configFiles`, `isPluginOnly` is true, so `enable` = install, `disable` = uninstall, and no key is read, written or receipted. `resolvePath` is used only as install evidence. | The OpenCode shape (`dash/src/capture/harnesses/opencode.ts:239-246`). `FACTORY_DIR` is honoured because the file source honours it (`dash/src/providers/droid.ts:68-69`), so both paths agree on the root. Install detection gets its own probe group (the `~/.factory` directory **and** `~/.factory/settings.json`), because a `.factory` tree exists for a Droid that never wrote a settings file (`dash/src/capture/detect.ts:80-140`). | ANSWERED — autonomous: decided from the OpenCode row and the detect seam |
| D17 | Does the Settings drawer need a change? | **No.** The drawer renders the registry's rows generically; a managed row with a plugin state gets Enable and Disable. The plan pins the parity with a test rather than with web code. | `dash/web/src/components/settings/HarnessCollectionList.tsx`, `dash/web/src/lib/kyberApi.ts:1947`, `dash/src/server/capture-routes.ts:81`. ADR 0033 and the display-layer rule forbid moving logic into the surfaces. | ANSWERED — autonomous: decided from the display-layer rule |
| D18 | Does Squad / `kyber-weave update` install the plugin? | **Yes, in preserve mode, exactly as the OpenCode plugin is installed.** `KyberDashCapturePluginStep` runs `kyberdash kyber capture plugin install --harness factory --preserve` after a global Factory deploy — **with no root** (D35): the Factory installer's root is `$FACTORY_DIR` else `~/.factory`, and the step hands it none. The step is **harness-aware**: one plugin call per capture-plugin harness, in a fixed order with the OpenCode call first and the Factory call following it, and the **OpenCode** call is handed its own resolved global root. `SelfUpdater` runs `kyberdash kyber capture plugin install --harness factory --preserve --if-installed` — the same verb in the same preserve mode, with `--if-installed` and **no root**, because the plugin is installed where the harness already has it — and the OpenCode call it already made is unchanged (D33, D34). The Settings row carries a manual note naming both. | The Factory global root is `~/.factory` with no environment override (`src/KyberWeave.Core/Squad/Deployment/SquadGlobalRoots.cs:24,54,124`), and Squad already owns `~/.factory/droids/` and `~/.factory/skills/` — the plugin directory is a third, distinct subtree, so no collision (`docs/kyber-squad/onboarding.md:576`). The step never changes Squad's exit code. **AMENDED (D33, D34):** the step's shape (one call per capture-plugin harness, in a fixed order) and the updater's exact verb are now stated here, because "the same with `--if-installed`" did not say whether the preserve mode travelled with it — and D11 had already decided that mode. **AMENDED (D35):** the Factory call is handed **no root**: `--config-dir` is the OpenCode root and the Factory verb refuses it, so the plugin is always installed under the root Factory itself loads it from. | ANSWERED — autonomous: decided from the OpenCode step (`KyberDashCapturePluginStep.cs:161-169`) and the Factory global-root rule; **amended** by D33, D34 and D35 |
| D19 | Documentation impact | **`extends-component`; `impact-components: [KyberDash, KyberSquad]`.** A new ADR records the Factory capture decision; ADR 0031 is amended in place for the Factory row. | The choice — a hooks plugin and a Kyber-owned plugin directory rather than a settings edit or a native-OTel dependency — constrains future Factory work and had viable rejected alternatives. The KyberSquad component is touched only by the global-install step and its documentation. | ANSWERED — autonomous: decided from the ADR criteria and the repository's scale vocabulary |
| D20 | Live verification | **A live Droid turn is an owner live check, not a CI gate.** The automated proof is the unit suite plus a replay e2e through the production pipeline. The live procedure (install, one scripted turn, read the dashboard) is documented in the runbook and executed by the owner. | The OpenCode live gate is likewise owner-run and never part of the test suite (`docs/dash/runbook.md` §1b). A live check that calls a model cannot be a CI gate. | ANSWERED — autonomous: decided from the OpenCode precedent |
| D21 | Scope boundaries | **Out of scope:** the sibling coverage gap named in the issue (Cursor, Warp, ZCode, Devin, Command Code) — captured as a todo under the todo index, not implemented here; a Factory *file* import rewrite (the even token split in `providers/droid.ts` stays as it is, with a follow-up todo filed — see the supporting column); any change to `dash/web` or `dash/tray`; Factory's own `settings.json`/`hooks.json` content. **AMENDED (D28): any change to the shared projection's session-derivation rules** is no longer out of scope without limit — **one droid-scoped change is now in scope**, the Factory source-precedence rule (D28), and it is inert for every other harness. Every other projection change stays out of scope. | The issue names the sibling gap as a separate concern. On the even split: the local evidence shows the companion `<session-id>.settings.json` carries **`lastCallTokenUsage`** and **`childInclusiveTokenUsageBySessionId`** beside `tokenUsage`, so the even split of the session total across assistant calls (`dash/src/providers/droid.ts:196-232`) is a **known lossy approximation** that better per-call data exists for — not an accident. It stays out of scope for this plan (the plugin work does not depend on it), but T20 must file a todo under the todo index recording the loss and the two fields that would remove it, so the limitation is tracked rather than forgotten. **AMENDED (D29):** of those two fields, `lastCallTokenUsage` is the better per-call data; `childInclusiveTokenUsageBySessionId` is the parent's aggregate of its children (A9), not a per-call breakdown, so the todo's evidence is the `lastCallTokenUsage` field. The projection carve-out is narrow on purpose: the T13 test proved that the plugin's and the file's rows cannot be reconciled by any join, so one harness needs one explicit rule, and the alternative — making every file-sourced record in the corpus declare `token_usage` as measured — would change every harness's published declarations to fix a Factory problem. | ANSWERED — autonomous: decided; the even-split follow-up is recorded as a todo requirement in T20; the projection carve-out is **amended** by D28 |
| D22 | The plan's own approval gate | **APPROVED. The plan moves to Ready and is executable; execution authority is the task list in §5.** Scale `extends-component` with `impact-components: [KyberDash, KyberSquad]`, `development-mode: test-first` and `autonomy: autonomous` are confirmed. Every autonomous row D0–D27 is accepted as recorded, including the five rows added by the reconciliation (D23–D27) and the correction of the model-resolution rule (D24). No task runs until the conductor dispatches it. | The conductor supplied explicit `AUTONOMOUS_APPROVAL: approve-and-execute` under resolved autonomy `autonomous` (repository root `AGENTS.md` Config Reg). `products/kyber-squad/agents/architect/references/plan-authoring.md` ("Ready and finalization"). The two documentation checks had already been run by the conductor against this plan and index (`docs validate .` and `docs drift .`: 0 errors, 0 warnings, 150 documents) and must be re-run after the reconciliation edits, since the architect session cannot run them. | ANSWERED — autonomous: recommendation accepted (`AUTONOMOUS_APPROVAL: approve-and-execute`) |
| D23 | Double counting between native spans and plugin rows in the same `droid` bucket | **Precedence rule, in this order: (1) the plugin envelope is claimed first; (2) a native `genai` span is claimed only when the session has no plugin rows; (3) a native span for a session that already has plugin rows is NOT summed with them — it is attributed to the session as corroborating evidence and excluded from the session's turn, tool and duration totals.** The adapter distinguishes the two sources by their attribute fingerprints, never by name: the plugin envelope carries `factory.contract_version = 1` plus a `factory.` marker; a native span carries `gen_ai.agent.name = 'droid'` and `gen_ai.operation.name` and **no** `factory.` attribute. | The native `gen_ai.conversation.id` **is** the Droid session id, and `session.id` is present on every native data point, so a native span and a plugin row are joinable to the same session and the same turn — summing them would double count turns, tools and duration. The native format also has no per-turn identity that matches the plugin's, and no subagent lineage at all (D2). The rule is deterministic, testable at the adapter seam, and stated in the ADR before the branch ships (H11). The consequence for the dashboard: a session with plugin rows shows the plugin's turns and tools; a native span for that session is visible as evidence (the claim is recorded) but adds no turn, no tool and no duration. | ANSWERED — autonomous: decided from the native attribute vocabulary (D12) and the existing no-double-count rule the OpenCode pipeline follows |
| D24 | How is the model resolved? (The issue's premise is wrong about the settings file's shape.) | **AMENDED (D39) — this chain is superseded by the one that shipped; D39 states it in full.** The chain as written gave link (2) to the plugin and link (3) to the transcript, and neither reader does that: the plugin reads the hook payload only, and no shipped reader reads the transcript's per-assistant `message.modelId`. **As shipped:** (1) the PLUGIN names a model only when the hook payload carries one; (2) the FILE SOURCE reads the companion per-session file `<session-id>.settings.json`'s `model` key and publishes it **wrapper-stripped**; (3) the SESSION publishes the model its **turn records** name, else the **counter record's** model, and **none** where no source named one — never guessed, never defaulted, and never taken from a nested block of `~/.factory/settings.json`. | The local evidence shows `~/.factory/settings.json` has **no top-level `model` key** — the model lives in nested blocks (`sessionDefaultSettings.model`, `missionModelSettings.workerModel`, `subagentModelSettings.{light,medium,heavy}Model`, `compactionModel`), so the issue's "otherwise the `model` in `~/.factory/settings.json`" would have read a key that does not exist and, worse, would have had to guess among nested blocks (a subagent's `mediumModel` is not the parent's model). That part of this row stands and is confirmed by the shipped code: no reader opens `~/.factory/settings.json` for a model. **AMENDED (D39):** the companion file's `model` key **is** the session's actual model (`minimax-m3.1-flash` on this machine), and it is the **file source** that reads it (`dash/src/providers/droid.ts:122-134`), not the plugin — the committed bundle contains no settings read at all. The transcript's `message.modelId` is **not** a link in the shipped chain: the file source names the session's model from the companion file and prices against it, and nothing reads `message.modelId`. The token counters stay the file source's job (D9), and the plugin reads no file of the owner's at all (D4). | ANSWERED — autonomous: decided from the local environment evidence (the actual file shapes); **superseded** by D39 |
| D25 | Child (subagent) sessions and their tokens | **AMENDED (D29) — this row's subject changes: a child session's tokens are paired from the child's OWN companion file, not from the parent's child-usage map.** A child session's tokens are paired from **its own** `<child-id>.settings.json`, through D8's exact session-id pairing: a Factory child session is a first-class Factory session with its own transcript and its own companion file, the file source discovers it as its own session, and the plugin's `SubagentStop` span carries the same child session id — so the child's plugin rows and the child's file row pair by exact id, exactly as any other Factory session pairs. A child session whose own companion file is absent (or carries no `tokenUsage`) reads `not_measurable` with a stated reason; it never borrows the parent's counters and never borrows another child's. The parent's `childInclusiveTokenUsageBySessionId` map is **not** a token source, and a child's usage is never folded into the parent's session totals. The plugin's `SubagentStop` event still supplies the child session id; the parent-child linkage is exact-match only, and a child with no linkable parent stays an unlinked child, never a fabricated one. | The original row's subject was the parent's `childInclusiveTokenUsageBySessionId` map, on the evidence that the companion file carries it. That evidence holds, and it shows something the original row missed: the map is the parent's **aggregate view** of its children, and on this machine each entry is byte-for-byte the child's own companion-file counters, while the parent's `inclusiveTokenUsage` is the parent's own `tokenUsage` plus every child's entry (A9). The child session is therefore already a session the file source discovers, with its own usage already stated by its own file — so the child pairing needs **no file-import change at all**, and D21's boundary holds untouched. Folding a map entry into the parent would double count the child's tokens (they are the child session's own totals); folding it into the child would give the child a second source for a number its own file already states. The native `genai` format carries no subagent lineage at all (D2), which is a further reason the plugin path is primary. `DROID_PARENT_SESSION_ID` is a machine environment variable that adds `parent_session.id` for linking headless/CI sub-sessions **across processes** — it is not a subagent-to-parent link within a session, so it is not used for this. The characterization test that pins the map as ignored (`dash/src/providers/droid.test.ts:274`) stays exactly as it is: under this rule its title and its assertions are both correct. | ANSWERED — autonomous: **amended** by D29; the original answer (the parent's child-usage map as the source) is superseded by the child's own companion file, on the local evidence in A9 |
| D26 | What does KyberDash report when an organisation pins a telemetry endpoint? | **The native path is reported as unavailable, never as working.** The Factory row's status states that native OpenTelemetry export is configured by machine environment variables, that an organisation-managed `telemetry` block **overrides** those variables, and that where it does, Droid sends nothing to the local receiver — so the plugin path is the only capture path in that deployment. The status never claims the native path is available; it reports what is observed (native spans arriving or not) and names the override as the reason a deployment sees none. | Org-managed values override the machine environment variables, and a missing `${VAR}` reference disables the customer sink for that session — so a developer whose organisation pins an endpoint gets **no local export, silently**. The local evidence shows this user's org cache holds only `modelPolicy` and `routedModelVisibilityPolicy` and **no `telemetry` block**, so the caveat is real but not universal on this machine. The machine variables are documented as advisory ("Environment variables live on the developer's machine, which makes them advisory"), and only the org-managed `telemetry` block is a manager capability (`Settings → Enterprise Controls → Raw Configuration`). KyberDash cannot read an organisation's managed settings, so the honest report is the observed behaviour plus the named cause, not a guess about the organisation's configuration. | ANSWERED — autonomous: decided from the research digest's org-override rule and the local org-cache evidence |
| D27 | How is the Ready lifecycle state represented, given the ontology's closed status vocabulary? | **The frontmatter `status` stays inside the vocabulary — `current` — and the plan's lifecycle state is stated in the body, which is authoritative.** The body status line reads "Ready — approved for execution 2026-11-01 (D22)", and the `## Active Plans` row in `docs/plans/README.md` shows Ready. A `status: ready` frontmatter would be a `docs validate` error, because the ontology's vocabulary is `current`, `draft`, `needs-review`, `superseded` and any other value is `InvalidVocabulary` at Error severity. | `src/KyberWeave.Core/Configuration/OntologyConfig.cs:30-31` (the default status vocabulary) and `src/KyberWeave.Core/Docs/Validation/DocSpecValidator.cs:203-212` (the closed-vocabulary check); `products/kyber-squad/agents/architect/references/plan-authoring.md` ("Reconcile body status, frontmatter lifecycle, and index status in the same save. The body status is authoritative when they disagree."). Archived plans use `archived`/`complete` only because the loader excludes the archive folder from the corpus. The choice keeps `docs validate` at zero findings while the lifecycle state stays unambiguous to a reader of the plan and of the index. | ANSWERED — autonomous: decided from the validator's closed vocabulary and the plan-authoring rule that the body status is authoritative |
| D28 | How a `droid` session that holds **both** plugin rows and file rows is accounted for — the paired session's token totals and its turn count | **The Factory source-precedence rule: each source is used for what only that source can measure, and neither source is counted twice.** In a `droid` session holding at least one plugin row and at least one file row: (1) the **turn records** are the **plugin rows** — the turn count, the timeline's turns, the request roots, the per-turn model, the tool invocations, the duration and the context turns are read from the plugin, because the plugin is the PRIMARY mechanism (D2) and it alone observes the turn boundary, the tools and the timing; (2) the **counter records** are the **file rows** — the session's token totals (input, output, cacheRead, cacheCreation) and the availability that gates them are read from the file rows, because the file source is the only source of counters (D9) and its counters are a **session-level** total split evenly across assistant calls, so they belong to the session and never to a turn; (3) the file row in such a session contributes **nothing else** — it is not a turn, not a timeline entry, not a request root, not a tool invocation, and its duration is not added to the session's duration; it contributes its counters and its cost; (4) the session's **cost** is summed over the turn records and the counter records together (the two sets are disjoint, so no cost is counted twice), and the cost-basis verdict is computed over that same union; (5) the **context analysis** reads the turn records, and where no turn record carries a measured input it reads the counter records, so a Factory session keeps the context figures the file source gave it before the plugin arrived. A `droid` session with plugin rows and **no** file row keeps today's behaviour: the plugin rows are both the turn records and the counter records, so the plugin's `not_measurable` declarations and their reasons gate the totals. A `droid` session with file rows and **no** plugin row keeps today's behaviour: the file rows are both. Every other harness is untouched — the rule is inert outside `droid`. The rule is exact-match only: no fuzzy join, no time window, no counter-equality join. It never fabricates: a paired session's totals are the file row's counters, and a plugin-only session's totals are absent, never zero. | This generalizes D23's precedence principle — the plugin envelope wins and the other source is evidence only — from the native export to the file source, with the one difference the local evidence forces: the file row is **not** quarantined, because it carries the only counters and the only priced cost, so it stays as a counter carrier rather than being dropped. A turn-level **join** was rejected and cannot work: the twin-join rule pairs a file row with a non-file row only over **identical reported counters** (`dash/src/canon/twin-dedupe.ts:293-320`), the plugin row carries zeros, and an all-zero row is excluded from the counter clustering because it identifies nothing (`dash/src/canon/twin-dedupe.ts:191-196`); and because the file row's counters are a session total split evenly across assistant calls, there is **no turn-level correspondence** between a plugin turn and a file row to join over — a join would both mis-attribute the split and drop the counters of the rows it did not join. Before this rule the paired session read its totals as `not_measurable` — the plugin row's declaration won the merge because the file row's declaration map lists only what a file source **cannot** measure (`dash/src/canon/measurability.ts:425-486`, `dash/src/synth/synth.ts:534-537`) and says nothing about `token_usage` — and it counted two turns for one Factory turn. The alternative fix — making every file-sourced record declare `token_usage` as `measured` — was rejected: it would change what every file-sourced session in the corpus declares, for every harness, to fix a Factory problem, and it would have to guess whether a stored zero is a measured zero or an absent counter. The file source's rows carry no content and no tool records of their own (the Factory reader yields no reader turn, so `synthesizeToolCalls` returns none — `dash/src/synth/synth.ts:845-847`), so demoting the file row to a counter carrier loses no text, no tool and no attribution. | ANSWERED — autonomous: recommendation accepted |
| D29 | Where a child (subagent) session's tokens come from | **From the child's own companion file, through D8's exact session-id pairing — never from the parent's `childInclusiveTokenUsageBySessionId` map.** The map is the parent's aggregate view of its children, not the child's own usage, and the child's own companion file already states the child's own usage. A child session with no companion file of its own reads `not_measurable` with a stated reason; it never borrows the parent's counters and never borrows another child's. A child's usage is never folded into the parent's session totals. The parent-child linkage stays exact-match only, and a child with no linkable parent stays an unlinked child, never a fabricated one. | This is the amendment of D25, and the evidence is local and read-only (A9): the parent session's companion file names the child with counters that are exactly the child's own companion-file counters, the child's own transcript's `session_start` names the child's id (and its `callingSessionId` names the parent), and the parent's `inclusiveTokenUsage` is the parent's own `tokenUsage` plus every child's entry. So the child session is already a session the file source discovers, with its own usage already stated by its own file: the pairing is the ordinary D8 exact-id pairing and **no file-import change is needed**, which is what lets D21's boundary stand. The characterization test that pins the map as ignored (`dash/src/providers/droid.test.ts:274`) therefore stays exactly as it is — its title and its assertions are both correct under this rule — and no assertion is weakened anywhere. | ANSWERED — autonomous: recommendation accepted |
| D30 | Must the plugin adapter's `not_measurable` reasons name the file-source pairing? | **Yes, and D30 fixes the wording.** All four of the plugin adapter's counter-class reasons name the file-source pairing that would supply the counters, while still naming the harness and the metric. The reason is static — the adapter cannot know whether a file row will pair — so it names the **rule**, not the outcome: `token_usage` — "Droid telemetry did not report token usage for this span; the Factory session file's companion settings supply it when this session's id matches a file-sourced session."; `output` — the same sentence with "output token usage"; `cache_read` — "Droid did not export a cache read counter; the Factory session file's companion settings supply it when this session's id matches a file-sourced session."; `cache_creation` — the same sentence with "cache creation counter". | D8 and D9 already require the reason to name the file-source pairing ("a reason naming the file-source pairing", "a reason that names the file-source pairing"); the shipped wording — "Droid telemetry did not report token usage for this span." — did not, so the requirement was unmet rather than optional, and the adapter test only asserted the harness and the metric, which is why it passed. Under D28 the reason surfaces exactly where it should: a paired session's availability comes from the file rows, so the plugin's reason is published only when the pairing did **not** resolve — which is why a static rule-naming reason is accurate where it is published, and no projection-side rewrite is needed. The reason is never a claim that the tokens exist. | ANSWERED — autonomous: recommendation accepted |
| D31 | Where Factory's native OTLP metrics GO, and what "accepted" means for them | **AMENDED (D38, D42) — the destination is the canon store's own `metric_point` table, and a point's identity is its content digest.** The table is `metric_point`, created by the store's own schema and written through the production sink's `upsertMetrics` and the `ingestMetricBatch` seam; the `otel_metric` name this row carried was never in any code (D38). The canon projection does NOT consume metrics in this branch (a todo). The writer's "never accepted-and-dropped" rule does NOT change. (1) **The destination.** One row per **distinct** decoded metric data point — a point's identity is a **content digest** of everything the row carries, so two byte-identical decoded points collapse into one row and a re-delivered point lands on the row it already occupies instead of beside it (D42) — carrying the metric's name, its kind (`gauge`/`sum`/`histogram`/`exponentialHistogram`/`summary`), its unit, the aggregation temporality, the point's timestamp, the point's value — or, for a histogram or a summary, its count and its sum — the point's attributes, the resource attributes, the scope name, and the exporter's `service.name` as the row's **source**. A point that carries no value keeps it absent, never zero (`docs/rules/honest-unobservability.md`). The canon layer gains `dash/src/canon/metric-ingest.ts` with `ingestMetricBatch(metrics, canon)`, mirroring `ingestLogBatch(logs, canon)`. The table is added to `SCHEMA_SQL` as an `IF NOT EXISTS` statement, so **no schema-version bump and no migration step are needed** — the store applies the full schema on every open, and the `model_context_window_catalog` table landed the same way (`dash/src/canon/store.ts:553-565`, `:2983`). (2) **The production sink.** `dash/src/otel/service.ts`'s `toCanon` gains `upsertMetrics`, which calls `ingestMetricBatch` and then writes the fenced audit row `canon.logIngest('otlp:metrics', <points received>)` — the shape the span sink uses for its per-source rows and the log sink uses for its `otlp:logs` row. A metrics batch changes no projected record, so it requests no projection (the pending-log precedent). The metric rows carry the exporter's `service.name` as their source and **no harness attribution**: the source name is never harness evidence (R6.2), and a metric point has no span for the fingerprint vote to run on. The full corpus wipe clears the table; a scoped per-harness wipe does not, because those rows' harness attribution is unknown — the standing the unclaimed quarantine rows already have (`dash/src/canon/store.ts:145-151`). (3) **The writer's rule is unchanged.** It stays: a sink that cannot persist metrics does not offer the port, and the batch is refused rather than accepted-and-dropped (`dash/src/otel/writer.ts:50-58`, `:463-479`). The production sink now offers the port, so the refusal is unreachable in production and remains the writer's answer for a sink without one; the refusal path is currently untested and is pinned by T27. (4) **"Accepted and counted but not projected" is NOT compatible with that rule, and the rule is not weakened to accommodate it.** The rule's purpose is that the exporter's 200 means the batch was persisted; a count-only receipt keeps the batch's size and drops its values. An OTLP exporter pushes on its own cadence and does not resend a batch KyberDash answered 200 for, so the values would be gone permanently; and the repo has no "received and discarded" state — `ingest_log` records RECEIVED activity, the tables record STORED facts, and every other signal has a table (spans → `records`, logs → `pending_logs`/`quarantined_logs`). If a later build chooses to discard metric values, that is a change to the writer's rule and must be stated as one: the port's contract would become "durably receipted, values not persisted", the telemetry inventory would have to say so, and the loss would be accepted deliberately. | The production consequence of the seam as it stands: `dash/src/otel/service.ts:144` builds the production sink as `{ upsertMany, upsertLogs }` — no `upsertMetrics` — so a metrics batch finds no port in the writer, the writer refuses it (`writer.ts:463-479`), and the receiver answers **500** with the body code `OTLP_INTERNAL` while recording `OTLP_PERSISTENCE_FAILED` in the rolling failure window (`dash/src/otel/receiver.ts:1549-1562`). The refusal is not a corner case: it is every metrics request the running service receives, and it fills the window the receiver's greenness rule reads (`dash/src/jobs/receiver-host.ts:84-91`), so a Factory deployment with the native endpoint configured makes the receiver report failures and go not-green. The root cause is a coverage gap rather than a wiring mistake: the receiver's metrics cases hand the route a `RecordingStore` that offers the port directly (`dash/src/otel/receiver.test.ts:1103-1143`), so **nothing below the receiver's decode seam is exercised at all** — not the writer's queue, not the writer's refusal, not the production sink, not the store's persistence. The `pending_logs`-style pattern was considered and rejected: it exists because a log may arrive before the model call it enriches, and a metric point has no such dependency, so a pending table would be a terminal store wearing the wrong name. A service-owned metrics ledger was rejected too: the canon database is the one datastore (`docs/rules/kyberdash-display-layer.md`), and a second store would be invisible to the CLI and the dashboard. Two further divergences were found while checking this one, and both are repaired by T27/T28: the writer's persisted-fact hook counts spans and logs but not metrics (`writer.ts:666-676`), so a metrics-only commit — which is exactly Factory's default export shape (A6) — never moves the receiver's `lastAcceptedAt`; and the writer's metrics refusal has no test. **AMENDED (D38, D42):** the table's shipped name is `metric_point` (`dash/src/canon/store.ts:597-614`) and its key is a content digest under a `UNIQUE` constraint (`dash/src/canon/store.ts:593-600`, `:1762-1781`, `:3042-3060`), so "one row per decoded point" is "one row per **distinct** decoded point" — D42 states the consequence. | ANSWERED — autonomous: recommendation accepted; **amended** by D38 and D42 |
| D32 | The rule for a normalized record's status at the receiver-timing seam | **An error status is never downgraded.** `growWithReceiverTiming` keeps the record's own `error` status and takes the exporter's OTLP status code for every other value: `record.status = record.status === 'error' ? 'error' : span.status.code`. The repair lives in `growWithReceiverTiming` (`dash/src/canon/ingest.ts:143-147`), whose doc is amended: it gives the record the timing, and the outcome **unless the adapter already determined one**. A native Factory span carrying `error.type` therefore keeps the status the `droid` adapter set (`dash/src/canon/adapters/droid.ts:307-313`), so the stored record reads `error` and the policy-denied tool call counts as an error in the session's tool error counts (`dash/src/canon/sessions.ts:1534`) and in the run outcome derivation (`dash/src/canon/outcome.ts:296`, `:504`) — which is the whole point of the adapter's rule. The error type itself stays in the record's raw attributes: it is the evidence of **why** the call was denied, and the status only says that it was. | **The rule is inert for every other harness, and the proof is in the code:** `baseRecord` stamps `status: 'unspecified'` (`dash/src/canon/adapters/copilot.ts:666-694`), and the `droid` adapter is the **only** adapter under `dash/src/canon/adapters/` that assigns `record.status` — every other adapter's record still carries `'unspecified'` when `growWithReceiverTiming` runs, so its stored status remains the exporter's status code, byte for byte. The three alternatives were considered and rejected: (a) repairing it at the call site (`dash/src/canon/ingest.ts:641`) would split the function's contract across two places and would still have to ask "did the adapter set a status?", which the call site cannot answer; (b) making the adapter's error a separate field the projection reads would leave `record.status` wrong for every consumer that reads it — the stored column (`dash/src/canon/store.ts:3116`), the session's tool error counts, the run row (`dash/src/canon/runs.ts:219`) and the outcome derivation — and would change the projection for every harness; (c) repairing it in the adapter's record shape is the same as (b). The defect is invisible at the adapter seam because the adapter-level case asserts the record the adapter returned (`dash/src/canon/adapters/droid.test.ts:540`), while the production path calls `growWithReceiverTiming` immediately afterwards (`ingest.ts:641`): the RED test must therefore pin the **stored** record, which is what the production path writes. | ANSWERED — autonomous: recommendation accepted |
| D33 | The capture-plugin step's shape, and what the legacy update fixtures pin | **The step is HARNESS-AWARE: it runs ONE plugin call per capture-plugin harness, in a fixed order — the OpenCode call keeps its place and the Factory call follows it — on both sides (the Squad deploy step and the self-updater). **AMENDED (D35):** the OpenCode call is handed its resolved global root; the **Factory call is handed no root at all** on both sides — the Factory installer resolves `$FACTORY_DIR` else `~/.factory` itself — so the plugin always lands under the root Factory loads it from.** The three legacy update fixtures are updated **in place** to the two-plugin sequences, and their substance is unchanged: the engine runs first, then the tray, then the plugin reconciliation; the "already on" path still reconciles and still does not download; `--no-restart` still skips the engine and the tray and still updates the plugin(s). The modernization **states that substance** rather than a bare count — each plugin entry names the harness it belongs to. | **The escalation is a legacy detail in a coupled fixture, not a contradiction between two invariants**, and the evidence is in the fixture's own code: the legacy helper records EVERY call on the one capture seam and names it "engine" when the first argument is `engine` and "plugin" otherwise (`tests/KyberWeave.Tests/UpdateCommandTests.cs:846-855`), so the OpenCode call and the Factory call are recorded under the SAME step name and the sequence's plugin COUNT grew from one harness to two. The fixtures' doc comments state propositions that are all still true — "the engine runs first, then the tray, then the plugin" (`:902`), "the 'already on' path still runs the engine, tray and plugin reconciliation, and still does not download" (`:923`), "--no-restart skips the engine and the tray, but the plugin still upgrades when it is installed" (`:986`) — and none of them is about how many plugin calls the update makes. The worker's claim is confirmed: under D18 (the updater always runs the Factory verb with `--if-installed`) no implementation can satisfy the legacy counts, because the updater's Factory call is unconditional — `--if-installed` decides what the VERB does, not whether the call happens, and the only way to avoid the call would be for the updater to read the owner's Factory tree itself, which would put KyberDash's plugin paths inside the updater and contradict D18. So the legacy counts are stale under the approved design and the fix is the fixture's, not the design's. The worker's refusal to work around it — by skipping the Factory step on the "already current" path or under `--no-restart`, which would leave the Factory plugin stale in exactly the cases the OpenCode plugin is refreshed — is right, and is now the plan's requirement. The other legacy sequence is unaffected: `--no-kyberdash` returns before any plugin call, so that fixture still asserts an empty list (`UpdateCommandTests.cs:1024`). | ANSWERED — autonomous: recommendation accepted; **amended** by D35 |
| D34 | Must the updater's Factory call carry `--preserve`, and must the RED test pin it? | **Yes to both: the call is `kyberdash kyber capture plugin install --harness factory --preserve --if-installed`, and the RED test pins the flag's PRESENCE.** The mode travels with the verb, the way it already does for the OpenCode call; the updater passes `--if-installed` and **no root**, because the plugin is installed where the harness already has it. | D11 already decided the mode: "**preserve** for `squad` / `kyber-weave update` (kept with a warning, exit 0)". The OpenCode updater call has carried `--preserve` since before this branch (`PluginArgs`, `tests/KyberWeave.Tests/UpdateCommandTests.cs:883-886`), and the Factory call is the same verb for the same optional surface, so it must be the same mode: without `--preserve` the verb's strict mode REFUSES an owner-edited plugin with exit 1, so every update would report a failed plugin step for an owner-edited Factory plugin — while the sibling OpenCode plugin, edited the same way, is kept with a warning. The worker's reasoning is right and its implementation stands. The RED test must pin the **presence** of `--preserve`: the current case deliberately asserts neither, so a later change could drop the flag and turn every edited-plugin update into a warning with no test failing. The pin is the exact argument list — `["kyber","capture","plugin","install","--harness","factory","--preserve","--if-installed"]` — mirroring the `PluginArgs` constant the OpenCode case asserts exactly; it subsumes the case's present partial assertions (the verb's first four arguments, `--if-installed` present, no `--config-dir`) and adds the mode, so nothing is weakened. D18's and T18's wording is amended to be unambiguous, because "the same with `--if-installed`" never said whether the mode travelled with it. | ANSWERED — autonomous: recommendation accepted |
| D35 | What `--config-dir` means on the **Factory** plugin install | **Option B: the flag is the OpenCode config root and the Factory path takes NO root from it. The Squad step does not pass it for Factory, and the Factory plugin-install verb REFUSES it with a usage error (exit 2) instead of ignoring it. No precedence rule is added: the Factory installer's root stays `$FACTORY_DIR` else `~/.factory` — the resolution the Factory file provider already uses.** The OpenCode call is unchanged and keeps its `--config-dir`. | **A is rejected on a functional ground**: honouring the flag for Factory would let a caller select a root Factory never reads, and Squad's Factory root is `~/.factory` with no environment override, so a flag that won over `FACTORY_DIR` would break the D28 pairing. **C is incoherent**: the receipt and consent live under KyberDash's state directory, not the harness root. **B keeps one meaning for the flag**: its help text already says "OpenCode config root" (`dash/src/cli/register.ts:470`) and the command surface already says it "does not move the Factory tree" (`docs/cli-command-surface.md:394`). **Documents that must change:** the CLI command surface's plugin-install row and its exit-code row; the runbook's Factory plugin paragraph; the plan's D18 and D33; the harvest rows H7, H17 and H18. | ANSWERED — autonomous: recommendation accepted |
| D36 | Does the harness-collection rule's Factory row need a wording change once D28 lands? | **Yes.** The row must state the split, not only the join: the plugin rows are the session's turns, the file rows are its counters and its cost, and neither source is counted twice; a plugin-only session's token classes render `not_measurable` with a reason naming the file-source pairing and the session-id equality (D30's wording); a near-miss id stays its own session. The row's existing statements stand. | The harvested row (`docs/rules/kyberdash-harness-collection.md:37`) says the pairing is "joined on exact session-id equality" with the file source as the counter source — true, but incomplete: it does not say the two sources are **split** rather than summed, so a reader could take a paired session's turn count to be the sum of both sources' turns, which is the double count D28 removes. A harness row is ground truth (`:15-18`), so it has to say which source owns which figure. A harvest edit, not a code change: H7 carries the requirement and T20 lands it. | ANSWERED — autonomous: recommendation accepted |
| D37 | Is the plan's §3.3 claim about the status row reporting native-export availability accurate? | **AMENDED (D41) — the reporting SHIPPED, so the "not shipped yet" marker is removed and §3.3 describes the row as it stands.** D26's requirement stands unchanged: the row reports the native path as **unavailable** with the named cause, never as working, and names the organisation's pin as the cause where there is one. | The shipped row reported the plugin, the receipt, the resolved plugin directory and the consent (`dash/src/capture/harnesses/factory.ts:66-77`); it did **not** report the native-export availability or the organisation override, so this row's caution was right when it was written — the closeout must not claim what the row does not say, and nothing about the native path may be inferred from the row's silence. **AMENDED (D41):** the reporting is now part of the row: the Factory row carries the native OpenTelemetry export's availability and its named cause — available, unavailable with the organisation's pin as the cause, or not configured — on every surface the row reaches, the printed status, the structured row and the API route, because they all read the row's own status description (`dash/src/capture/harnesses/factory.ts:71-85`, `:100-113`, `:121`). The requirement that the row **say** the state rather than stay silent (D26) is met and shipped (commit `e140472`), so the marker this row asked for is stale and §3.3 must now describe the shipped row. | ANSWERED — autonomous: recommendation accepted; **amended** by D41 |
| D38 | What is the name of the canon table the native OTLP metric points land in? | **`metric_point`.** The shipped table is `metric_point`, created by the store's own schema and written through the production sink's `upsertMetrics` and the `ingestMetricBatch` seam. This plan's earlier text named `otel_metric`; that name was never carried by any code, and the shipped implementation and the landed harvest documents both say `metric_point` (`docs/dash/architecture.md:689-691`, `docs/dash/telemetry-inventory.md:317-318`). **The plan's text is corrected to the shipped name; no code changes, and the table is not renamed.** | The table is the store's own, applied on every open with `CREATE TABLE IF NOT EXISTS`, so no schema-version bump and no migration step are needed (the `model_context_window_catalog` precedent). Its columns carry the point's name, kind, unit and aggregation temporality, the verbatim nanosecond timestamp and its ISO reading, the value — or the count and sum — the point's attributes, the resource attributes, the scope, and the exporter's `service.name` as the row's source; a point that carried no value keeps it absent, never zero (`dash/src/canon/store.ts:597-614`). The row names no harness: a metric point has no span, so a scoped per-harness wipe cannot claim it and only the full corpus wipe clears it (`dash/src/canon/store.ts:93-96`, `:5015`). | ANSWERED — autonomous: decided from the shipped schema and the landed harvest documents |
| D39 | How is the model resolved, as the shipped code actually resolves it? | **The shipped chain, in three steps.** (1) **The PLUGIN names a model only when the hook payload carries one** — the mapper reads the payload's `model` and emits `factory.model.id` only then (`dash/src/capture/harnesses/factory-plugin/mapper.ts:202-203`). The shipped bundle reads the hook payload and nothing else, and Factory's own hook payload documents no `model` field, so a plugin-sourced turn **generally names none** — and an absent model stays absent all the way down: the adapter never defaults one (`dash/src/canon/adapters/droid.ts:163-168`, `:327-340`). (2) **The FILE SOURCE reads the session's `model`** from the per-session companion file beside the transcript, `<session-id>.settings.json`, and publishes it **wrapper-stripped** (`dash/src/providers/droid.ts:122-134`, `:270-274`). (3) **The SESSION publishes one model**: the model its **turn records** name, and where no turn record names one, the **counter record's** model; where no source named one, it publishes **none** — never a default, never a guess (`dash/src/canon/sessions.ts:1758-1766`). **`~/.factory/settings.json` is never a model source.** | All three steps are pinned by the shipped pairing test: the file source's model stands in when the plugin turn names none (`dash/src/canon/sessions.droid-pairing.test.ts:813-830`); the turn's own model wins and the file source's model is not published beside it (`:832-853`); and a plugin-only session whose turn names none publishes none (`:855-873`). The plugin never opens the companion file (D4), so the plan's old link (2) — the plugin reading the companion file for the model — described a reader that does not exist, and the transcript's `message.modelId` is not a link at all: no shipped reader reads it. `~/.factory/settings.json` has no top-level `model` key — its model lives in nested per-role blocks — and reading one would attribute a subagent's model to its parent, so no reader reads it for a model. | ANSWERED — autonomous: decided from the shipped mapper, the shipped file source and the shipped session projection |
| D40 | Is the `unknown` a Factory session can publish a measured model? | **No: the literal `unknown` is the FILE SOURCE's own placeholder, not a measured model, and this plan does not change it.** The file source's parser substitutes the literal string `unknown` for a companion file that names no model (`dash/src/providers/droid.ts:134`), and the shipped characterization test pins exactly that (`dash/src/providers/droid.test.ts:271`). This is pre-existing file-source behaviour, outside this plan's scope (D21): the plugin path never produces it, and an absent companion model is not a measurement. | Where it can surface: the file source's parsed call carries `model: 'unknown'`, the synthesized record carries it in its raw attributes (`dash/src/synth/synth.ts:513-514`), and the session's model reads those attributes (`dash/src/canon/sessions.ts:276-300`) — so a session whose plugin turn names none and whose companion file names none publishes that literal as its model. That is the file source's long-standing standing, unchanged here, and it is why the harvest's model chain names the placeholder explicitly rather than leaving a reader to take it for a measurement. Changing it would be a file-source change, which D21's boundary excludes; the follow-up is recorded, not done. | ANSWERED — autonomous: decided from the shipped parser and its characterization test |
| D41 | Is §3.3's "not shipped yet" marker still true? | **No: the native-export availability reporting SHIPPED, and the marker is removed.** The Factory status row reports the native OpenTelemetry export's availability and its named cause — available, unavailable with the organisation's pin as the cause, or not configured — on every surface the row reaches: the printed status, the structured row and the API route. D26's requirement — the native path is reported as unavailable with its named cause, never as working — is met and shipped (commit `e140472`). | D37 answered this question correctly for its moment: the row then reported the plugin, the receipt, the resolved plugin directory and the consent, and not the native path, so the closeout had to refrain from claiming it. The reporting is now part of the row's own status description (`dash/src/capture/harnesses/factory.ts:71-85`, `:100-113`, `:121`), so the marker is stale. Nothing else in D37 changes: D26 stands, and the requirement that the row **name** the cause rather than stay silent stands. | ANSWERED — autonomous: decided from the shipped status row |
| D42 | Is a metric point's identity per decoded point, or per content? | **Option A: the content-digest identity is correct and intended, and this plan's "one row per decoded point" is corrected.** A metric point's identity is a **content digest** of everything the row carries — its name, kind, unit, aggregation temporality, nanosecond timestamp, value (or count and sum), point attributes, resource attributes, scope and source (`dash/src/canon/store.ts:1762-1781`). That digest is the row's key under a `UNIQUE` constraint, and the write is an upsert that restates the row it already occupies rather than replacing it (`dash/src/canon/store.ts:593-600`, `:3042-3060`). So the table holds **one row per distinct decoded point**: two byte-identical decoded points collapse into one row, and a re-delivered point lands on the row it already occupies instead of beside it. **No implementation change is required, and none is made here.** | **The duplicate case is reachable, and every reachable duplicate is the same point delivered twice.** The receiver hands the decoded metrics over as they arrived, with no point filtered or aggregated away (`dash/src/otel/receiver.ts:687-700`), and the receiver persists a batch before it answers it — so a batch KyberDash stored and then failed to answer for is delivered again by the exporter's own retry, and the digest is what makes that re-delivery idempotent instead of doubling the rows. Two genuinely distinct measurements cannot collide: the nanosecond timestamp and the value are both part of the digest, and Factory's native export stamps a point at its collection instant (a 60-second flush, A6). **Consequence for a reader:** the table's row count is the count of *distinct* points, while the receiver-activity row under `otlp:metrics` records the points the batch *carried* (`dash/src/canon/metric-ingest.ts:47-54`, `dash/src/otel/service.ts:218-238`) — the two numbers differ when a batch repeats a point, and that difference is the intended one between what arrived and what is stored. **What it costs:** the multiplicity of byte-identical points is not preserved, and no shipped test pins the collision (`dash/src/canon/metric-ingest.test.ts:214` writes three distinct points), so a later change to the identity would be unobserved. That gap is recorded as a follow-up for T20 — a todo, and the same correction to the two harvest documents — not as a change to this plan or to the code. | ANSWERED — autonomous: decided from the shipped identity, the receiver's hand-over and the OTLP retry rule |

## Design harvest

`documentation-impact: extends-component`; `impact-components: [KyberDash, KyberSquad]`.

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | KyberDash | adr | New ADR: Factory (Droid) capture is a hooks-plugin pipeline — the plugin directory, the receipt, the consent file, the `droid` wire contract, and the rejection of native OTel as the primary path (D2, D3, D5, D12). |
| H2 | KyberDash | adr | ADR 0031 amended in place: the Factory writer owns a plugin directory and a consent file, declares no config key, and never writes `~/.factory/settings.json` or `~/.factory/hooks.json` (D4, D10, D16). |
| H3 | KyberDash | architecture | `docs/dash/architecture.md`: the Factory capture pipeline (hooks → plugin → OTLP → `droid` adapter → store → projection → `/api/kyber/*`), the plugin directory layout, the file-backed spool, the consent contract, the install modes, and the `droid` harness id shared with the file source (D3, D6, D8, D11, D15). |
| H4 | KyberDash | reference | `docs/dash/telemetry-inventory.md`: the Factory rows — what the plugin measures, what the file source measures, and the honest token position (D8, D9). |
| H5 | KyberDash | runbook | `docs/dash/runbook.md`: enable and verify Factory capture, the consent verb, the live check, and the uninstall procedure. |
| H6 | KyberDash | reference | `docs/cli-command-surface.md`: `kyber capture enable/disable/status --harness factory`, `kyber capture plugin install --harness factory`, and the new `kyber capture plugin consent` verb (D10). |
| H7 | KyberDash | rule | `docs/rules/kyberdash-harness-collection.md`: the Factory row in the per-harness ground truth table — plugin hooks as the PRIMARY mechanism, file ingest as the token source, and the completeness predicate (D2, D8), plus the D28 split (D36). |
| H8 | KyberDash | onboarding | `docs/dash/onboarding.md`: the Factory capture plugin section (what it collects, what consent governs, how to turn it on). |
| H9 | KyberSquad | architecture | `docs/kyber-squad/architecture.md` and `docs/kyber-squad/onboarding.md`: the global Factory capture-plugin step (preserve mode) beside the existing OpenCode step, and the `~/.factory/plugins/` subtree Squad does not own (D18). |
| H10 | KyberDash | reference | `docs/install.md` §Updating: the plugin step's Factory guidance line (D18). |
| H11 | KyberDash | architecture | `docs/dash/architecture.md`: the authorized native-OpenTelemetry path for Factory — `OTEL_TELEMETRY_ENDPOINT`, the `genai` attribute vocabulary, the `POST /v1/metrics` route, the plugin-first precedence rule (D23), the tokenless and lineage-free limits, and the organisation-override reporting rule (D26). |
| H12 | KyberDash | reference | `docs/dash/telemetry-inventory.md`: the Factory model-resolution chain (D24, as superseded by **D39**) and the child-session token pairing rule (D25), stated as the model and token facts the Factory rows carry — including the file source's own `unknown` placeholder (D40). |
| H13 | KyberDash | architecture | `docs/dash/architecture.md`: the Factory source-precedence rule (D28) — in a `droid` session holding both plugin rows and file rows, the plugin rows are the session's turns (count, timeline, per-turn model, tools, duration, context) and the file rows are the session's counters (the totals and the availability that gates them) and its cost; neither source is counted twice, and the rule is inert for every other harness. |
| H14 | KyberDash | reference | `docs/dash/telemetry-inventory.md`: the child-session token source (D29) — a child session's tokens come from the child's **own** companion file through the exact-id pairing, and the parent's `childInclusiveTokenUsageBySessionId` map is the parent's aggregate view and never a token source; and the plugin rows' `not_measurable` reasons name the file-source pairing (D30). H12's child-pairing half is superseded by this row. |
| H15 | KyberDash | architecture | `docs/dash/architecture.md` and `docs/dash/telemetry-inventory.md`: the native OTLP metrics destination (D31, as amended by **D38** and **D42**) — the decoded metric points are persisted in the canon store's **`metric_point`** table through the production sink's `upsertMetrics` and the `ingestMetricBatch` seam, one row per **distinct** decoded point (a point's identity is a content digest, so a re-delivered point lands on the row it already occupies); nothing projects them yet (a todo); the receiver activity records the batch as `otlp:metrics` with the points it carried; and the writer's rule is unchanged — a sink that cannot persist metrics is refused, never accepted-and-dropped. **The two documents' current "one row" shorthand is corrected by T20** (D42). |
| H16 | KyberDash | architecture | `docs/dash/architecture.md`: the status rule at the normalization seam (D32) — an error status is never downgraded, so the `droid` adapter's `error.type` reaches the stored record and the policy-denied tool call counts as an error; the rule is inert for every other harness. |
| H17 | KyberSquad | architecture | `docs/kyber-squad/architecture.md` and `docs/kyber-squad/onboarding.md`: the capture-plugin step's harness-aware shape (D33, extending H9) — one plugin call per capture-plugin harness, in a fixed order with the OpenCode call first and the Factory call following it, the OpenCode call handed its OWN resolved global root (`~/.config/opencode`) and the Factory call handed NONE (D35) — and the updater's Factory call (D34): the same verb in preserve mode with `--if-installed` and no root. |
| H18 | KyberDash | reference | The `--config-dir` flag's scope (D35): it is the OpenCode config root; the Factory plugin-install verb refuses it (exit 2), and the Factory tree always lands under `$FACTORY_DIR` else `~/.factory`. |

## 3. The design, in detail

### 3.1 The owned plugin tree

```
~/.factory/plugins/kyberdash-factory/
├── .factory-plugin/
│   └── plugin.json          # Factory plugin manifest
└── hooks/
    ├── hooks.json           # registers the script on the eight hook events
    └── kyberdash-factory.mjs # the bundled capture script
```

Alongside it, under `~/.kyberdash/` (KyberDash's own state directory, never the owner's Factory
tree):

| Path | Owner | Meaning |
|---|---|---|
| `~/.kyberdash/factory-plugin-receipt.json` | KyberDash | `{ "version": 1, "root": "<plugin dir>", "files": [{ "path": ..., "sha256": ... }] }` |
| `~/.kyberdash/factory-plugin.json` | KyberDash | consent: `{ "version": 1, "content": false }` |
| `~/.kyberdash/capture/factory/outbox.jsonl` | KyberDash | bounded file spool (500 events / 4 MB, drop-oldest) |
| `~/.kyberdash/capture/factory/diagnostics.log` | KyberDash | bounded 64 KB diagnostic tail |

The plugin root may carry `.factory-plugin/plugin.json`, `hooks/hooks.json`, `mcp.json` and
`droids/`; KyberDash ships only the manifest and the hooks, and never writes `mcp.json` or
`droids/`. `~/.factory/plugins/` exists on this machine and holds only `sync_stamp.json`, so the
subtree is free of collision with Squad's `~/.factory/droids/` and `~/.factory/skills/` (§2.5).

### 3.2 The hook → span mapping

| Hook event | Span | Attributes (contract `factory.contract_version = 1`) | Content |
|---|---|---|---|
| `SessionStart` | `factory.session` | `factory.session.id`, `factory.cwd` (path only) | none |
| `UserPromptSubmit` | `factory.turn` (op `llm.invoke`) | `factory.session.id`, `factory.message.id`, `factory.turn.prompt.text` (capped) | prompt text, gated |
| `PreToolUse` | `factory.tool` (start) | `factory.session.id`, `factory.tool.name`, `factory.tool.call_id`, `factory.tool.input_bytes` | tool arguments, gated |
| `PostToolUse` | `factory.tool` (end; same span id) | plus `factory.tool.status`, `factory.tool.error_type`, `factory.tool.result_bytes` | tool result, gated |
| `Stop` | `factory.turn` (end) | `factory.session.id`, `factory.message.id` | none |
| `SubagentStop` | `factory.child_session` | `factory.session.id` (child), `factory.parent.session.id` | none |
| `SessionEnd` | `factory.session` (end) | `factory.session.id` | none |
| `PreCompact` | not collected in v1 | — | — |

Every attribute the payload does not carry is **absent**, never zero and never empty string.

**The model (D24, as shipped — D39).** The chain has three steps, and they belong to three
different readers. (1) **The plugin** names a model only when the hook payload carries one:
the mapper emits `factory.model.id` only then, and the shipped bundle reads the hook payload and
nothing else. Factory's own hook payload documents no `model` field, so a plugin-sourced turn
generally names none — and an absent model stays absent all the way down, because the adapter
never defaults one. (2) **The file source** reads the session's `model` from the companion
per-session file `<session-id>.settings.json` beside the transcript — read-only, and never
written — and publishes it **wrapper-stripped**. (3) **The session** publishes **one** model:
the model its **turn records** name, and where no turn record names one, the **counter
record's** model. A turn's own model always wins, so a paired session publishes one model and
never two; a session whose sources named none publishes **none** — never a default, never a
guess. `~/.factory/settings.json` is never a model source: it has no top-level `model` key at
all, its model lives in nested per-role blocks, and reading one would attribute a subagent's
model to its parent.

**The `unknown` placeholder (D40).** A companion file that names no model leaves the file
source with its own long-standing placeholder, the literal `unknown` — the file source's own
string, not a measured model, and one the plugin path never produces. It is pre-existing
file-source behaviour outside this plan's scope (D21), and this plan does not change it; it is
named here so that no reader mistakes it for a measurement.

**Child sessions (D25, amended by D29).** `SubagentStop` carries the child session id; the parent
linkage is an exact match only, and a child that cannot be linked stays an unlinked child rather
than a fabricated one. A child session's tokens are paired from the **child's own** companion file
`<child-id>.settings.json`, through the same exact-id pairing as any other Factory session: a
child session is a first-class Factory session with its own transcript and its own companion file,
so the file source discovers it as its own session and the plugin's child rows pair with it. A
child with no companion file of its own reads `not_measurable` with a stated reason, and never
borrows the parent's counters. The parent's `childInclusiveTokenUsageBySessionId` map is the
parent's aggregate view of its children, not the child's own usage: it is never a token source, and
a child's usage is never folded into the parent's session totals (A9).

### 3.3 Install, status and the Settings row

`capture status --harness factory` and `GET /api/kyber/capture` report, for Factory: the plugin
state (`on` / `partial` for an older plugin of ours / `off` / `unknown` for an edited, foreign or
unreadable tree), the receipt state, the resolved plugin path, the consent state, the install
evidence, and the receiver liveness — the same fields the OpenCode row reports, plus the resolved
plugin directory for a directory-shaped plugin.

The row also states, as fixed facts and never as an echo of owner text: that native
OpenTelemetry export is configured by machine environment variables (`OTEL_TELEMETRY_ENDPOINT` and
its siblings), that an organisation-managed `telemetry` block overrides them, and that a
deployment under such an override exports nothing locally — so where no native span arrives, the
row reports the native path as **unavailable** with that named cause, never as working (D26). The
plugin path is unaffected by the native path's presence or absence.

**The paragraph above describes the row as it ships (D41).** The native-export availability
reporting **is** part of the Factory row: the row carries the native OpenTelemetry export's
state — available, unavailable with the named cause, or not configured — on every surface the
row reaches (the printed status, the structured row and the API route), because they all read
the row's own status description (`dash/src/capture/harnesses/factory.ts:71-85`, `:100-113`,
`:121`). D37's "not shipped yet" marker was right when it was written and is removed here: it
told a reader not to infer anything from the row's silence, and the row no longer is silent.
Nothing about the native path may be inferred from the row's silence — but the row now states
the state, so there is no silence left to infer from.

### 3.4 The native-OpenTelemetry group (authorized — D12 branch A)

Factory's native export **can** be pointed at the KyberDash receiver: `export
OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"` is Factory's own documented example, and Droid
pushes OTLP/HTTP directly, so no collector is needed. Two additions make that usable:

**The receiver.** A `POST /v1/metrics` path beside `/v1/traces` and `/v1/logs`: OTLP/JSON metrics
decoded to a metrics shape and handed to a metrics sink port with the same admission, shedding and
health-window discipline as the span path (`dash/src/otel/receiver.ts:1119-1121,1370-1386`). Only
OTLP/HTTP is documented for Factory — port 4318, no gRPC, no 4317 — so no other protocol is
added. **Logs are never exported by Droid**, so `/v1/logs` is untouched by this branch.

**Where the metrics go (D31, as shipped — D38, D42).** The metrics sink port is backed by the
**canon store**: the production sink implements `upsertMetrics`, which writes the decoded metric
points to the canon store's own **`metric_point`** table — one row per **distinct** decoded
point, since a point's identity is a content digest of everything the row carries, so two
byte-identical decoded points collapse into one row and a re-delivered point lands on the row it
already occupies — carrying the metric's name, kind, temporality, the point's timestamp and
value (or its count and sum), the point's attributes, the resource attributes and the scope —
and then records the batch as receiver activity under the source `otlp:metrics`, with the points
the batch carried. The writer's rule is unchanged: a sink that cannot persist metrics does not
offer the port, and the batch is **refused**, never accepted and dropped. **Nothing projects
metrics yet** — the canon projection and the dashboard have no metric concept, and the metrics'
projection is a todo, not part of this branch. The table is a destination that keeps the values;
the projection that would read them is later work.

**The native claim.** The `droid` adapter recognises the native `genai` fingerprint. The exact
vocabulary to recognise (D12):

| Aspect | Value |
|---|---|
| Root span | `invoke_agent droid` (Internal) |
| Children | `chat <model>` (Client); `execute_tool <tool>` (Internal) — tool spans are **siblings** of the chat spans, never nested |
| Operation | `gen_ai.operation.name` ∈ `invoke_agent` \| `chat` \| `execute_tool` |
| Agent | `gen_ai.agent.name` = `droid`, on the root |
| Session | `gen_ai.conversation.id` = the Droid session id; `session.id` on every data point |
| Model | `gen_ai.request.model` (chat span) |
| Provider | `gen_ai.provider.name` ∈ `anthropic` \| `openai` \| `gcp.gemini` \| `x_ai` \| `deepseek` \| `mistral_ai` |
| Tool | `gen_ai.tool.name`, `gen_ai.tool.call.id`, `gen_ai.tool.type` = `function` |
| Outcome | `error.type` = `rejected` for a policy-denied tool call; `gen_ai.response.finish_reasons` (array) |
| Server | `server.address`, `server.port` |
| Content (gated) | `gen_ai.input.messages` (root), `gen_ai.output.messages` (chat), `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result` |
| Common (every data point) | `user.id`, `user.email`, `organization.id`, `session.id`, `model.id`, `parent_session.id` |
| Resource | `service.name` = `cli`, `service.version`, `os.type`, `os.version`, `host.arch`, `terminal.type`, `telemetry.granularity` |

**Precedence and no double counting (D23).** The plugin envelope is claimed first. A native span
is claimed only when its session has no plugin rows; a native span for a session that already has
plugin rows is attributed as corroborating evidence and **excluded** from that session's turn,
tool and duration totals. A native span is distinguished from a plugin row by fingerprint, never
by name: the plugin envelope carries `factory.contract_version = 1` and a `factory.` attribute; a
native span carries `gen_ai.agent.name = 'droid'` and no `factory.` attribute.

**What the native path cannot do.** It carries **no token or cost counters** (D12), and it carries
**no subagent lineage** (D2). It therefore complements the plugin rows and never replaces them.

**Where it is silently absent (D26).** An organisation-managed `telemetry` block overrides the
machine environment variables, so a deployment whose organisation pins an endpoint exports nothing
locally. KyberDash reports that state as observed — native export unavailable — and names the
override as the cause; it never reports the native path as working when no native span arrives.
On this machine no organisation pins an endpoint (§2.5), so the caveat is real but not universal.

### 3.5 The paired Factory session (D28, D29, D30)

One Factory session is observed by two sources. The **plugin** observes the turn boundary, the
tools, the timing and (under consent) the prompt; it observes **no counters at all**. The **file
source** observes the counters, the cost and the model; it observes **no turn boundary and no
content**. The two are therefore never joined and never merged into one turn: they are **split by
what each can measure**.

| The session's figure | Read from | Why |
|---|---|---|
| turn count, timeline turns, request roots, per-turn model (where the plugin turn names one), tool invocations, duration, context turns | the **plugin rows** | the plugin is the PRIMARY mechanism (D2) and alone observes the turn boundary, the tools and the timing |
| token totals (input, output, cacheRead, cacheCreation) and the availability that gates them | the **file rows** | the file source is the only source of counters (D9), and its counters are a session total split evenly across assistant calls — a session-level fact, never a turn-level one |
| cost | the turn records **and** the counter records | the file row carries the only priced cost; the two sets are disjoint, so no cost is counted twice |
| the session's published **model** | the **turn records'** model, else the **counter record's** model, else none | the model comes from the source that measured it (D39): a turn's own model always wins, the file source's model stands in when no turn record names one, and a session whose sources named none publishes none — never a default, never a guess |

The file row in a paired session is a **counter carrier**: it contributes its counters and its
cost and nothing else — it is not a turn, not a timeline entry, not a request root, not a tool
invocation, and its duration is not added to the session's duration. A plugin-only session keeps
the plugin's own declarations, so its totals read `not_measurable` with the reason D30 fixes. A
file-only session is unchanged. Every other harness is unchanged: the rule is inert outside
`droid`.

The rule is exact-match only — no fuzzy join, no time window, no counter-equality join — and it
never fabricates: a paired session's totals are the file row's counters, and a plugin-only
session's totals are absent, never zero.

A **child** session is paired the same way, from the child's own companion file (D29), and the
plugin's `not_measurable` reasons name this pairing (D30).

**The model, in a paired session (D39).** The two sources do not measure the model the same
way. The plugin's turn names one only when the hook payload carries one — and Factory's own hook
payload documents no `model` field, so it generally names none. The file source names the
session's model from its companion file and publishes it wrapper-stripped. The session therefore
publishes the turn's own model where it has one, the file source's model where no turn record
names one, and **none** where no source named one. Where the companion file names no model at
all, the file source's own `unknown` placeholder is what it publishes — a literal of the file
source's, not a measured model (D40).

## 4. Test contract (test-first)

Every unit has a RED task (tests only) and a GREEN task (implementation), authored by different
workers. RED evidence must show the contract test existed and failed for the intended missing
behaviour before the implementation landed; GREEN uses the same test, unweakened.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/capture/harnesses/factory-plugin/contract.test.ts`, `mapper.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/contract.test.ts src/capture/harnesses/factory-plugin/mapper.test.ts` | The contract's key set, span names and id derivation are stable; each hook event maps to at most one span; absent payload fields stay absent; a payload with no model carries no model attribute | The two files exist and fail against the empty module | Same tests pass, unweakened |
| T2 | same as T1 | same as T1 | Production contract and mapper exist and satisfy T1 | — | T1 green |
| T3 | `dash/src/capture/harnesses/factory-plugin/entry.test.ts`, `dash/src/capture/harnesses/transport/*.test.ts`, `dash/src/capture/consent-file.test.ts`, `dash/src/architecture/factory-leaves.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/entry.test.ts src/capture/harnesses/transport src/capture/consent-file.test.ts src/architecture/factory-leaves.test.ts` | The hook runner reads stdin, exits 0 on any input (including malformed JSON), never writes to stdout except the hook protocol, honours the consent file, drains and bounds the spool; the shared modules stay import-free leaves; the OpenCode bundle still bundles to the committed bytes | The new files exist and fail; the OpenCode drift test still passes (no regression) | All green, OpenCode drift test green |
| T4 | same as T3 | same as T3 | The shared transport/consent modules exist, the OpenCode modules are shims, the Factory entry exists | — | T3 green; `bundle-opencode-plugin.mjs --check` clean |
| T5 | `dash/src/capture/harnesses/factory-plugin/consent.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/consent.test.ts` | A consent file is on only for `{version:1,content:true}`; missing, oversized, malformed or unknown-version read as off; the env override wins | The file exists and fails | Same test passes |
| T6 | same as T5 | same as T5 | The Factory consent reader and the shared consent writer exist | — | T5 green |
| T7 | `dash/src/capture/harnesses/factory-plugin/install.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/install.test.ts` | Install writes the whole tree and the receipt; a foreign or edited tree is refused in strict mode and kept in preserve mode; an absent tree with `ifInstalled` writes nothing; uninstall removes the tree only when every digest matches; `~/.factory/settings.json` and `~/.factory/hooks.json` stay byte-identical through install, enable, disable and uninstall; a dry run writes nothing | The file exists and fails | Same tests pass, unweakened |
| T8 | same as T7 | same as T7 | The Factory installer, receipt reader/writer, state and status lines exist | — | T7 green |
| T9 | `dash/src/capture/harnesses/factory.test.ts`, `dash/src/capture/status.test.ts` (Factory cases), `dash/src/capture/detect.test.ts` (Factory probes), `dash/src/capture/harnesses/factory-plugin/install-command.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory.test.ts src/capture/status.test.ts src/capture/detect.test.ts src/capture/harnesses/factory-plugin/install-command.test.ts` | The registry holds a Factory writer; `capture status` lists it with plugin, receipt and consent state; `enable` installs and `disable` removes without touching the settings file; the Settings API reports the same row; `capture plugin install --harness factory` accepts the id and rejects an unknown one with exit 2; the status row also reports the native path as **unavailable** with its named cause when the organisation override or the machine environment yields no native span (D26, D37) | The new files/cases exist and fail | Same tests pass; the OpenCode registry and status cases stay green |
| T10 | same as T9 | same as T9 | The Factory writer, its registry row, the detect probes, the status lines and the plugin-install verb exist | — | T9 green |
| T11 | `dash/src/canon/adapters/droid.test.ts`, `dash/src/canon/adapters/droid-envelope.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/adapters/droid.test.ts src/canon/adapters/droid-envelope.test.ts` | The adapter claims only the Factory plugin envelope under contract `1`; an unknown contract version scores 0 and quarantines; a native `gen_ai.agent.name=droid` span is claimed only when the native branch is authorized; a plugin record without paired tokens declares `not_measurable` with a reason, never 0; the adapter stamps `harness: 'droid'` | The files exist and fail | Same tests pass; the OpenCode adapter cases stay green |
| T12 | same as T11 | same as T11 | The `droid` adapter, the envelope leaf and the `ADAPTERS` registration exist | — | T11 green |
| T13 | `dash/src/canon/sessions.droid-pairing.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/sessions.droid-pairing.test.ts` | A plugin row and a file row with the same session id form one session and the file row's tokens are the session's; different ids stay separate and the plugin row's tokens read `not_measurable` with a reason; no token is invented; a child session whose id is in the companion file's `childInclusiveTokenUsageBySessionId` is paired, and one that is not reads `not_measurable` with a stated reason (D25) | The file exists and fails (or passes with **no production change** — see the task note) | The pairing rule holds; a needed production change is a scope-checked edit, not a weakening. **The scope decision this cell required has been made: D28, D29 and D30, and the follow-on work is T22–T26. The child clause of this row's observable behaviour is superseded by D29** — the child is paired from its **own** companion file, not from the parent's child-usage map — and the two child cases move to T26. |
| T14 | `dash/src/providers/droid.test.ts` (Factory token cases), `dash/src/refresh/refresh.integration.test.ts` (droid job) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/providers/droid.test.ts src/refresh/refresh.integration.test.ts` | `dash refresh` still yields a `droid` session whose tokens come from the companion `*.settings.json` `tokenUsage`; the refresh job is unaffected by the plugin work | The new cases exist and fail or pass unchanged | The file source is proven unchanged by the plugin work |
| T15 | `dash/src/otel/receiver.test.ts` (metrics path cases), `dash/src/canon/adapters/droid.test.ts` (native claim) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/otel/receiver.test.ts src/canon/adapters/droid.test.ts` | `POST /v1/metrics` is served and answers 200/4xx per the existing discipline; a wrong path still 404s; a native span carrying `gen_ai.agent.name=droid` and `gen_ai.operation.name` is claimed; a native span for a session that already has plugin rows is claimed as evidence and is **excluded** from that session's turn, tool and duration totals (D23); a native span carries no token counter, so no token is taken from it | The cases exist and fail | Same cases pass |
| T16 | same as T15 | same as T15 | The metrics route and the native claim exist | — | T15 green |
| T17 | `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs`, `tests/KyberWeave.Tests/UpdateCommandTests.cs` | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~SquadCapturePluginStepTests|FullyQualifiedName~UpdateCommandTests"` | A global Factory deploy runs `kyberdash kyber capture plugin install --harness factory --preserve` and names no root (D35); `kyber-weave update` runs `kyberdash kyber capture plugin install --harness factory --preserve --if-installed` and names no root (D34); neither changes the command's exit code; a missing KyberDash is a skip, not a failure | The new cases exist and fail | Same cases pass; the OpenCode step cases stay green |
| T18 | same as T17 | same as T17 | The step and the updater run the Factory plugin verb | — | T17 green; the step runs one plugin call per capture-plugin harness, in a fixed order, the OpenCode call with its own resolved global root and the Factory call with none (D33, D35), and the OpenCode call is byte-identical to what it was (D33) |
| T19 | `dash/e2e/harness-collection.spec.ts` (Factory replay scenario) | `scripts/proc-guard.sh run --timeout 600 -- npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/harness-collection.spec.ts` | A recorded Factory hook payload, replayed through the production plugin → receiver → `droid` adapter → projection → dashboard, renders a Factory session with model and tool attribution and honest token state | The scenario exists and fails | The scenario passes with the isolated config; the other scenarios stay green |
| T20 | governed documents | `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .` | Every H-row's target document carries the durable item, including the D28 split in the collection rule (D36) and the `--config-dir` scope in the command surface and the runbook (D35); the closeout claims the status row's native-path availability reporting, which ships (D41), and the two metrics documents state the point's content-digest identity rather than a bare "one row" (D42); the plan moves to the archive folder with its closeout mapping | The documents fail the harvest checks | Both commands report zero findings |
| T21 | **No automated test — this is the manual, owner-run live verification** that replaces one. Read-only procedure documented in `docs/dash/runbook.md`; evidence written to the gitignored `artifacts/evidence/factory-capture-live/<UTC-stamp>/` | `scripts/proc-guard.sh run -- npm --prefix dash run dev -- kyber capture status --harness factory`, then one scripted Droid turn, then the dashboard | A live Droid turn produces a dashboard-visible Factory session with model and tool attribution; tokens are paired from the file source or honestly `not_measurable`; content is absent unless the consent file says `content: true`; the check records which of A1–A5 and A8 held | n/a — manual verification | The runbook procedure is executed and its result reported; a failure is reported as a failure |
| T22 | `dash/src/canon/sessions.droid-pairing.test.ts` (amended), `dash/src/canon/droid-pairing.test.ts` (new) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/sessions.droid-pairing.test.ts src/canon/droid-pairing.test.ts` | A paired session carries the FILE row's counters **and** counts ONE Factory turn (the plugin's), and keeps the file row's cost; a file-only session's own turns, counters and cost are unchanged; the split helper returns the plugin rows as the turn records and the file rows as the counter records for a mixed `droid` session, and no split for a plugin-only, file-only or non-`droid` session (D28) | The amended cases exist and fail on the turn count and the cost; the leaf's test fails against the absent module | Same tests pass, unweakened; the six cases that already passed still pass, and the child-pairing case stays RED until T26 corrects its fixture |
| T23 | same as T22 | same as T22 | The source-precedence rule exists in the projection and is inert for every other harness | — | T22 green; `dash/src/canon/sessions.test.ts` and the whole dash unit suite stay green |
| T24 | `dash/src/canon/adapters/droid.test.ts` (the plugin-path reason case), `dash/src/canon/sessions.droid-pairing.test.ts` (the plugin-only session's published reason) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/adapters/droid.test.ts src/canon/sessions.droid-pairing.test.ts` | Every one of the plugin adapter's four counter-class reasons names the file source and the session-id pairing, while still naming the harness and the metric; the session's published reason names it too (D30) | The strengthened assertions exist and fail against the shipped wording | Same tests pass; the native path's reasons are unchanged |
| T25 | same as T24 | same as T24 | The adapter's four reasons carry D30's wording | — | T24 green |
| T26 | `dash/src/canon/sessions.droid-pairing.test.ts` (the two child cases) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/sessions.droid-pairing.test.ts` | The child case's fixture is the real Factory shape — the child session is its own session with its own companion file carrying the child's usage, and the parent's companion file keeps its child-usage map so the parent's own counters stay the parent's own — and the child session carries the child's own usage, never the parent's (D29); the unmapped child still reads `not_measurable` with a reason | The corrected fixture is committed with the evidence that it passes against the projection as D28 specifies — a test-only correction, no production change | The child cases pass; `dash/src/providers/droid.test.ts` is unchanged and green |
| T27 | `dash/src/canon/metric-ingest.test.ts` (new), `dash/src/otel/service.test.ts` (the production-wiring case), `dash/src/otel/writer.test.ts` (the metrics cases) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/metric-ingest.test.ts src/otel/service.test.ts src/otel/writer.test.ts` | The canon store persists the decoded metric points, one row per **distinct** data point (a point's identity is its content digest — D38, D42), with no value dropped and none fabricated; a `POST /v1/metrics` against the **running collector service** answers 200 and the points are in the store; a writer whose sink cannot persist metrics **refuses** the batch and queues nothing; the batch is recorded as receiver activity under `otlp:metrics`; a metrics-only commit moves the receiver's last-persisted fact; a metrics batch requests no projection (D31) | The cases exist and fail: the production-wiring case answers 500, the store has no table, the writer's refusal is unpinned | Same tests pass, unweakened |
| T28 | same as T27 | same as T27 | The metrics destination exists: the `metric_point` table (D38), the canon metric ingest, the production sink's `upsertMetrics`, and the persisted-fact rule that counts metrics | — | T27 green; the schema version does **not** change (an `IF NOT EXISTS` table needs no migration step); an existing store opens unchanged; the metric rows carry the exporter's source and no harness attribution; the writer's refusal path is unchanged |
| T29 | `dash/src/canon/ingest.test.ts` (the stored-record case) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/ingest.test.ts src/canon/adapters/droid.test.ts` | A native Factory span carrying `error.type` is stored with the status `error`, even when the exporter's own OTLP status is `unset` or `ok`; a span whose exporter status is `error` is still stored as `error`; a span with no error evidence keeps the exporter's status code exactly (D32) | The stored-record case exists and fails (the status reads `unset`/`ok`); the adapter-level case still passes | Same tests pass, unweakened; every other harness's stored status is unchanged |
| T30 | same as T29 | same as T29 | The status rule exists at the receiver-timing seam | — | T29 green; `growWithReceiverTiming`'s doc states that it gives the outcome only when the adapter did not determine one |
| T31 | `tests/KyberWeave.Tests/UpdateCommandTests.cs` (the three legacy reconciliation fixtures, and the Factory updater call's mode) | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~UpdateCommandTests"` | The three legacy reconciliation fixtures expect the two-plugin sequences and name each plugin entry's harness; the Factory updater call is pinned to the exact argument list including `--preserve`; no other assertion in the file moved (D33, D34) | The three legacy cases fail today on the stale counts — the escalation | Same tests pass, unweakened: the fixtures' substance is unchanged and now stated |
| T32 | `dash/src/capture/harnesses/factory-plugin/install-command.test.ts`, `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/install-command.test.ts` | The Factory verb **refuses** `--config-dir` with exit 2 and writes nothing; the Factory step call names no root (D35) | The cases exist and fail today | Same tests pass |
| T33 | same as T32 | same as T32 | The verb refuses the flag and the step stops passing it | — | T32 green; the OpenCode verb's `--config-dir` is unchanged, and the OpenCode step call is byte-identical (D35) |

## 5. Dispatchable tasks

Skills are named, not owners: the conductor maps them to the live specialist inventory.

### T1 — RED: the Factory wire contract and the hook-to-span mapper

**Objective.** Write the failing contract tests that pin the wire vocabulary and the event mapping
before any production code exists.
**Files.** `dash/src/capture/harnesses/factory-plugin/contract.test.ts`,
`dash/src/capture/harnesses/factory-plugin/mapper.test.ts` (new).
**Acceptance criteria.** The tests name every attribute key, the three span names, the id
derivation (deterministic from the session id plus the anchor id, as the OpenCode contract does
at `dash/src/capture/harnesses/opencode-plugin/contract.ts:60-76`), and one case per hook event.
Each case asserts an **absent** attribute for a payload that does not carry it.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T2 — GREEN: the Factory wire contract and the hook-to-span mapper

**Objective.** Implement `contract.ts` (a leaf: `node:` imports only) and `mapper.ts`.
**Files.** `dash/src/capture/harnesses/factory-plugin/contract.ts`,
`dash/src/capture/harnesses/factory-plugin/mapper.ts` (new).
**Acceptance criteria.** T1 green, unweakened; the contract module has no non-`node:` value
import (asserted by the leaf test in T3); no attribute is fabricated.
**Depends on.** T1.
**Skills.** `typescript-dev`.

### T3 — RED: the hook runner, the shared transport and consent leaves

**Objective.** Write the failing tests for the one-shot hook runner, the shared transport
extraction, the shared consent writer and the leaf rule.
**Files.** `dash/src/capture/harnesses/factory-plugin/entry.test.ts`,
`dash/src/capture/harnesses/transport/outbox.test.ts`,
`dash/src/capture/harnesses/transport/otlp-sender.test.ts`,
`dash/src/capture/consent-file.test.ts`, `dash/src/architecture/factory-leaves.test.ts` (new).
**Acceptance criteria.** The tests cover: malformed stdin, a hook event with no payload fields,
the consent file off and on, the bounded spool (drop-oldest at the bound), the POST deadline,
the exit code 0 on every path, stdout carrying only the hook protocol, the shared modules being
import-free leaves, and **the OpenCode bundle still bundling to the committed bytes**
(`dash/src/capture/harnesses/opencode-plugin/bundle.test.ts` must stay green — it is the
regression witness for D14: the committed artefact must be CURRENT, not necessarily unchanged).
**Depends on.** T1 (the vocabulary).
**Skills.** `test-dev`, `typescript-dev`.

### T4 — GREEN: the shared transport, the shared consent writer, the hook runner

**Objective.** Extract the shared leaves, turn the OpenCode modules into re-export shims, and
implement the Factory hook runner and its file-backed spool.
**Files.** `dash/src/capture/harnesses/transport/outbox.ts`,
`dash/src/capture/harnesses/transport/otlp-sender.ts`,
`dash/src/capture/consent-file.ts`,
`dash/src/capture/harnesses/opencode-plugin/transport/outbox.ts` and
`.../transport/otlp-sender.ts` (shims),
`dash/src/capture/harnesses/opencode-plugin/consent-write.ts` (shim),
`dash/src/capture/harnesses/factory-plugin/entry.ts`,
`dash/src/capture/harnesses/factory-plugin/spool.ts` (new).
**Acceptance criteria.** T3 green; `node dash/scripts/bundle-opencode-plugin.mjs --check` exits
0 (the committed OpenCode bytes are CURRENT — a fresh bundle of the entry — not that they never changed; D14); the hook runner exits 0 on every input and never
throws.
**Depends on.** T3, T2.
**Skills.** `typescript-dev`.

### T5 — RED: the Factory consent reader

**Objective.** Write the failing tests for the consent read side.
**Files.** `dash/src/capture/harnesses/factory-plugin/consent.test.ts` (new).
**Acceptance criteria.** On/off/absent/invalid/unreadable/oversized are each pinned; the env
override (`KYBERDASH_FACTORY_CONTEXT`) is pinned as taking precedence over the file.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T6 — GREEN: the Factory consent reader

**Objective.** Implement `consent.ts` (a leaf, `node:` built-ins only, as the OpenCode consent
reader is at `dash/src/capture/harnesses/opencode-plugin/consent.ts`).
**Files.** `dash/src/capture/harnesses/factory-plugin/consent.ts` (new).
**Acceptance criteria.** T5 green.
**Depends on.** T5, T4 (the shared consent writer).
**Skills.** `typescript-dev`.

### T7 — RED: the plugin installer, the receipt and the refusal rules

**Objective.** Write the failing tests for install, upgrade, refusal, uninstall, the receipt and
the byte-identity guarantee.
**Files.** `dash/src/capture/harnesses/factory-plugin/install.test.ts` (new).
**Acceptance criteria.** Strict refuses a foreign tree and an edited tree with exit 1 and writes
nothing; preserve keeps both with a warning and exit 0; the receipt lists every installed file
with its digest; uninstall removes the tree only when every digest matches and otherwise leaves
it with a refusal; `~/.factory/settings.json` and `~/.factory/hooks.json` stay byte-identical
after install, enable, disable and uninstall; `--dry-run` writes nothing; `ifInstalled` on an
absent tree writes nothing. The byte-identity cases **create** an owner `~/.factory/hooks.json`
(with a foreign hook in it) and a project `.factory/hooks.json`, because this machine has neither
(§2.5) and the criterion is about an owner who has them.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T8 — GREEN: the plugin installer, the receipt and the state/status surface

**Objective.** Implement `install.ts`: paths, the tree writer, the receipt, the classifier
(absent / foreign / edited / older / newer / same), strict and preserve modes, uninstall,
`state` and `describeStatus`.
**Files.** `dash/src/capture/harnesses/factory-plugin/install.ts` (new).
**Acceptance criteria.** T7 green; the receipt format is `{version, root, files[]}`; writes are
atomic per file and refuse a symlink at a target path, as the OpenCode installer does
(`install.ts:107-136`).
**Depends on.** T7, T4.
**Skills.** `typescript-dev`.

### T9 — RED: the registry row, the status surface and the plugin-install verb

**Objective.** Write the failing tests for the Factory writer as a registry row.
**Files.** `dash/src/capture/harnesses/factory.test.ts` (new),
`dash/src/capture/status.test.ts` (Factory cases),
`dash/src/capture/detect.test.ts` (Factory probes),
`dash/src/capture/harnesses/factory-plugin/install-command.test.ts` (new).
**Acceptance criteria.** The registry contains a `factory` writer with the declared shape
(D16); `capture status --harness factory` prints the plugin state, the receipt state, the
resolved plugin path and the consent state; `capture enable --harness factory` installs and
`disable` removes it, touching no settings file; `GET /api/kyber/capture` reports the same row;
`capture plugin install --harness factory` accepts the id and `--harness bogus` exits 2.
**Depends on.** T7 (the installer the row drives).
**Skills.** `test-dev`, `typescript-dev`.

### T10 — GREEN: the registry row, the status surface and the plugin-install verb

**Objective.** Implement the Factory writer, register it, add the detect probes, and generalize
the plugin-install verb to both harnesses.
**Files.** `dash/src/capture/harnesses/factory.ts` (new),
`dash/src/capture/harnesses/registry.ts` (register the row),
`dash/src/capture/detect.ts` (the `factory` probe group),
`dash/src/capture/harnesses/factory-plugin/install-command.ts` (new; the OpenCode command keeps
working),
`dash/src/cli/register.ts` (the verb's `--harness` help text and the new consent verb).
**Acceptance criteria.** T9 green; the OpenCode row's cases stay green; the registry order gains
Factory at the end (a status-only row order rule already appends Kilo last); the Factory status row
reports the native-export availability with its named cause — never as working when no native span
can arrive (D26, D37).
**Depends on.** T9, T8.
**Skills.** `typescript-dev`.

### T11 — RED: the `droid` canon adapter

**Objective.** Write the failing tests for the adapter that claims the Factory plugin envelope.
**Files.** `dash/src/canon/adapters/droid.test.ts`,
`dash/src/canon/adapters/droid-envelope.test.ts` (new).
**Acceptance criteria.** The adapter claims only spans carrying `factory.contract_version = 1`
plus a `factory.` marker; an unknown or missing version scores 0 (quarantine, as the OpenCode
envelope does at `dash/src/canon/adapters/opencode-envelope.ts:74-79`); a normalized record
carries `harness: 'droid'`, the session id, the model and the tool name; a record with no token
counter declares `token_usage` `not_measurable` with a reason naming the file-source pairing and
never 0. The **native** claim is authorized (D12 branch A) and is written here: a span carrying
`gen_ai.agent.name = 'droid'` plus `gen_ai.operation.name` is claimed by the same adapter, and
the **precedence rule of D23** is pinned — a native span for a session that already has plugin
rows is claimed as evidence and excluded from that session's turn, tool and duration totals. A
native span carries no token counter, so no token is taken from it, ever.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T12 — GREEN: the `droid` canon adapter

**Objective.** Implement `droid-envelope.ts` (a leaf) and `droid.ts` (the adapter), and register
the adapter in `ADAPTERS`.
**Files.** `dash/src/canon/adapters/droid-envelope.ts`,
`dash/src/canon/adapters/droid.ts` (new),
`dash/src/canon/ingest.ts` (registration).
**Acceptance criteria.** T11 green; the OpenCode adapter's cases stay green; the adapter's
`detect` never reads the span's source name (R6.2).
**Depends on.** T11, T2.
**Skills.** `typescript-dev`.

### T13 — Session pairing (test-first, production change only if the test proves one is needed)

**Objective.** Prove the D8 pairing rule: a plugin row and a file row with the same session id
form one session whose tokens come from the file row; different ids stay separate with the
plugin row honest. Prove the D25 child rule alongside it: a child session whose id is in the
companion file's `childInclusiveTokenUsageBySessionId` is paired, and one that is not reads
`not_measurable` with a stated reason.
**Files.** `dash/src/canon/sessions.droid-pairing.test.ts` (new).
**Acceptance criteria.** The test states both rules and passes against the current projection if
the projection already groups by session id (likely); **if it does not**, the implementation
change is confined to the projection's session grouping and is reported as a scope decision
before it is made, never a weakening of the assertion.
**Depends on.** T12.
**Skills.** `test-dev`, `typescript-dev`.

**Outcome and follow-on (D28–D30).** The test exists and six of its eight cases pass. The two
that fail proved three gaps, and the scope decision this task required has been made: **D28**
(the Factory source-precedence rule), **D29** (the child session's token source) and **D30** (the
adapter's reasons name the pairing). The projection change is therefore specified, not improvised,
and it is delivered by **T22–T26**; the two child cases move to **T26**.

### T14 — The file source stays the token source

**Objective.** Prove that `dash refresh` still produces the `droid` session with tokens from the
companion `*.settings.json`, so the plugin work does not disturb it.
**Files.** `dash/src/providers/droid.test.ts` (Factory token cases),
`dash/src/refresh/refresh.integration.test.ts` (the droid job case).
**Acceptance criteria.** The cases exist and pass; any change they demand is a scope decision.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T15 — RED: the native-OTel receiver path and claim (authorized)

**Objective.** Write the failing tests for `POST /v1/metrics` and the native `droid` claim.
**Files.** `dash/src/otel/receiver.test.ts` (new cases), `dash/src/canon/adapters/droid.test.ts`
(the native cases).
**Acceptance criteria.** The route serves the path, answers 200 for a valid OTLP/JSON metrics
body, 4xx for a malformed one, keeps 404 for an unknown path, and participates in the paused-503
and the health window like the other signal paths. The native cases pin the §3.4 vocabulary:
`gen_ai.operation.name` ∈ `invoke_agent`, `chat`, `execute_tool`, `gen_ai.agent.name` =
`droid`, `gen_ai.conversation.id` as the session id, `gen_ai.request.model`, `gen_ai.tool.name`
and `gen_ai.tool.call.id`, `error.type` = `rejected`; and the D23 precedence rule: a native span
for a session that already has plugin rows adds no turn, tool or duration.
**Depends on.** T11 (the adapter's vocabulary).
**Skills.** `test-dev`, `typescript-dev`.

### T16 — GREEN: the native-OTel receiver path and claim (authorized)

**Objective.** Implement the metrics route and the native claim.
**Files.** `dash/src/otel/receiver.ts`, `dash/src/otel/writer.ts` (the metrics sink port),
`dash/src/canon/adapters/droid.ts` (the native claim).
**Acceptance criteria.** T15 green; the plugin path is unaffected; no token counter is taken from
a native span that does not carry one; only OTLP/HTTP on 4318 is added — no gRPC path, no 4317,
no `OTEL_EXPORTER_OTLP_PROTOCOL` support.
**Depends on.** T15, T12.
**Skills.** `typescript-dev`.

### T17 — RED: the Squad and update steps for the Factory plugin

**Objective.** Write the failing .NET tests for the global-install step and the updater.
**Files.** `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs`,
`tests/KyberWeave.Tests/UpdateCommandTests.cs` (new cases).
**Acceptance criteria.** A global Factory deploy runs
`kyberdash kyber capture plugin install --harness factory --preserve` and names no root (D35); the
updater runs `kyberdash kyber capture plugin install --harness factory --preserve --if-installed`
and names no root (D34); the step's outcome is never Squad's exit code; a missing
KyberDash is a skip; the OpenCode step's cases stay green.
**Depends on.** none.
**Skills.** `test-dev`, `csharp-dev`.

### T18 — GREEN: the Squad and update steps for the Factory plugin

**Objective.** Extend `KyberDashCapturePluginStep` and `SelfUpdater`.
**Files.** `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`,
`src/KyberWeave.Cli/Update/SelfUpdater.cs`.
**Acceptance criteria.** T17 green; the step is **harness-aware** — one plugin call per
capture-plugin harness, in a fixed order with the OpenCode call first and the Factory call
following it; the OpenCode call is handed its own resolved global root and the **Factory call is
handed no root at all** (D35), so the plugin lands under `$FACTORY_DIR` else `~/.factory`; the
OpenCode invocation is byte-identical to what it was.
**Depends on.** T17, T10 (the verb must exist).
**Skills.** `csharp-dev`.

### T19 — The replay e2e through the production pipeline

**Objective.** Add a Factory scenario to the conformance spec: a recorded hook payload through
the production plugin, receiver, `droid` adapter, projection and dashboard.
**Files.** `dash/e2e/harness-collection.spec.ts`, its fixtures.
**Acceptance criteria.** The scenario renders a Factory session with model and tool attribution
and an honest token state; the spec runs only with the isolated config
(`dash/playwright.isolated.config.ts`); the existing scenarios stay green; the scenario is
labelled as replay evidence, not live capture.
**Depends on.** T12, T4.
**Skills.** `test-dev`, `typescript-dev`.

### T20 — Documentation harvest and closeout

**Objective.** Land every H-row, amend ADR 0031, create the new ADR, update the plan's closeout
mapping, and archive the plan.
**Files.** `docs/dash/architecture.md`, `docs/dash/telemetry-inventory.md`,
`docs/dash/runbook.md`, `docs/dash/onboarding.md`, `docs/cli-command-surface.md`,
`docs/rules/kyberdash-harness-collection.md`, `docs/kyber-squad/architecture.md`,
`docs/kyber-squad/onboarding.md`, `docs/install.md`, `docs/adr/0031-…md`, the new ADR,
`docs/adr/README.md` (the ADR index row), `docs/plans/README.md` (the row moves to Archived
Plans), `docs/todo/` (the sibling coverage gap), and the plan file itself (`git mv` to
`docs/archive/plans/`).
**Acceptance criteria.** `docs validate . --merge-ready` and `docs drift .` report zero findings;
the closeout mapping links every D-row to its destination; the ADR waiver / documentation waiver
rules are honoured. **Three harvests carry rules the plan set:** the harness-collection rule's
Factory row states the D28 split (D36, H7); the CLI command surface's plugin-install row and
exit-code row and the runbook's Factory plugin paragraph state the `--config-dir` scope (D35,
H18); and the closeout **claims** the status row's native-path availability reporting, because
it ships (D41, commit `e140472` — D37's "not shipped yet" marker no longer holds). **The two
metrics documents state the point's identity, not just its count** (D42): the architecture and
the telemetry inventory say **one row per distinct decoded point**, a point's identity being a
content digest, so a re-delivered point lands on the row it already occupies — their current
"one row" shorthand is corrected here rather than left to mislead. **Three todos are filed under
the todo index** (D21): the sibling harness
coverage gap (Cursor, Warp, ZCode, Devin, Command Code), the Factory file source's even
token split, recording that the companion `<session-id>.settings.json` carries `lastCallTokenUsage`
and `childInclusiveTokenUsageBySessionId`, so the split is a known lossy approximation with
better data available (§2.5), and the metric point's content-digest identity (D42) — that the
multiplicity of byte-identical decoded points is not preserved and that no shipped test pins the
collision, so a later change to the identity would be unobserved.
**Depends on.** T10, T12, T16, T18, T19.
**Skills.** `app-docs-standard`, `kyber-docs-authoring`.

### T21 — The live check (owner-run, not a CI gate)

**Objective.** Document and run, with the owner, the live proof: install the plugin, run one
scripted Droid turn, and read the Factory session in the dashboard.
**Files.** `docs/dash/runbook.md` (the procedure), `artifacts/evidence/factory-capture-live/<UTC-stamp>/`
(gitignored).
**Acceptance criteria.** The session appears with model and tool attribution; tokens are either
paired from the file source or honestly `not_measurable`; content is absent unless consent says
`content: true`. The check also records which assumptions held and which did not: the hook's
`session_id` against the transcript's session id (A2), the hook payload's exact field names and
whether it carries a model (A3), what identity `Stop` and `SubagentStop` carry (A4), the hook
script's exit-code and stdout protocol (A5), the plugin's discovery path (A1), and the model's
resolution link (D24). A failure is reported as a failure, never worked around.
**Depends on.** T20.
**Skills.** `app-docs-standard`.

### T22 — RED: the Factory source-precedence contract (D28)

**Objective.** Pin the accounting rule of D28 in the pairing test: a paired session carries the FILE
row's counters **and** counts ONE Factory turn (the plugin's), and keeps the file row's cost; a
file-only session is unchanged.
**Files.** `dash/src/canon/sessions.droid-pairing.test.ts` (amended),
`dash/src/canon/droid-pairing.test.ts` (new).
**Acceptance criteria.** The paired-session case is **extended, never weakened**: every existing
assertion stays, and the case adds `expect(summary.turn_count).toBe(1)` and the cost assertion (the
session's cost is the file row's priced cost, not the plugin's unknown one). A new case — "counts
one Factory turn for a paired session, and the file row is not a second turn" — fails today on the
turn count (it is 2). A new case — "keeps a file-only session's own turns, counters and cost" — is
a guard that passes today. A new case — "keeps the paired session's context figures" — pins that
the file source's context figures survive the plugin's arrival. The new leaf's test pins the split
helper's contract: for a mixed `droid` session it returns the plugin rows as the turn records and
the file rows as the counter records; for a plugin-only session, a file-only session and a
non-`droid` session it returns no split at all. The two child cases are **not** touched here — they
move to T26, and the child-pairing case stays RED until T26 corrects its fixture, which is
expected and is not a failure of this task.
**Depends on.** T12, T13.
**Skills.** `test-dev`.

### T23 — GREEN: the Factory source-precedence rule (D28)

**Objective.** Implement the split and apply it in the session projection.
**Files.** `dash/src/canon/droid-pairing.ts` (new leaf),
`dash/src/canon/sessions.ts` (the session row's derivation).
**Acceptance criteria.** T22 green, unweakened. In a mixed `droid` session the turn count, the
timeline's turns, the request roots, the per-turn model, the tool invocations, the duration and the
context turns are read from the plugin rows, and the token totals and the availability that gates
them are read from the file rows; the file row contributes its counters and its cost and nothing
else; the cost is summed over the turn records and the counter records together, and the
cost-basis verdict is computed over that same union; the context analysis falls back to the counter
records when no turn record carries a measured input. A plugin-only `droid` session and a
file-only `droid` session behave exactly as they do today, and **every other harness is
unchanged** — `dash/src/canon/sessions.test.ts` and the whole dash unit suite stay green. No
counter is summed twice and no zero is fabricated.
**Depends on.** T22.
**Skills.** `typescript-dev`.

### T24 — RED: the pairing reasons name the file-source pairing (D30)

**Objective.** Strengthen the reason assertions so the requirement D8 and D9 already state is
pinned.
**Files.** `dash/src/canon/adapters/droid.test.ts` (the plugin-path reason case),
`dash/src/canon/sessions.droid-pairing.test.ts` (the plugin-only session's published reason).
**Acceptance criteria.** Each of the adapter's four counter-class reasons must name the file source
and the session-id pairing (a regex over the file source's name and the session), while the
existing assertions (the reason names the harness and the metric) keep holding. The plugin-only
session's **published** reason must name it too. The strengthened assertions fail against the
shipped wording.
**Depends on.** T12, T13.
**Skills.** `test-dev`.

### T25 — GREEN: the adapter's reason wording (D30)

**Objective.** Implement D30's wording.
**Files.** `dash/src/canon/adapters/droid.ts`.
**Acceptance criteria.** T24 green; the four reasons are exactly D30's; the native path's reasons
are unchanged; no other adapter case moves; the reason never claims the tokens exist.
**Depends on.** T24.
**Skills.** `typescript-dev`.

### T26 — The child-pairing fixture correction (D29; test-only, no production change)

**Objective.** Correct the two child cases' fixture to the real Factory shape, and record that the
child pairing needs no production change beyond D28's split.
**Files.** `dash/src/canon/sessions.droid-pairing.test.ts` (the two child cases).
**Acceptance criteria.** The fixture writes the child session as its **own** Factory session — its
own `<child-id>.jsonl` and its own `<child-id>.settings.json` carrying the child's usage — and the
parent's companion file **keeps** its `childInclusiveTokenUsageBySessionId` map, so the parent's own
counters stay the parent's own. The child case asserts that the child session carries the child's
own usage and never the parent's; the unmapped child still reads `not_measurable` with a reason.
The corrected cases pass against the projection as D28 specifies, with **no production change** —
that is the evidence this task exists to record. `dash/src/providers/droid.test.ts` is **unchanged**
and green: its characterization case ("ignores the companion file child-usage map, so the child
tokens never reach the session") is correct under D29 and stays.
**Depends on.** T23.
**Skills.** `test-dev`.

### T27 — RED: the native metrics destination and the production wiring (D31)

**Objective.** Pin where the native OTLP metrics go, and pin the production path itself — the
seam the receiver's own tests cannot reach.
**Files.** `dash/src/canon/metric-ingest.test.ts` (new),
`dash/src/otel/service.test.ts` (the production-wiring case),
`dash/src/otel/writer.test.ts` (the metrics cases).
**Acceptance criteria.**
1. **The canon store persists the metric points.** A decoded metrics batch lands in the store's
   `metric_point` table (D38), one row per **distinct** data point — a point's identity is a
   content digest of everything the row carries, so two byte-identical decoded points collapse
   into one row and a re-delivered point lands on the row it already occupies (D42) — carrying
   the metric's name, kind, unit, the
   aggregation temporality, the point's timestamp, the point's value — or, for a histogram or a
   summary, its count and its sum — the point's attributes, the resource attributes, the scope and
   the exporter's `service.name`. The rows survive a reopen of the store. **No metric value is
   dropped and none is fabricated**: a point that carries no value keeps it absent, never a zero.
2. **The production wiring serves the path.** A `POST /v1/metrics` against the **running
   collector service** — the real `toCanon` sink, not a store double — answers **200**, and the
   points are in the store. This is the case that fails today: the production sink has no metrics
   port, so the batch is refused and the receiver answers 500.
3. **The writer refuses a batch a sink cannot persist.** `writeMetrics` on a sink without the
   metrics port throws and queues nothing — never accepts-and-drops.
4. **The receiver activity records the metrics.** The batch writes one `ingest_log` row with the
   source `otlp:metrics`, sized to the points received, and the write is fenced: a failing audit
   write never fails the ingest it audits.
5. **A metrics-only commit moves the receiver's last-persisted fact.** The persisted-fact hook
   counts metrics as well as spans and logs, so a Factory deployment whose export is
   metrics-only (A6) is not left reporting a stale last-persisted time.
6. **A metrics batch requests no projection** — it changes no projected record.
**Depends on.** T16 (the route and the native claim exist).
**Skills.** `test-dev`.

### T28 — GREEN: the native metrics destination (D31)

**Objective.** Implement the destination and the production sink.
**Files.** `dash/src/canon/store.ts` (the `metric_point` DDL in `SCHEMA_SQL` — the shipped name,
D38 — and the store method
that writes the points), `dash/src/canon/metric-ingest.ts` (new — the OTLP-metric → row mapping,
mirroring `log-ingest.ts`), `dash/src/otel/service.ts` (the sink's `upsertMetrics`),
`dash/src/otel/writer.ts` (the persisted-fact hook counts metrics).
**Acceptance criteria.** T27 green, unweakened. The **schema version does not change** — an
`IF NOT EXISTS` table needs no migration step, so an existing store opens unchanged, exactly as
`model_context_window_catalog` landed. The metric rows carry the exporter's `service.name` as
their source and **no harness attribution** (R6.2): the source name is never harness evidence, and
a metric point has no span for the fingerprint vote to run on. The full corpus wipe clears the
table; a scoped per-harness wipe does not, because those rows' harness attribution is unknown.
The audit write is fenced. No projection is requested for a metrics batch. The writer's refusal
path is unchanged — a sink without the port still refuses the batch.
**Depends on.** T27.
**Skills.** `typescript-dev`.

### T29 — RED: the stored native error status (D32)

**Objective.** Pin the outcome at the seam the defect actually lives at: the **stored** record.
**Files.** `dash/src/canon/ingest.test.ts` (the stored-record case).
**Acceptance criteria.** A native Factory span carrying `error.type` is **stored** with the status
`error`, even when the exporter's own OTLP status is `unset` or `ok`; a span whose exporter status
is `error` is still stored as `error`; a span with no error evidence keeps the exporter's status
code exactly, so every other harness's stored records are unchanged. The case goes through the
production ingest path (`ingestBatch`), not the adapter alone: the adapter-level case already
passes, and the defect appears only after normalization.
**Depends on.** T16.
**Skills.** `test-dev`.

### T30 — GREEN: the status rule at the receiver-timing seam (D32)

**Objective.** Implement the rule so an error status is never downgraded.
**Files.** `dash/src/canon/ingest.ts` (`growWithReceiverTiming`).
**Acceptance criteria.** T29 green, unweakened. The function keeps the record's own `error` status
and takes the exporter's status code for every other value; its doc states that it gives the
record the timing, and the outcome **unless the adapter already determined one**. The change is
confined to that function: the adapter, the projection, the store and every other harness's stored
records are untouched.
**Depends on.** T29.
**Skills.** `typescript-dev`.

### T31 — The legacy update fixtures: the two-plugin sequences and the updater call's mode (D33, D34)

**Objective.** Modernize the three legacy reconciliation fixtures in `UpdateCommandTests.cs` to the
two-plugin sequences, and pin the Factory updater call's exact argument list. **Test-only: no
production change is required — the worker's implementation stands.**
**Files.** `tests/KyberWeave.Tests/UpdateCommandTests.cs` (the three legacy fixtures and the
Factory updater case).
**Acceptance criteria.**
1. `RunReconcilesAfterCommitInEngineTrayPluginOrder` — the expected sequence becomes
   `["engine","tray","plugin","plugin"]`, and **add**
   `Assert.Equal("opencode", HarnessOf(steps[2].Args));` and
   `Assert.Equal("factory", HarnessOf(steps[3].Args));` so the fixture states "one plugin step per
   capture-plugin harness, OpenCode before Factory" instead of a bare count. The existing
   `Assert.Equal(PluginArgs, steps[2].Args)` stays as it is — the OpenCode call is the first plugin
   call — and so do the `Assert.All(...)`, `steps[0].Args` and `steps[1].Args` assertions. The doc
   comment names the new order.
2. `RunAlreadyCurrentStillReconcilesEngineTrayAndPlugin` — the expected sequence becomes
   `["engine","tray","plugin","plugin"]`, with the same two harness assertions; the no-download
   assertion stays as it is; the doc comment names the plugin steps.
3. `RunWithNoRestartSkipsEngineAndTrayButStillUpdatesThePlugin` — the expected sequence becomes
   `["plugin","plugin"]`, with `Assert.Equal("opencode", HarnessOf(steps[0].Args));` and
   `Assert.Equal("factory", HarnessOf(steps[1].Args));`; the doc comment says the capture plugins
   still upgrade.
4. The method names, the `RunReconciling` helper, the `PluginArgs` constant and every other fixture
   in the file stay as they are. `RunWithNoKyberDashSkipsEngineTrayAndPlugin` still asserts an
   empty list — `--no-kyberdash` returns before any plugin call.
5. `RunInstallsTheFactoryCapturePluginWithTheIfInstalledFlag` — add a `FactoryPluginArgs` constant
   beside `PluginArgs`, holding
   `["kyber","capture","plugin","install","--harness","factory","--preserve","--if-installed"]`, and
   assert `Assert.Equal(FactoryPluginArgs, factory.Args);`. The case's present partial assertions
   (the verb's first four arguments, `--if-installed` present, no `--config-dir`) are subsumed by
   the exact list and may be kept; the mode is pinned either way.
**Test command.** `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~UpdateCommandTests|FullyQualifiedName~SquadCapturePluginStepTests"`
**Depends on.** T18.
**Skills.** `test-dev`.

### T32 — RED: the Factory verb refuses `--config-dir`, and the step hands the Factory call no root (D35)

**Objective.** Pin the flag's scope on both sides: the Factory plugin-install verb refuses a root
flag it cannot honour, and the Squad step stops passing one.
**Files.** `dash/src/capture/harnesses/factory-plugin/install-command.test.ts` (new case),
`tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs` (the amended Factory cases).
**Acceptance criteria.**
1. `runFactoryPluginInstall` with a `configDir` answers **exit 2**, a stderr message naming the
   flag and saying where the Factory plugin goes, and **writes nothing** — no plugin tree, no
   receipt, no consent file.
2. Without the flag the behaviour is unchanged: the tree lands under `$FACTORY_DIR` else
   `~/.factory` (the existing installer cases stand).
3. The Squad step's Factory call is `kyberdash kyber capture plugin install --harness factory
   --preserve` — **no `--config-dir`** — and the OpenCode call keeps `--config-dir <opencodeRoot>`
   and its place in the order.
4. The case that pinned "the verb receives the resolved Factory global root" is **replaced** by
   one that pins "the Factory call names no root, so the verb resolves the harness's own root" —
   the same substance, stated against the rule D35 sets.
**Test command.** `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/install-command.test.ts` and
`scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~SquadCapturePluginStepTests"`.
**Depends on.** T18.
**Skills.** `test-dev`, `csharp-dev`.

### T33 — GREEN: the verb refuses the flag, and the step stops passing it (D35)

**Objective.** Implement the refusal and the corrected invocation.
**Files.** `dash/src/capture/harnesses/factory-plugin/install-command.ts`,
`dash/src/cli/register.ts` (the flag reaches the Factory branch so the refusal is reachable, and
the plugin-install command's `--config-dir` help names its scope),
`src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`.
**Acceptance criteria.** T32 green, unweakened. The OpenCode verb's `--config-dir` behaviour is
unchanged, the OpenCode step call is byte-identical, the Factory call is handed no root, and the
verb's exit-code contract names the new usage error. **This supersedes T18's Factory-call
shape**: the earlier T18 implementation handed the Factory call the resolved `~/.factory` root,
which D35 rejects. The documents change with it: the CLI command
surface's plugin-install row and its exit-code row, and the runbook's Factory plugin paragraph
(H18).
**Depends on.** T32.
**Skills.** `typescript-dev`, `csharp-dev`.

## 6. Dependency graph

```
T1 ─► T2 ─┐
           ├─► T4 ─► T6 ─┐
T3 ────────┘             ├─► T8 ─► T10 ─► T18 ─┐
T5 ─► T6 ─┘             │                     │
T7 ─► T8 ─┘             │                     │
           T9 ─► T10 ───┘                     │
T11 ─► T12 ─► T13 ───────────────────────────┤
T15 ─► T16 (needs T11, T12) ──────────────────┤
T14 (independent)                              │
T17 ─► T18 ─────────────────────────────────────┤
T19 (needs T4, T12) ────────────────────────────┤
T13 ─► T22 ─► T23 ─► T26 ──────────────────────┤
T12 ─► T24 ─► T25 ─────────────────────────────┤
T16 ─► T27 ─► T28 ─────────────────────────────┤
T16 ─► T29 ─► T30 ─────────────────────────────┤
T18 ─► T31 ────────────────────────────────────┤
T18 ─► T32 ─► T33 ────────────────────────────┤
                                              ▼
                                             T20 ─► T21
```

T14 is independent and may run at any time. T15/T16 are authorized (D12 branch A) and hang off
T11/T12 for the vocabulary; they do not block the plugin path.
No dependency is declared for file-scope overlap alone: T1/T5/T7/T11/T14/T17 have disjoint
scopes and may run concurrently.

## 7. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Factory's plugin discovery path or `${DROID_PLUGIN_ROOT}` expansion differs from the expectation | low | Factory's documentation confirms the layout (`.factory-plugin/plugin.json` plus `hooks/hooks.json` at the plugin root, `${DROID_PLUGIN_ROOT}` expansion) and this machine's `~/.factory/plugins/` exists (§2.5); the live check in T21 is the detector; the plugin tree is Kyber-owned and removable; the install path is a single function (`factory-plugin/install.ts`) |
| The hook payload's field names differ from the expectation (A3) | medium | The documented payload carries `session_id`, `tool_name`, `tool_input`, `tool_response`, `session_duration_ms` and `message_count`; it does **not** document a model field, which is why the model comes from the source that measured it (D39) — the plugin names one only when the payload carries it, and the file source names it from the companion file. The mapper tolerates every absent field; no attribute is fabricated; the contract is versioned, so a wrong guess is a version bump, not a data corruption |
| The hook's `session_id` differs from the jsonl `session_start` id (A2) | medium | The exact-match pairing degrades to `not_measurable`, never to a wrong number; the live check reports which case held |
| A Factory hook event blocks the host while the script posts | low | The POST has a ≤ 2 s abort deadline; every error is swallowed; the script exits 0; the spool absorbs a failed delivery |
| The shared-transport extraction drifts the OpenCode bundle | low | The committed bundle's drift test is the witness and is in T3's scope; `bundle-opencode-plugin.mjs --check` must exit 0 |
| Two adapters named `droid` | low | D7 pins one adapter with two claim rules, checked in that order; the registry throws on a duplicate name |
| Native spans and plugin rows double-count in one `droid` session | medium | D23 pins the precedence rule (plugin envelope first; a native span for a session that already has plugin rows is evidence, excluded from turns, tools and duration), T15 pins it in a test, and the rule is stated in the ADR (H11) before the branch ships |
| Native export is silently absent where an organisation pins an endpoint | medium | D26 makes the row report the native path as unavailable with that named cause, never as working; on this machine no organisation pins an endpoint (§2.5). The plugin path is unaffected either way |
| Plugin rows and file rows double-count in one `droid` session | **high — it is the observed defect** | D28 pins the source-precedence rule (the plugin defines the session's turns, the file defines its counters, neither is counted twice); T22 pins it in the pairing test and T23 implements it; the rule is inert for every other harness, and the paired session's totals are the file row's counters rather than a sum of two sources |
| The source-precedence rule changes a published figure for a Factory session that has no plugin rows | low | the rule applies only to a **mixed** session; a file-only and a plugin-only `droid` session keep today's figures, and `dash/src/canon/sessions.test.ts` stays green (T23) |
| A child session's tokens are unavailable because the child has no companion file of its own | medium | D29 pairs a child from its **own** companion file through the ordinary exact-id pairing, and the parent's child-usage map is never a token source (A9); a child with no companion file of its own reads `not_measurable` with a stated reason and never the parent's counters |

| The native metrics path answers 500 in production | **high — it is every metrics request the running service receives** | D31 gives the metrics a destination (the canon store's `metric_point` table — D38) and the production sink the port that persists them; T27 pins the production wiring and T28 implements it. The writer's refusal rule is unchanged, so a sink that still cannot persist refuses the batch rather than dropping it |
| The metrics are persisted but nothing projects them | medium | D31 keeps the destination and makes the projection a todo (T20); the telemetry inventory states that the metrics are stored and not yet projected, so no reader is told a metric was measured and shown as one (the honest-unobservability rule) |
| The stored record's status loses the adapter's error evidence | **high — a policy-denied tool call reads as an ordinary one** | D32's rule never downgrades an error status at the receiver-timing seam, and it is inert for every other harness (the `droid` adapter is the only adapter that assigns a status); T29 pins the stored record and T30 implements it |
| The legacy update fixtures are rewritten instead of modernized | medium | D33 records that the plugin count was incidental to what the three fixtures proved, that their substance is unchanged, and that the modernization states it (each plugin entry names its harness); T31 updates them in place, keeps their names, their helper and their other assertions, and the diff shows that no other assertion moved |
| The updater's Factory call drifts into strict mode | medium | D34 pins the mode in the RED test as the exact argument list, including `--preserve`; D11 already mandates preserve mode for `kyber-weave update`, so a strict-mode Factory call would contradict a decision the plan had already made — and it would turn every owner-edited Factory plugin into a failed step |
| The Factory plugin is installed under a root Factory never reads | medium | D35 keeps the Factory root with the installer (`$FACTORY_DIR` else `~/.factory`, the resolution the file source uses), hands the Factory call **no root**, and refuses the flag — so the plugin and the file source always read the same root and the D28 pairing holds; A's precedence was rejected on exactly this ground |
| A reader takes the file source's `unknown` placeholder for a measured model | low | D40 names the placeholder as the file source's own string, in §3.2, in §3.5 and in the telemetry inventory (H12); the plugin path never produces it; changing it is a file-source change outside this plan's scope (D21) and is recorded as a follow-up, not done |
| Two byte-identical decoded metric points collapse into one row | low | D42 keeps the content-digest identity and corrects the plan's phrasing to "one row per **distinct** decoded point"; the duplicate case is a re-delivery of the same point, which the digest makes idempotent; the multiplicity is not preserved, no shipped test pins the collision, and the gap is a todo for T20 |
| The native path's availability is claimed before the row reports it | low | D41 replaces the marker with the shipped fact: the row carries the native-export state and its named cause on every status surface (commit `e140472`), so the closeout claims what ships and nothing more |
| The command surface and the runbook keep describing the flag as inert for Factory | medium | D35 names the documents that must change — the CLI command surface's plugin-install row and exit-code row, and the runbook's Factory plugin paragraph (H18) — and T33 carries the edits with the code |

## 8. Out of scope

The sibling harness coverage gap (Cursor, Warp, ZCode, Devin, Command Code) is filed as a todo
under the todo index, not implemented here (D21). So is the Factory file source's even token
split, with the evidence that `lastCallTokenUsage` exists and would remove the approximation (the
companion file's `childInclusiveTokenUsageBySessionId` is the parent's aggregate of its children,
not a per-call breakdown — A9, D29). A rewrite of the Factory file import is not in scope.
No change to `dash/web` or `dash/tray`. No change to the shared projection's derivation rules
beyond what T13 proves is missing **and D28 specifies** — the one droid-scoped change, the Factory
source-precedence rule, and nothing else. The canon projection's **consumption** of the native
OTLP metrics is out of scope too: the metrics are persisted (D31), not projected, and their
projection is a todo for T20 to file. No write to any Factory settings or hooks file, ever.

## 9. Verification gates

| Gate | Command | Requirement |
|---|---|---|
| Dash types | `scripts/proc-guard.sh run -- npm --prefix dash run typecheck` | zero errors |
| Dash lint | `scripts/proc-guard.sh run -- npm --prefix dash run lint` | zero findings |
| Dash unit | `scripts/proc-guard.sh run -- npm --prefix dash run test` | zero failures |
| Dash reachability | `scripts/proc-guard.sh run -- npm --prefix dash run check:reachable` | clean |
| .NET | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests` | zero failures |
| E2E conformance | `scripts/proc-guard.sh run --timeout 600 -- npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/harness-collection.spec.ts` | green, isolated config only |
| Docs | `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .` | zero findings each |
| Task gate | `task-gate` on every changed file, per the repository's declared gate set | green |
| Live | T21's owner-run procedure | reported, never claimed by CI |

## 10. Review

The whole change is reviewed as one diff against `origin/main`, with the council gates the
repository declares (`.kyber-weave/kyber-weave.yml`). The reviewer checks, in particular: that
no Factory settings or hooks file is written anywhere in the diff; that the consent path never
turns content on by itself; that no token value is fabricated; that the OpenCode bundle bytes are
unchanged; that the native path implements D23's precedence rule exactly, so a native span and a
plugin row for one session are never summed; and that the native path stays OTLP/HTTP on 4318
only.

## 11. Closeout

`docs-dev` harvests every H-row into its target document, amends ADR 0031 in place, creates the
new ADR, updates the plan's closeout mapping with one destination per durable decision, moves the
plan file to `docs/archive/plans/`, and moves the `## Active Plans` row to `## Archived Plans` in
`docs/plans/README.md` — in the same change, so `docs validate --merge-ready` passes with no
plan left in the active folder (`KW-DOC-LIFECYCLE-003`).

## 12. Assumptions and open questions

Factory-side facts, each labelled by its evidence source: **documented** (Factory's own
documentation, via the research digest), **observed** (this machine's real Factory state, via the
local-environment digest), or **open** (still to be confirmed by the live check).

| ID | Assumption or question | Evidence so far | Resolution point | Effect if wrong |
|---|---|---|---|---|
| A1 | Factory discovers a plugin at `~/.factory/plugins/<name>/` with a `.factory-plugin/plugin.json` manifest and a `hooks/hooks.json`, and expands `${DROID_PLUGIN_ROOT}` in the hooks file | **Documented**: the plugin root holds `.factory-plugin/plugin.json`, `hooks/hooks.json`, `mcp.json` and `droids/`; plugin hooks live at `hooks/hooks.json` in the plugin root and expand `${DROID_PLUGIN_ROOT}`. **Observed**: `~/.factory/plugins/` exists on this machine and holds only `sync_stamp.json`, so the directory is the discovery root and is free of collision | The live check in T21 confirms the loader picks the installed plugin up | The install path changes; the unit stays Kyber-owned and removable |
| A2 | The hook payload's `session_id` equals the `session_start` entry's id in `~/.factory/sessions/*.jsonl` | **Open** — the hook payload is documented to carry `session_id`, but the equality with the transcript's id is not documented | The live check in T21, which reads both | Sessions split in two; tokens read `not_measurable` (honest, not wrong) |
| A3 | The hook payload's field names | **Partly documented**: the payload carries `session_id`, `tool_name`, `tool_input`, `tool_response`, `session_duration_ms` and `message_count`. **No model field is documented**, and the model is resolved by D39's chain instead. The exact field names and which events carry them are still open | The live check in T21 | The mapper drops attributes it cannot find; the contract is versioned and the fix is a version bump |
| A4 | What identity `Stop` and `SubagentStop` carry for turn close and child linkage | **Open** — the events exist and are documented, but the identity they carry is not | The live check in T21 | A turn closes on the next event instead; a child session is not linked (reported as not captured, never guessed) |
| A5 | The hook script's execution protocol | **Partly documented**: hooks are arbitrary shell commands that receive JSON on stdin. The exit-code and stdout expectations are still open | The live check in T21 | The script is renamed or invoked differently; the hook registration is the only change |
| A6 | Whether the native export needs `OTEL_LOG_MESSAGE_CONTENT` for spans | **RESOLVED, and the issue's phrasing is only half right.** Default is metrics only, delta temporality, 60-second flush. Under the **`legacy`** format, spans exist only as the content spans `droid.message.user`, `droid.message.assistant`, `droid.tool.call`, `droid.tool.result`, and only when `OTEL_LOG_MESSAGE_CONTENT=true`; `aggregate` + `legacy` is metrics only with zero spans. Under the **`genai`** format, the trace structure is emitted whenever a customer endpoint is configured and the content flag gates only the four content attributes; `aggregate` + `genai` keeps the full trace with the content attributes stripped. **Tokens and cost are never exported**, in either format | The research digest; no further check needed | Nothing — the plan states both branches and never asserts the `legacy`-only rule as general |
| A7 | Whether Factory's native OTLP endpoint is user-configurable | **RESOLVED: YES.** `OTEL_TELEMETRY_ENDPOINT` (base URL; metrics → `{endpoint}/v1/metrics`, spans → `{endpoint}/v1/traces`; falls back to `OTEL_EXPORTER_OTLP_ENDPOINT`) and its sibling variables are machine environment variables; there is no personal `settings.json` telemetry key; Factory's own documented example is `export OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"`. **Caveat (D26)**: an organisation-managed `telemetry` block overrides the machine variables, so a pinned organisation gets no local export. On this machine the org cache holds no `telemetry` block | The research digest; the live check exercises the local case | None for the plugin path; D26 governs how the native path is reported |
| A8 | The model's resolution when the hook payload carries none | **Observed, and now settled by the shipped code (D39)**: `~/.factory/settings.json` has no top-level `model` key (its model lives in nested per-role blocks) and is never read for a model; the companion `<session-id>.settings.json` carries the session's `model`, and the **file source** is the reader that publishes it, wrapper-stripped; each transcript assistant record carries `message.modelId`, and **no shipped reader reads it** — it is not a link in the chain. A plugin turn names a model only when the hook payload carries one, and Factory's own payload documents no model field, so it generally names none | D39's chain; the live check records which source supplied the model | The model is absent on the row — never guessed; and where the file source's own `unknown` placeholder stands in, it is the file source's literal, not a measurement (D40) |
| A9 | A Factory child (subagent) session is a **first-class session on disk**: it has its own transcript and its own companion settings file, and the file source discovers it as its own session | **Observed** (read-only inspection of this machine, 2026-11-01): the parent session `5f50fd96-…`'s companion file names the child `bfde2f1a-…`; the child's own `<child-id>.settings.json` carries exactly the counters the parent names for it; the child's own transcript's `session_start` names the child's id, and its `callingSessionId` names the parent | D29 pairs the child from its **own** companion file; T26's corrected fixture is the evidence | A child session with no companion file of its own reads `not_measurable` with a stated reason — honest, never the parent's counters |
| A10 | The parent's `childInclusiveTokenUsageBySessionId` map holds the **same numbers** the child's own companion file holds, and the parent's `inclusiveTokenUsage` is the parent's own `tokenUsage` plus every child's entry | **Observed** (read-only inspection of this machine, 2026-11-01): the parent `5f50fd96-…` reports `tokenUsage` input 1253 / output 5816 / cacheRead 320826 and `inclusiveTokenUsage` input 369300 / output 47551 / cacheRead 1938106 — the difference is exactly its child's entry (368047 / 41735 / 1617280) | D29's rule that the map is never a token source, and that a child's usage is never folded into the parent's totals | Folding a map entry into the parent would double count the child's tokens; the rule prevents it, and the characterization test that pins the map as ignored stays correct |
| A11 | What an OTLP exporter does with a metrics batch the receiver refuses | **Documented (OTLP, not Factory-specific):** an exporter retries a failed export on its own schedule, and a 5xx is a retryable failure. The receiver's own failure window records each refusal, so the refusals are observable without knowing the exporter's retry policy | The live check in T21, if the native endpoint is configured | A deployment whose exporter retries sees repeated refusals and a filled failure window — which is the production consequence D31 repairs. The plan does not depend on the retry: with a destination, the path succeeds |

## 13. Investigation record

Seven evidence sources, each labelled by who produced it and what it can prove:

1. **Repository evidence (architect session).** `codegraph_explore`, `docs_explore`,
   `docs_for_symbol` and `docs_glossary` are not loadable in the architect session (§2.1), so the
   findings in §2.2 come from direct reads and targeted greps of the named repository files.
2. **Documentation checks (conductor).** `docs validate .` and `docs drift .` were run by the
   conductor against this saved plan and index, over a CodeGraph-indexed checkout: both **pass,
   0 errors, 0 warnings**, 150 documents.
3. **External and local evidence (research digest and local-environment digest, both relayed by
   the conductor).** Factory's own documentation (Part 1 of the reconciliation) and a read-only
   inspection of this machine's real Factory state (Part 2). These are the only sources for
   Factory-side behaviour, and every statement drawn from them is labelled as such in §2.5 and
   in the `## Decisions` table. Where the two disagree with the issue text — the shape of
   `~/.factory/settings.json` and the "spans need `OTEL_LOG_MESSAGE_CONTENT`" claim — the
   corrected form is recorded (D24, A6) and the issue's form is not carried forward.
4. **The D28–D30 amendment (architect session, 2026-11-01).** The same tooling limits apply: the
   CodeGraph MCP tool and the Kyber-Weave MCP documentation tools are not loadable in this
   session, and this session has no shell, so the `codegraph explore` CLI fallback is unavailable
   too — the discovery below is direct reads and targeted greps. **Discovery: codegraph 0, docs 0,
   direct reads 15 files and 8 targeted greps** — `dash/src/canon/measurability.ts`,
   `dash/src/canon/sessions.ts`, `dash/src/canon/sessions.droid-pairing.test.ts`,
   `dash/src/canon/twin-dedupe.ts`, `dash/src/canon/adapters/droid.ts`,
   `dash/src/canon/ingest.ts`, `dash/src/canon/store.ts`, `dash/src/canon/types.ts`,
   `dash/src/canon/runs.ts`, `dash/src/providers/droid.ts`, `dash/src/providers/droid.test.ts`,
   `dash/src/synth/synth.ts`, `dash/src/synth/readers/` (a directory listing),
   `dash/src/analysis/signals.ts`, and this plan; the greps covered `measurability`,
   `childInclusive`, `synthesizeCall(`, `.measurability`, `turnsOf` and `droidAdapter`. The reads
   were necessary because the semantic index could not be loaded at all, and each one answered a
   specific question the three findings raised: the file-source declaration table and the merge
   rule (Finding 1), the twin-join rule and its all-zero exclusion (Findings 1 and 3), the
   session projection's derivation of the turn set, the totals, the availability, the cost, the
   timeline and the duration (Findings 1 and 3), the adapter's reason wording (the small
   finding), and the file source's companion-file reader (Finding 2). **Local evidence (read-only,
   outside the repository):** three files under this machine's real `~/.factory/sessions/` — the
   parent session `5f50fd96-…`'s companion settings file, the child session `bfde2f1a-…`'s own
   companion settings file, and the child's own transcript — which is the evidence for A9 and A10
   and therefore for D29. No file outside the plan was written. **The two documentation checks
   could not be re-run in this session** for the reason in §2.1 and must be re-run by the
   conductor against this amended plan.
5. **The D31–D32 amendment (architect session, 2026-11-01).** The same tooling limits apply: the
   CodeGraph MCP tool and the Kyber-Weave MCP documentation tools are not loadable in this
   session, and this session has no shell, so the `codegraph explore` CLI fallback is unavailable
   too — the discovery below is direct reads and targeted greps. **Discovery: codegraph 0, docs 0,
   direct reads 11 files and 9 targeted greps** — `dash/src/otel/service.ts`,
   `dash/src/otel/writer.ts`, `dash/src/otel/receiver.ts`, `dash/src/otel/receiver.test.ts`,
   `dash/src/canon/ingest.ts`, `dash/src/canon/ingest.test.ts`, `dash/src/canon/store.ts`,
   `dash/src/canon/log-ingest.ts`, `dash/src/canon/adapters/droid.ts`,
   `dash/src/canon/adapters/droid.test.ts`, `dash/src/canon/adapters/copilot.ts`, and this plan;
   the greps covered `writeMetrics`, `OTLP_PERSISTENCE_FAILED`, `growWithReceiverTiming`,
   `baseRecord`, `record.status`, `CREATE TABLE`, `lastAcceptedAt`, `metrics`, and the writer's
   and service's construction sites. The reads were necessary because the semantic index could
   not be loaded at all, and each one answered a specific question the two seams raised: the
   production sink's shape and the writer's metrics port and refusal rule (Seam 1); the receiver's
   metrics route, its error mapping and its health window (Seam 1); the canon schema's
   `IF NOT EXISTS` discipline and the store's wipe scope (Seam 1); the log-ingest module the
   metrics ingest mirrors (Seam 1); the normalization seam's status write and the adapters that
   assign a status (Seam 2); and the consumers of a stored record's status — the session's tool
   error counts, the run row and the outcome derivation (Seam 2). Four governed documents were read
   directly because the Kyber-Weave MCP documentation tools are unavailable: `docs/rules/
   honest-unobservability.md`, and the receiver-activity passages of `docs/dash/architecture.md`,
   `docs/dash/runbook.md` and `docs/dash/telemetry-inventory.md`. No file outside the plan was
   written. **The two documentation checks could not be re-run in this session** for the reason in
   §2.1 and must be re-run by the conductor against this amended plan.
6. **The D33–D34 mediation (architect session, 2026-11-01).** The same tooling limits apply: the
   CodeGraph MCP tool and the Kyber-Weave MCP documentation tools are not loadable in this
   session, and this session has no shell, so the `codegraph explore` CLI fallback is unavailable
   too. **Discovery: codegraph 0, docs 0, direct reads 7 files and 12 targeted greps** —
   `KyberDashCapturePluginStep.cs`, `SelfUpdater.cs`, `UpdateCommandTests.cs`,
   `SquadCapturePluginStepTests.cs`, `SquadGlobalRoots.cs`, `SquadTarget.cs`, and this plan. The
   reads answered the escalation's questions one by one: the legacy helper's step naming, the
   legacy fixtures' assertions and doc comments, the new RED fixtures, the worker's implementation
   on both sides, the global-root resolution, and the plan's D11/D18/T17/T18 wording. One read of
   the capture-plugin step returned a stale copy of the file; it was re-read before any conclusion
   was drawn from it. No file outside the plan was written. **The two documentation checks could not
   be re-run in this session** for the reason in §2.1.
7. **The D35–D37 amendment and the D14 correction (architect session, 2026-11-01).** The same
   tooling limits apply: the CodeGraph MCP tool and the Kyber-Weave MCP documentation tools are
   not loadable in this session, and this session has no shell, so the `codegraph explore` CLI
   fallback is unavailable too. **Discovery: codegraph 0, docs 0, direct reads 10 files (nine
   source files plus this plan) and 9 targeted greps** — the two installers, the shared transport
   sender, the Factory capture writer,
   `cli/register.ts` and this plan. Three governed documents were read directly because the
   documentation tools are unavailable: `docs/cli-command-surface.md`, `docs/dash/runbook.md` and
   `docs/rules/kyberdash-harness-collection.md`. No file outside the plan was written. **The two
   documentation checks could not be re-run in this session**.
8. **The D38–D42 amendment (architect session, 2026-11-01).** The same tooling limits apply: the
   CodeGraph MCP tool and the Kyber-Weave MCP documentation tools are not loadable in this
   session, and this session has no shell, so the `codegraph explore` CLI fallback is unavailable
   too. **Discovery: codegraph 0, docs 0, direct reads 18 source files and 2 governed documents,
   plus 20 targeted greps** — `dash/src/canon/store.ts`, `dash/src/canon/metric-ingest.ts`,
   `dash/src/canon/metric-ingest.test.ts`, `dash/src/canon/sessions.ts`,
   `dash/src/canon/droid-pairing.ts`, `dash/src/canon/adapters/droid.ts`,
   `dash/src/canon/sessions.droid-pairing.test.ts`, `dash/src/providers/droid.ts`,
   `dash/src/providers/droid.test.ts`, `dash/src/synth/synth.ts`, `dash/src/otel/service.ts`,
   `dash/src/otel/writer.ts`, `dash/src/otel/receiver.ts`,
   `dash/src/capture/harnesses/factory.ts`,
   `dash/src/capture/harnesses/factory-plugin/mapper.ts`, the committed plugin bundle
   (`dash/src/capture/harnesses/factory-plugin/generated/plugin-source.ts`), the two documentation
   validators (`src/KyberWeave.Core/Docs/Validation/DocDriftLinter.cs`,
   `src/KyberWeave.Core/Docs/Validation/DocSpecValidator.cs` — read to learn what the drift gate
   resolves, since this session could not run it) and this plan; plus
   `docs/dash/architecture.md` and `docs/dash/telemetry-inventory.md`, read directly because the
   documentation tools are unavailable. The reads were necessary because the semantic index could
   not be loaded at all,
   and each one answered a specific question: the metric table's DDL, its content-digest key and
   its upsert (**D38**, **D42**); the ingest seam's count and the production sink's port (**D38**,
   **D42**); the receiver's hand-over of the decoded metrics (**D42**); the file source's
   companion-file read and its `unknown` placeholder (**D39**, **D40**); the plugin mapper's
   payload-only model read and the committed bundle's file reads — **none** (**D39**, D4); the
   adapter's model bridge and the session projection's model chain (**D39**); the status row's
   native-export line (**D41**). The harvest's own report was checked against the code rather
   than copied: two of its statements needed narrowing (the plugin reads the hook payload only,
   so it is the **file source** that resolves the model; and the plan's `otel_metric` name was
   this plan's own, never a shipped name). No file outside the plan was written. **The two
   documentation checks could not be re-run in this session** for the reason in §2.1 and must be
   re-run by the conductor against this amended plan.
