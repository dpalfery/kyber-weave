---
id: plans/2026-09-29-release-checksums-unsigned-windows
title: Complete release checksums and document unsigned Windows binaries
doc-type: plan
status: Archived
component: Distribution
owner: dpalfery
last-reviewed: 2026-09-29
archive-date: 2026-09-29
archive-outcome: Complete - Tasks T1–T8 approved by council (2026-09-29). Post-council tasks T9 and T10 added for technical debt and docs accuracy. T9 passed audit pass 2; T10 escalated one finding (README.md:221) remediated by new task T11 (passed pass 1). Full gate suite re-run verified all work; no second council pass. Durable content in ADR 0026, docs/distribution.md, docs/install.md, and docs/dash/runbook.md.
development-mode: standard
keywords:
  - SHA256SUMS
  - checksum manifest
  - Authenticode
  - SmartScreen
  - code signing deferred
---

# Complete release checksums and document unsigned Windows binaries

## Status

Archived, 2026-09-29. The user approved the original plan (T1–T5) in chat on 2026-09-29
("approve and execute"). Initial audit cycle: T1, T3, and T5 passed; T2 used all three
audit passes and failed pass 3 with two findings, which escalated. The plan was revised
to add remediation task T6 and optional tasks T7 and T8.

The conductor decided Q5–Q8 under a user delegation (2026-09-29) and relayed approval to
execute the remediation (A10, A11). All remediation tasks (T6, T7, T8) passed audit.

The code-review council issued APPROVE over tasks T1–T8 (2026-09-29, minor notes).
Post-council, the user requested technical debt and documentation accuracy work: T9 (bash 3.2
robustness) passed audit pass 2; T10 (detective/preventive terminology) exhausted three
audit passes and escalated one finding (README.md:221 "terminal TUI"). Task T11 was created
to remediate that finding and passed audit pass 1. The full gate suite was re-run to
verify all work; no second council pass occurred. Every decision is resolved; see the
[decision record](#decision-record). The documentation checks have completed and are recorded
in the PR. Addresses [issue #132](https://github.com/dpalfery/kyber-weave/issues/132) by
deferring Authenticode signing and relying on SHA-256 checksums instead.

**Archived 2026-09-29**: Implementation complete. Tasks T1–T8 received council APPROVE. Post-council tasks T9 (bash 3.2 robustness, passed audit pass 2) and T10 (detective/preventive terminology, exhausted 3 audit passes with one escalation) were added per user request for technical debt and docs accuracy. Task T11 was created to remediate T10's escalated README.md:221 finding and passed audit pass 1. Full gate suite re-run verified all work. Durable content harvested into [ADR 0026](../../adr/0026-release-integrity-checksums-signing-deferred.md), [docs/distribution.md](../../distribution.md), [docs/install.md](../../install.md), and [docs/dash/runbook.md](../../dash/runbook.md). User-owned verification steps U1–U4 remain pending (see below).

## Problem and goal

The Windows tray installer `kyberdash-tray-win-x64-setup.exe` and the Windows `.exe` files
inside the `win-x64` archives are not Authenticode-signed. SmartScreen warns on first run,
and users have no documented way to check that a download is intact.

The goal is to publish a checksum manifest that is known to be complete and correct, to fail
the release when it is not, and to document the unsigned state accurately: the SmartScreen
**More info -> Run anyway** steps, and hash verification on Windows, macOS, and Linux.

## Development mode

`standard`. The user explicitly approved it on 2026-09-29, and the conductor relayed that
approval. Standard mode still
requires automated tests: the release workflow already has content tests in
`tests/KyberWeave.Tests/ReleaseTests.cs`, and they are the regression surface.

## Approved decisions

The user made these decisions. The conductor relayed them on 2026-09-29.

- **A1: Signing deferred.** Authenticode signing is not implemented. Azure signing (Trusted
  Signing / Key Vault) is ruled out on cost.
- **A2: No GPG.** The manifest is not GPG-signed, and no detached signature is published.
- **A3: SignPath Foundation is a possible future option only.** No work toward it here.
- **A4: Issue #132 is already commented and labelled `deferred`.** This plan does not
  change the issue's state.
- **A5: Scope covers the Windows tray installer and all release artifacts, including the
  Windows CLI binaries.** Q2 fixes the exact asset set.
- **A6: The docs explain SmartScreen and hash verification.** README, the release-notes
  text, `docs/distribution.md`, and the docs the documentation standard routes them to.
- **A7: `development-mode: standard` is approved.**
- **A8: The [verification contract](#verification-contract-standard-mode) is approved as
  written.** Two rows were added in the same save: T5 (the ADR, needed by Q4) and the
  matching T4 closeout row. They follow the same docs-gate form as T3.
- **A9: Plan approved for execution** ("approve and execute").
- **A10: Remediation decisions Q5–Q8.** These were decided by the conductor under a user
  delegation on 2026-09-29, and the conductor relayed them. The user delegated low-stakes
  questions to the conductor, so these are the conductor's choices, not per-question user
  answers. All four follow the architect's recommendations.
- **A11: Remediation approved for execution.** This covers T6, T7, T8, and the added
  verification-contract rows. The conductor relayed the user's delegation instruction as
  the approve-and-execute on 2026-09-29.

Q1–Q4 in the [decision record](#decision-record) were answered in the A9 approval. Q5–Q8
were decided under A10.

## Decision record

The user answered all four in chat on 2026-09-29, and the conductor relayed them.

| Id | Decision | Answer | Status |
|---|---|---|---|
| Q1 | Manifest file name | **(a)** Keep `SHA256SUMS.txt` only. There is no `SHA256SUMS` alias and no rename. | ANSWERED |
| Q2 | Manifest coverage | **(a)** An explicit list of 20 assets (finding 5). A missing or unexpected asset fails the release. | ANSWERED |
| Q3 | Verification of the workflow change | **(a)** The logic lives in `scripts/verify-release-checksums.sh`, which `ReleaseTests` exercises. The first RC after merge is the live test. There is no `dry_run` input. | ANSWERED |
| Q4 | Durable record of the signing deferral | **(a) plus (b)** A "Code signing status" section in `docs/distribution.md`, **and** an ADR, `docs/adr/0026-release-integrity-checksums-signing-deferred.md` (task T5). The ADR records: SHA-256 checksums as the integrity mechanism; no Authenticode or GPG for now; Azure signing ruled out on cost; SignPath Foundation as the revisit option; issue #132 as the tracker. There is no todo. | ANSWERED |
| Q5 | Accept remediation task T6 (T2's two audit findings) and its verification-contract row | **(a)** Accept T6 as written. Decided by conductor under user delegation, 2026-09-29. | ANSWERED |
| Q6 | T7: fix the script's `--list` argument guard and the release.yml header comment | **(a)** Accept T7, and T6 adds a test for the two-argument `--list` call. Decided by conductor under user delegation, 2026-09-29. | ANSWERED |
| Q7 | T8a: ADR 0026 accuracy nits (lines 18 and 48) | **(a)** Accept. Decided by conductor under user delegation, 2026-09-29. | ANSWERED |
| Q8 | T8b: replace the `release.yml:383–390` citation in `docs/distribution.md` | **(a)** Replace it with the job and step name. Decided by conductor under user delegation, 2026-09-29. | ANSWERED |

## Investigation findings

All findings were self-gathered by direct file reads against `ef9ec78`. CodeGraph and
`docs_explore` were not available in this harness, so only narrow lookups were made.

1. **A manifest already exists, as `SHA256SUMS.txt`.** The `release` job of
   `.github/workflows/release.yml` (step `Checksums`, about lines 716–721) runs
   `sha256sum * | tee SHA256SUMS.txt` over every downloaded artifact and uploads the result
   with `gh release create ... release-assets/*`. It never checks the result.
2. **Every client depends on the name `SHA256SUMS.txt`:**
   - `scripts/install.sh` (line 674)
   - `src/KyberWeave.Cli/Update/GitHubReleaseClient.cs:158` (`kyber-weave update`)
   - `GitHubSquadReleaseSource.cs:21` (`squad install/update`)
   - `dash/src/install/origin.ts:16` (`kyberdash menubar`, both macOS and Windows)
   - `npm/lib/download.js`
   - the local loop, `scripts/release-local.sh`

   Renaming the file would break every installed binary's update path and every historical
   tag. This drives Q1.
3. **Defect: the published manifest lists itself with a stale hash.**
   - `build-kyberdash` uploads a per-RID `dash/dist-bin/SHA256SUMS.txt` in each of its five
     artifacts (lines 423–441).
   - The release job downloads them with `merge-multiple: true`, so one of those files, the
     last written, lands in `release-assets/`.
   - `sha256sum *` then hashes that stale file, and `tee` overwrites it concurrently.

   The result is that the published `SHA256SUMS.txt` carries a line for
   `SHA256SUMS.txt` whose hash cannot match the published file. A user running
   `sha256sum -c SHA256SUMS.txt` sees a `FAILED` line. Clients that look up one name are
   unaffected. `scripts/release-local.sh` (lines 338–346) already avoids this by hashing to a
   temp file and then moving it into place.
4. **Nothing checks completeness.** A build job that uploads nothing fails through
   `if-no-files-found: error`. But no step asserts that the manifest names every expected
   asset, and nothing else, or that each hash verifies.
5. **The release asset set today has 20 files, plus the manifest:**

   | Group | Count | Assets |
   |---|---|---|
   | CLI | 5 | `kyber-weave-{linux-x64,linux-arm64,osx-x64,osx-arm64}.tar.gz`, `kyber-weave-win-x64.zip` |
   | MCP | 5 | `kyber-weave-mcp-<rid>` with the same set |
   | KyberDash | 5 | `kyberdash-{darwin-arm64,darwin-x64,linux-arm64,linux-x64}.tar.gz`, `kyberdash-win-x64.zip` |
   | Tray | 3 | `kyberdash-tray-darwin-arm64.zip`, `kyberdash-tray-darwin-x64.zip`, `kyberdash-tray-win-x64-setup.exe` |
   | Squad | 2 | `kyber-squad-<version>.zip`, `kyber-squad-plugin-<version>.zip` |

6. **The release notes are inaccurate about signing.** The inline notes (lines 764–768) say
   the tray installer "is not signed with an EV certificate". It is not Authenticode-signed
   at all, and the Windows CLI, MCP, and KyberDash `.exe` files are not mentioned.
   `ReleaseTests.ReleaseNotesCarryTheWindowsSmartScreenLine` pins only `SmartScreen` and
   `More info`. The notes have no separate template file: they are a heredoc inside the
   `release` job.
7. **Tests pin the current checksum step text.** `ReleaseTests.TrayAssetsJoinTheChecksumManifest`
   asserts the literal `sha256sum *` and the `release` job's `needs:` list. Changing the step
   means updating that test in the same change.
8. **Signing state per artifact, for the docs:**
   - The macOS tray is Developer ID-signed (team `J2UNNQ466J`), notarized, and asserted in
     CI, per the archived `docs/archive/todo/macos-developer-id-signing.md` Part A.
   - The macOS CLI, MCP, and `kyberdash` binaries are ad-hoc signed. `install.sh` strips
     quarantine from them.
   - The Windows tray installer and all Windows `.exe` files are unsigned.
   - Linux binaries are unsigned. That is normal for Linux.
9. **`kyberdash menubar` already verifies the tray.** `dash/src/install/menubar.ts`
   `download()` (lines 124–150) checks SHA-256 against `SHA256SUMS.txt` before handing the
   installer to `installWindows`. Manual downloads from the Releases page are the path that
   has no verification.
10. **Windows users have no installer script.** `docs/install.md` ("Supported platforms",
    lines 124–127) tells them to download the Windows archives manually. That is where hash
    verification and the SmartScreen and Mark-of-the-Web guidance belong.
11. **There is no dry-run mode.** A `workflow_dispatch` run on `main` builds everything.
    `build-tray` and `release` both wait on the `release` environment approval, and approval
    publishes a real (pre-)release. `workflow_dispatch` refuses any ref except `main`. So the
    changed workflow can only run end to end after merge. This drives Q3.
12. **No workflow linter is a declared gate.** `actionlint`, `yamllint`, and `shellcheck` are
    not in the `AGENTS.md` gates or in `review gates`. YamlDotNet is already a Core dependency,
    so a test can parse `release.yml` as YAML.
13. **Out-of-scope inaccuracies found during discovery.** Recorded, not fixed here:
    - `docs/distribution.md` line 199 says `--prerelease=auto`. The workflow passes
      `--prerelease`.
    - `README.md` line 209–210 lists an "Electron desktop app" and a "Windows tray
      companion". The tray is Tauri, for macOS and Windows.

## Scope

In:

- Release manifest correctness and completeness: all assets per Q2, name per Q1.
- A fail-closed check that runs before `gh release create`, and a post-publish asset check.
- Accurate release-notes text on signing and verification.
- User documentation of SmartScreen, Mark-of-the-Web, and hash verification on three OSs.
- Maintainer documentation of signing state and the manifest check.
- ADR 0026, recording the signing deferral (Q4).

Out:

- Authenticode, GPG, and SignPath signing (A1–A3).
- Developer ID signing of the macOS CLI binaries.
- Removing the per-RID `SHA256SUMS.txt` that `build-kyberdash` uploads. The release job
  neutralizes it instead.
- Running the release manifest check from `scripts/release-local.sh`. The local tree holds
  one RID, so the full expected list does not apply.
- Windows support in `install.sh`.
- The inaccuracies in finding 13.
- Any change to issue #132.

## Design

The manifest is built by a new script, `scripts/verify-release-checksums.sh <asset-dir>
<version>` (Q3-a), which `release.yml` calls. It runs in this order:

1. Deletes any `SHA256SUMS.txt` that came from the downloaded artifacts.
2. Computes the expected asset list from `<version>` (finding 5). A Bash array holds it, as
   the single place the asset set is declared.
3. Fails, naming the problem, when an expected file is absent or an unexpected file is
   present.
4. Hashes the expected files into a temp file with `sha256sum`, then moves it to
   `SHA256SUMS.txt`, mirroring `release-local.sh`.
5. Asserts that:
   - there is one line per expected asset;
   - every line matches `^[0-9a-f]{64}  <name>$`;
   - no line names `SHA256SUMS.txt`;
   - `sha256sum --check --strict SHA256SUMS.txt` passes.

Any failure exits non-zero with `::error::`, so `gh release create` never runs.

After `gh release create`, a step runs `gh release view "$TAG" --json assets`. It asserts
that `SHA256SUMS.txt` plus all 20 expected names are present. It then downloads the
published `SHA256SUMS.txt` and compares it byte for byte with the local copy.

Link targets shared between tasks, fixed here so T1, T3, and T5 can run in parallel:

- `docs/install.md#verifying-a-download`, which T3 creates and T1 and T5 link to.
- `docs/install.md#windows-unsigned-binaries-and-smartscreen`, which T3 creates and T1 and T5
  link to.
- `docs/distribution.md#code-signing-status`, which T3 creates and T5 links to.
- `docs/adr/0026-release-integrity-checksums-signing-deferred.md` (ADR id `adr/0026`), which
  T5 creates and T3 links to from the Code signing status section.

Each link resolves once all three tasks have landed. The docs gates in T3 and T5 are run
again together in T4.

## Tasks

### T1: Release workflow manifest and notes

**Required skill:** `github-devops`

**Files:**

- `.github/workflows/release.yml`: the `release` job's `Checksums` step, the
  `Create tag and GitHub Release` notes heredoc, a new post-publish verification step, and
  the header comment (lines 16–19).
- `scripts/verify-release-checksums.sh` (new).

**Objective:** Implement the design. Rewrite the SmartScreen note so it says:

- the Windows tray installer and the Windows `.exe` files are **not Authenticode-signed**;
- SmartScreen shows "Windows protected your PC", and **More info -> Run anyway** proceeds;
- how to verify with `SHA256SUMS.txt`, linking both anchors above;
- that the macOS tray is Developer ID-signed and notarized.

The note must no longer say "EV certificate".

**Acceptance:**

- The script exits 0 on a complete fixture. It exits non-zero, naming the cause, for each of:
  - a missing asset;
  - an extra asset;
  - a stale inbound `SHA256SUMS.txt` that would otherwise be listed;
  - a hash mismatch.
- The `release` job runs the script before `gh release create`.
- The post-publish step fails on a missing uploaded asset.
- Every `uses:` stays SHA-pinned.
- The file parses as YAML.
- `shellcheck` is clean where it is installed. It is not a declared gate; record whether it
  ran.

**Depends on:** none.

### T2: Release tests

**Required skill:** `test-dev`

**Files:** `tests/KyberWeave.Tests/ReleaseTests.cs`. Fixture helpers go in the same file or
the existing fixtures folder.

**Objective:**

- Update `TrayAssetsJoinTheChecksumManifest` so it asserts that the release job invokes
  `verify-release-checksums.sh` before `gh release create`, instead of the literal
  `sha256sum *`.
- Add script tests, one per acceptance case in T1, running the script against temp fixture
  directories. Skip on Windows, as the existing shell tests do.
- Add a test that the expected list in the script names all 20 assets, including the three
  tray assets that `origin.ts` requests.
- Add a test that `release.yml` parses with YamlDotNet.
- Add a test that the notes contain `not Authenticode-signed`, `More info`, `Run anyway`, and
  `SHA256SUMS.txt`, and do not contain `EV certificate`.

**Acceptance:** Every new test and every existing `ReleaseTests` test passes.

**Depends on:** T1. The tests consume the script's interface and the step names.

### T3: User and maintainer documentation

**Required skill:** `docs-dev`, following `app-docs-standard` and `kyber-weave-docs`.

**Files:**

- `docs/install.md`
- `docs/distribution.md`
- `docs/dash/runbook.md` (§6, "Deployed shape")
- `README.md` (install paragraph only)

**Objective:**

- **`docs/install.md`:** add a **Verifying a download** section:
  - Windows PowerShell: `Get-FileHash <file> -Algorithm SHA256`, compared with the matching
    line in `SHA256SUMS.txt`, plus a one-liner that compares them.
  - macOS: `shasum -a 256 --ignore-missing -c SHA256SUMS.txt`.
  - Linux: `sha256sum --ignore-missing -c SHA256SUMS.txt`.
- **`docs/install.md`:** add a **Windows: unsigned binaries and SmartScreen** section:
  - which files are unsigned;
  - verify the hash first;
  - "Windows protected your PC" -> **More info** -> **Run anyway**;
  - Mark-of-the-Web on `.exe` files extracted from a downloaded zip (Properties ->
    **Unblock**, or `Unblock-File`);
  - `kyberdash menubar` already verifies the tray installer's hash before it runs it.
- **`docs/distribution.md`:** add a **Code signing status** section:
  - a per-artifact table (finding 8);
  - signing deferred per #132, Azure ruled out on cost, no GPG, SignPath a future option,
    with a link to ADR 0026 as the decision of record;
  - integrity rests on HTTPS plus `SHA256SUMS.txt`;
  - what the release job's manifest check enforces, and that a new asset means editing the
    expected list.

  Also update the release-flow paragraph to match.
- **`docs/dash/runbook.md`:** add one sentence saying the Windows installer is unsigned, with
  a link to the install section.
- **`README.md`:** add one sentence and a link for Windows users.
- Bump `last-reviewed` on every doc touched.

**Acceptance:**

- Both anchors resolve.
- No document claims the Windows artifacts are signed.
- `docs validate . --merge-ready` and `docs drift .` show zero findings.

**Depends on:** none. It uses the link targets fixed in the design.

### T5: ADR 0026, release integrity through checksums with signing deferred

**Required skills:** `docs-dev`, following `architecture-decision-record` and
`kyber-weave-docs`.

**Files:**

- `docs/adr/0026-release-integrity-checksums-signing-deferred.md` (new)
- `docs/adr/README.md` (one new index row)

**Objective:** Write the ADR to the repository's existing conventions:

- frontmatter `id: adr/0026-release-integrity-checksums-signing-deferred`,
  `doc-type: adr`, `status: current`, `owner: dpalfery`, `last-reviewed: 2026-09-29`;
- title `# ADR 0026: ...`;
- sections Status (Accepted, 2026-09-29), Context, Decision, Alternatives considered, and
  Consequences.

It records:

- **The decision:** release integrity rests on HTTPS plus a complete, fail-closed
  `SHA256SUMS.txt` covering every Release asset (Q1-a, Q2-a).
- **Scope of the deferral:** Windows artifacts, both the tray installer and the `.exe`
  binaries, are not Authenticode-signed for now, and nothing is GPG-signed.
- **Rejected alternatives:**
  - Azure Trusted Signing or Key Vault signing, on cost;
  - GPG-signed manifests, which the user does not want;
  - an EV certificate, which this plan does not evaluate (record it only as not pursued).
- **Revisit trigger:** SignPath Foundation is the revisit option, and
  [issue #132](https://github.com/dpalfery/kyber-weave/issues/132) is the tracker.
- **Consequences:** SmartScreen warnings until reputation builds; the documented manual hash
  check; and that a new asset requires editing the expected list.

It notes that macOS tray Developer ID signing is unaffected. It links `docs/distribution.md`
and `docs/install.md` through the fixed link targets.

**Acceptance:**

- The ADR number 0026 is unused (0025 is the highest today, and 0006 is archived, not free).
- The index row matches the existing table columns.
- `docs validate . --merge-ready` and `docs drift .` show zero findings once T3 has landed.

**Depends on:** none. The file scope does not overlap T1–T3, and it uses the link targets
fixed in the design.

### T6: Remediation, release test assertion fixes

This remediates T2's pass-3 audit failure (task-reviewer, 2026-09-29). Accepted under Q5 (a)
and Q6 (a).

**Required skill:** `test-dev`

**Files:** `tests/KyberWeave.Tests/ReleaseTests.cs` only.

**Objective:**

1. **`ReleaseNotesAccuratelyDescribeWindowsSigningState`.** The test asserts
   `Assert.DoesNotMatch(@"\bEV\s+certificate", workflow)`, and its summary and inline
   comments say this catches "EV certificate" even when the phrase is split across lines.
   It does not. At HEAD (`ef9ec78`, release.yml lines 765–766) the notes heredoc reads
   `> ... not signed with an EV` followed by an indented `> certificate, ...`. The `>`
   blockquote marker sits between the two words, and `\s+` cannot match it. Today the test
   fails on the HEAD text only through `Contains("not Authenticode-signed")`.
   - Change the pattern so it tolerates the indentation and the blockquote marker, for
     example `\bEV\s+(?:>\s*)*certificate`.
   - Keep the comments, and make them state exactly what the pattern catches.
   - In the same test, add a positive control: `Assert.Matches` with the same pattern
     against a string literal that reproduces the HEAD shape. That is `EV`, a newline, the
     heredoc indentation, `> `, then `certificate`. This proves in-tree that the negative
     assertion is not vacuous. Hold the pattern in one local or constant so both assertions
     use the same text.
2. **`TrayAssetsJoinTheChecksumManifest`.** `Assert.Contains("VERSION", verifyStep, ...)`
   passes whatever the step says. `verifyStep` runs from the step name to the end of the
   file, and bare `VERSION` appears in `env:` blocks and in later steps. Replace the two
   separate `Contains` calls with one assertion of the full invocation, as release.yml
   line 722 writes it:
   `bash scripts/verify-release-checksums.sh release-assets "${VERSION}"`.
3. **Two-argument `--list` guard (Q6 a):** add one script test. `verify-release-checksums.sh --list
   <dir>` with no version must exit non-zero and print the usage error on stderr. It must
   not print an `unbound variable` error. Skip it on Windows, as the existing shell tests
   do. This item consumes T7's fix.

Change nothing else in the file. Do not rename tests and do not reformat unrelated code.

**Acceptance:**

- Finding 1: the pattern matches the HEAD text. Show this two ways: the in-test positive
  control passes, and a fresh check of the pattern against
  `git show ef9ec78:.github/workflows/release.yml` reports a match. The pattern does not
  match the current `release.yml`. The comments no longer claim more than the pattern
  catches.
- Finding 2: the test asserts the full invocation string in one `Assert.Contains`, and no
  bare `"VERSION"` assertion remains.
- Item 3: the new test passes against T7's script, and fails against the
  current guard, `$# -lt 2`. Record this as a manual check, not a required RED, because the
  plan is in standard mode.
- Every `ReleaseTests` test passes.

**Audit:** `task-reviewer`, up to three passes, in standard mode against the T6 row of the
[verification contract](#verification-contract-standard-mode). A failed third pass escalates
to the conductor at end of run.

**Depends on:** T2, because it edits T2's output, and T7, because item 3 tests T7's guard.

### T7: Script argument guard and workflow header comment

Accepted under Q6 (a).

**Required skill:** `github-devops`

**Files:**

- `scripts/verify-release-checksums.sh`, the usage guard at lines 21–35.
- `.github/workflows/release.yml`, the header comment at lines 16–20 only.

**Objective:**

- Make the guard require three arguments when the first is `--list`, and two otherwise.
  On a short call, emit the existing `::error::usage:` line and exit 1. Behavior does not
  change for any correct call.
- Reword "posts an asset-count verification check" in the header so it says what the
  post-publish step does. The step checks that the published asset names are exactly the
  expected set plus `SHA256SUMS.txt`, and that the published manifest is byte-identical to
  the local one.

**Acceptance:**

- `--list <dir>` with no version exits 1 with the usage error, not `unbound variable`.
- Correct two-argument and three-argument calls behave as before, so the existing script
  tests pass.
- Only comment lines change in `release.yml`, and it still parses as YAML.

**Depends on:** none.

### T8: ADR and distribution doc accuracy nits

Item (a) is accepted under Q7 (a), and item (b) under Q8 (a).

**Required skill:** `docs-dev`, following `architecture-decision-record` and
`kyber-weave-docs`.

**Files:**

- (a) `docs/adr/0026-release-integrity-checksums-signing-deferred.md`, lines 18 and 48.
- (b) `docs/distribution.md`, the `release.yml:383–390` citation in the Code signing status
  table (line 241).

**Objective:**

- **(a) ADR 0026:**
  - Line 18: replace "Linux binaries are unsigned by design" with wording that matches
    finding 8, that Linux binaries are unsigned, as is normal for Linux.
  - Line 48: state the SmartScreen dialog text as typical wording that varies by Windows
    build. Do not present it as an exact string.
  - The decision itself does not change.
- **(b) `docs/distribution.md`:** replace the line-number citation with the job and step
  name that signs the macOS KyberDash binary after the Node SEA injection. The link to
  `release.yml` stays.

**Acceptance:**

- `docs validate . --merge-ready` and `docs drift .` show zero findings.
- The ADR still states every Q4 element.
- No `release.yml:<line>` citation remains in `docs/distribution.md`.

**Depends on:** none.

### T9: Script robustness for bash 3.2 and empty asset cases

**Required skill:** `github-devops`, `test-dev`

**Files:** `scripts/verify-release-checksums.sh`, `tests/KyberWeave.Tests/ReleaseTests.cs`

**Objective:**

Fix `set -u` unbound-variable errors on empty arrays and empty asset directories under bash 3.2. Add a test for this case.

**Acceptance:**

- `set -u` mode does not trigger on empty arrays or missing asset directories.
- The test passes, confirming the fix works.

**Depends on:** T1, T2.

**Note:** Added after council APPROVE per user request; addresses technical debt.

### T10: ADR 0026 and docs detective/preventive accuracy pass

**Required skill:** `docs-dev`

**Files:** `docs/adr/0026-release-integrity-checksums-signing-deferred.md`, `docs/distribution.md`, `docs/install.md`, `docs/dash/runbook.md`, `README.md`

**Objective:**

Clarify that the pre-publish `verify-release-checksums.sh` check is fail-closed and preventive (runs before `gh release create`, prevents publication), while the post-publish "Verify published release assets" step is detective only (runs after `gh release create`, can only detect and fail the job but not prevent publication).

**Acceptance:**

- Terminology clearly distinguishes preventive (pre-publish, fail-closed) from detective (post-publish, detection-only).
- `docs validate . --merge-ready` and `docs drift .` show zero findings.

**Depends on:** T1, T3, T5, T8.

**Note:** Added after council APPROVE per user request; ensures documentation accuracy.

### T11: README.md terminal TUI reference update

**Required skill:** `docs-dev`

**Files:** `README.md` (line 221)

**Objective:**

Remediate T10's escalated finding: remove stale "terminal TUI Dashboard" reference in README.md
that no longer matches the KyberDash product name and capabilities. The reference was part of a
broader TUI/Dashboard terminology consistency pass, with other stale launch-config names in
`.vscode/launch.json` spun off as a separate follow-up task not part of this plan.

**Acceptance:**

- README.md line 221 is updated and no longer refers to "terminal TUI" for the Dashboard.
- `docs validate . --merge-ready` and `docs drift .` show zero findings.

**Depends on:** T10 (escalated finding).

**Audit:** `task-reviewer`, first pass resolved the escalation. **Note:** Added post-council 
per user escalation from T10; addresses documentation accuracy.

### T4: Closeout

**Required skill:** `docs-dev`

**Files:**

- this plan
- `docs/plans/README.md`
- the archive location `docs/archive/plans/`

**Objective:**

- Record the evidence.
- Confirm that the durable content is harvested into the T3 docs and into ADR 0026 (T5).
- Archive this plan. Move its index row to Archived Plans, naming `docs/distribution.md`,
  `docs/install.md`, and ADR 0026 in the Canonical Docs / Harvested ADRs column.
- Confirm that `docs/adr/README.md` lists ADR 0026.
- Rerun the docs gates.

**Acceptance:** `docs validate . --merge-ready` and `docs drift .` show zero findings, and no
plan remains in `docs/plans/` (`KW-DOC-LIFECYCLE-003`).

**Depends on:** T1, T2, T3, T5, T6, T7, T8, and code review.

## Dependency graph and concurrency

```text
T1 ──► T2 ──► T6 ──┐
T7 ─────────► T6   │
T3 ────────────────┼──► review ──► T4
T5 ────────────────┤
T8 ────────────────┘

T9 (T1,T2) ──► post-review
T10 (T1,T3,T5,T8) ──► T11 (escalation) ──► post-review
```

- T1, T3, and T5 have disjoint file scopes. They share only the link targets fixed in the
  design.
- T2 consumes T1's script.
- T6 edits T2's output, and T6 item 3 consumes T7's guard.
- T7 touches only the script guard and release.yml comment lines. T1 has passed audit, so
  it does not overlap live work. T8 touches only the ADR and one distribution.md cell. T5
  and T3 have passed audit, so there is no overlap there either.
- T9 (script robustness) depends on T1 and T2 output.
- T10 (docs accuracy) depends on T1, T3, T5, T8 output. T10 exhausted audit passes and
  escalated one finding, which T11 remediates.
- T11 depends on T10's escalated finding.
- Original run: `MAX_CONCURRENCY: 3`, with T1, T3, and T5 running together.
- Remediation run: `MAX_CONCURRENCY: 2`. T7 and T8 run together. T6 starts once T7 has
  passed audit, and may overlap T8 if T8 is still running.
- Post-council tasks T9, T10, and T11 were added per user request and verified through
  task audits and a full gate suite re-run. T4 closeout runs independently.

## Verification contract (standard mode)

| Task | Automated test project or file | Runner command | Acceptance behavior | Additional current evidence |
|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/ReleaseTests.cs` (via T2) | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseTests"` | Script accepts complete and rejects the four defect fixtures. Step order holds. Notes are accurate. YAML parses. Actions stay pinned. | `bash -n` and `shellcheck` on the script if installed (self-check, not a gate). `actionlint .github/workflows/release.yml` if installed (not a declared gate, so record ran or not-available). A manual run of the script against a hand-built fixture directory. |
| T2 | same | same, then the full `dotnet test` from `AGENTS.md` | All new and existing `ReleaseTests` pass. | `dotnet build` (warnings are errors) and `dotnet format` verify. |
| T3 | No automated test. The docs gates replace it. | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready` and `... docs drift .` | Zero findings. Anchors resolve. | Reviewer reads the three-OS commands against the manifest format (`<64 hex>  <name>`). |
| T5 | No automated test. The docs gates replace it. | same two docs commands as T3 | Zero findings (including `KW-DOC-*` frontmatter and link checks on the ADR). ADR index row present. | Reviewer confirms that the ADR states every Q4 element: checksums as the mechanism; no Authenticode or GPG; Azure rejected on cost; SignPath as the revisit option; #132 as the tracker. |
| T6 | `tests/KyberWeave.Tests/ReleaseTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseTests"` | The EV pattern matches the HEAD blockquote-split text, shown by the in-test positive control, and does not match the current workflow. `TrayAssetsJoinTheChecksumManifest` asserts the full `bash scripts/verify-release-checksums.sh release-assets "${VERSION}"` invocation. The two-argument `--list` test passes. All `ReleaseTests` pass. | A fresh match of the pattern against `git show ef9ec78:.github/workflows/release.yml`. `dotnet build` (warnings are errors) and `dotnet format --verify-no-changes`. Diff limited to `ReleaseTests.cs`. Audit by `task-reviewer`, up to three passes. |
| T7 | `tests/KyberWeave.Tests/ReleaseTests.cs` (existing script tests, plus T6 item 3) | same `ReleaseTests` filter | Short `--list` call exits 1 with the usage error. Correct calls are unchanged. The workflow still parses. | A manual `bash scripts/verify-release-checksums.sh --list /tmp` run showing the usage error. `bash -n`, and `shellcheck` if installed (record ran or not-available). `git diff` of release.yml shows comment lines only. |
| T8 | No automated test. The docs gates replace it. | same two docs commands as T3 | Zero findings. | Reviewer confirms the ADR still states every Q4 element, and that no `release.yml:<line>` citation remains in `docs/distribution.md`. |
| T9 | `tests/KyberWeave.Tests/ReleaseTests.cs` and `scripts/verify-release-checksums.sh` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseTests"` | Script fix: `set -u` mode does not trigger unbound-variable on empty arrays or empty asset directories under bash 3.2. Regression test `VerifyReleaseChecksumsReportsEveryAssetForEmptyDirectory` passes. Correct calls still work. | Added post-council per user request. Manual test: `bash -u scripts/verify-release-checksums.sh --list /tmp/empty-dir` exits with usage error, not unbound-variable. Note: bash 5+ behavior exercised by CI on Ubuntu; bash 3.2 manually validated during development. |
| T10 | No automated test. The docs gates replace it. | same two docs commands as T3 | Zero findings. ADR 0026 and docs clearly state: pre-publish is fail-closed and preventive; post-publish "Verify published release assets" is detective (runs after publication, detects and fails job, does not prevent). | Added post-council per user request. Reviewer confirms preventive/detective terminology is accurate and consistent across ADR and distribution/install/dash/README docs. T10 exhausted 3 audit passes and escalated README.md:221 finding. |
| T11 | No automated test. The docs gates replace it. | same two docs commands as T3 | Zero findings. README.md line 221 no longer contains stale "terminal TUI Dashboard" reference. | Added to remediate T10's escalated finding. Passed audit pass 1. Stale `.vscode/launch.json` launch-config names spun off as separate follow-up task. |
| T4 | none (docs lifecycle) | same two docs commands | Zero findings. Plan archived. The archive row names ADR 0026. | `review gates . --out artifacts/gates.json` |
| Post-merge | none (live) | User-owned U1–U4 | The RC release shows all 21 assets. Windows verification: `sha256sum -c` / `sha256sum -c` / PowerShell commands pass. SmartScreen and Unblock flow match docs. (U4 optional) Comment on #132 with RC and docs links. | Run URL and screenshots attached to the PR or to #132. U1–U4 remain PENDING. |

Fresh evidence is required after implementation, and again after any rework driven by review.

## User-owned steps

All steps U1–U4 remain **PENDING** until after merge and RC publication.

- **U1: Trigger a test release.** After merge, run Actions → Release → Run workflow on `main`
  with **Make production release** unchecked. That publishes the next RC; there is no dry
  run (finding 11). Approve `release` for `build-tray` and `release`. Confirm that the run is
  green, including the manifest and post-publish steps.
- **U2: Verify on a real Windows machine.** Download:
  - `kyberdash-tray-win-x64-setup.exe`
  - `kyber-weave-win-x64.zip`
  - `SHA256SUMS.txt`

  Verify each with the documented PowerShell commands. Run the setup and confirm that the
  SmartScreen and **Unblock** flow matches `docs/install.md`.
- **U3: Verify on macOS or Linux.** Run `shasum -a 256 -c SHA256SUMS.txt` or
  `sha256sum -c SHA256SUMS.txt` over a full download. It must report no `FAILED` line, which
  confirms that the finding 3 defect is gone.
- **U4 (optional, outward-facing): Comment on #132.** Link the RC and the docs. The issue
  stays `deferred`.

### Follow-up work (out of scope for this plan)

- **Stale VS Code launch configuration names:** The `.vscode/launch.json` "Terminal TUI 
  Dashboard" launch-config names identified during T10 are stale and do not match the 
  current KyberDash capabilities and product naming. These have been spun off as a 
  separate follow-up task to be addressed independently.
- **Bash 5.x behavior of T9 fix:** The `set -u` robustness fix in T9 was developed and 
  validated under bash 3.2 (local development environment). Bash 5.x behavior is not 
  locally available but is exercised by CI on Ubuntu runners in the full test suite.

## Risks

- **The workflow change is only proven post-merge** (finding 11). This is mitigated by the
  script tests and by the fail-closed step before publish. If the first RC fails at the
  manifest step, nothing is published, and the fix goes through a follow-up PR.
- **The expected list can drift from the build matrices.** Adding an asset would then fail
  the release. That is the intended fail-closed behavior, and T3 documents it for
  maintainers.
- **SmartScreen wording and flow vary by Windows build.** U2 is the ground truth.

## Out-of-scope boundaries

No signing of any kind. No new publish channels. No change to client verification code in
`install.sh`, the updater, Squad, or `menubar`. No change to release approval or
authorization.

## Verification gates

The `AGENTS.md` command block applies: format, build, test, skill gates, and
`docs validate . --merge-ready` plus `docs drift .`. `review gates .` runs once before review.
The local update loop is not required, because neither `install.sh` nor the self-updater
changes.

## Review

A code-review council runs over the whole change after T1, T2, T3, T5, T6, T7, and T8. `release.yml` is
CODEOWNERS-protected, so the pull request needs `dpalfery` review.

## Closeout

`docs-dev` owns T4.
