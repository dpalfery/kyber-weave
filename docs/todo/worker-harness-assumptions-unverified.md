---
id: todo/worker-harness-assumptions-unverified
title: Confirm the three harness behaviours only a live session can settle
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-08
---

# Confirm the three harness behaviours only a live session can settle

The harness check that closed with the worker-text trim confirmed every row it could from this tree and
marked the rest `[NOT LIVE-VERIFIED]` with its fallback. Three of those rows decide whether the worker
baseline and the docs server behave as designed, and no amount of reading this repository can settle
them: they are statements about what an installed harness does.

The rows, their markers and their fallbacks are in [the capability
matrix](../kyber-squad/requirements.md#harness-behaviour-the-product-relies-on). What is unpinned:

1. **The worker profile's shell grant.** The `worker` capability profile is `process.execute: allow`,
   and each renderer maps that onto the harness's own shell tool where the target enforces a tool list.
   Nothing establishes that the shell starts, finds the binary and returns inside a worker's budget, so
   `kyber-weave doctor --ensure` as a worker's first command rests on the render alone. The fallback is
   `STATUS: TOOL_FAILURE` under `stop-on-tool-failure`, which stops the run before any worker is
   dispatched rather than after.
2. **Renderer root distinctness beyond the render.** The thirteen roots are distinct as rendered, and
   that half is pinned. Which directories a harness reads *beyond* its own root is vendor behaviour:
   Antigravity's `.agents/skills` is also read by Pi, ZCode and Command Code, so a co-deployment is the
   one case where a distinct root may still be a shared one. The per-harness `{{harness}}` token is
   validated at run start, so a harness reading a co-deployed tree is at worst a wrong label rather
   than wrong instructions.
3. **An MCP tool-call timeout above the refresh bound.** The docs server bounds a CodeGraph refresh at
   30 seconds and refreshes at most once every 60. No harness's MCP client timeout is known from this
   tree, so whether a tool call can outlast a refresh is unestablished. Lowering the bound is the
   fallback and needs no harness fact.

## How to close it

One worker dispatch and one docs tool call on each harness that matters. For the shell: a worker runs
`kyber-weave doctor --ensure .` and returns its output and exit code. For the roots: install
Antigravity with Pi, ZCode and Command Code side by side in one repository and dispatch one worker on
each, recording which agent body each harness loaded. For the timeout: call a `kw-docs` tool while a
CodeGraph sync is running and record whether the call completes.

Record each result in the matrix in place — the table is the deliverable, and a row that stays
`[NOT LIVE-VERIFIED]` is the correct state until one of these runs has been made. Nothing here is a
code change until a result contradicts a row.
