---
id: todo/kyberdash-windows-code-signing
title: The Windows tray installer is not Authenticode-signed
doc-type: todo
component: Distribution
status: draft
owner: dpalfery
last-reviewed: 2026-09-24
---

# The Windows tray installer is not Authenticode-signed

Deferred by the KyberDash context-surfaces specification (Requirement 13.4). No Windows
code-signing certificate is configured, so `kyberdash-tray-win-x64-setup.exe` ships unsigned
and the release notes carry the SmartScreen warning `release.yml` writes (Requirement 12.4).

## What is known

- Requirement 12.4 already covers both states: signed when a certificate is configured,
  the SmartScreen note when it is not. Only the certificate and the signing step are missing.
- An EV certificate clears SmartScreen reputation immediately; a standard OV certificate
  builds reputation over downloads.

## What needs deciding

- Which certificate (EV, OV, or Azure Trusted Signing) and where its secret lives — the
  `release` environment, like the macOS signing secrets.
- Whether the CLI binaries (`kyber-weave`, `kyber-weave-mcp`, `kyberdash`) are signed in the
  same step.
