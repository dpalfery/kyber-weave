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
list in §5: T1–T21, each with its RED/GREEN split, acceptance criteria and test command. The
conductor dispatches them; no task runs before it does. The reconciliation of the external
evidence digest and the local-environment digest (D12, D23–D26, A1–A8) is folded into this
version; the two documentation checks must be re-run by the conductor after these edits, because
the architect session cannot run them (§2.1).

**Lifecycle note (D27).** The frontmatter `status` is `current`, not `ready`: the ontology's
status vocabulary is closed (`current`, `draft`, `needs-review`, `superseded` —
`src/KyberWeave.Core/Configuration/OntologyConfig.cs:30-31`), and any other value is a
`docs validate` error. The body status is authoritative when the two disagree
(`products/kyber-squad/agents/architect/references/plan-authoring.md`, "Start or resume"), so
**Ready** is this plan's lifecycle state, and the `## Active Plans` row in
`docs/plans/README.md` states it.

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
run them itself.

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
| Each transcript record carries `type` (`session_start`), `id`, `timestamp`, `message`, `seq`, `parentId`, `messageIndex`, `title`, `cwd`, `hostId`, `turnId`, `reason`, `resultKind`; an assistant `message` carries `modelId` (`minimax-m3.1-flash`), `apiProvider` (`fireworks`), `reasoningEffort`, `userMessageSource`, `openaiMessageId`. **No `usage` field exists on any message** | The per-message model is `message.modelId`, the third link of D24's chain. `parentId` gives parent/child turn linkage that the native `genai` OTLP format does not carry (D2) |
| `~/.factory/org-managed-settings.cache.json` holds `factoryTier` (`team_annual`) and a `managedSettings` object with **only** `modelPolicy` and `routedModelVisibilityPolicy` — **no `telemetry` block** | For this user no organisation pins an endpoint, so a machine environment variable would take effect. The org-override caveat (D12, D26) is real but not universal |
| The installed Factory CLI reports version **1.1.0** | The version is recorded in the status surface and the live check so a future Factory change is diagnosable |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D0 | Development mode | `test-first`. Every unit below has a RED task and a GREEN task, separated adversarially. | Supplied in the packet; repository default. | ANSWERED — packet |
| D1 | Autonomy | `autonomous`. Open items are decided and recorded here; only a hard block would escalate, and none did. | Supplied in the packet; matches Config Reg `<autonomy>` in the root `AGENTS.md`. | ANSWERED — packet |
| D2 | Which collection path is primary? | **The hooks-based Factory plugin.** Not a status-only row, not native Factory OpenTelemetry, not file-only ingest, and not a merge into `~/.factory/hooks.json`. | It is the only path that yields turn, tool and session spans with per-event content gating and no dependency on org telemetry settings. The OpenCode plugin is the established precedent for a hook-based harness (`dash/src/capture/harnesses/opencode-plugin/`). The file source stays as the token source (D9). The external evidence strengthens this: the native `genai` format carries **no subagent lineage** — Factory's data reference states that subagent turns are separate trace roots and that the `parent_session.id` linkage "has no equivalent in the GenAI conventions", so a subagent's trace cannot be joined to its parent's (§2.5) — whereas the transcript's own `parentId` does carry that linkage, and the plugin's `SubagentStop` event carries the child session id. Native export is also **tokenless by documentation** (D12), so it can never be the primary path. | ANSWERED — autonomous: decided from the issue's rejected alternatives, the repository precedent, and Factory's own data reference |
| D3 | What exactly is the owned unit? | **A Kyber-owned plugin directory** `~/.factory/plugins/kyberdash-factory/` holding `.factory-plugin/plugin.json`, `hooks/hooks.json` and `hooks/kyberdash-factory.mjs`, installed and removed as one unit with one receipt. Project scope (`.factory/plugins/`) is **not** used: the capture is per-user and the owner may have many projects. | A directory is required because Factory's plugin shape is a manifest plus a hooks directory (issue body, confirmed by Factory's own documentation). The unit is Kyber-owned, so it is reversible; the owner's hook file is never touched (D4). The discovery root `~/.factory/plugins/` exists on this machine and holds only `sync_stamp.json` (§2.5), so the subtree is free of collision; the live check (T21) confirms the loader picks the installed plugin up, and it remains the detector for that residual (A1). | ANSWERED — autonomous: decided from Factory's documented plugin layout and the observed local plugin root; the residual live confirmation is A1's |
| D4 | Does anything write the owner's Factory files? | **No.** `~/.factory/settings.json`, `~/.factory/hooks.json`, `.factory/settings.json` and `.factory/hooks.json` are never created, read, merged, rewritten or restored — not by `enable`, not by `disable`, not by `squad`, not by `update`. They stay byte-identical. The one read-only exception is the **companion per-session file** `<session-id>.settings.json` beside a session's transcript, which the plugin reads for a single key — the session's `model` (D24) — and never writes; it is not one of the shared settings files. | ADR 0026 (never own harness settings) and ADR 0031's file-plugin amendments (own a whole file, never a shared settings file). `resolvePath` points at `~/.factory/settings.json` **only** as install evidence (D16), never as a write target. The byte-identity acceptance criterion is pinned by a test that creates an owner `~/.factory/hooks.json` and a project `.factory/hooks.json` first, because this machine has neither (§2.5, T7). | ANSWERED — autonomous: decided from ADR 0026 / ADR 0031 |
| D5 | Wire contract: reuse the OpenCode contract-3 keys? | **No. A new Factory contract, version `1`, with `factory.*` attribute keys and its own span names**, in its own leaf module `dash/src/capture/harnesses/factory-plugin/contract.ts`. | The OpenCode adapter claims any span carrying the contract-3 markers (`dash/src/canon/adapters/opencode-envelope.ts:47-79`), so reusing `opencode.*` keys would attribute Factory rows to OpenCode — a cross-harness mis-attribution, which the fingerprint rule (R6.2) exists to prevent. A separate contract also lets the two plugins evolve independently. | ANSWERED — autonomous: decided from the adapter fingerprint rule |
| D6 | Which canonical harness id do the plugin's rows carry? | **`droid`** — the id the file source already stores (`dash/src/providers/droid.ts:384`, `dash/src/refresh/registry.ts:406`). The plugin adapter is therefore named `droid`. | One harness bucket for one harness. The dashboard shows Factory once, with the plugin's turns and the file source's tokens in the same bucket. Display name: `Factory (Droid)`. | ANSWERED — autonomous: decided from the existing file-source id |
| D7 | One adapter or two? | **One adapter, named `droid`**, claiming (a) the Factory plugin envelope (`factory.contract_version = 1`) and (b) native `gen_ai.*` spans carrying `gen_ai.agent.name=droid` — the native claim is authorized (D12 branch A) and governed by D23's precedence rule. The plugin envelope is checked first; a span is claimed by one rule only. | `AdapterRegistry.register` throws on a duplicate name (`dash/src/canon/adapters/registry.ts:73-79`), so two adapters named `droid` are impossible. Precedence is explicit so the plugin path's rows never depend on the native claim, and so a native span and a plugin row for one session are never summed (D23). | ANSWERED — autonomous: decided from the registry's uniqueness rule |
| D8 | How do plugin turns and file tokens reach one session? | **By exact session-id equality only.** The plugin stamps the hook's `session_id`; the file source stores the `session_start` entry's id (`dash/src/providers/droid.ts:150-153`). When they are equal the shared projection groups them into one session and the file row carries the tokens; when they differ, the plugin records carry `not_measurable` with a reason naming the file-source pairing. **No fuzzy join, no time-window join, no token fabrication.** | **Assumption A2** (below): the hook's `session_id` equals the jsonl `session_start` id. The rule is exact-match, so a wrong assumption degrades to `not_measurable`, never to a wrong number. | ANSWERED — autonomous: low-confidence, no authoritative source (cost of wrong: sessions split in two; mitigated by the exact-match rule and the live check) |
| D9 | Where do tokens come from? | **Only from the file source**, through D8's exact-id pairing. The plugin **does not** read `transcript_path` and does not read `~/.factory/settings.json`; it never writes a zero. A plugin record with no paired file row declares `token_usage` (and the counter classes) `not_measurable` with a reason that names the file-source pairing, the way the OpenCode adapter declares an absent counter. | The hook payload carries no counters (issue body, and Factory's own data reference: token and cost data "are not exported as customer OTEL metrics"). The local evidence confirms where the counters actually live: the **companion per-session file** `<session-id>.settings.json` beside the transcript, whose `tokenUsage` carries `inputTokens`, `outputTokens`, `cacheCreationTokens`, `cacheReadTokens`, `thinkingTokens` and `factoryCredits` — which is exactly what `dash/src/providers/droid.ts:129-135` already reads, and **not** `~/.factory/settings.json`. The plugin never reads the transcript, which would be a second, ungated content reader. Honest unobservability is the rule (`docs/rules/honest-unobservability.md`). | ANSWERED — autonomous: decided from the issue's token rule, the local environment evidence, and the honest-unobservability rule |
| D10 | Content consent | **Off by default and never turned on by enabling the plugin.** The consent file is `~/.kyberdash/factory-plugin.json` (`{ "version": 1, "content": <boolean> }`). The install creates it with `content: false` only when it is absent. `capture enable` (the Settings switch) installs the plugin and **does not** change consent. The owner turns content on explicitly, through a new verb `kyberdash kyber capture plugin consent --harness factory --on|--off`, or by editing the file. `KYBERDASH_FACTORY_CONTEXT=1|0` overrides it, mirroring the OpenCode override. | The issue's acceptance criterion: "enabling the plugin never turns content on". This **differs** from OpenCode, whose Settings switch also sets consent (`dash/src/capture/harnesses/opencode.ts:63-66`), and the difference is deliberate: for Factory the switch is "install the capture", not "start recording my prompts". The verb is additive and cheap to drop if the owner prefers editing the file. | ANSWERED — autonomous: decided from the issue's acceptance criterion |
| D11 | Install modes and refusal | **Strict** for `capture enable` (a foreign or owner-edited plugin directory is refused with exit 1 and nothing is written) and **preserve** for `squad` / `kyber-weave update` (kept with a warning, exit 0). The receipt records every installed file's path and SHA-256; removal requires every digest to match, so an edited file is left in place. An absent plugin with `--if-installed` writes nothing. | The OpenCode installer's exact semantics (`dash/src/capture/harnesses/opencode-plugin/install.ts:239-334`), extended from one file to a tree. The receipt shape grows from `{version,path,sha256}` to `{version, root, files:[{path,sha256}]}` — a superset, not a replacement. | ANSWERED — autonomous: decided from the OpenCode installer |
| D12 | Is Factory's native OpenTelemetry a second path? | **ANSWERED — branch A: the endpoint IS user-configurable to an arbitrary local address, so the native group (T15/T16) is AUTHORIZED.** The endpoint is set by **machine environment variables** or by an **org-managed `telemetry` block**; there is **no personal `settings.json` telemetry key** (a `telemetry` block in a user or project settings file is ignored with a warning, "read from the organization level only"). The documented machine variables are `OTEL_TELEMETRY_ENDPOINT` (base URL; metrics → `{endpoint}/v1/metrics`, spans → `{endpoint}/v1/traces`; falls back to `OTEL_EXPORTER_OTLP_ENDPOINT`), `OTEL_TELEMETRY_HEADERS` (falls back to `OTEL_EXPORTER_OTLP_HEADERS`), `OTEL_LOG_MESSAGE_CONTENT`, `OTEL_METRIC_EXPORT_INTERVAL` (default 60000), `OTEL_CUSTOMER_ENABLED`, `OTEL_TELEMETRY_FORMAT` (`legacy` \| `genai`), `OTEL_TELEMETRY_GRANULARITY` (`user` \| `aggregate`), `OTEL_RESOURCE_ATTRIBUTES` and `DROID_PARENT_SESSION_ID`. **Factory's own documented example is `export OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"`** — the KyberDash receiver address. The endpoint must be `http`/`https` with no query string or fragment. **No collector is bundled or required**: Droid pushes OTLP/HTTP directly to the endpoint, so KyberDash is simply the customer's own collector. Only OTLP/HTTP is documented — port 4318, `/v1/metrics` and `/v1/traces`; no gRPC path, no `OTEL_EXPORTER_OTLP_PROTOCOL`, no port 4317. **Signals:** default is metrics only, delta temporality, 60-second flush. Under `legacy`, spans exist only as the content spans `droid.message.user`, `droid.message.assistant`, `droid.tool.call`, `droid.tool.result`, and only when `OTEL_LOG_MESSAGE_CONTENT=true`; `aggregate` + `legacy` is metrics only, zero spans. Under `genai`, the **trace structure is emitted whenever a customer endpoint is configured** and the content flag gates only the four content attributes; `aggregate` + `genai` keeps the full trace with the content attributes stripped. **Logs are never exported to a customer endpoint**, so KyberDash's `POST /v1/logs` receives nothing from Droid. **Tokens and cost are never exported** — the documentation states affirmatively that they "are not exported as customer OTEL metrics" and directs users to Factory's hosted org-level Analytics API; there is no `gen_ai.usage.*` metric and no token attribute. | Consequences of branch A, all now binding: the receiver grows a third signal path (`POST /v1/metrics`, T15/T16); the `droid` adapter gains a native claim with the exact vocabulary in §3.4; native spans are **tokenless**, so they can never replace the plugin or the file pairing (D9); the `genai` format carries **no subagent lineage**, which the plugin path does carry (D2); and the native path is **advisory** on the developer machine but **silently unavailable** where an organisation pins an endpoint (D26). The plugin path is unaffected by every one of these facts. | ANSWERED — autonomous: accepted from the research digest (Factory's own documentation, verified twice); the digest's branch A is confirmed by Factory's documented `OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"` example |
| D13 | How is the plugin script delivered? | **One self-contained ESM file**, `hooks/kyberdash-factory.mjs`, bundled by esbuild from `dash/src/capture/harnesses/factory-plugin/entry.ts` and committed as generated text (`factory-plugin/generated/plugin-source.ts`), exactly like the OpenCode bundle. Its first line is the ownership marker `// kyberdash-factory-plugin v1`. | A plugin tree cannot resolve `node_modules`, so the script must be self-contained. The committed text keeps npm and SEA builds on the same bytes, and the existing drift-test pattern (`bundle.test.ts`) proves it. | ANSWERED — autonomous: decided from the OpenCode bundle contract |
| D14 | Shared code: transport and consent writes | **Extract the generic parts into shared leaves and keep the old paths as re-export shims.** `dash/src/capture/harnesses/transport/{outbox.ts,otlp-sender.ts}` and `dash/src/capture/consent-file.ts` become the shared modules; `opencode-plugin/transport/*.ts` and `opencode-plugin/consent-write.ts` become thin re-exports so the OpenCode bundle, its imports and its tests are unchanged. | Copying the outbox would fork a bounded, security-relevant component; the repository's review lenses flag duplication. The shim pattern keeps the OpenCode bundle byte-stable (its drift test proves it). The Factory plugin adds a **file-backed** spool (D15) rather than reusing the in-memory outbox, so the shared module contributes the OTLP body builder, the delivery error type and the retry/429 policy, not the queue. | ANSWERED — autonomous: decided from the duplication rule; cost of wrong: an OpenCode bundle drift, caught by the existing drift test and `docs validate` |
| D15 | How does a one-shot hook process deliver a span? | **A bounded synchronous POST plus a bounded file-backed spool.** The hook script reads stdin, maps the event to at most one span, POSTs it to `http://127.0.0.1:4318/v1/traces` with a ≤ 2 s abort deadline, and on failure appends it to `~/.kyberdash/capture/factory/outbox.jsonl` (bounded: 500 events / 4 MB, drop-oldest). Each run drains the spool first. Every error is swallowed, stdout carries only the hook protocol, diagnostics go to a bounded 64 KB tail file, and the script always exits 0. | The OpenCode plugin is a long-lived process with an in-memory outbox (`transport/outbox.ts:1-20`); a Factory hook script is one process per event, so an in-memory queue would lose the span when the process exits. The file spool is the file-backed analogue of the same policy (bounded, drop-oldest, retry with backoff, 429 halves the batch). Never fail the host is the issue's hard requirement. | ANSWERED — autonomous: decided (cost of wrong: a lost span on a transient receiver outage; mitigated by the spool) |
| D16 | The registry row's shape | **`kind: 'managed'`, `id: 'factory'`, `displayName: 'Factory (Droid)'`, `desiredKeys: () => ({})`, a `filePlugin`, `resolvePath` → `~/.factory/settings.json` honouring `FACTORY_DIR`.** Because it declares no config key and no `configFiles`, `isPluginOnly` is true, so `enable` = install, `disable` = uninstall, and no key is read, written or receipted. `resolvePath` is used only as install evidence. | The OpenCode shape (`dash/src/capture/harnesses/opencode.ts:239-246`). `FACTORY_DIR` is honoured because the file source honours it (`dash/src/providers/droid.ts:68-69`), so both paths agree on the root. Install detection gets its own probe group (the `~/.factory` directory **and** `~/.factory/settings.json`), because a `.factory` tree exists for a Droid that never wrote a settings file (`dash/src/capture/detect.ts:80-140`). | ANSWERED — autonomous: decided from the OpenCode row and the detect seam |
| D17 | Does the Settings drawer need a change? | **No.** The drawer renders the registry's rows generically; a managed row with a plugin state gets Enable and Disable. The plan pins the parity with a test rather than with web code. | `dash/web/src/components/settings/HarnessCollectionList.tsx`, `dash/web/src/lib/kyberApi.ts:1947`, `dash/src/server/capture-routes.ts:81`. ADR 0033 and the display-layer rule forbid moving logic into the surfaces. | ANSWERED — autonomous: decided from the display-layer rule |
| D18 | Does Squad / `kyber-weave update` install the plugin? | **Yes, in preserve mode, exactly as the OpenCode plugin is installed.** `KyberDashCapturePluginStep` runs `kyberdash kyber capture plugin install --harness factory --preserve --config-dir <factory global root>` after a global Factory deploy; `SelfUpdater` runs the same with `--if-installed`. The Settings row carries a manual note naming both. | The Factory global root is `~/.factory` with no environment override (`src/KyberWeave.Core/Squad/Deployment/SquadGlobalRoots.cs:24,54,124`), and Squad already owns `~/.factory/droids/` and `~/.factory/skills/` — the plugin directory is a third, distinct subtree, so no collision (`docs/kyber-squad/onboarding.md:576`). The step never changes Squad's exit code. | ANSWERED — autonomous: decided from the OpenCode step (`KyberDashCapturePluginStep.cs:161-169`) and the Factory global-root rule |
| D19 | Documentation impact | **`extends-component`; `impact-components: [KyberDash, KyberSquad]`.** A new ADR records the Factory capture decision; ADR 0031 is amended in place for the Factory row. | The choice — a hooks plugin and a Kyber-owned plugin directory rather than a settings edit or a native-OTel dependency — constrains future Factory work and had viable rejected alternatives. The KyberSquad component is touched only by the global-install step and its documentation. | ANSWERED — autonomous: decided from the ADR criteria and the repository's scale vocabulary |
| D20 | Live verification | **A live Droid turn is an owner live check, not a CI gate.** The automated proof is the unit suite plus a replay e2e through the production pipeline. The live procedure (install, one scripted turn, read the dashboard) is documented in the runbook and executed by the owner. | The OpenCode live gate is likewise owner-run and never part of the test suite (`docs/dash/runbook.md` §1b). A live check that calls a model cannot be a CI gate. | ANSWERED — autonomous: decided from the OpenCode precedent |
| D21 | Scope boundaries | **Out of scope:** the sibling coverage gap named in the issue (Cursor, Warp, ZCode, Devin, Command Code) — captured as a todo under the todo index, not implemented here; a Factory *file* import rewrite (the even token split in `providers/droid.ts` stays as it is, with a follow-up todo filed — see the supporting column); any change to the shared projection's session-derivation rules; any change to `dash/web` or `dash/tray`; Factory's own `settings.json`/`hooks.json` content. | The issue names the sibling gap as a separate concern. On the even split: the local evidence shows the companion `<session-id>.settings.json` carries **`lastCallTokenUsage`** and **`childInclusiveTokenUsageBySessionId`** beside `tokenUsage`, so the even split of the session total across assistant calls (`dash/src/providers/droid.ts:196-232`) is a **known lossy approximation** that better per-call data exists for — not an accident. It stays out of scope for this plan (the plugin work does not depend on it), but T20 must file a todo under the todo index recording the loss and the two fields that would remove it, so the limitation is tracked rather than forgotten. | ANSWERED — autonomous: decided; the even-split follow-up is recorded as a todo requirement in T20 |
| D22 | The plan's own approval gate | **APPROVED. The plan moves to Ready and is executable; execution authority is the task list in §5.** Scale `extends-component` with `impact-components: [KyberDash, KyberSquad]`, `development-mode: test-first` and `autonomy: autonomous` are confirmed. Every autonomous row D0–D27 is accepted as recorded, including the five rows added by the reconciliation (D23–D27) and the correction of the model-resolution rule (D24). No task runs until the conductor dispatches it. | The conductor supplied explicit `AUTONOMOUS_APPROVAL: approve-and-execute` under resolved autonomy `autonomous` (repository root `AGENTS.md` Config Reg). `products/kyber-squad/agents/architect/references/plan-authoring.md` ("Ready and finalization"). The two documentation checks had already been run by the conductor against this plan and index (`docs validate .` and `docs drift .`: 0 errors, 0 warnings, 150 documents) and must be re-run after the reconciliation edits, since the architect session cannot run them. | ANSWERED — autonomous: recommendation accepted (`AUTONOMOUS_APPROVAL: approve-and-execute`) |
| D23 | Double counting between native spans and plugin rows in the same `droid` bucket | **Precedence rule, in this order: (1) the plugin envelope is claimed first; (2) a native `genai` span is claimed only when the session has no plugin rows; (3) a native span for a session that already has plugin rows is NOT summed with them — it is attributed to the session as corroborating evidence and excluded from the session's turn, tool and duration totals.** The adapter distinguishes the two sources by their attribute fingerprints, never by name: the plugin envelope carries `factory.contract_version = 1` plus a `factory.` marker; a native span carries `gen_ai.agent.name = 'droid'` and `gen_ai.operation.name` and **no** `factory.` attribute. | The native `gen_ai.conversation.id` **is** the Droid session id, and `session.id` is present on every native data point, so a native span and a plugin row are joinable to the same session and the same turn — summing them would double count turns, tools and duration. The native format also has no per-turn identity that matches the plugin's, and no subagent lineage at all (D2). The rule is deterministic, testable at the adapter seam, and stated in the ADR before the branch ships (H11). The consequence for the dashboard: a session with plugin rows shows the plugin's turns and tools; a native span for that session is visible as evidence (the claim is recorded) but adds no turn, no tool and no duration. | ANSWERED — autonomous: decided from the native attribute vocabulary (D12) and the existing no-double-count rule the OpenCode pipeline follows |
| D24 | How is the model resolved? (The issue's premise is wrong about the settings file's shape.) | **Corrected chain, in order: (1) the model on the hook payload, when the event carries one; (2) the companion per-session file `<session-id>.settings.json`'s `model` key; (3) the transcript's per-assistant-record `message.modelId`; (4) otherwise the model is ABSENT — never guessed, never defaulted, and never taken from a nested block of `~/.factory/settings.json`.** The plugin itself resolves (1) and (2); (3) is the file source's, which already reads the same companion file. | The local evidence shows `~/.factory/settings.json` has **no top-level `model` key** — the model lives in nested blocks (`sessionDefaultSettings.model`, `missionModelSettings.workerModel`, `subagentModelSettings.{light,medium,heavy}Model`, `compactionModel`), so the issue's "otherwise the `model` in `~/.factory/settings.json`" would have read a key that does not exist and, worse, would have had to guess among nested blocks (a subagent's `mediumModel` is not the parent's model). The companion per-session file carries the session's actual `model` (`minimax-m3.1-flash` on this machine), and the transcript carries `message.modelId` per assistant record. The plugin reads the companion file **read-only** for this one key; it never writes it, and the token counters stay the file source's job (D9). Reading a file is not reading the owner's settings for a *write* purpose, so ADR 0026 is not engaged. | ANSWERED — autonomous: decided from the local environment evidence (the actual file shapes) |
| D25 | Child (subagent) sessions and their tokens | **A child session's tokens are paired from the companion file's `childInclusiveTokenUsageBySessionId`, keyed by the child session id.** A child session whose id is **not** in that map reads `not_measurable` with a stated reason; one that is in the map is paired, and the pairing is exact-match only. The plugin's `SubagentStop` event supplies the child session id; the parent-child linkage is exact-match only, and a child with no linkable parent stays an unlinked child, never a fabricated one. | The local evidence shows the companion file carries `childInclusiveTokenUsageBySessionId` — child-session token usage is locally available, keyed by child session id, so the honest-unobservability rule for child rows can be stated precisely instead of blanket "not measurable". The native `genai` format carries no subagent lineage at all (D2), which is a further reason the plugin path is primary. `DROID_PARENT_SESSION_ID` is a machine environment variable that adds `parent_session.id` for linking headless/CI sub-sessions **across processes** — it is not a subagent-to-parent link within a session, so it is not used for this. | ANSWERED — autonomous: decided from the local environment evidence and Factory's data reference |
| D26 | What does KyberDash report when an organisation pins a telemetry endpoint? | **The native path is reported as unavailable, never as working.** The Factory row's status states that native OpenTelemetry export is configured by machine environment variables, that an organisation-managed `telemetry` block **overrides** those variables, and that where it does, Droid sends nothing to the local receiver — so the plugin path is the only capture path in that deployment. The status never claims the native path is available; it reports what is observed (native spans arriving or not) and names the override as the reason a deployment sees none. | Org-managed values override the machine environment variables, and a missing `${VAR}` reference disables the customer sink for that session — so a developer whose organisation pins an endpoint gets **no local export, silently**. The local evidence shows this user's org cache holds only `modelPolicy` and `routedModelVisibilityPolicy` and **no `telemetry` block**, so the caveat is real but not universal on this machine. The machine variables are documented as advisory ("Environment variables live on the developer's machine, which makes them advisory"), and only the org-managed `telemetry` block is a manager capability (`Settings → Enterprise Controls → Raw Configuration`). KyberDash cannot read an organisation's managed settings, so the honest report is the observed behaviour plus the named cause, not a guess about the organisation's configuration. | ANSWERED — autonomous: decided from the research digest's org-override rule and the local org-cache evidence |
| D27 | How is the Ready lifecycle state represented, given the ontology's closed status vocabulary? | **The frontmatter `status` stays inside the vocabulary — `current` — and the plan's lifecycle state is stated in the body, which is authoritative.** The body status line reads "Ready — approved for execution 2026-11-01 (D22)", and the `## Active Plans` row in `docs/plans/README.md` shows Ready. A `status: ready` frontmatter would be a `docs validate` error, because the ontology's vocabulary is `current`, `draft`, `needs-review`, `superseded` and any other value is `InvalidVocabulary` at Error severity. | `src/KyberWeave.Core/Configuration/OntologyConfig.cs:30-31` (the default status vocabulary) and `src/KyberWeave.Core/Docs/Validation/DocSpecValidator.cs:203-212` (the closed-vocabulary check); `products/kyber-squad/agents/architect/references/plan-authoring.md` ("Reconcile body status, frontmatter lifecycle, and index status in the same save. The body status is authoritative when they disagree."). Archived plans use `archived`/`complete` only because the loader excludes the archive folder from the corpus. The choice keeps `docs validate` at zero findings while the lifecycle state stays unambiguous to a reader of the plan and of the index. | ANSWERED — autonomous: decided from the validator's closed vocabulary and the plan-authoring rule that the body status is authoritative |

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
| H7 | KyberDash | rule | `docs/rules/kyberdash-harness-collection.md`: the Factory row in the per-harness ground truth table — plugin hooks as the PRIMARY mechanism, file ingest as the token source, and the completeness predicate (D2, D8). |
| H8 | KyberDash | onboarding | `docs/dash/onboarding.md`: the Factory capture plugin section (what it collects, what consent governs, how to turn it on). |
| H9 | KyberSquad | architecture | `docs/kyber-squad/architecture.md` and `docs/kyber-squad/onboarding.md`: the global Factory capture-plugin step (preserve mode) beside the existing OpenCode step, and the `~/.factory/plugins/` subtree Squad does not own (D18). |
| H10 | KyberDash | reference | `docs/install.md` §Updating: the plugin step's Factory guidance line (D18). |
| H11 | KyberDash | architecture | `docs/dash/architecture.md`: the authorized native-OpenTelemetry path for Factory — `OTEL_TELEMETRY_ENDPOINT`, the `genai` attribute vocabulary, the `POST /v1/metrics` route, the plugin-first precedence rule (D23), the tokenless and lineage-free limits, and the organisation-override reporting rule (D26). |
| H12 | KyberDash | reference | `docs/dash/telemetry-inventory.md`: the Factory model-resolution chain (D24) and the child-session token pairing rule (D25), stated as the model and token facts the Factory rows carry. |

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

**The model (D24).** The hook payload carries a model on some events; when it does, that model is
used. When it does not, the plugin reads the **companion per-session file**
`<session-id>.settings.json`'s `model` key — read-only, and never written. If neither carries one,
the model is **absent**: it is never guessed and never defaulted from a nested block of
`~/.factory/settings.json` (that file has no top-level `model` key at all; its model lives in
nested per-role blocks, and reading one would attribute a subagent's model to its parent). The
file source's third link — the transcript's per-assistant `message.modelId` — fills the model for
file-derived rows, not for plugin rows.

**Child sessions (D25).** `SubagentStop` carries the child session id; the parent linkage is an
exact match only, and a child that cannot be linked stays an unlinked child rather than a
fabricated one. A child session's tokens are paired from the companion file's
`childInclusiveTokenUsageBySessionId`, keyed by that child session id; a child with no entry there
reads `not_measurable` with a stated reason.

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

### 3.4 The native-OpenTelemetry group (authorized — D12 branch A)

Factory's native export **can** be pointed at the KyberDash receiver: `export
OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"` is Factory's own documented example, and Droid
pushes OTLP/HTTP directly, so no collector is needed. Two additions make that usable:

**The receiver.** A `POST /v1/metrics` path beside `/v1/traces` and `/v1/logs`: OTLP/JSON metrics
decoded to a metrics shape and handed to a metrics sink port with the same admission, shedding and
health-window discipline as the span path (`dash/src/otel/receiver.ts:1119-1121,1370-1386`). Only
OTLP/HTTP is documented for Factory — port 4318, no gRPC, no 4317 — so no other protocol is
added. **Logs are never exported by Droid**, so `/v1/logs` is untouched by this branch.

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
| T9 | `dash/src/capture/harnesses/factory.test.ts`, `dash/src/capture/status.test.ts` (Factory cases), `dash/src/capture/detect.test.ts` (Factory probes), `dash/src/capture/harnesses/factory-plugin/install-command.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory.test.ts src/capture/status.test.ts src/capture/detect.test.ts src/capture/harnesses/factory-plugin/install-command.test.ts` | The registry holds a Factory writer; `capture status` lists it with plugin, receipt and consent state; `enable` installs and `disable` removes without touching the settings file; the Settings API reports the same row; `capture plugin install --harness factory` accepts the id and rejects an unknown one with exit 2 | The new files/cases exist and fail | Same tests pass; the OpenCode registry and status cases stay green |
| T10 | same as T9 | same as T9 | The Factory writer, its registry row, the detect probes, the status lines and the plugin-install verb exist | — | T9 green |
| T11 | `dash/src/canon/adapters/droid.test.ts`, `dash/src/canon/adapters/droid-envelope.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/adapters/droid.test.ts src/canon/adapters/droid-envelope.test.ts` | The adapter claims only the Factory plugin envelope under contract `1`; an unknown contract version scores 0 and quarantines; a native `gen_ai.agent.name=droid` span is claimed only when the native branch is authorized; a plugin record without paired tokens declares `not_measurable` with a reason, never 0; the adapter stamps `harness: 'droid'` | The files exist and fail | Same tests pass; the OpenCode adapter cases stay green |
| T12 | same as T11 | same as T11 | The `droid` adapter, the envelope leaf and the `ADAPTERS` registration exist | — | T11 green |
| T13 | `dash/src/canon/sessions.droid-pairing.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/sessions.droid-pairing.test.ts` | A plugin row and a file row with the same session id form one session and the file row's tokens are the session's; different ids stay separate and the plugin row's tokens read `not_measurable` with a reason; no token is invented; a child session whose id is in the companion file's `childInclusiveTokenUsageBySessionId` is paired, and one that is not reads `not_measurable` with a stated reason (D25) | The file exists and fails (or passes with **no production change** — see the task note) | The pairing rule holds; a needed production change is a scope-checked edit, not a weakening |
| T14 | `dash/src/providers/droid.test.ts` (Factory token cases), `dash/src/refresh/refresh.integration.test.ts` (droid job) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/providers/droid.test.ts src/refresh/refresh.integration.test.ts` | `dash refresh` still yields a `droid` session whose tokens come from the companion `*.settings.json` `tokenUsage`; the refresh job is unaffected by the plugin work | The new cases exist and fail or pass unchanged | The file source is proven unchanged by the plugin work |
| T15 | `dash/src/otel/receiver.test.ts` (metrics path cases), `dash/src/canon/adapters/droid.test.ts` (native claim) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/otel/receiver.test.ts src/canon/adapters/droid.test.ts` | `POST /v1/metrics` is served and answers 200/4xx per the existing discipline; a wrong path still 404s; a native span carrying `gen_ai.agent.name=droid` and `gen_ai.operation.name` is claimed; a native span for a session that already has plugin rows is claimed as evidence and is **excluded** from that session's turn, tool and duration totals (D23); a native span carries no token counter, so no token is taken from it | The cases exist and fail | Same cases pass |
| T16 | same as T15 | same as T15 | The metrics route and the native claim exist | — | T15 green |
| T17 | `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs`, `tests/KyberWeave.Tests/UpdateCommandTests.cs` | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~SquadCapturePluginStepTests|FullyQualifiedName~UpdateCommandTests"` | A global Factory deploy runs `kyberdash kyber capture plugin install --harness factory --preserve --config-dir <root>`; `kyber-weave update` runs the same with `--if-installed`; neither changes the command's exit code; a missing KyberDash is a skip, not a failure | The new cases exist and fail | Same cases pass; the OpenCode step cases stay green |
| T18 | same as T17 | same as T17 | The step and the updater run the Factory plugin verb | — | T17 green |
| T19 | `dash/e2e/harness-collection.spec.ts` (Factory replay scenario) | `scripts/proc-guard.sh run --timeout 600 -- npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/harness-collection.spec.ts` | A recorded Factory hook payload, replayed through the production plugin → receiver → `droid` adapter → projection → dashboard, renders a Factory session with model and tool attribution and honest token state | The scenario exists and fails | The scenario passes with the isolated config; the other scenarios stay green |
| T20 | governed documents | `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .` | Every H-row's target document carries the durable item; the plan moves to the archive folder with its closeout mapping | The documents fail the harvest checks | Both commands report zero findings |
| T21 | **No automated test — this is the manual, owner-run live verification** that replaces one. Read-only procedure documented in `docs/dash/runbook.md`; evidence written to the gitignored `artifacts/evidence/factory-capture-live/<UTC-stamp>/` | `scripts/proc-guard.sh run -- npm --prefix dash run dev -- kyber capture status --harness factory`, then one scripted Droid turn, then the dashboard | A live Droid turn produces a dashboard-visible Factory session with model and tool attribution; tokens are paired from the file source or honestly `not_measurable`; content is absent unless the consent file says `content: true`; the check records which of A1–A5 and A8 held | n/a — manual verification | The runbook procedure is executed and its result reported; a failure is reported as a failure |

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
regression witness for D14).
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
0 (the committed OpenCode bytes are unchanged); the hook runner exits 0 on every input and never
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
Factory at the end (a status-only row order rule already appends Kilo last).
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
`kyberdash kyber capture plugin install --harness factory --preserve --config-dir <root>`; the
updater runs the same with `--if-installed`; the step's outcome is never Squad's exit code; a
missing KyberDash is a skip; the OpenCode step's cases stay green.
**Depends on.** none.
**Skills.** `test-dev`, `csharp-dev`.

### T18 — GREEN: the Squad and update steps for the Factory plugin

**Objective.** Extend `KyberDashCapturePluginStep` and `SelfUpdater`.
**Files.** `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`,
`src/KyberWeave.Cli/Update/SelfUpdater.cs`.
**Acceptance criteria.** T17 green; the Factory global root comes from the same
`SquadGlobalRoots` resolution Squad uses (`~/.factory`, no override).
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
rules are honoured. **Two todos are filed under the todo index** (D21): the sibling harness
coverage gap (Cursor, Warp, ZCode, Devin, Command Code), and the Factory file source's even
token split, recording that the companion `<session-id>.settings.json` carries `lastCallTokenUsage`
and `childInclusiveTokenUsageBySessionId`, so the split is a known lossy approximation with
better data available (§2.5).
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
| The hook payload's field names differ from the expectation (A3) | medium | The documented payload carries `session_id`, `tool_name`, `tool_input`, `tool_response`, `session_duration_ms` and `message_count`; it does **not** document a model field, which is why D24 adds the companion-file and transcript links. The mapper tolerates every absent field; no attribute is fabricated; the contract is versioned, so a wrong guess is a version bump, not a data corruption |
| The hook's `session_id` differs from the jsonl `session_start` id (A2) | medium | The exact-match pairing degrades to `not_measurable`, never to a wrong number; the live check reports which case held |
| A Factory hook event blocks the host while the script posts | low | The POST has a ≤ 2 s abort deadline; every error is swallowed; the script exits 0; the spool absorbs a failed delivery |
| The shared-transport extraction drifts the OpenCode bundle | low | The committed bundle's drift test is the witness and is in T3's scope; `bundle-opencode-plugin.mjs --check` must exit 0 |
| Two adapters named `droid` | low | D7 pins one adapter with two claim rules, checked in that order; the registry throws on a duplicate name |
| Native spans and plugin rows double-count in one `droid` session | medium | D23 pins the precedence rule (plugin envelope first; a native span for a session that already has plugin rows is evidence, excluded from turns, tools and duration), T15 pins it in a test, and the rule is stated in the ADR (H11) before the branch ships |
| Native export is silently absent where an organisation pins an endpoint | medium | D26 makes the row report the native path as unavailable with that named cause, never as working; on this machine no organisation pins an endpoint (§2.5). The plugin path is unaffected either way |

## 8. Out of scope

The sibling harness coverage gap (Cursor, Warp, ZCode, Devin, Command Code) is filed as a todo
under the todo index, not implemented here (D21). So is the Factory file source's even token
split, with the evidence that `lastCallTokenUsage` and `childInclusiveTokenUsageBySessionId`
exist and would remove the approximation. A rewrite of the Factory file import is not in scope.
No change to `dash/web` or `dash/tray`. No change to the shared projection's derivation rules
beyond what T13 proves is missing. No write to any Factory settings or hooks file, ever.

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
| A3 | The hook payload's field names | **Partly documented**: the payload carries `session_id`, `tool_name`, `tool_input`, `tool_response`, `session_duration_ms` and `message_count`. **No model field is documented**, and the model is resolved by D24's chain instead. The exact field names and which events carry them are still open | The live check in T21 | The mapper drops attributes it cannot find; the contract is versioned and the fix is a version bump |
| A4 | What identity `Stop` and `SubagentStop` carry for turn close and child linkage | **Open** — the events exist and are documented, but the identity they carry is not | The live check in T21 | A turn closes on the next event instead; a child session is not linked (reported as not captured, never guessed) |
| A5 | The hook script's execution protocol | **Partly documented**: hooks are arbitrary shell commands that receive JSON on stdin. The exit-code and stdout expectations are still open | The live check in T21 | The script is renamed or invoked differently; the hook registration is the only change |
| A6 | Whether the native export needs `OTEL_LOG_MESSAGE_CONTENT` for spans | **RESOLVED, and the issue's phrasing is only half right.** Default is metrics only, delta temporality, 60-second flush. Under the **`legacy`** format, spans exist only as the content spans `droid.message.user`, `droid.message.assistant`, `droid.tool.call`, `droid.tool.result`, and only when `OTEL_LOG_MESSAGE_CONTENT=true`; `aggregate` + `legacy` is metrics only with zero spans. Under the **`genai`** format, the trace structure is emitted whenever a customer endpoint is configured and the content flag gates only the four content attributes; `aggregate` + `genai` keeps the full trace with the content attributes stripped. **Tokens and cost are never exported**, in either format | The research digest; no further check needed | Nothing — the plan states both branches and never asserts the `legacy`-only rule as general |
| A7 | Whether Factory's native OTLP endpoint is user-configurable | **RESOLVED: YES.** `OTEL_TELEMETRY_ENDPOINT` (base URL; metrics → `{endpoint}/v1/metrics`, spans → `{endpoint}/v1/traces`; falls back to `OTEL_EXPORTER_OTLP_ENDPOINT`) and its sibling variables are machine environment variables; there is no personal `settings.json` telemetry key; Factory's own documented example is `export OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"`. **Caveat (D26)**: an organisation-managed `telemetry` block overrides the machine variables, so a pinned organisation gets no local export. On this machine the org cache holds no `telemetry` block | The research digest; the live check exercises the local case | None for the plugin path; D26 governs how the native path is reported |
| A8 | The model's resolution when the hook payload carries none | **Observed**: `~/.factory/settings.json` has no top-level `model` key (its model lives in nested per-role blocks), the companion `<session-id>.settings.json` carries the session's `model`, and each transcript assistant record carries `message.modelId` | D24's chain; the live check records which link supplied the model | The model is absent on the row — never guessed |

## 13. Investigation record

Three evidence sources, each labelled by who produced it and what it can prove:

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
