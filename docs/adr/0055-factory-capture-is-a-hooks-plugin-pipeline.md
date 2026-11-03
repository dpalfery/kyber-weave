---
id: adr/0055-factory-capture-is-a-hooks-plugin-pipeline
title: Factory (Droid) Capture Is a Hooks-Plugin Pipeline
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-02
code-refs:
  - droidAdapter
  - installFactoryPlugin
  - mapHookEvent
  - FACTORY_CONTRACT_VERSION
---

# ADR 0055: Factory (Droid) Capture Is a Hooks-Plugin Pipeline

## Status

Accepted, 2026-11-02, with the Factory (Droid) capture plan. It amends
[ADR 0031](0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) in place for the
Factory writer.

Amended 2026-11-02: the plugin's `SubagentStop` event produces no span, because Factory's
documented payload for that event carries no child session id; and the native **span** claim is
withdrawn, so the native **metrics** path is what remains of the native path.

Amended 2026-11-02: the plugin's `SubagentStop` event is **not registered** at all. A
registration that produces no span buys nothing and costs a process per subagent stop. The
plugin's `hooks/hooks.json` is keyed **directly by event name**, the shape Factory's own
documentation shows for a plugin's hooks file; the earlier top-level `hooks` wrapper was the
shape Factory documents for the `hooks` key inside `settings.json`, and it worked only through
undocumented tolerance.

Amended 2026-11-02: the owned unit is a Kyber-owned **marketplace tree** that KyberDash
publishes and then installs through the harness's own plugin commands, not a plugin directory
KyberDash writes into the Factory root. The plugin that runs is the harness's **cached copy** of
that tree, and `${DROID_PLUGIN_ROOT}` expands to the cached path. The route this record first
recorded was measured to be false: a plugin directory written under `~/.factory/plugins/` was
never loaded by the harness.

## Context

Factory (`droid`) was already a Kyber-Squad target and KyberDash already ingested it — but only
as a **file** source, with no Settings row and no capture plugin. The file source loses the turn
and tool timing (it splits one session-level token total evenly across the session's assistant
calls), and the model it attributes is the session's model rather than the per-turn one.

Four collection paths were available, and the choice is expensive to revisit:

1. A status-only row: the harness is reported, nothing is captured.
2. Factory's own native OpenTelemetry export: Factory's documentation confirms the endpoint is
   configurable by machine environment variables, and `export OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"`
   is Factory's own example — the loopback address the KyberDash receiver listens on. No collector
   is needed, because Droid pushes OTLP/HTTP directly.
3. A merge into the owner's `~/.factory/hooks.json`.
4. A hooks-based plugin KyberDash owns, published as a marketplace and installed by the
   harness's own plugin commands.

The native export is real, but it is tokenless by Factory's own documentation — token and cost
data "are not exported as customer OTEL metrics" — and its `genai` format carries no subagent
lineage: a subagent's trace is a separate trace root, so it cannot be joined to its parent's. The
Factory transcript's own `parentId` does carry that linkage; the plugin's `SubagentStop` event
does not — its documented payload names `task_name`, `task_result`, `task_error` and
`stop_hook_active`, and no child session id — so that event produces no span and is not
registered.

## Decision

KyberDash captures Factory through one hooks-plugin pipeline:

Factory's hook events, then the Kyber-owned `kyberdash-factory` plugin, then OTLP/HTTP JSON spans
at contract `1`, then the `droid` canon adapter, then the canonical store, then the shared
projection, then `/api/kyber/*`.

- **The owned unit is a Kyber-owned marketplace tree.** KyberDash publishes the tree under
  its own state directory and asks the harness to install the plugin from it: `droid plugin
  marketplace add <the tree>`, then `droid plugin install kyberdash-factory@kyberdash --scope
  user`. The tree carries the marketplace manifest and the plugin payload — the manifest's
  `plugins[]` entry names the plugin and points at the payload by a **relative** path — and the
  payload holds `.factory-plugin/plugin.json`, `hooks/hooks.json` and
  `hooks/kyberdash-factory.mjs`. The registered marketplace name is the **source directory's
  basename**, not the `name` field inside the manifest, so the tree's directory name is
  load-bearing and the plugin identity is `kyberdash-factory@kyberdash`. The tree is owned and
  removed as one unit with one receipt, `~/.kyberdash/factory-plugin-receipt.json`, which records
  the SHA-256 digest of every file KyberDash wrote; the harness's cache copy and its own plugin
  record are the harness's files, and KyberDash never writes or deletes them by hand — it asks
  the harness to. The teardown is `droid plugin uninstall kyberdash-factory@kyberdash --scope
  user` followed by `droid plugin marketplace remove kyberdash`, and KyberDash removes its own
  tree afterwards, because a local marketplace's source directory survives the harness's
  removal. Project scope is not used: the capture is per-user, and the owner may work in many
  projects.
- **The tree is upgraded, not left where it was.** A tree KyberDash wrote, whose bytes still
  match its receipt, and whose content is not the current bundle is recognised as an **upgrade**:
  the published tree and its receipt are rewritten, and the harness's **cached copy** — the copy
  that actually runs — is refreshed with `droid plugin update kyberdash-factory@kyberdash
  --scope user`. Two harness behaviours make that the exact sequence. `droid plugin install` is
  **refused** for a plugin that is already installed ("already installed at user scope"), so it
  is never run against one; and `droid plugin update` **fails without the marketplace
  registration** ("Marketplace \"kyberdash\" not found"), so the registration is restored first
  when the harness holds the plugin but not the marketplace. A tree whose bytes the owner
  changed stays the owner's and is never overwritten, and a tree no receipt proves is not
  KyberDash's at all.
- **The wire contract is Factory's own.** Version `1`, the `factory.*` attribute namespace, its
  own span names (`factory.session`, `factory.turn`, `factory.tool`),
  and deterministic id derivations. The OpenCode contract-3 keys are not reused: the OpenCode
  adapter claims every span carrying them, so an `opencode.*` key here would attribute every
  Factory row to OpenCode. The `SubagentStop` event produces no span: Factory's documented
  payload for it names `task_name`, `task_result`, `task_error` and `stop_hook_active`, and no
  child session id, while the common `session_id` is the session the hook fires in — the
  **parent's**, for a subagent's stop. A span there would name the parent as the child, so none
  is written, and a child session's own turns arrive as the child's own plugin spans. The event
  is **not registered** either: a registration that produces no span buys nothing and costs a
  process per subagent stop. The plugin's `hooks/hooks.json` is keyed **directly by event
  name**, the shape Factory's own documentation shows for a plugin's hooks file; the earlier
  top-level `hooks` wrapper was the shape Factory documents for the `hooks` key inside
  `settings.json`, and it worked only through undocumented tolerance.
- **One canonical harness id: `droid`.** It is the id the Factory file source already stores, so
  the dashboard shows Factory once and the plugin's turns sit beside the file source's counters.
  One adapter, named `droid`, claims the plugin envelope (`factory.contract_version = 1`) and
  nothing else.
- **The native span claim is withdrawn; the plugin envelope is the only Factory span claimed.**
  No adapter claims a native span. The `droid` adapter used to recognise the native `genai`
  fingerprint by attribute — never by the span's source name — and that fingerprint
  (`gen_ai.agent.name` = `droid`) sits on the trace **root alone**. The registry normalises a
  group's score by the group's span count, so a trace of one root and `k` children scores
  `1/(1+k)` against the registry's 0.6 threshold, and a real turn's trace scores at most 0.5.
  The `legacy` format's content-shaped spans carry no attribute fingerprint at all — only span
  names, which no adapter may claim. A native span therefore leaves the corpus for quarantine as
  `unclaimed`, its decoded packet kept as the audit evidence. Measured against real captured
  spans: accepted 0, quarantined 2.
- **Native OpenTelemetry is an authorized second path, never the primary one — and only its
  metrics are collected.** The receiver serves `POST /v1/metrics` beside `/v1/traces` and
  `/v1/logs`, and the decoded metric points are persisted in the canon store's own
  `metric_point` table. The native path carries no token or cost counter
  and no subagent lineage, so it complements the plugin rows and the file source's counters; it
  replaces neither. Factory's endpoint is set by machine environment variables
  (`OTEL_TELEMETRY_ENDPOINT` and its siblings), and an organisation-managed `telemetry` block
  overrides them, so a deployment under such an override exports nothing locally and the plugin
  path is unaffected.
- **Content consent is off by default, and installing the capture never turns it on.** The
  consent file is `~/.kyberdash/factory-plugin.json` (`{ "version": 1, "content": <boolean> }`),
  created with `content: false` only when absent. The Settings switch installs the capture and
  leaves the owner's decision exactly as it was; only the consent verb
  (`kyberdash kyber capture plugin consent --harness factory --on|--off`) or the owner editing
  the file records the decision to capture the prompt text. `KYBERDASH_FACTORY_CONTEXT=1|0`
  overrides the file. This differs from the OpenCode writer, whose Settings switch does set
  consent, and the difference is deliberate: for Factory the switch is "install the capture", not
  "start recording my prompts".
- **KyberDash never writes the owner's Factory files; the harness does, on request.**
  `~/.factory/settings.json`, `~/.factory/hooks.json`, `.factory/settings.json` and
  `.factory/hooks.json` are never created, read, merged, rewritten or restored **by KyberDash** —
  not by enable, not by disable, not by Squad, not by `kyber-weave update`. The Factory writer
  owns the marketplace tree and a consent file, declares no config key, and receipted no key.
  What changes the owner's settings file is the **delegated install**: the harness's own commands
  write the `extraKnownMarketplaces` and `enabledPlugins` entries into it, and the harness's
  uninstall removes the `enabledPlugins` entry. That delegated write is safe because the writer
  owns a whole separate file and never a shared settings file — KyberDash holds no merge of the
  owner's settings and no partial edit to restore. The restore criterion is therefore
  **semantic**, not byte-identity: no entry the install added remains, no entry the owner had is
  lost, and every other key is unchanged in content. Because the harness's uninstall deletes a
  pre-existing `enabledPlugins: false` rather than restoring it, the uninstaller handles that
  case itself rather than assume the harness's commands satisfy the criterion on their own. Any
  residue the harness's uninstall leaves behind is reported with the exact command that removes
  it, and is never patched by hand.
- **Install modes are strict and preserve.** `capture enable` refuses an owned tree it cannot
  prove — a foreign or owner-edited one, or a name the harness already holds under another owner
  — with exit 1 and writes nothing; Squad and `kyber-weave update` keep such a tree with a
  warning and exit 0. Removal requires every recorded digest to match, so an edited file is left
  in place.

## Alternatives considered

1. **A status-only row.** Rejected. It reports a harness KyberDash can already read, and leaves
   the turn and tool timing unmeasured for the same reason as before.
2. **Native OpenTelemetry as the primary path.** Rejected. It is tokenless by Factory's own
   documentation, so it can never carry a session's token totals, and its `genai` format carries
   no subagent lineage, so a subagent's turn cannot be joined to its parent. It is authorized as
   a complementary path for its metrics instead, and no native span is claimed.
3. **Merging into the owner's `~/.factory/hooks.json`.** Rejected. It edits a shared harness
   settings file, which is exactly what ADR 0026 forbids and what ADR 0031's file-plugin
   amendments avoid by owning a separate tree.
4. **File-only ingest.** Rejected as an end state. It stays the token source, because it is the
   only source of Factory's counters, but it cannot observe a turn boundary.
5. **Reusing the OpenCode contract-3 keys.** Rejected. The OpenCode adapter claims any span
   carrying them, so the reuse would mis-attribute every Factory row to OpenCode.
6. **A plugin directory written into the Factory root.** Rejected, and disproven by measurement.
   `~/.factory/plugins/kyberdash-factory/` looked like the OpenCode writer's shape, but Factory
   never loaded it: after that install `droid plugin list` reported "No plugins installed" and a
   real Droid turn produced no plugin spans at all. Factory loads a plugin that came from a
   marketplace, and the marketplace route is also the harness's own supported lifecycle, so it is
   the route most likely to survive a Factory upgrade.

## Consequences

- The plugin is a one-shot process per hook event, not a long-lived one, so it parks an
  undelivered event in a bounded file-backed spool under KyberDash's own state directory and
  drains it on the next run. It never fails the host: every error is swallowed and the hook
  exits 0.
- Factory's token totals come from the file source, joined to the plugin's rows on **exact
  session-id equality** — no fuzzy join, no time window, no counter-equality join. A plugin
  row's own counters are never measured and never zero: it declares them `not_measurable` with
  a reason that names the file source and the session-id pairing as where the number comes
  from. No token value is ever fabricated and none is reported as zero.
- The Factory file source's own even split of the session's token total across its assistant
  calls stays as it is. It is a known lossy approximation, and the per-call data that would
  remove it is tracked separately.
- Where an organisation pins a telemetry endpoint, the native path is silently absent. KyberDash
  reports what it observes, and the plugin path is unaffected.
- The live proof of capture is an owner-run check, like the OpenCode live gate: a live Droid
  turn calls a model and cannot be a CI gate. The recorded replay through the production
  pipeline proves what the dashboard renders from that replay, not that a live turn is captured.
- Any future Factory collection source is a new decision, and it must state how it avoids double
  counting against the plugin and the file source.

## Related

- [ADR 0031: The KyberDash Capture Command Owns Harness Telemetry Keys](0031-kyberdash-capture-command-owns-harness-telemetry-keys.md)
  — the record this one amends for the Factory writer
- [ADR 0026: Kyber Utilities Deploys Owned Files and Never Owns Harness Settings](0026-kyber-utilities-owned-files-not-settings.md)
  — the bar on writing a shared harness settings file
- [ADR 0048: OpenCode Capture Is a V2-Only Plugin Pipeline](0048-opencode-capture-is-a-v2-only-plugin-pipeline.md)
  — the precedent this pipeline follows
- [KyberDash architecture](../dash/architecture.md) — the pipeline in detail
