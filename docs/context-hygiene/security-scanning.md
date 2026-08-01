---
id: context-hygiene/security-scanning
title: Instruction-surface scanning
doc-type: reference
status: current
component: ContextHygiene
owner: dpalfery
last-reviewed: 2026-08-01
code-refs:
  - InstructionSurfaceScanner
---

# Instruction-surface scanning

Skills and agent definitions have one property in common that ordinary configuration does
not: **their text becomes instructions**. Anything loaded into a model's context is
executed in the loosest sense of the word, which makes both artifact classes a trust
surface and makes them scannable by one engine.

`InstructionSurfaceScanner` is that engine. `skill scan` and `agent scan` are two entry
points into it, emitting the same findings under different rule-id prefixes —
`KW-SKILL-SEC-*` and `KW-AGENT-SEC-*` — so a host can gate the two artifact classes at
different severities.

## What it looks for

### Prompt injection and safety bypass — `-001`…`-008`

| Pattern | Skill | Agent |
|---|---|---|
| Ignore-previous-instructions | `KW-SKILL-SEC-001` | `KW-AGENT-SEC-001` |
| Disregard-guidelines | `KW-SKILL-SEC-002` | `KW-AGENT-SEC-002` |
| System-prompt override | `KW-SKILL-SEC-003` | `KW-AGENT-SEC-003` |
| Persona hijack | `KW-SKILL-SEC-004` | `KW-AGENT-SEC-004` |
| Exfiltration phrasing | `KW-SKILL-SEC-005` | `KW-AGENT-SEC-005` |
| Sandbox bypass | `KW-SKILL-SEC-008` | `KW-AGENT-SEC-008` |

### Concealment — `-006`, `-007`

HTML comments and base64 blobs. Both hide text from a human reviewer that a model still
reads, which is the defining shape of an instruction-surface attack: the review and the
execution see different documents.

### Risky scripts — `KW-SKILL-SEC-010`…`-013`

`curl | sh`, `wget | sh`, `eval` of base64, and destructive commands. Skills may carry
executable resources; these are the shapes worth stopping before a skill ships.

### Hardcoded secrets — `-020`…`-025`

AWS keys, GitHub tokens, private keys, Slack tokens, OpenAI keys, and password
assignments. A secret in a skill is a secret in every context that skill is loaded into.

### Provenance — `-030`…`-032`

Missing author, version, or license. Not vulnerabilities; they are the metadata that makes
an artifact auditable at all, and their absence is worth a finding.

## Necessary, not sufficient

Pattern matching finds known shapes. It does not find a plausibly-worded instruction that
happens to be malicious, and it never will. Pair scanning with human review of the diff,
and treat a clean scan as the floor rather than the verdict.

`allowed-tools` in the Agent Skills spec is **experimental and not a security control**.
Do not gate on it.

## Related

- [Skill governance](skills.md) — `skill scan` in context
- [Agent harness governance](agents.md) — `agent scan` in context
- [CI Pipelines](../ci-pipelines/architecture.md) — severity gating and SARIF output
