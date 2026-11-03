---
id: todo/windows-node-gate-probe
title: Probe Node gate commands on Windows before lifting the os restriction
doc-type: todo
status: draft
component: ReviewCouncil
owner: dpalfery
last-reviewed: 2026-10-27
---

# Probe Node gate commands on Windows before lifting the `os` restriction

Every Node gate in the shipped typescript catalogue (`npm`, `npx`) is written with
`os: unix`, because on Windows those commands are `.cmd` shims and starting them from
`ProcessRunner` without a shell is not verified (see [Review council
architecture](../code-review/architecture.md#the-shipped-gate-catalogue)). A gate that
names `os: unix` is skipped on a Windows host (`KW-REVIEW-026`), so today a Windows
checkout runs none of its Node gates.

Probe on Windows whether `npm`/`npx` start and behave through `ProcessRunner`'s
shell-free argv path. If they do, lift the restriction in the typescript family file;
if they do not — or need a shell, a `cmd /c` wrapper, or per-host path handling — keep
`os: unix` and record the outcome here instead.

This todo is complete when the typescript family either ships Node gates without the
`os: unix` restriction (verified green on Windows) or documents here why the
restriction stays.
