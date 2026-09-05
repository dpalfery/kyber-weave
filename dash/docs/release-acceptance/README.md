# CodeBurn release acceptance

This directory turns the August 2026 dogfood audit into a repeatable release gate. It is an evidence system, not a claim that unit tests are equivalent to using the product.

## Release rule

A candidate is release-ready only when the exact distributable artifact is pinned by SHA and checksum, automated gates pass, both personas complete, every shipped surface has genuine click-through evidence, correctness reconciles independently, recovery cases pass, and the final machine state is known.

Never graduate evidence from another SHA, tag, package, or locally modified build. A skipped or inaccessible surface is `blocked`, not `pass`.

## Cadence

- Every mainline change: existing CI and focused regression tests.
- Before a minor release: automated runner, frozen golden-corpus parity, install/package identity, smoke click-through on every shipped surface, and all previously failed case IDs.
- Before a major release or material parser/cache/UI change: the full registry in `cases.csv`, two clean install cycles, three frozen cold trials, five warm trials, real-corpus sampling, adversarial recovery, accessibility, residue inventory, and independent review.
- After release: append the shipped artifact result. Do not rewrite old runs.

## Evidence ladder

1. Source inspection or unit test: proves code intent only.
2. Frozen fixture: proves deterministic output under controlled inputs.
3. Packed and installed artifact: proves the distributable being assessed.
4. Genuine interaction: proves a user-visible path was clicked and inspected.
5. Affected-machine proof: required for OS/account/provider-specific failures that cannot be reproduced locally.

A finding is confirmed only when the strongest evidence available is recorded and plausible measurement artifacts have been falsified. Code inspection can explain a result; it cannot substitute for installed UI behavior.

## Standard run

1. Freeze `upstream/main` or the release candidate and record ancestry.
2. Inventory installed CLI/Desktop/Menu Bar copies, processes, package managers, config/cache sizes, mounted images, and reversible backups before mutation.
3. Run the automated gate into a new external evidence directory:

   ```bash
   node scripts/release-acceptance/run.mjs --mode package --output /absolute/evidence/run-id
   ```

4. Test Persona A with isolated HOME/config/cache and current first-user documentation only.
5. Test Persona B against a frozen copy of the real provider corpus. Never call an empty corpus fast.
6. Reconcile one frozen golden corpus across packed CLI, installed CLI, TUI, Desktop, Menu Bar, and local Web with identical filters.
7. Click every registry row whose `method` is `computer-use` or `browser`; inspect resulting state and capture screenshots.
8. Run recovery cases on copies only. Never corrupt real histories, credentials, or caches.
9. Restore one current installation per intended surface, compatible config/history, rebuilt caches, and an inert checksummed backup. Record residue.
10. Validate `run.json` against `ledger.schema.json`, append one immutable line to `ledger/history.jsonl`, and obtain independent sign-off.

## Timing and correctness

Use a monotonic clock. Record first visible feedback, first useful output, complete result, warm navigation, CPU/RSS where practical, and whether totals are identical. Controlled performance distributions use three cold trials and five warm trials on the same frozen corpus.

The inherited observational flags are:

- no visible UI feedback within 250 ms;
- warm navigation above 1 second;
- a pause without stage/progress copy;
- a documented product SLA violation;
- a regression against the immediately previous comparable ledger run.

These are triage signals unless a product SLA says otherwise. Do not relabel them as contractual thresholds.

Correctness requires independently calculated calls, input/output/cache/reasoning tokens, priced/unpriced/estimated semantics, cost to sub-cent precision, time boundaries, deduplication/resume handling, and delegated-agent attribution. Investigate every mismatch; do not average it away.

## Privacy and safety

- Telemetry and remote sync stay off unless a named test explicitly authorizes a local receiver or dry run.
- Never print secrets or enumerate environment variables. Preserve Keychain entries.
- Provider histories and real caches are read-only; destructive recovery uses copies.
- Screenshots and logs must be reviewed for project names, paths, spend, account data, and credentials before sharing.
- Release signing/notarization, unexpected permissions, credentials, CAPTCHAs, and irreversible deletion are human gates.

## Files

- `cases.csv`: stable test IDs, method, evidence, threshold, automation, and release-blocking policy.
- `AGENT-RUNBOOK.md`: copyable execution contract for an audit agent.
- `ledger.schema.json`: required run-record structure.
- `ledger/history.jsonl`: append-only record of what worked at each exact artifact.
- `templates/`: timing, click-through, accuracy, finding, and residue headers.
- `scripts/release-acceptance/run.mjs`: provenance, automated tests, package identity, checksums, logs, and dirty-worktree detection.

## Ownership

Agents can autonomously run deterministic tests, package locally, calculate fixture truth, profile, collect screenshots, test recovery on copies, and draft findings. Humans own product semantics, SLA choices, Apple credentials/signing, unexpected security prompts, real-account consent, and affected-machine validation. No agent should merge, publish a release, or transmit data merely because the automated ledger is green.
