---
id: archive/plans/2026-09-26-install-sh-local-origin
title: install.sh loopback release origin
doc-type: plan
status: archived
component: Distribution
owner: dpalfery
last-reviewed: 2026-09-26
development-mode: test-first
keywords:
  - install.sh
  - release origin
  - update loop
---

# install.sh loopback release origin

**Status: Complete**

Complete and archived on 2026-09-26. Fixes #125. The evidence is in Closeout.

Input: [GitHub issue #125](https://github.com/dpalfery/kyber-weave/issues/125).

`development-mode: test-first`. The conductor relayed that choice. The user did not opt out.

Approved for execution. The user replied "approve" on 2026-09-26. That answer responds to the presented approve-and-execute gate. While this plan was open, frontmatter `status` was `current` because the ontology closed set has no `ready` value, and this heading and the plan index carried the lifecycle word Ready. Closeout sets frontmatter `status` to `archived` and this heading to Complete, the lifecycle word the plan index uses for a finished plan.

## Problem and goal

`scripts/update-loop.sh` proves `kyber-weave update` and `kyber-weave squad install` against binaries served from loopback. It does not prove `scripts/install.sh`, which [distribution.md](../../distribution.md) and [install.md](../../install.md) document as the first-install channel.

The CLI reaches that server because `ReleaseOrigin` honours `KYBER_WEAVE_RELEASE_ORIGIN` and accepts only a loopback authority. `install.sh` has no equivalent. `RELEASE_BASE`, `LATEST_API`, and `RELEASES_API` are fixed to GitHub, and `fetch` / `fetch_stdout` reject any URL that does not start with `https://`. The loop therefore stages its "from" binaries with `tar` or `cp`. The installer's download, checksum comparison, and `cp`-then-`mv` replace stay unproven until a real release exists.

Goal: `install.sh` reads `KYBER_WEAVE_RELEASE_ORIGIN` and installs a pinned version from the existing loopback release server only for `http` or `https` with no userinfo and host `127.0.0.1`, `localhost`, or `[::1]`. A corrupted asset fails the checksum comparison instead of being installed. Unset or non-loopback configuration keeps refusing non-HTTPS URLs. After the loopback server is listening, `--from working` and a git ref stage by `install.sh --install-dir "$BIN" --version <from>`. `--from installed` keeps the copy, because it means "use the binaries already on this machine". `dash/src/install/origin.ts` stays unchanged.

## Approved decisions

| Id | Decision | Approval provenance |
|---|---|---|
| P1 | The artifact is a plan. | Conductor relayed `USER_CHOICE: PLAN` on 2026-09-26. |
| P2 | `development-mode` is `test-first`. | Conductor relayed `DEVELOPMENT_MODE: test-first` on 2026-09-26. The user did not opt out. |
| D1 | `scripts/install.sh` reads `KYBER_WEAVE_RELEASE_ORIGIN`. | User replied 'use all recommended' on 2026-09-26, selecting D1-A. |
| D2 | Accept only `http` or `https`, no userinfo, and host `127.0.0.1`, `localhost`, or `[::1]`, with an optional port. Reject everything else, including other `127.*` addresses. | User replied 'use all recommended' on 2026-09-26, selecting D2-A. |
| D3 | While the override is active, curl uses `--proto '=http,https'` and keeps `--proto-redir '=https'`. An `http` origin refuses wget. An `https` loopback origin keeps today's flags for both tools. | User replied 'use all recommended' on 2026-09-26, selecting D3-C. |
| D4 | `dash/src/install/origin.ts` is out of scope. Leave it unchanged and record the divergence. | User replied 'use all recommended' on 2026-09-26, selecting D4-B. |
| D5 | After the server is listening, `working` and a git ref stage by `install.sh --install-dir "$BIN" --version <from>`. `--from installed` keeps the copy, because it means "use the binaries already on this machine". | User replied 'D5A' on 2026-09-26, selecting D5-A. |
| P3 | Approve and execute this plan. | User replied "approve" on 2026-09-26. That answer responds to the presented approve-and-execute gate. |

## Investigation findings

Kyber-Weave `docs_explore` was unavailable (the MCP namespace was missing). Discovery started at [docs/README.md](../../README.md). `.codegraph/` is present. `codegraph explore` was used for `ReleaseOrigin`, the local release server, and the plan validators. `scripts/install.sh` and `scripts/update-loop.sh` are not indexed; they were read directly. Nothing under `docs/archive/` is execution authority. While it was open, this file was the active Ready plan linked from [docs/plans/README.md](../../plans/README.md). [docs/specs/README.md](../../specs/README.md) has no open spec.

- [docs/catalog.md](../../catalog.md) assigns Distribution to `scripts`, [install.md](../../install.md), and [distribution.md](../../distribution.md).
- [distribution.md](../../distribution.md) already states the loopback contract for `KYBER_WEAVE_RELEASE_ORIGIN`, points the remaining installer gap at issue #125, and requires `./scripts/update-loop.sh` when `install.sh` changes. No ADR covers the override.
- `ReleaseOrigin.Resolve` and `ReleaseOrigin.EnsureAllowed` in `src/KyberWeave.Cli/Update/ReleaseOrigin.cs` are the reference behavior. `ReleaseOriginTests` already pins that C# contract. This plan does not change it.
- `scripts/local-release-server.py` binds `127.0.0.1`, prints the port, and serves both `/repos/<owner>/<repo>/releases...` and `/<owner>/<repo>/releases/download/<tag>/<file>`. No server work is required for the paths `install.sh` builds once those three constants share one origin.
- `scripts/install.sh` sets the three endpoints near the top of the file. `fetch` and `fetch_stdout` are the scheme guards. `kyber_weave_lookup_checksum` and `kyber_weave_verify_checksum` are defined before the `KYBER_WEAVE_INSTALL_LIB` return and are what `tests/KyberWeave.Tests/ReleaseTests.cs` calls. `verify_and_extract` repeats the awk match after that return instead of calling the helper. `install_binary` does `cp` to a dotfile, then `mv -f`. Issue #125's "neither has a local test" is true for those two main-body copies and false for the helper.
- `scripts/update-loop.sh` defaults `--from` to `working`, exports `KYBER_WEAVE_RELEASE_ORIGIN` only after staging, and requires curl on PATH.
- `dash/src/install/origin.ts` `releaseOrigin` replaces the download prefix with `KYBER_WEAVE_RELEASE_ORIGIN` and does not check the host. D4 leaves that file unchanged. The variable is not loopback-safe for every consumer.

## Test contract

`development-mode: test-first`. Every implementation task has a row. D1–D5 are approved, and the assertions below use those options verbatim.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/ReleaseTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseTests` | `KYBER_WEAVE_RELEASE_ORIGIN` unset, or set to a non-loopback origin, refuses an `http://` URL and installs no file. A legal origin is only `http` or `https`, with no userinfo, and host `127.0.0.1`, `localhost`, or `[::1]`, with an optional port. Any other host, including other `127.*` addresses, is refused. With a legal loopback origin, `install.sh --install-dir <dir> --version <pinned> --no-mcp` downloads from `scripts/local-release-server.py` and installs. A byte flipped in that asset after `SHA256SUMS.txt` is written exits non-zero, reports a SHA-256 mismatch, and leaves the install directory without `kyber-weave`. While the override is active, curl uses `--proto '=http,https'` and keeps `--proto-redir '=https'`. An `http` origin refuses wget. An `https` loopback origin keeps today's flags (`curl --proto '=https' --proto-redir '=https'`, `wget --https-only`). An HTTP redirect away from loopback is not installed. `dash/src/install/origin.ts` is not exercised. Existing `ReleaseTests` checksum and RID facts stay green. POSIX-only facts skip on Windows the way the class already does. | The new facts exist and fail on unchanged `install.sh` because the loopback URL is refused before any checksum comparison, or because the helper they call is not defined. The failure is that refusal or a missing helper, not an edited assertion. | The same filter passes. The mismatch fact fails on the checksum comparison, not on the scheme guard. Assertions are not weakened. |
| T2 | `tests/KyberWeave.Tests/ReleaseTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseTests` | Same behavior as T1. T2 is the implementation that makes those facts pass. | T1's RED run, recorded before `scripts/install.sh` is edited. | T1's GREEN acceptance. |
| T3 | No separate unit test. Integration command: `scripts/update-loop.sh` | `./scripts/update-loop.sh` | After the server is listening, `--from working` and a git ref stage by `install.sh --install-dir "$BIN" --version <from>` under `KYBER_WEAVE_RELEASE_ORIGIN`. `--from installed` keeps the copy, because it means "use the binaries already on this machine". Every check the loop already prints still passes, including the self-update, the Squad install unless `--skip-squad` is passed, and the KyberDash cases unless `--no-kyberdash` is passed. | No unit RED. The missing loopback install is already RED under T1. Before T3, the default `working` path extracts with `tar` inside `stage_from_release_tree` and never runs `install.sh`. | `./scripts/update-loop.sh` exits 0. The run log shows `install.sh` staged the `working` and git-ref "from" binaries. The `installed` path still copies. Existing PASS lines are still present. Needs `node` and `npm` on PATH when KyberDash is included, as [distribution.md](../../distribution.md) already states. |
| T4 | No product test. | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | [install.md](../../install.md) documents `KYBER_WEAVE_RELEASE_ORIGIN` and the D2 host limit. [distribution.md](../../distribution.md) stops describing the installer half of the loop as waiting on issue #125. It states that, after the server is listening, `working` and a git ref stage by `install.sh --install-dir "$BIN" --version <from>`, and that `--from installed` keeps the copy. Neither page describes `dash/src/install/origin.ts` as loopback-checked. | No product RED. | Both commands exit 0. `docs validate . --merge-ready` stays expected to fail with `KW-DOC-LIFECYCLE-003` until T5 archives this plan. |
| T5 | No test. Closeout. | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | This plan is archived and the index no longer lists it as active. Canonical pages match the shipped behavior. | No RED. | `--merge-ready` exits 0 because this file is no longer under `docs/plans/`. Drift exits 0. |

Changing an assertion in this table, including weakening one to reach green, returns the plan to Draft and needs the conductor to relay reapproval.

## Tasks

### T1 — RED: pin the installer against the loopback server

- **Objective:** Add the failing `ReleaseTests` facts in the Test contract before any edit to `scripts/install.sh`. The facts assert `KYBER_WEAVE_RELEASE_ORIGIN`, the D2 hosts, and the D3 curl and wget split.
- **Files / symbols:** `tests/KyberWeave.Tests/ReleaseTests.cs`. The facts drive `scripts/install.sh` and `scripts/local-release-server.py`. They use the `ReleaseTests` shell harness and must not set `KYBER_WEAVE_INSTALL_LIB` on the full-install facts.
- **Acceptance:** The new facts fail for the reason in the Test contract. Existing facts in the class still pass.
- **Depends on:** none. D1, D2, and D3 are approved.
- **Required skill:** `test-dev`

### T2 — GREEN: honour the override in install.sh

- **Objective:** Make T1 pass. Resolve `RELEASE_BASE`, `LATEST_API`, and `RELEASES_API` from `KYBER_WEAVE_RELEASE_ORIGIN`. Reject every origin D2 rejects. In `fetch` and `fetch_stdout`, an active `http` override uses curl `--proto '=http,https'` and `--proto-redir '=https'` and refuses wget. An `https` loopback origin keeps `curl --proto '=https' --proto-redir '=https'` and `wget --https-only`.
- **Files / symbols:** `scripts/install.sh`. Endpoint constants. `fetch`. `fetch_stdout`. The helpers must be defined before the `KYBER_WEAVE_INSTALL_LIB` return when T1 calls them that way. `verify_and_extract` must use `kyber_weave_verify_checksum` so the live install and `ReleaseTests` do not keep two awk parsers. `install_binary` stays the `cp`-then-`mv` replace. Do not edit `dash/src/install/origin.ts`.
- **Acceptance:** T1 GREEN. Unset and non-loopback values still refuse non-HTTPS URLs. A corrupted asset fails the checksum comparison. `ReleaseOrigin` and `ReleaseOriginTests` are unchanged.
- **Depends on:** T1 RED evidence.
- **Required skill:** `test-dev` re-runs the contract. No specialist skill in the inventory covers POSIX `sh`. The conductor assigns the script edit directly.

### T3 — Stage the loop through install.sh

- **Objective:** After the server is listening, `working` and a git ref stage by `install.sh --install-dir "$BIN" --version <from>`. `--from installed` keeps the copy, because it means "use the binaries already on this machine". The process environment exports `KYBER_WEAVE_RELEASE_ORIGIN`.
- **Files / symbols:** `scripts/update-loop.sh`. `stage_from_release_tree` for `working` and a git ref. `stage_from_directory` for `--from installed`. The server start that exports `KYBER_WEAVE_RELEASE_ORIGIN`. Do not change `scripts/local-release-server.py`, `scripts/release-local.sh`, or `dash/src/install/origin.ts`.
- **Acceptance:** Test-contract row T3.
- **Depends on:** T2.
- **Required skill:** `test-dev` for the GREEN re-run of T1 if the loop task touches shared fixtures. No specialist skill covers this shell script. The conductor assigns the edit directly.

### T4 — Document the override and the loop

- **Objective:** Update the canonical Distribution pages so they describe the answered behavior.
- **Files / symbols:** `docs/install.md` options table. `docs/distribution.md` section "Verifying a release locally", including the sentence that issue #125 tracks the installer gap.
- **Acceptance:** Test-contract row T4. The pages name `KYBER_WEAVE_RELEASE_ORIGIN`, the D2 host limit, and the D5 staging rule: after the server is listening, `working` and a git ref stage by `install.sh --install-dir "$BIN" --version <from>`, and `--from installed` keeps the copy. They do not document `dash/src/install/origin.ts` as loopback-checked.
- **Depends on:** T2 and T3, so the pages describe code that exists.
- **Required skill:** `kyber-weave-docs`

### T5 — docs-dev closeout

- **Objective:** After review, archive this plan and harvest any durable rule into the canonical pages. T4 may already hold that prose.
- **Files / symbols:** this file, `docs/plans/README.md`, and `docs/archive/plans/` as the archive destination the index already describes.
- **Acceptance:** Test-contract row T5.
- **Depends on:** Review approval. T4.
- **Required skill:** `kyber-weave-docs`

No task edits `dash/src/install/origin.ts`.

## Dependency graph and MAX_CONCURRENCY

```text
T1 ─> T2 ─> T3 ─> T4 ─> review ─> T5
```

`MAX_CONCURRENCY: 1`

Audit: D1–D5 are approved and are not gates. T1 and T2 are a RED-then-GREEN pair, and T2 consumes T1's failing tests. T3 consumes a working `install.sh` from T2. T4 consumes that finished seam. T1 is the only writer of `tests/KyberWeave.Tests/ReleaseTests.cs`. T2 is the only writer of `scripts/install.sh`. T3 is the only writer of `scripts/update-loop.sh`. T4 is the only writer of the two Distribution pages. Those file scopes do not overlap, but the RED/GREEN and "docs describe shipped behavior" edges still serialize them. No open decision remains.

## Risks

- D2 rejects loopback addresses in `127.0.0.0/8` other than `127.0.0.1`. The local server binds `127.0.0.1`, so the loop is unaffected. Callers using another `127.*` host will be refused.
- D3 means an `http` override requires curl. `update-loop.sh` already does. A machine whose only downloader is wget cannot use an `http` origin.
- `dash/src/install/origin.ts` keeps accepting a non-loopback `KYBER_WEAVE_RELEASE_ORIGIN`. The loop already exports that variable. This plan must not claim the variable is safe for every consumer.
- `verify_and_extract` and `kyber_weave_verify_checksum` can drift until T2 makes the live path call the helper. T1's corrupted-asset fact is what stops a green helper test from hiding a second parser.
- `./scripts/update-loop.sh` publishes single-file binaries and, unless `--no-kyberdash` is set, builds KyberDash. It is the integration proof, not the RED cycle.
- `docs validate . --merge-ready` fails with `KW-DOC-LIFECYCLE-003` while this file remains in `docs/plans/`. That is the merge gate described in the plan index, not a defect in this Ready plan.

## Out of scope

- Changing `ReleaseOrigin`, `ReleaseOriginTests`, `ChecksumVerifier`, or `BinaryInstaller.Replace`.
- Changing `scripts/local-release-server.py` or `scripts/release-local.sh`.
- `dash/src/install/origin.ts`. D4 leaves it unchanged.
- `npm/lib/download.js`, which has its own HTTPS-only `RELEASE_BASE` and is not the documented first-install channel.
- Windows `.zip` installs. `install.sh` already refuses Windows.
- Cross-RID loop builds. [distribution.md](../../distribution.md) already leaves those out of reach.
- Any new `KW-*` rule id.

## Verification gates

Before review, in addition to the Test contract:

- `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseTests`
- `./scripts/update-loop.sh` (the distribution loop required for an `install.sh` change)
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`

The repository gate suite (`review gates`) runs at review. This change does not touch the self-updater's C# replace path. It does touch `install.sh` and the loop, so the loop command above is required. `docs validate . --merge-ready` is the T5 gate, not a gate this Draft can pass.

## Review

Review follows the repository review council after T1–T4 are green. The reviewer checks the diff against the approved decisions: `KYBER_WEAVE_RELEASE_ORIGIN`, the three-host check, the curl and wget split, `working` and git-ref staging through `install.sh` after the server is listening, `--from installed` still copying, and `dash/src/install/origin.ts` untouched. A failing checksum must be the reason a corrupted asset is refused.

## docs-dev closeout

T5 is the closeout. `kyber-weave-docs` verifies the evidence, confirms [install.md](../../install.md) and [distribution.md](../../distribution.md) carry the shipped rule, archives this plan under `docs/archive/plans/`, and updates [docs/plans/README.md](../../plans/README.md) so the active list no longer links it. No ADR is expected unless an answer chooses a constraint the canonical pages cannot hold. Closeout then re-runs `docs validate . --merge-ready` and `docs drift .`.

## Closeout (T5, 2026-09-26)

**Canonical documentation.** [install.md](../../install.md) and [distribution.md](../../distribution.md) already describe the shipped rule: `KYBER_WEAVE_RELEASE_ORIGIN`, the hosts `127.0.0.1`, `localhost`, and `[::1]`, the curl and wget split, and the staging rule that, after the loopback server is listening, `working` and a git ref stage by `install.sh --install-dir "$BIN" --version <from>` while `--from installed` keeps the copy. Neither page describes `dash/src/install/origin.ts` as loopback-checked, and neither still describes issue #125 as an open installer gap. No ADR: the constraint fits those two pages.

**Test evidence.** Commit `7c2a659` adds the failing `ReleaseOrigin` facts and changes only `tests/KyberWeave.Tests/ReleaseTests.cs`. At that commit, `FullyQualifiedName~KyberWeave.Tests.ReleaseTests.ReleaseOrigin` was 43 failed / 0 passed. Each failure was `/bin/sh: 2: fetch: not found`, including `ReleaseOriginRejectsByteFlippedAfterTheChecksumIsWritten` (exit 127), so the refusal happened before checksum comparison. The complement of pre-existing `ReleaseTests` (`FullyQualifiedName~KyberWeave.Tests.ReleaseTests&FullyQualifiedName!~ReleaseOrigin`) passed 65. Commit `0a6ed70` changes only `scripts/install.sh`. GREEN: the `ReleaseOrigin` filter passed 43 (`/opt/cursor/artifacts/release-origin-tests.log`) and `FullyQualifiedName~ReleaseTests` passed 118 (`/opt/cursor/artifacts/release-tests.log`).

**Loop evidence.** Commit `69a6890` changes only `scripts/update-loop.sh`. `/opt/cursor/artifacts/update-loop-working.log` contains `install.sh staged the working from binaries` and `EXIT:0`. `/opt/cursor/artifacts/update-loop-git-ref.log` contains `install.sh staged the git-ref from binaries` and `EXIT:0`. `/opt/cursor/artifacts/update-loop-installed.log` contains `copied the binaries already on this machine` and `EXIT:0`, and does not stage through `install.sh`.

**Review.** The 2026-09-26 council returned **APPROVE**, risk **MEDIUM**, under `KW-REVIEW-024` (`/opt/cursor/artifacts/code-review-report.md`). All 15 declared gates passed: format-whitespace, format-style, build, test, skill-validate, skill-lint, skill-scan, docs-validate, docs-drift, inspectcode, duplicates, ts-typecheck, ts-test, ts-lint, ts-reachable. One accepted minor finding (a redundant `System.Globalization.CultureInfo` qualifier in `ReleaseTests.cs`) did not block and is not fixed in this closeout.

**Index.** [docs/plans/README.md](../../plans/README.md) no longer lists this plan as active. The archived-plans row points at [install.md](../../install.md) and [distribution.md](../../distribution.md), with no ADR.
