---
id: archive/todo/opencode-1x-runtime-support
title: OpenCode 1.x runtime support for the KyberDash capture plugin is unverified
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-31
status: superseded
documentation-impact: no-design-change
---

# OpenCode 1.x runtime support for the KyberDash capture plugin is unverified

Superseded: OpenCode capture is V2-only, and OpenCode 1.x support is removed rather than added
([ADR 0048](../../adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline.md)). The shipped behaviour is in
[the architecture](../../dash/architecture.md#opencode-v2-capture-plugin-pipeline-kyberdash-opencode).

The KyberDash OpenCode capture plugin (`kyberdash-opencode.js`) is shaped for the OpenCode
2.1.4 loader: a single default export `{ id: 'kyberdash-opencode', setup }`, with one
`context` hook registered in `setup`. That shape is the only one verified on the owner's
machine. The 1.x loader accepts a different shape (`{ id, server }` with 1.x hooks such as
`experimental.chat.system.transform` and `tool.definition`), but no OpenCode 1.x runtime is
installed on the owner's machine, so the 1.x loader rule cannot be checked.

Until a real 1.x install is tested, OpenCode 1.x users get no capture from this plugin. A
combined default object (`{ id, setup, server }`) was rejected because the 2.1.4 schema may
reject an extra key. Support needs a 1.x install to pin the loader rule and the hook payload
first, then a shape that loads on both runtimes or a per-version entry.

Acceptance for closing this todo: pin the 1.x loader rule from a real 1.x install in the
fixture that records the 2.1.4 pin, then add a load test per shape and confirm the plugin
loads on both 1.x and 2.1.4.

The decision record for the 2.1.4 shape is in the archived plan
[2026-10-25-opencode-capture-deploy-kyberdash](../plans/2026-10-25-opencode-capture-deploy-kyberdash.md)
(decisions D1 and D2).

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| OC1X-1 | Does the KyberDash OpenCode capture plugin support OpenCode 1.x? | No. OpenCode capture supports OpenCode 2.x only. Support for 1.x is removed rather than added, so there is no 1.x loader shape to pin. | The plugin is shaped for the 2.x loader (`{ id, setup }`); no 1.x runtime was ever verified, and carrying both shapes risked the 2.x schema rejecting an extra key. Live evidence in the plan: OpenCode 1.19.4 failed to load the V2 plugin. | Plan [2026-10-30-opencode-v2-capture](../plans/2026-10-30-opencode-v2-capture.md) decision D1, approved in AP1 on 2026-10-30. |

## Closeout mapping

Links are written from this archive entry's location (`docs/archive/todo/`).

| ID | Destination |
|---|---|
| OC1X-1 | [ADR 0048](../../adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline.md) records the V2-only decision; [dash/architecture.md](../../dash/architecture.md#opencode-v2-capture-plugin-pipeline-kyberdash-opencode) describes the shipped plugin pipeline. |

## Design harvest

Documentation waiver: no new design survives from this todo. The question it raised is closed by
removing 1.x support, which [ADR 0048](../../adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline.md) and the architecture section above already state.
