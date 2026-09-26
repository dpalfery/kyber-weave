---
id: plans/2026-09-26-install-sh-local-origin
title: install.sh loopback release origin
doc-type: plan
status: draft
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

**Status: Draft**

Input: [GitHub issue #125](https://github.com/dpalfery/kyber-weave/issues/125).

`development-mode: test-first`. The conductor relayed that choice. The user did not opt out.

This Draft is not executable. The decision ledger is open. Recommendations there are not approved decisions.

## Problem and goal

`scripts/update-loop.sh` proves `kyber-weave update` and `kyber-weave squad install` against binaries served from loopback. It does not prove `scripts/install.sh`, which [distribution.md](../distribution.md) and [install.md](../install.md) document as the first-install channel.

The CLI reaches that server because `ReleaseOrigin` honours `KYBER_WEAVE_RELEASE_ORIGIN` and accepts only a loopback authority. `install.sh` has no equivalent. `RELEASE_BASE`, `LATEST_API`, and `RELEASES_API` are fixed to GitHub, and `fetch` / `fetch_stdout` reject any URL that does not start with `https://`. The loop therefore stages its "from" binaries with `tar` or `cp`. The installer's download, checksum comparison, and `cp`-then-`mv` replace stay unproven until a real release exists.

Goal: once the open decisions below are answered, `install.sh` can install a pinned version from the existing loopback release server under the same loopback-only rule `ReleaseOrigin` already enforces, the local loop stages the "from" side through that installer on the paths the ledger selects, and a corrupted asset fails the checksum comparison instead of being installed. Unset or non-loopback configuration keeps refusing non-HTTPS URLs.

## Approved decisions

| Id | Decision | Approval provenance |
|---|---|---|
| P1 | The artifact is a plan. | Conductor relayed `USER_CHOICE: PLAN` on 2026-09-26. |
| P2 | `development-mode` is `test-first`. | Conductor relayed `DEVELOPMENT_MODE: test-first` on 2026-09-26. The user did not opt out. |

No implementation decision is approved. Ledger recommendations are not approvals.

## Decision ledger

Draft only. Remove this section when the plan leaves Draft. Status stays `OPEN` until the conductor relays an answer keyed by id.

### D1 — Override variable name

| | |
|---|---|
| Status | OPEN |
| Dependency | none |
| Recommendation | D1-A |

- **D1-A.** Use `KYBER_WEAVE_RELEASE_ORIGIN`, the name `ReleaseOrigin.EnvironmentVariable` already publishes and the name `scripts/update-loop.sh` already exports.
- **D1-B.** Use a distinct `KYBER_WEAVE_*` name that only `install.sh` reads.

D1-A keeps one redirect for the loop and matches the flag/env pairing `install.sh` already uses. D1-B isolates the shell from `dash/src/install/origin.ts`, which already reads `KYBER_WEAVE_RELEASE_ORIGIN` with no loopback check (see D4). Either option can be implemented inside the existing script. This is not a new product interface.

### D2 — POSIX loopback check

| | |
|---|---|
| Status | OPEN |
| Dependency | none |
| Recommendation | D2-A |

`ReleaseOrigin.Resolve` rejects a non-absolute URL, a scheme other than `http` or `https`, and any host that fails `IsLoopbackAuthority`. The exception text names `127.0.0.1`, `[::1]`, and `localhost`. `IsLoopbackAuthority` is wider than that sentence: `Uri.IsLoopback` includes `127.0.0.0/8`. The installer is POSIX `sh` and cannot assume a URL parser or that `kyber-weave` is already installed.

- **D2-A.** Accept only `http` or `https`, no userinfo, and a host of `127.0.0.1`, `localhost`, or `[::1]`, with an optional port. Reject everything else, including other `127.*` addresses.
- **D2-B.** Also accept a `127.*` glob, approximating `127.0.0.0/8` without parsing octets.
- **D2-C.** Delegate the check to Python, `kyber-weave`, or another URL parser.

D2-A matches the exception text and needs no parser. D2-B accepts hosts that are not loopback (`127.999.1.1`). D2-C breaks first install on a machine that has neither the CLI nor a parser, which is the installer's audience. The whole-`/8` gap in D2-A is an accepted divergence from `Uri.IsLoopback` unless a later answer selects D2-B.

### D3 — curl and wget scheme guards

| | |
|---|---|
| Status | OPEN |
| Dependency | none |
| Recommendation | D3-C |

Today both `fetch` and `fetch_stdout` refuse a non-`https://` URL, then run `curl --proto '=https' --proto-redir '=https'` or `wget --https-only`. `scripts/local-release-server.py` speaks plain HTTP on `127.0.0.1` and does not redirect. `ReleaseOrigin.EnsureAllowed` allows any HTTPS URL, and allows HTTP only for a loopback authority while the override is active. The issue asked about curl only. The wget branch is the same policy.

- **D3-A.** While the override is active, widen curl to `--proto '=http,https' --proto-redir '=http,https'` and drop wget `--https-only`.
- **D3-B.** While the override is active, bypass both scheme guards and rely on the pre-request host check.
- **D3-C.** While the override is active, curl uses `--proto '=http,https'` and keeps `--proto-redir '=https'`. An `http` origin refuses wget, because `--https-only` cannot express "first hop may be HTTP, redirects stay HTTPS". An `https` loopback origin keeps today's flags for both tools.

D3-A lets curl follow an HTTP redirect off the box, which `EnsureAllowed` refuses. D3-B also allows non-HTTP schemes. D3-C matches the remark on `EnsureAllowed`: plain HTTP is only the loopback hop, and a redirect away from that server stays HTTPS. The local server does not redirect, so the loop still completes. The loop already requires curl.

### D4 — KyberDash origin helper

| | |
|---|---|
| Status | OPEN |
| Dependency | none |
| Recommendation | D4-B |

`dash/src/install/origin.ts` `releaseOrigin` replaces the download prefix with `KYBER_WEAVE_RELEASE_ORIGIN` and does not check the host. Issue #125 names `scripts/install.sh` and `scripts/update-loop.sh` only. The loop already exports the variable before `kyber-weave update`.

- **D4-A.** In scope. Apply the same loopback rule to `releaseOrigin`, with a failing test in the existing tray install tests before the edit.
- **D4-B.** Out of scope. Leave `origin.ts` unchanged. The plan records the divergence so later work does not treat the variable as loopback-safe for every consumer.

D4-B matches the issue's seam. D4-A is a separate behavior change to a helper whose comment says the override replaces the whole prefix so a test server need not imitate GitHub's layout. If the answer is D4-A, this plan returns to Draft and gains a Test-contract row before that edit is scheduled. No dash task is dispatchable while D4 is open.

### D5 — Which loop paths call install.sh

| | |
|---|---|
| Status | OPEN |
| Dependency | D1 |
| Recommendation | D5-A |

Not asked in this handoff. D1 is still open, and this is the fifth material question.

The issue names `stage_from_directory`. The default `--from` is `working`, which uses `stage_from_release_tree` (`tar`) and never calls `stage_from_directory`. The server starts only after that staging. `--from installed` copies whatever is already in the install directory; those binaries are not a tag the loop just published.

- **D5-A.** After the server is listening, `working` and a git ref stage by `install.sh --install-dir "$BIN" --version <from>`. `--from installed` keeps the copy, because it means "use the binaries already on this machine".
- **D5-B.** Every `--from` mode, including `installed`, stages through `install.sh`.
- **D5-C.** Change only the `cp` inside `stage_from_directory`. The default `working` run stays on `tar`.

D5-A is what makes the issue's verification true for the default run without changing what `--from installed` means. D5-C leaves the default run unproven. The invocation uses the variable D1 selects, so this decision waits on D1.

## Investigation findings

Kyber-Weave `docs_explore` was unavailable (the MCP namespace was missing). Discovery started at [docs/README.md](../README.md). `.codegraph/` is present. `codegraph explore` was used for `ReleaseOrigin`, the local release server, and the plan validators. `scripts/install.sh` and `scripts/update-loop.sh` are not indexed; they were read directly. Nothing under `docs/archive/` is execution authority. [docs/plans/README.md](README.md) has no active plan. [docs/specs/README.md](../specs/README.md) has no open spec.

- [docs/catalog.md](../catalog.md) assigns Distribution to `scripts`, [install.md](../install.md), and [distribution.md](../distribution.md).
- [distribution.md](../distribution.md) already states the loopback contract for `KYBER_WEAVE_RELEASE_ORIGIN`, points the remaining installer gap at issue #125, and requires `./scripts/update-loop.sh` when `install.sh` changes. No ADR covers the override.
- `ReleaseOrigin.Resolve` and `ReleaseOrigin.EnsureAllowed` in `src/KyberWeave.Cli/Update/ReleaseOrigin.cs` are the reference behavior. `ReleaseOriginTests` already pins that C# contract. This plan does not change it.
- `scripts/local-release-server.py` binds `127.0.0.1`, prints the port, and serves both `/repos/<owner>/<repo>/releases...` and `/<owner>/<repo>/releases/download/<tag>/<file>`. No server work is required for the paths `install.sh` builds once those three constants share one origin.
- `scripts/install.sh` sets the three endpoints near the top of the file. `fetch` and `fetch_stdout` are the scheme guards. `kyber_weave_lookup_checksum` and `kyber_weave_verify_checksum` are defined before the `KYBER_WEAVE_INSTALL_LIB` return and are what `tests/KyberWeave.Tests/ReleaseTests.cs` calls. `verify_and_extract` repeats the awk match after that return instead of calling the helper. `install_binary` does `cp` to a dotfile, then `mv -f`. Issue #125's "neither has a local test" is true for those two main-body copies and false for the helper.
- `scripts/update-loop.sh` defaults `--from` to `working`, exports `KYBER_WEAVE_RELEASE_ORIGIN` only after staging, and requires curl on PATH.
- `dash/src/install/origin.ts` is the D4 finding. It is not a second installer for the CLI archives.

## Test contract

`development-mode: test-first`. Every implementation task has a row. Assertions that depend on an open decision are written only after that decision is `ANSWERED`. A recommendation must not be hardcoded as the expected value before then.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Tests/ReleaseTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseTests` | With the D1 variable unset or set to a non-loopback origin, an `http://` URL is refused and no file is installed. With a D2-legal loopback origin, `install.sh --install-dir <dir> --version <pinned> --no-mcp` downloads from `scripts/local-release-server.py` and installs. A byte flipped in that asset after `SHA256SUMS.txt` is written exits non-zero, reports a SHA-256 mismatch, and leaves the install directory without `kyber-weave`. Transport follows the answered D3 option, including an HTTP redirect away from loopback, which must not be installed. Existing `ReleaseTests` checksum and RID facts stay green. POSIX-only facts skip on Windows the way the class already does. | The new facts exist and fail on unchanged `install.sh` because the loopback URL is refused before any checksum comparison, or because the helper they call is not defined. The failure is that refusal or a missing helper, not an edited assertion. | The same filter passes. The mismatch fact fails on the checksum comparison, not on the scheme guard. Assertions are not weakened. |
| T2 | `tests/KyberWeave.Tests/ReleaseTests.cs` | `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseTests` | Same behavior as T1. T2 is the implementation that makes those facts pass. | T1's RED run, recorded before `scripts/install.sh` is edited. | T1's GREEN acceptance. |
| T3 | No separate unit test. Integration command: `scripts/update-loop.sh` | `./scripts/update-loop.sh` | On the paths D5 selects, the "from" binaries in the sandbox are those `install.sh` installed from the loopback server. Every check the loop already prints still passes, including the self-update, the Squad install unless `--skip-squad` is passed, and the KyberDash cases unless `--no-kyberdash` is passed. | No unit RED. The missing loopback install is already RED under T1. Before T3, the default `working` path extracts with `tar` inside `stage_from_release_tree` and never runs `install.sh`. | `./scripts/update-loop.sh` exits 0. The run log shows `install.sh` performed the D5-selected stage. Existing PASS lines are still present. Needs `node` and `npm` on PATH when KyberDash is included, as [distribution.md](../distribution.md) already states. |
| T4 | No product test. | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .` and `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | [install.md](../install.md) documents the answered variable and the loopback limit. [distribution.md](../distribution.md) stops describing the installer half of the loop as waiting on issue #125, and describes the staging path D5 selected. | No product RED. | Both commands exit 0. `docs validate . --merge-ready` stays expected to fail with `KW-DOC-LIFECYCLE-003` until T5 archives this plan. |
| T5 | No test. Closeout. | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .` | This plan is archived and the index no longer lists it as active. Canonical pages match the shipped behavior. | No RED. | `--merge-ready` exits 0 because this file is no longer under `docs/plans/`. Drift exits 0. |

Changing an assertion in this table, including weakening one to reach green, returns the plan to Draft and needs the conductor to relay reapproval.

## Tasks

### T1 — RED: pin the installer against the loopback server

- **Objective:** Add the failing `ReleaseTests` facts in the Test contract before any edit to `scripts/install.sh`.
- **Files / symbols:** `tests/KyberWeave.Tests/ReleaseTests.cs`. The facts drive `scripts/install.sh` and `scripts/local-release-server.py`. They use the `ReleaseTests` shell harness and must not set `KYBER_WEAVE_INSTALL_LIB` on the full-install facts.
- **Acceptance:** The new facts fail for the reason in the Test contract. Existing facts in the class still pass. The expected variable, hosts, and transport match answered D1, D2, and D3 only.
- **Depends on:** D1, D2, and D3 `ANSWERED`. No task output.
- **Required skill:** `test-dev`

### T2 — GREEN: honour the override in install.sh

- **Objective:** Make T1 pass. Resolve the three endpoints from the D1 variable, reject origins D2 rejects, and apply the D3 transport split in both `fetch` and `fetch_stdout`.
- **Files / symbols:** `scripts/install.sh`. Endpoint constants. `fetch`. `fetch_stdout`. The helpers must be defined before the `KYBER_WEAVE_INSTALL_LIB` return when T1 calls them that way. `verify_and_extract` must use `kyber_weave_verify_checksum` so the live install and `ReleaseTests` do not keep two awk parsers. `install_binary` stays the `cp`-then-`mv` replace.
- **Acceptance:** T1 GREEN. Unset and non-loopback values still refuse non-HTTPS URLs. A corrupted asset fails the checksum comparison. `ReleaseOrigin` and `ReleaseOriginTests` are unchanged.
- **Depends on:** T1 RED evidence. D1, D2, D3 `ANSWERED`.
- **Required skill:** `test-dev` re-runs the contract. No specialist skill in the inventory covers POSIX `sh`. The conductor assigns the script edit directly.

### T3 — Stage the loop through install.sh

- **Objective:** On the paths D5 selects, replace archive extraction or `cp` with `install.sh --install-dir "$BIN" --version <from>` after the loopback server answers `/healthz`.
- **Files / symbols:** `scripts/update-loop.sh`. `stage_from_release_tree`. `stage_from_directory`. The server start that exports the D1 variable. Do not change `scripts/local-release-server.py` or `scripts/release-local.sh`.
- **Acceptance:** Test-contract row T3. `--from installed` changes only if D5-B is the answer.
- **Depends on:** T2. D5 `ANSWERED`. D1 `ANSWERED` (the export name).
- **Required skill:** `test-dev` for the GREEN re-run of T1 if the loop task touches shared fixtures. No specialist skill covers this shell script. The conductor assigns the edit directly.

### T4 — Document the override and the loop

- **Objective:** Update the canonical Distribution pages so they describe the answered behavior.
- **Files / symbols:** `docs/install.md` options table. `docs/distribution.md` section "Verifying a release locally", including the sentence that issue #125 tracks the installer gap.
- **Acceptance:** Test-contract row T4. The pages name the answered variable and the D5 staging path. They do not document `dash/src/install/origin.ts` as loopback-checked unless D4-A was implemented.
- **Depends on:** T2 and T3, so the pages describe code that exists.
- **Required skill:** `kyber-weave-docs`

### T5 — docs-dev closeout

- **Objective:** After review, archive this plan and harvest any durable rule into the canonical pages. T4 may already hold that prose.
- **Files / symbols:** this file, `docs/plans/README.md`, and `docs/archive/plans/` as the archive destination the index already describes.
- **Acceptance:** Test-contract row T5.
- **Depends on:** Review approval. T4.
- **Required skill:** `kyber-weave-docs`

No task edits `dash/src/install/origin.ts` while D4 is open.

## Dependency graph and MAX_CONCURRENCY

```text
D1 ─┬─> T1 ─> T2 ─> T3 ─> T4 ─> review ─> T5
D2 ─┤              ^
D3 ─┘              │
D5 ────────────────┘
D4  (no task unless answered D4-A, which reopens Draft)
```

`MAX_CONCURRENCY: 1`

Audit: T1 and T2 are a RED-then-GREEN pair on one behavior, and T2 consumes T1's failing tests. T3 consumes a working `install.sh` from T2 and the D5 path list. T4 consumes the finished seam so the docs do not describe an unapproved option. T1 is the only writer of `tests/KyberWeave.Tests/ReleaseTests.cs` among these tasks. T2 is the only writer of `scripts/install.sh`. T3 is the only writer of `scripts/update-loop.sh`. T4 is the only writer of the two Distribution pages. Those file scopes do not overlap, but the RED/GREEN and "docs describe shipped behavior" edges still serialize them. D1–D4 are independent of each other and are the parallel batch in this handoff. D5 depends on D1 and is not in that batch.

## Risks

- D2-A rejects loopback addresses in `127.0.0.0/8` other than `127.0.0.1`. The local server binds `127.0.0.1`, so the loop is unaffected. Callers using another `127.*` host will be refused.
- D3-C means an `http` override requires curl. `update-loop.sh` already does. A machine whose only downloader is wget cannot use an `http` origin.
- `dash/src/install/origin.ts` will keep accepting a non-loopback `KYBER_WEAVE_RELEASE_ORIGIN` if D1-A and D4-B are both answered. The loop already exports that variable. This plan must not claim the variable is safe for every consumer.
- `verify_and_extract` and `kyber_weave_verify_checksum` can drift until T2 makes the live path call the helper. T1's corrupted-asset fact is what stops a green helper test from hiding a second parser.
- `./scripts/update-loop.sh` publishes single-file binaries and, unless `--no-kyberdash` is set, builds KyberDash. It is the integration proof, not the RED cycle.
- `docs validate . --merge-ready` fails with `KW-DOC-LIFECYCLE-003` while this file remains in `docs/plans/`. That is the merge gate described in the plan index, not a defect in this Draft.

## Out of scope

- Changing `ReleaseOrigin`, `ReleaseOriginTests`, `ChecksumVerifier`, or `BinaryInstaller.Replace`.
- Changing `scripts/local-release-server.py` or `scripts/release-local.sh`.
- `dash/src/install/origin.ts`, unless D4 is answered D4-A and the Test contract is extended first.
- `npm/lib/download.js`, which has its own HTTPS-only `RELEASE_BASE` and is not the documented first-install channel.
- Windows `.zip` installs. `install.sh` already refuses Windows.
- Cross-RID loop builds. [distribution.md](../distribution.md) already leaves those out of reach.
- Any new `KW-*` rule id.

## Verification gates

Before review, in addition to the Test contract:

- `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseTests`
- `./scripts/update-loop.sh` (the distribution loop required for an `install.sh` change)
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .`
- `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`

The repository gate suite (`review gates`) runs at review. This change does not touch the self-updater's C# replace path. It does touch `install.sh` and the loop, so the loop command above is required. `docs validate . --merge-ready` is the T5 gate, not a gate this Draft can pass.

## Review

Review follows the repository review council after T1–T4 are green. The reviewer checks the answered ledger against the diff: the variable name, the host check, the curl and wget split, the D5 staging paths, and that dash was left untouched unless D4-A was approved and re-contracted. A failing checksum must be the reason a corrupted asset is refused.

## docs-dev closeout

T5 is the closeout. `kyber-weave-docs` verifies the evidence, confirms [install.md](../install.md) and [distribution.md](../distribution.md) carry the shipped rule, archives this plan under `docs/archive/plans/`, and updates [docs/plans/README.md](README.md) so the active list no longer links it. No ADR is expected unless an answer chooses a constraint the canonical pages cannot hold. Closeout then re-runs `docs validate . --merge-ready` and `docs drift .`.
