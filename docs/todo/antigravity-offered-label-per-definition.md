---
id: todo/antigravity-offered-label-per-definition
title: Label each MCP catalogue definition as offered, and test a mixed block's members
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# Label each MCP catalogue definition as offered, and test a mixed block's members

A tool-definitions block that mixes catalogue parts (`resident: false`) with resident parts carries one
label for the whole block, so a resident definition in that block is labelled `offered (MCP catalogue)`
as well (see the [telemetry inventory](../dash/telemetry-inventory.md#antigravity-tool-definitions-and-system-prompt)).

Two changes. Derive the offered label per definition rather than per block. Add a test for the block
member case, which has no dedicated test today. The block-level label is a display choice, so it can
stay for blocks that are wholly catalogue.
