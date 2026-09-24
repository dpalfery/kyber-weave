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
- No certificate clears SmartScreen on its own: EV-, OV- and Azure Artifact Signing-signed
  files all show a warning until the publisher builds reputation over downloads. EV lost its
  instant bypass in 2024.

## What needs deciding

- Which certificate (Azure Artifact Signing, formerly Trusted Signing, or an OV
  certificate) and where its secret lives — the
  `release` environment, like the macOS signing secrets.
- Whether the CLI binaries (`kyber-weave`, `kyber-weave-mcp`, `kyberdash`) are signed in the
  same step.
