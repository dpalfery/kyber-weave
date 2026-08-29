# Release-acceptance agent runbook

Use this contract when assigning a CodeBurn candidate to an audit agent.

## Objective

Certify or reject the exact candidate that will ship. Prove installation, time-to-value, correctness, responsiveness, recovery, privacy, and final machine hygiene across CLI, TUI, Desktop, Menu Bar, and local Web.

## Non-negotiable controls

- Fetch the authoritative remote and pin SHA, ancestry, versions, artifact checksums, toolchain, macOS, and hardware before testing.
- Start from a clean worktree. Never silently restore build-generated changes; record and fail the reproducibility gate.
- Use only the packed/installable artifacts produced by intended entrypoints.
- Use genuine Computer Use for Desktop and Menu Bar and genuine browser interaction for Web. Screenshots or CLI output alone do not prove a click flow.
- Test Persona A in isolated HOME/config/cache by following current documentation literally.
- Test Persona B on a frozen copy of the real corpus. Keep real histories and credentials read-only.
- Run the registry in `cases.csv`; additions are welcome, omissions are not.
- Record monotonic raw timings and preserve JSON/CSV/logs/screenshots.
- Independently calculate the golden corpus. Identical product surfaces are necessary but not sufficient if they agree on the same wrong number.
- Treat all `requiresApproval` or other consent ambiguity conservatively. Do not infer permission.
- Do not send telemetry/sync, publish, merge, open issues, or alter external systems unless separately authorized.

## Required sequence

1. Candidate resolution and pre-mutation inventory.
2. Reversible backup/quarantine and clean install.
3. Automated runner in `package` mode.
4. Two uninstall/reinstall cycles with identity, Gatekeeper, process-path, and restart checks.
5. Persona A onboarding, empty state, help/errors, every visible destination/control, keyboard, resize, themes, accessibility.
6. Golden fixture: three cold and five warm runs per relevant execution path.
7. Persona B heavy-corpus first useful/full timings, navigation, peak resources, and at least 30 raw sessions over at least five available providers.
8. Identical-filter reconciliation across all surfaces.
9. Offline, locked/unreadable provider, corrupt/interrupted cache, concurrent runs, SIGINT/restart, sleep/wake, and login restart.
10. Final single-install inventory, restored compatible state, retained inert backup, and residue manifest.
11. Findings with severity, exact prerequisites/repro, expected/actual/frequency, redacted evidence, privacy impact, owner, size, and autonomous/HITL classification.
12. Append the ledger only after independent review.

## Verdicts

- `ready`: every release-blocking case passed on the exact artifact; no P0/P1 remains; all shipped surfaces have runtime proof.
- `conditional`: no known correctness/privacy loss, but a named human/affected-machine/release-identity gate remains.
- `not-ready`: any P0/P1, incorrect or silently empty accounting, unusable ordinary cold path, broken installation/trust, skipped shipped surface, or uncontained residue.

Do not soften a verdict because a fix seems easy. Do not inflate an edge case without proving an ordinary user can reach it.
