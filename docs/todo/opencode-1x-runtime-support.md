---
id: todo/opencode-1x-runtime-support
title: OpenCode 1.x runtime support for the KyberDash capture plugin is unverified
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-26
---

# OpenCode 1.x runtime support for the KyberDash capture plugin is unverified

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
[2026-10-25-opencode-capture-deploy-kyberdash](../archive/plans/2026-10-25-opencode-capture-deploy-kyberdash.md)
(decisions D1 and D2). The shipped behaviour is described in
[dash/architecture.md](../dash/architecture.md), section "OpenCode plugin span/turn envelope".
