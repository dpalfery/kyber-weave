---
id: todo/antigravity-family-scope-resolve-harness
title: Antigravity family scope is shadowed by the descriptor id
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# Antigravity family scope is shadowed by the descriptor id

`resolveHarnessScope` (`dash/src/refresh/registry.ts`, around lines 661 to 690) checks
`descriptorFor(id)` before it expands a family. As a result `--harness antigravity` resolves only the
`~/.gemini/antigravity` descriptor, and never `antigravity-cli` or `antigravity-ide`. The claude and
copilot families do not have this problem.

The consequence is that a single family import misses the CLI conversations. The runbook tells the
operator to name each descriptor explicitly as a workaround. Fix the resolution so that a family id
expands to its members when it has no descriptor of its own, and keep an explicit descriptor id
resolving to exactly that descriptor. Add a test that pins both cases.
