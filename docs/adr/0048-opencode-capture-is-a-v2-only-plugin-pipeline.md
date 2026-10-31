---
id: adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline
title: OpenCode Capture Is a V2-Only Plugin Pipeline
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-31
code-refs:
  - createSetup
  - isSupportedOpencodeEnvelope
  - OPENCODE_KEYS
---

# ADR 0048: OpenCode Capture Is a V2-Only Plugin Pipeline

## Status

Accepted, 2026-10-30, with the OpenCode V2 capture plan. It amends
[ADR 0031](0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) in place for the OpenCode writer.

## Context

Three facts were measured on 2026-10-30 on the owner's Mac, with OpenCode CLI 2.2.1, OpenCode Desktop 2.2.2
and KyberDash 0.1.8-rc.12:

- OpenCode 2.x gives a V2 plugin every field the owner wants through its bus events and hooks: usage, cost,
  turn boundaries, prompt text, system prompt, tool catalogue and skills. The KyberDash plugin then kept almost none of them.
- OpenCode's native OpenTelemetry export carried no usage: 0 of 55,261 spans. OpenCode ignores the
  `experimental.openTelemetry` key that KyberDash wrote into `opencode.jsonc`, and it warns about that key on every start.
- The V1 table import read empty `session`, `message` and `part` tables. The V2 `session_v2` table is not a durable
  record: sessions disappear from it, and the cause is not yet known.

OpenCode 1.x and 2.x load plugins through different shapes. A 1.19.4 install failed to load the V2 `{ id, setup }`
export, so one plugin cannot serve both without two code paths. The owner requires OpenCode V2 only.

## Decision

KyberDash captures OpenCode through one V2-only pipeline:

OpenCode 2.x hooks and bus events, then the `kyberdash-opencode` plugin, then OTLP/HTTP JSON spans at contract `3`,
then the OpenCode adapter, then the canonical store, then the shared projection, then `/api/kyber/*`, then the dashboard.

The plugin is the single source of OpenCode data:

- The OpenCode adapter claims only contract-3 envelope spans.
- The native OpenCode OpenTelemetry mappings (AI SDK, OpenInference and the legacy `ai.*` names) are removed.
  Native spans fall to the unclaimed quarantine.
- The V1 table and file import is removed. No V2 database importer is added.
- OpenCode 1.x is not supported.
- The OpenCode capture writer declares no config key. Enable installs the plugin file and sets content consent on.
  Disable sets consent off and removes the plugin file. Both also run a one-time restore of the
  `experimental.openTelemetry` value that earlier KyberDash builds wrote.
- Cost is OpenCode's reported figure, carried verbatim. No published-rate fallback applies to OpenCode.

## Alternatives considered

1. **Keep native OpenCode OpenTelemetry as the primary source, with the plugin as a complement.** Rejected. Native
   spans carried no usage at 2.2.x, and the key that would enable them is ignored. Keeping the mappings means
   maintaining code for a source that delivers nothing.
2. **Import OpenCode's V2 session database.** Rejected for now. The rows are not durable, and a second source
   for the same sessions would double count against the plugin. A future importer needs its own decision that
   resolves the double counting.
3. **Support V1 and V2 side by side.** Rejected. The two loader shapes differ, V1 was never verified on an
   install, and two paths double the test and fixture surface. The owner asked for V2 only.
4. **Keep writing `experimental.openTelemetry` into `opencode.jsonc`.** Rejected. OpenCode 2.2.x ignores the key,
   so writing it leaves a config entry that does nothing.
5. **Price OpenCode usage from KyberDash's published rate table.** Rejected. The table does not price OpenCode's
   Zen or Go models, and OpenCode's reported cost already reflects the plan the owner is on, including zero-cost
   subscription use. Only the reported figure matches OpenCode's own totals.

## Consequences

- Only OpenCode 2.x is captured. Versions below 2.2.1 are unverified, and OpenCode 1.x users get no capture.
- Native OpenCode spans from other exporters are never attributed to OpenCode. They stay in the unclaimed quarantine.
- Nothing before the plugin is recovered from OpenCode's database. Rows from the earlier pipeline stay until the
  owner removes them with `dash clean --harness opencode --no-reingest --yes`. KyberDash never removes them itself.
- OpenCode cost figures are OpenCode's own. They match OpenCode's totals, not KyberDash's published-rate prices
  for other harnesses.
- The plugin depends on OpenCode 2.x bus and hook surfaces. An OpenCode change to them breaks capture until the
  plugin changes. The wire contract version invalidates stored rows when the plugin's shape changes.
- Until the OpenCode live gate records a passing run on the owner's machine, live capture is unverified. Replay
  tests prove the pipeline on recorded events only.
- Any future OpenCode source, such as a V2 importer or a new event feed, is a new decision. It must state how it
  avoids double counting against the plugin.
- The shared projection now sums session duration over request roots whenever records nest. That rule applies to
  every nested harness, not only OpenCode.
