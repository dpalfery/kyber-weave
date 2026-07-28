---
name: incident-triage
description: Use to triage an incoming production incident report, classify its severity, and route it to the right on-call team. Use when someone reports an outage, degradation, or production error. Do NOT use for routine service requests, feature questions, or non-production environments.
license: MIT
metadata:
  author: sre-team
  version: 2.0.1
---

# Incident Triage Runbook

## When to use
A production incident is reported and must be classified and routed. Routine requests do not belong here.

## Steps
1. Gather the symptom, blast radius, and first-observed time.
2. Classify severity using `references/severity-matrix.md`.
3. Page the owning on-call team for that service.
4. Open the incident channel and post the initial summary.

## Failure handling
- If the owning team cannot be determined, ALWAYS escalate to the incident commander rota.
- NEVER close an incident without a documented resolution.

## Example
A report arrives: "Checkout is returning 500s for ~30% of users." Classify against the severity matrix (high blast radius → Sev2), page the checkout on-call, and open the incident channel with the summary.
