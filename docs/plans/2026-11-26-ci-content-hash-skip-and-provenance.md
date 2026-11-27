---
id: plans/2026-11-26-ci-content-hash-skip-and-provenance
title: CI content-hash skip receipts and provenance
doc-type: plan
status: current
component: CI Pipelines
owner: dpalfery
last-reviewed: 2026-11-26
development-mode: test-first
documentation-impact: extends-component
impact-components:
  - CI Pipelines
---

# CI content-hash skip receipts and provenance

**Status: Review required, 2026-11-26.** Autonomy: supervised. The owner approved the plan at the
approve-and-execute gate, all three tasks are committed and integrated, and the closeout harvest is
complete; the review council runs next and the archive step follows it.

## Problem and goal

GitHub Actions re-runs every job of every workflow on every commit, even when the exact inputs a
job consumes are byte-identical to inputs a previous run already proved green, and GitHub links
no evidence from that previous run to this one. This plan adds a content-hash receipt to the
repository's CI and release jobs: each participating job fingerprints its inputs with
`hashFiles()`, looks up a tiny JSON receipt under that fingerprint in `actions/cache`, skips its
heavy steps and displays the prior run's URL on a hit — a fresh hit only: a receipt older than its
maximum age is a miss and the job runs in full (D16) — and on a miss runs, attests its build
products, and saves a receipt naming the run that proved the inputs.

The workflows covered are the repository's actual set, enumerated from the tree — not assumed:

- `.github/workflows/ci.yml` (17 jobs; the pull-request and push CI workflow)
- `.github/workflows/release.yml` (12 jobs; the release workflow)
- `.github/workflows/pr-naming-recheck.yml` (1 job)
- `.github/workflows/gate-catalogue-toolchains.yml` (1 job)

**Eligibility.** Not every job skips. A job is skip-eligible only when all three hold:

1. **Full input coverage** — every input that can change the job's verdict is inside the
   fingerprint. Jobs whose tests and gates read anywhere in the repository hash the whole tree
   (minus `.git`); jobs with a provably narrower input universe hash exactly that set. Every set
   includes the workflow file itself, so any workflow edit invalidates every fingerprint.
2. **No consumed per-run output** — nothing outside the job consumes an output that exists only in
   this run (an artifact a sibling job downloads, a publish, a release). The release build chain is
   the one sanctioned exception: it skips as one unit and the publish jobs verify the published
   state instead of re-publishing (D10).
3. **Same inputs, same verdict within the receipt's maximum age** — re-running with identical
   inputs and the pinned toolchains cannot produce a different verdict. Verdicts that drift with
   time — the security scans, whose rules and vulnerability databases change daily — are eligible
   only under the freshness ceiling (D8, D16): their receipts are honoured for four hours and no
   longer.

**Freshness (D16).** A cache hit is only a hit when the restored receipt is within its maximum
age: four hours (`14400` seconds) in both workflows, one uniform value the owner set for CI and,
at the gate-riding decision, chose for the release chain too, over a seven-day alternative. The
age is decided in the verdict step
from the restored receipt's `saved_at_epoch`, never from the cache key, which carries only the
content hash; a stale receipt is a miss whose summary line names the receipt's age and the
ceiling. A fork pull request restores nothing, so it is a miss regardless of age.

**Job inventory and disposition.**

| Workflow | Job | Disposition | Fingerprint input set |
|---|---|---|---|
| ci.yml | `changes`, `pr-naming`, `ci-summary` | Excluded — seconds of runtime, outputs consumed by other jobs, or a verdict on attacker-controlled text (the PR title) that no fingerprint covers | — |
| ci.yml | `codeql` (leg `${{ matrix.language }}`), `trivy`, `semgrep`, `gitleaks` | Eligible under the freshness ceiling (D8, D16): they skip only within four hours of the receipt; a stale receipt is a miss and the scan runs | `W` — the scans read the whole tree |
| ci.yml | `build-test` | Eligible | `W` (whole tree) |
| ci.yml | `integration-test` | Eligible, carrying the accepted AI-graded residual (D20) | `W` |
| ci.yml | `squad-filesystem-contract` | Eligible | `W` |
| ci.yml | `sbom` | Eligible | `S` |
| ci.yml | `publish-smoke` | Eligible | `W` |
| ci.yml | `update-loop` | Eligible | `W` |
| ci.yml | `skill-docs-gate` | Eligible | `W` |
| ci.yml | `ts-gates` | Eligible | `D` |
| ci.yml | `dash-e2e` | Eligible | `D` |
| ci.yml | `tray-rust` | Eligible | `T` |
| release.yml | `authorize`, `version`, `approve` | Excluded — authorization, version resolution and the human approval gate; no build minutes | — |
| release.yml | `build`, `build-kyberdash`, `pack-squad`, `build-tray`, `sbom`, `artifact-sboms`, `attest-sbom` | Eligible as one chain unit | the resolved version alone, in one shared chain key (`release-chain-<version>`) |
| release.yml | `release`, `gh-packages` | Verify-on-hit — never skip their verification; on a chain hit they verify the published state instead of re-publishing | chain key |
| pr-naming-recheck.yml | `recheck` | Excluded — one API POST that re-runs CI's own naming job (out-of-scope boundary) | — |
| gate-catalogue-toolchains.yml | `gate-catalogue-toolchains` | Excluded — the workflow's whole value is executing real toolchains against fixtures (out-of-scope boundary) | — |

**Fingerprint sets** (the exact `hashFiles()` arguments):

- `W` (the whole tracked tree — every top-level entry of the tracked tree, enumerated exactly; a
  repository-policy test enumerates the tracked top-level entries from the corpus and fails when
  one is missing from this list, so the set cannot rot as the tree grows). No bare `'*'` or `'.*'`
  glob appears, and neither does `hashFiles('**')`: the runner's expansion of those patterns
  against `.git` cannot be observed from a static test, so every directory is listed as `<dir>/**`
  and every root file by exact name (D15). At the tracked tree of revision `1405762e` the literal
  is:
  `hashFiles('.agents/**','.apm/**','.claude/**','.codex/**','.commandcode/**','.cursor/**','.devin/**','.factory/**','.github/**','.kilo/**','.kyber-weave/**','.opencode/**','.pi/**','.playwright-cli/**','.vscode/**','dash/**','docs/**','homebrew/**','npm/**','products/**','samples/**','scripts/**','src/**','templates/**','tests/**','.editorconfig','.gitattributes','.gitignore','.gitleaks.toml','.mcp.json','AGENTS.md','CLAUDE.md','CODE_OF_CONDUCT.md','CONTRIBUTING.md','Directory.Build.props','HANDOVER-WAVE4.md','HANDOVER.md','KyberWeave.Unit.slnf','KyberWeave.sln','KyberWeave.sln.DotSettings','LICENSE','NOTICE','README.md','REFACTOR-LAYERS.md','REVIEW.md','SECURITY.md','apm.yml','global.json','kilo.json')`
  `.git` is not tracked, so "every tracked top-level entry" needs no exception. The enumeration
  came from `git ls-tree HEAD`, not from a directory listing that could include build output.
- `S` (the `changes` job's own declared SBOM inputs, each alternation of its regex covered —
  `Directory\..*\.props` is unanchored, so `**/Directory.*.props` matches its scope at any depth):
  `hashFiles('**/package-lock.json','**/Cargo.lock','**/packages.lock.json','**/*.csproj','**/Directory.*.props','global.json','scripts/generate-sbom.sh','scripts/validate-sbom.py','scripts/syft-bootstrap.sh','dash/tray/vendor/**','.github/workflows/release.yml','.github/workflows/ci.yml')`
- `D` (dash — `ts-gates`, `dash-e2e`):
  `hashFiles('dash/**','global.json','Directory.Build.props','.github/workflows/ci.yml')` —
  `dash-e2e` builds `dash/server/KyberDash.sln` with the .NET SDK that root `global.json` pins and
  under the root `Directory.Build.props` (the only props file in the tree, verified), so both root
  files are inputs to its verdict; `ts-gates` is npm-only, and sharing the one dash key costs it a
  harmlessly wider set.
- `T` (tray): `hashFiles('dash/tray/**','dash/.nvmrc','.github/workflows/ci.yml')` — the tray's
  Linux UI build resolves Node from `dash/.nvmrc`, which sits outside `dash/tray/` and so outside
  the tray set's own directory.
- Release chain: the resolved version from the `version` job, and nothing else — the chain key is
  `release-chain-<version>`, with no tree hash. The chain computes no `hashFiles()` value at all:
  the `version` job refuses a tag that already exists, so only a re-run of the same run can hit.

The `D` and `T` sets follow the repository's own declared model: the `changes` job already treats
`dash/` as the input universe for the dash jobs (a pull request touching nothing under `dash/`
does not run them at all). The fingerprint is exactly as sound as that existing filter; it is not
weaker. A shared workflow file changes every fingerprint: `W` contains `.github/**`, and `D`, `T`
and `S` append the workflow file explicitly. A shared build script changes `W` and `S` (both hash
`scripts/**` or the specific scripts); the dash jobs run no repository script outside `dash/`, so
none is hashed for them.

**Cache keys and scope.** Key format: `receipt-<job-key>-<leg>-<input-hash>`, where `<leg>` is
`${{ matrix.rid }}` or `${{ matrix.os }}` for matrix jobs and `${{ runner.os }}` otherwise — a
macOS leg never satisfies a Windows leg's receipt. The release chain uses one shared key,
`release-chain-<version>`, with no leg and no tree hash: the chain skips or runs as a unit. Restore
is exact-key only, **no `restore-keys`**: a partial restore would restore the wrong run's receipt.
The key carries the content hash only, so it can never carry age: freshness is decided from the
restored receipt's `saved_at_epoch` in the verdict step (D16).
Receipts restore in every event; they save only from same-repository runs
(`github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository`),
so pushes to main, the schedule and dispatches populate the default-branch scope, which every
workflow and branch in the repository can read; a same-repo pull request's saves land inside that
pull request's own scope, readable only by its later runs — pull requests benefit from main's
receipts and refresh their own, never main's. Forks save nothing.

**Receipt data model.** One JSON file per job, `.receipt/receipt.json` in the workspace, written by
the job's own bash with `jq` from values passed through `env:` (the repository rule — `${{ github.* }}`
never appears inside a `run:` block). No repository script is added: the GitHub Actions standard
does not call for one and every existing gate step in these workflows inlines its shell.

| Field | Type | Nullable | Meaning |
|---|---|---|---|
| `schema` | string | no | Always `"1"` |
| `job` | string | no | The job's display `name:` (the API's only stable job identifier) |
| `input_hash` | string | no | The `hashFiles()` fingerprint this receipt is keyed under |
| `commit_sha` | string | no | `github.sha` of the run that saved it |
| `run_id` | string | no | `github.run_id` |
| `run_url` | string | no | `${{ github.server_url }}/${{ github.repository }}/actions/runs/<run_id>` |
| `artifact_digests` | object | yes | Artifact name → SHA-256 of the job's uploaded artifacts; `null` when the job produces none |
| `attested` | boolean | no | Whether a provenance attestation was created for those digests |
| `saved_at` | string | no | ISO-8601 UTC timestamp, what the summary displays |
| `saved_at_epoch` | string | no | The same instant as epoch seconds — the value the verdict step compares against the ceiling, so no runner's `date` dialect is parsed |
| `assets` | array | yes | Release-chain receipts only: sorted list of published archive names (excluding `SHA256SUMS.txt`); the manifest text itself is stored in `sha256sums` |

The release-chain receipt exists only after the `release` job verified the published release, so a
hit always names a fully published, verified release.

**Maximum ages (D16).** Both workflows set the same top-level `env:` value,
`RECEIPT_MAX_AGE_SECONDS: "14400"` (four hours) — one uniform ceiling across every skip-eligible
job in `ci.yml` (the four security scans included) and the whole release chain. The verdict step
reads the workflow's current value, so a ceiling can be tightened without touching any key. The
owner chose this knowing what it gives up: a seven-day release ceiling was on the table and was
declined, so a release re-run that happens more than four hours after the failed attempt — the
re-run-the-next-morning case, the chain-skip's main win — rebuilds the whole chain instead of
skipping it. That is the owner's decision, recorded here and in Risks, not an oversight.

**What a chain hit re-checks and what it carries (D19).** The hit path re-verifies the published
asset inventory, the published `SHA256SUMS.txt` by byte-compare, and both attestation types for
every archive — the CycloneDX SBOM predicate and the SLSA provenance predicate, via `gh
attestation verify`. It does not
re-run the macOS `codesign`/`spctl`/`stapler` assertions of the skipped `build-tray` legs: the
publishing run ran those assertions on exactly the bytes it published, and the hit path's manifest
byte-compare proves the published bytes are still exactly those, so that verification is carried by
byte-identity. `attest-sbom` skips with the chain on a hit — the hitting run builds nothing, so
there is nothing to attest — and the hit path verifies the attestations the publishing run
minted. "A hit always names a fully published, verified release" means exactly this scope.

**Step pattern inside each eligible job** (no new jobs anywhere — `CiAlwaysRunJobsTests` pins the
comment above `changes` to name every job with no `needs:`/`if:`, and `pr-naming-recheck.yml`
joins CI jobs by display name; adding jobs or renaming anything would break both). Every receipt
step declares `shell: bash` (D18): three eligible jobs have windows-latest legs whose default
shell is pwsh, where `date +%s` and the verdict step's bash arithmetic fail, and `ci.yml` already
sets `shell: bash` on its Windows tray steps:

1. `Compute input fingerprint` (`id: fp`) — writes the `hashFiles()` value to `$GITHUB_OUTPUT`.
2. `Look up the receipt` (`id: receipt`) — `actions/cache/restore` at the repository's existing
   `actions/cache@55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6.1.0` pin, `path: .receipt`, the key
   above, no restore-keys.
3. `Decide the verdict` (`id: verdict`) — always runs, and it is where freshness is enforced. It
   reads the restored receipt's `saved_at_epoch`, computes its age, and compares it against
   `env.RECEIPT_MAX_AGE_SECONDS`. `skip` is set only on a cache hit within the ceiling. A fresh
   hit writes the restored receipt to `$GITHUB_STEP_SUMMARY` and sets the job's outputs
   (`prior_run_url`, `receipt_hit`). A stale hit is a miss: the summary names the receipt's age
   and the ceiling ("receipt found but 6 hours old; ceiling is 4 hours — running in full"). A
   plain miss writes "no prior receipt; running in full", and on a fork pull request that line
   names the cause: fork pull requests cannot read the base repository's caches (D5). A restored
   receipt is validated before it is trusted (D18): a missing, unparsable, non-numeric or future
   `saved_at_epoch` — a future stamp would compute a negative age, always fresh — is a miss with
   its own summary line, and a receipt whose `input_hash` differs from the fingerprint step's
   `hash` is a miss too.
4. The heavy steps, each carrying `if: steps.verdict.outputs.skip != 'true'` composed with their
   existing conditions — the user's own recommendation, and the only shape that keeps `CI
   Summary` (the required check) green while a job reports a carried-over verdict. No step can
   skip before the verdict step has run: age is settled there, once, and never asked of the cache
   key.
5. `Digest and attest` (miss path, same-repo guard) — computes SHA-256 digests of the uploaded
   artifacts; artifact-producing jobs attest their build products with `actions/attest` in
   provenance mode (D7, as the owner approved), each attestation carrying `subject-path` naming
   the product and no `sbom-path`, so an SBOM-mode attestation cannot satisfy it; test reports
   and SARIF analyses are recorded as digests only.
6. `Save the receipt` (miss path only, `success()`, same-repo guard — never on the hit path, so a
   hit never refreshes `saved_at_epoch` and the ceiling always expires) — writes the JSON and
   calls `actions/cache/save` under `continue-on-error: true` (D18): a cache-reserve race with a
   concurrent run over identical inputs must never fail a green job, and a failed save fails open
   to the next run re-executing.

**Fork pull requests.** A pull request from a fork cannot read the base repository's caches, so
the lookup always misses and every eligible job runs in full; the fork's save and attest steps are
skipped by the same-repo guard so nothing is written that no later run can read. The job summary
states this in plain words. Fork behaviour is therefore identical to today's CI, plus one summary
line — no soundness change and no new fork-code execution (the restore/save/record steps run no
repository code). Age plays no part in the fork case: nothing restores, so the verdict step
reports a miss before freshness applies, and the same-repo guard on save and attest stays
independent of the age rule.

**Partial failure and rollback.** Receipts are saved only by jobs that succeeded, so a re-run
re-executes exactly the jobs that failed and skips the ones that passed. A release half-published
(the prior run failed between publishing steps) never has a chain receipt, so the re-run
re-publishes. A published release later deleted by hand makes the verify-on-hit path fail closed
on the missing assets. Cache eviction (7-day idle or quota pressure) simply misses and runs in
full: the mechanism fails open to running, never to skipping. A receipt past its maximum age is
the same kind of miss, with its own summary line naming the age and the ceiling.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| Fingerprint job inputs with `hashFiles()` in a fast gate step | Problem and goal, fingerprint sets; T1, T2 | Every eligible job gains a `Compute input fingerprint` step; the exact `hashFiles()` arguments are listed per job in the inventory table |
| Look up a cross-workflow receipt with `actions/cache/restore` keyed `receipt-<job-name>-<hash>`; default-branch caches are readable across workflows and branches | Cache keys and scope; T1, T2 | Exact key format and the pinned action; restore-only default-branch scope is what makes the receipt cross-workflow |
| A receipt older than four hours is not a hit — the job re-runs ("if the hash is more than 4 hours old then rerun, if not call it good") | D16; freshness paragraph; step pattern 3; T1/T2 test rows | The verdict step compares the restored receipt's `saved_at_epoch` against `RECEIPT_MAX_AGE_SECONDS` (`14400` in ci.yml); a stale receipt is a miss whose summary names the age and the ceiling; both workflows carry the same `14400` value (AP2 resolved to the uniform four hours) |
| On a hit, skip heavy steps via `if:` conditions and write the cached JSON receipt (run_id, run_url, commit_sha, artifact digest) to `$GITHUB_STEP_SUMMARY` and job outputs | Step pattern 3–4; receipt data model; T1, T2 | The `Decide the verdict` step writes the receipt to the summary and sets `prior_run_url`/`receipt_hit` outputs; heavy steps gate on `steps.verdict.outputs.skip`, which is set only on a hit within the maximum age |
| On a miss, run the build, generate signed SLSA provenance, save a tiny JSON receipt to `actions/cache/save` under the input hash, well within the 10 GB cache limit and 7-day retention | Step pattern 5–6; D6, D7; T1, T2 | Attestation step per artifact-producing job (`actions/attest` in provenance mode, D7 as the owner approved), save step with the same-repo guard; receipts are one small file per key |
| Gate the heavy steps inside the job rather than `jobs.<id>.if`, so required checks stay green and the prior run_url shows in the job summary | D1; step pattern 4 | No job-level `if:` is added anywhere; `Ci Summary` keeps its `needs` set intact |
| Cover all our jobs: pull-request and push CI, and the release job | Job inventory (enumerated from the tree, all four workflows); D2, D8, D9, D10, D16 | Every one of the 31 jobs has an explicit disposition; the four security scans are eligible under the four-hour freshness ceiling, and each remaining exclusion carries its reason |
| Supervised mode; review and approve the plan before development | Status line; AP1, AP2 | Q1–Q3 are answered, and both gate-riding items are resolved (AP2 to the uniform `14400`, D20 accepted); the approve-and-execute gate itself is still pending, and no task is dispatched before it |
| Conform to the GitHub Actions standard; surface any conflict as a Decision row | D1, D6, D7, D12, D14, D15, D16 | Conflicts (attestation action choice, `hashFiles('**')` risk, id-token placement, the freshness design) are decision rows, not silent choices |
| Name the existing tests that cover workflow files and the commands a worker runs | Investigation findings; Test contract; task Commands | `CiAlwaysRunJobsTests` and the classes reading `ci.yml`/`release.yml` are named with filtered commands |
| State per job the concrete input path set, and how the fingerprint moves when a shared workflow file or build script changes | Fingerprint sets paragraph; D17 | `W`/`S`/`D`/`T` are spelled out with the review-corrected inputs (the full tracked top-level enumeration, `global.json` and `Directory.Build.props` in `D`, `dash/.nvmrc` in `T`, `**/Directory.*.props` in `S`); the shared-file paragraph answers the change semantics |
| Address the branch-protection soundness risk with a stated policy | D2 (the three eligibility conditions) and D16 (the freshness ceiling) | A required check can only carry a skip when the fingerprint covers the job's whole input universe, the verdict is input-determined, and the receipt is within its maximum age |
| Address attestation for jobs that produce no artifacts | D7 | `artifact_digests: null`, `attested: false`; digest-only for reports and SARIF |
| Address fork pull requests | D5; fork paragraph | Expected behaviour and the summary line are stated |
| State where the receipt JSON lives, its fields, and that the workflow's own shell writes it | Receipt data model; D6 | `.receipt/receipt.json`, field table, bash + `jq` via `env:` |
| Documentation-impact declaration and Design harvest table; name the durable home and the ADR disposition | Scale; Design harvest | Runbook + standard + one new ADR |
| Persist `development-mode: test-first` and `autonomy: supervised` | Frontmatter (`development-mode`); Status line (autonomy) | Both are recorded |
| Rate every file-changing task with `Difficulty:` and exactly one `Technology:` | Tasks | T1 `hard`/github-actions, T2 `hard`/github-actions, T3 `easy`/github-actions |
| Run `kyber-weave docs tasks --write` then `--check`, then the two docs checks, before `PLAN_READY` | Finalization | The task file is written by the command, never by hand, and both docs checks are run in the worktree |

## Scale

`documentation-impact: extends-component`, `impact-components: [CI Pipelines]`. The change extends
an existing component's behaviour (CI workflows and their runbook); it creates no new component.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Skip at job level or step level? | Step-level `if:` on the heavy steps inside the job; no `jobs.<id>.if` is added anywhere | The user's own recommendation. `CI Summary` is the only required check and reads every job through `needs`; a step-level skip keeps it green and honest, while a job-level skip would remove the job's verdict from the run entirely. Also keeps `CiAlwaysRunJobsTests`'s contract (no job gains `needs:`/`if:`) and the `pr-naming-recheck.yml` name joins untouched | ANSWERED — explicit user instruction |
| D2 | Which jobs may skip (the soundness policy)? | A job skips only when (1) its complete input set is inside the fingerprint (whole-tree `W` for any job that reads anywhere), (2) nothing outside the job consumes a per-run output — the release chain being the one sanctioned unit exception with verify-on-hit publishes (D10), and (3) identical inputs plus the pinned toolchains cannot yield a different verdict — with one accepted strain on (3): the AI-graded `ProseRuleGradingTests` inside `integration-test` can change a verdict with no tree change (grader outage, confidence floor, secret presence), recorded as an accepted residual (D20) | This is the answer to the required-check soundness concern: a green `CI Summary` that skipped a job is sound exactly when the fingerprint covers the job's whole input universe and the verdict is a function of those inputs. Rejected: a required-check naming convention (it hides skips from the check list rather than making them sound) and per-job independent release skips (a skipped build leaves the publish jobs nothing to publish) | ANSWERED — design, for approval with the plan |
| D3 | What exactly does each job hash? | The per-job table above: `W` for jobs that read anywhere, `S`/`D`/`T` for jobs with the repository's own declared narrower input universes | The user asked for per-job input sets, not one global hash. `S` is the `changes` job's own SBOM regex as globs; `D`/`T` follow the existing `changes` filter's model that `dash/` is those jobs' input universe. Every set includes the workflow file, so a workflow edit re-runs everything, matching the `changes` job's `all()` rule for `ci.yml` edits | ANSWERED — design, for approval with the plan |
| D4 | Where are receipts stored and who may save them? | `actions/cache` under `receipt-<job-key>-<leg>-<input-hash>`; restore everywhere, save only from same-repository runs | Caches saved on the default branch are readable across all workflows and branches in the repository, which is what makes the receipt cross-workflow; caches saved in a pull request are readable only by that pull request's later runs — which is exactly the flaky-rerun win. A repository variable was rejected: variable names cannot be created per-hash without a PAT and a write path the repository forbids | ANSWERED — design, for approval with the plan |
| D5 | How do fork pull requests behave? | Restore always misses (forks cannot read the base repository's caches); every eligible job runs in full; save and attest steps carry the same-repo guard; the summary states the cause in plain words | GitHub-determined, not a choice. The guard also avoids writing cache entries no later run can read. Identical to today's CI plus one summary line; no new fork-code execution | ANSWERED — platform behaviour, documented |
| D6 | Receipt file: where, what fields, who writes it? | `.receipt/receipt.json`, the field table above, written by the workflow's own bash with `jq`, values through `env:` | The GitHub Actions standard calls for no repository script for this and every gate step in these workflows inlines its shell; a shared script would also need `shellcheck` and its own policy test for no gain. The untrusted-input rule (`${{ github.* }}` never inside `run:`) is pinned by the new policy tests | ANSWERED — standard conformance |
| D7 | How is provenance attested, including for jobs with no artifacts? | Artifact-producing jobs attest their **build products** with `actions/attest` in provenance mode on the miss path (same-repo guard), reusing the repository's existing `actions/attest@1e69f48… # v4.2.2` pin; test reports and SARIF analyses are recorded as digests only; no-artifact jobs carry `artifact_digests: null` and `attested: false`, their evidence being the authenticated run URL under a cache key only this repository's workflows can write | The request named `actions/attest-build-provenance`; the repository already pins and reviews `actions/attest` in `release.yml`'s `attest-sbom` job, and it produces the same signed SLSA provenance through the same API, so the owner's answer adopts the reuse instead of adding a second attestation action | ANSWERED — owner decision, supervised gate (Q3, 2026-11-26) |
| D8 | Do the security scans (`codeql`, `trivy`, `semgrep`, `gitleaks`) skip too? | Yes — skip-eligible under the four-hour freshness ceiling, hashing `W` (the scans read the whole tree); a receipt past the ceiling is a miss and the scan runs in full | The owner rejected the blanket exclusion in favour of a time limit: "if the hash is more than 4 hours old then rerun, if not call it good". The residual is real, stated in Risks, and accepted as the owner's tradeoff: scanner rules and vulnerability databases can change inside the four-hour window, and a skipped scan uploads no SARIF, so code scanning's diff against main sees no new analysis during the skip window. The ceiling bounds both exposures | ANSWERED — owner decision, supervised gate (Q1, 2026-11-26) |
| D9 | What about the control and auxiliary jobs? | Excluded with reasons: `changes`, `pr-naming`, `ci-summary`, `authorize`, `version`, `approve` (seconds of runtime, outputs consumed downstream, or verdicts on text no fingerprint covers); `pr-naming-recheck.yml` (one API POST); `gate-catalogue-toolchains.yml` (its value is executing real toolchains) | No build minutes are saved by skipping any of these; adding receipt plumbing to them would be complexity with no benefit | ANSWERED — design, for approval with the plan |
| D10 | How does the release workflow skip? | One chain fingerprint, the resolved version alone — the shipped key is `release-chain-<version>`, with no tree hash or tag (the `version` job refuses a tag that already exists, so only a re-run of the same run can hit); all build and SBOM jobs skip as a unit on a hit — `attest-sbom` included, because the hitting run builds nothing to attest; `release` and `gh-packages` never skip verification — on a hit they re-check the asset inventory, the published manifest by byte-compare, and both attestation types per archive against the chain receipt's recorded digests, and report the prior `run_url` | The chain receipt is written only by the `release` job after it verified the published release, so a hit always names a fully published release — where "verified" is exactly the D19 scope: re-checked on the hit path are inventory, manifest and both attestation predicates; carried by byte-identity are the macOS signature and notarization assertions the publishing run ran on those bytes. The rejected alternative was receipts for evidence only, no release skipping — simpler, but a re-run after a flaky macOS notarization failure then rebuilds the whole RID matrix for nothing. The chain's receipt honours the same four-hour ceiling as CI (D16) | ANSWERED — owner decision, supervised gate (Q2, 2026-11-26); claim narrowed per review |
| D11 | How is the skip surfaced? | The skipping job writes the receipt JSON to `$GITHUB_STEP_SUMMARY` and sets job outputs (`prior_run_url`, `receipt_hit`); `ci-summary` is unchanged | The user's instruction. `CI Summary` keeps its current table; each job's own summary carries the prior run, so the aggregator does not grow a second copy of every job's state | ANSWERED — explicit user instruction |
| D12 | Where are the new invariants pinned? | New repository-policy test classes (`CiContentHashSkipTests`, `ReleaseReceiptSkipTests`) in `tests/KyberWeave.RepoPolicy.Tests`; the GitHub Actions standard gains the policy rules in the same change | The standard is explicit: "A new structural invariant gets a test there rather than a paragraph here" — both happen together, test first | ANSWERED — standard conformance |
| D13 | Is runner-image drift in the fingerprint? | No — accepted residual risk, now stated fully | `ubuntu-latest`/`macos-latest` float and cannot be hashed. The toolchains pinned in the tree are hashed (`global.json`, `dash/.nvmrc`, the action pins reviewed by Dependabot), but two float outside any hash and are accepted residuals bounded by the freshness ceilings: the tray's Rust `toolchain: stable` resolves to whatever stable ships when the job runs, and .NET resolves within its band under `rollForward: latestFeature`, so a patch release can change a verdict with no tree change. A hit therefore proves the same tree passed under the toolchains of the moment; it cannot prove the same runner image or the same floating toolchain | ANSWERED — accepted risk, recorded in Risks; extended per review |
| D14 | Any new jobs or renames? | None — receipt steps live inside existing jobs; no display name, job key or `needs` edge changes | `CiAlwaysRunJobsTests` pins the `changes` comment to job ids without `needs:`/`if:`; `pr-naming-recheck.yml` joins CI jobs by display `name:`; `ReleaseTests` and siblings pin release job structure. Adding jobs or renaming would break these for no benefit | ANSWERED — design |
| D15 | Why not `hashFiles('**')` or bare root globs for the whole tree? | Every tracked top-level entry enumerated exactly — directories as `<dir>/**`, root files by exact name — with no bare `'*'`/`'.*'` glob and no `hashFiles('**')`, pinned complete by a policy test that enumerates the tracked tree | The runner's expansion of `'*'`/`'.*'` against `.git` cannot be observed from a static test, so the plan asserts no glob semantics at all: the enumerated list — taken from `git ls-tree HEAD` at authoring time and kept complete by the corpus test — makes the answer irrelevant. The test and the plan's literal agree on the definition, a tracked top-level entry being a path with no `/`, so the set cannot rot as the tree grows and cannot silently re-key skips from commit-keyed to content-keyed or back | ANSWERED — design; root-file route chosen per review |
| D16 | How long is a receipt honoured — the freshness rule? | Every cache hit is checked for age before anything may skip. The verdict step compares the restored receipt's `saved_at_epoch` against the workflow's top-level `RECEIPT_MAX_AGE_SECONDS`: `14400` (four hours), one uniform value across every skip-eligible job in `ci.yml` — the four security scans included — and the whole release chain in `release.yml`. A stale receipt is a miss whose summary names the receipt's age and the ceiling; the age is never taken from the cache key; a fork pull request is a miss before freshness applies | One uniform value, not per-job values: the owner gave one number, and per-job tuning adds surface nobody asked for. A seven-day release ceiling was recommended to preserve the chain-skip's main win — a re-run the morning after a flaky macOS notarization failure — and the owner declined it at the gate-riding decision, accepting that a re-run more than four hours after the failed attempt rebuilds the whole chain; that tradeoff is recorded in Risks. The ceiling is read at verdict time from the workflow env, so it can be tightened without touching any key. Rejected: a purely content-keyed skip with no time bound — it would honour a scan receipt from last week | ANSWERED — owner decision, supervised gate (Q1 and AP2, 2026-11-26) |
| D17 | Fingerprint-set corrections forced by the review | `D` gains `global.json` and `Directory.Build.props` (`dash-e2e` builds `dash/server/KyberDash.sln` under both); `T` gains `dash/.nvmrc` (the tray's Node pin sits outside `dash/tray/`); `S` widens `Directory.*.props` to `**/Directory.*.props` (the `changes` job's regex is unanchored); `W` enumerates every tracked top-level entry exactly | Verified against the tracked tree at `1405762e` before the change: `git ls-tree HEAD` shows 25 top-level directories (including `.agents`, `.claude`, `.factory`, `homebrew`, `npm`, `samples`) and 24 root files (including `.gitleaks.toml`, `global.json`), exactly one `Directory.Build.props` in the tree, and the sbom regex unanchored on props. Each correction closes a gap where a receipt hit could skip a job over inputs it never fingerprinted, violating D2(1); the T1 test pins each named file inside its set's argument list | ANSWERED — review findings, verified against the tracked tree |
| D18 | Receipt-step hardening forced by the review | (a) The chain save is miss-only — no save step is reachable on a hit, so a hit never refreshes `saved_at_epoch`. (b) Every save step carries `continue-on-error: true`, the one sanctioned non-check exception to the standard's `continue-on-error` rule. (c) Every receipt step declares `shell: bash`. (d) The verdict step validates the restored receipt: a missing, unparsable, non-numeric or future `saved_at_epoch` is a miss, and `input_hash` must equal the fingerprint step's `hash` | (a) Without it the four-hour ceiling never expires and the chain skips indefinitely, defeating D16. (b) Cache keys are immutable, so a save racing a concurrent run over identical inputs errors on the reserve; a best-effort evidence write must never fail a green job, and its failure mode is fail-open to re-running — the standard's rule targets blocking checks, and T3 writes this sanctioned exception into the standard. (c) Three eligible jobs have windows legs whose pwsh default breaks `date +%s` and the verdict arithmetic. (d) A malformed receipt must be a quiet miss, not a noisy failure, and a future stamp must never count as always-fresh | ANSWERED — review findings |
| D19 | What does a release chain hit re-check, and what does it merely carry? | The hit path re-checks the asset inventory, the published manifest by byte-compare, and both attestation types per archive, and matches every published digest against the receipt. It does not re-run the macOS signature and notarization assertions, and `attest-sbom` skips with the chain (the hitting run builds nothing to attest); both are carried by byte-identity, because the publishing run verified signature and notarization on exactly the bytes the hit path digest-matches | The rejected shape — excluding `attest-sbom` from the chain skip so attestation always runs — is impossible on a hit: there are no artifacts in the hitting run to attest. The choice was therefore extending verification versus narrowing the claim, and the plan does both: the hit path verifies the publishing run's attestations, and D10's wording names exactly what the hit path re-checks | ANSWERED — review finding; design choice recorded |
| D20 | Is `integration-test` eligible despite its AI-graded tests? | Yes — eligible, with the residual recorded rather than hidden: `ProseRuleGradingTests` asks an external model (`TYPESAFE_API_KEY`, `KW_PROSE_GRADE`), and a grader outage, a confidence floor or the secret's absence changes the verdict with no tree change | The owner's four-hour ceiling bounds exactly this class of time-sensitive residual, so the job stays eligible under it: a skip never carries the AI-graded verdict across the ceiling. The outage behaviour itself is unchanged by this plan (skip-with-warning versus fail, as `ci.yml` documents). Accepted by the owner at the gate-riding decision (2026-11-26); the residual stays recorded here and in Risks | ANSWERED — owner decision, supervised gate (2026-11-26): the job stays eligible |
| AP1 | Approve and execute | Pending | Supervised run: Q1–Q3 are answered, and both gate-riding items are resolved — AP2 to the uniform `14400` (four hours) and D20 accepted (`integration-test` stays eligible). The approve-and-execute gate itself is still pending: no task is dispatched until the owner approves | ANSWERED: owner approval, Owner approved and execute at the approve-and-execute gate (supervised mode): scale extends-component on CI Pipelines; AP2 resolved to the uniform four-hour ceiling 14400 in both workflows and D20 accepted, both recorded as owner decisions., 2026-11-26 |
| AP2 | The release-chain ceiling | Resolved: `14400` (four hours) for `release.yml`, the same value as `ci.yml` — the owner chose the uniform ceiling over the recommended seven days | The tradeoff is accepted knowingly and stated in Risks: a release re-run more than four hours after the failed attempt rebuilds the whole chain, RID matrix included, instead of skipping on the chain receipt. The seven-day alternative was recommended to preserve the re-run-the-morning-after win and was declined | ANSWERED — owner decision, supervised gate (2026-11-26) |

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| CI Pipelines | runbook (`docs/ci-pipelines/workflows-runbook.md`) | Content-hash receipts and skips | The mechanism end to end: the step pattern, the per-job fingerprint sets and why each job's set is what it is, the cache key and scope rules with the corrected wording (a pull request's saves are PR-scoped; only default-branch runs populate the readable-everywhere scope), the freshness rule (the verdict step, `saved_at_epoch`, `RECEIPT_MAX_AGE_SECONDS` and its single uniform value), the receipt validation rule, the `shell: bash` declaration, fork behaviour, and the release chain's verify-on-hit shape with what it carries rather than re-checks |
| CI Pipelines | coding standard (`docs/standards/github-actions/github-actions.standards.md`) | Content-hash skip receipts | The eligibility policy (the three conditions), the maximum-age rule (a receipt's age bounds how long a skip is honoured; one uniform `14400` in both workflows), the fingerprint enumeration completeness rule (every tracked top-level entry, no bare root globs), the receipt validation and `shell: bash` rules, the key format with no restore-keys, the same-repo save guard, the miss-only failure-tolerant save and its sanctioned `continue-on-error` exception, the attestation placement rule, and the rule that the fingerprint set is pinned complete by a policy test |
| CI Pipelines | ADR (`docs/adr/`, new) | CI jobs skip on content-hash receipts only when the fingerprint covers the job's whole input set and the receipt is inside its maximum age | The decision — content-hash receipts skip jobs only when the fingerprint covers the job's whole input set and the receipt is inside its maximum age — and the rejected alternatives: job-level `if:` (breaks the required check's evidence), per-job independent release skips (breaks the artifact chain), `hashFiles('**')` (the `.git` hazard), and a purely content-keyed skip with no time bound (it would honour a scan receipt from last week) |

An ADR is warranted, not waived: the choice constrains every future workflow edit, had viable
alternatives that are rejected with reasons, and unsound relaxations would be silent.

## Investigation findings

- **Workflow inventory** (read in full, at `rev=1405762e` on branch `factory/feat/ci-content-hash-skip`):
  `ci.yml` — 17 jobs (`changes`, `pr-naming`, `build-test`, `integration-test`, `sbom`,
  `squad-filesystem-contract`, `publish-smoke`, `update-loop`, `codeql`, `trivy`, `semgrep`,
  `gitleaks`, `skill-docs-gate`, `ts-gates`, `dash-e2e`, `tray-rust`, `ci-summary`);
  `release.yml` — 12 jobs (`authorize`, `version`, `approve`, `build`, `build-kyberdash`,
  `pack-squad`, `build-tray`, `sbom`, `artifact-sboms`, `attest-sbom`, `release`, `gh-packages`);
  `pr-naming-recheck.yml` — 1 job; `gate-catalogue-toolchains.yml` — 1 job.
- **Tests that read the workflow files** (a worker runs these filtered, from the project's own
  `AGENTS.md` command): `CiAlwaysRunJobsTests` (ci.yml comment/job contract), `CiSkillGateCoverageTests`
  and `PrNamingWorkflowTests` (ci.yml), `SbomTests` (ci.yml and release.yml), `ReleaseTests` and
  `ReleaseNotesTests` and `UpstreamSeveranceTests` (release.yml), `McpRenameRepoGuardTests` and
  `TaskGatesRepositoryConfigTests` (both files). Command shape:
  `dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~<Class>"`.
- **Standard constraints that shape the design** (from the `github-actions` coding standard,
  fetched through the docs tool, not read as a file): every `uses:` pins a full commit SHA with a
  version comment; `${{ github.* }}` never reaches a `run:` block (the `changes` job is the
  model); `id-token: write` appears only in a job that mints a Sigstore certificate with a comment
  naming why (`attest-sbom` is the model); `continue-on-error` only on the three advisory scans; a
  workflow change ships with its pinning test in `KyberWeave.RepoPolicy.Tests`; `actionlint` is
  the build check for a workflow file.
- **GitHub Actions cache scope facts** the design depends on: caches created on the default branch
  restore in every workflow and branch of the repository; caches created in a pull request restore
  only in that pull request's later runs; a pull request from a fork can read none of the base
  repository's caches. Consequence, stated honestly: a push to main cannot restore a receipt a PR
  run saved, so the wins are (a) re-runs after flaky failures (same tree, same scope), (b) revert
  or reopened PRs whose tree matches a main-tested tree, and (c) release re-runs against the same
  inputs. A routine fresh pull request almost never hits; the owner should want this mechanism for
  the re-run and revert cases, not for everyday PR cost.
- **`hashFiles('**')` hazard**: nothing in the repository pins whether the glob includes `.git`;
  the enumerated top-level set (D15) makes the answer irrelevant, and a policy test keeps the
  enumeration complete.
- **Tracked top-level inventory** (verified read-only with `git ls-tree HEAD` at `1405762e`):
  25 directories — including `.agents`, `.claude`, `.codex`, `.commandcode`, `.cursor`, `.devin`,
  `.factory`, `.kilo`, `.opencode`, `.pi`, `.playwright-cli`, `.vscode`, `homebrew`, `npm`,
  `samples` — and 24 root files, including `.editorconfig`, `.gitleaks.toml`, `.mcp.json`,
  `global.json` and the single `Directory.Build.props`; no root `Directory.Packages.props` or
  `NuGet.config` exists. The review's enumeration finding is verified: `W` now enumerates all of
  them, and the sbom regex's `Directory\..*\.props` alternation is unanchored, so `S` uses
  `**/Directory.*.props`.
- **`ci.yml`'s `changes` job already encodes per-job input universes** (the SBOM regex, the
  dash/dotnet/code flags); the fingerprint sets reuse those declarations rather than inventing a
  second model.
- **Discovery: codegraph 0, docs 2, direct reads 7.** The subject is workflow YAML and governed
  documents, not indexed code symbols, so the kw-docs server answered the standard and the
  runbook location, and the four workflow files, `CiAlwaysRunJobsTests.cs`, `docs/plans/README.md`
  and `docs/archive/plans/README.md` were read directly — the workflows and the test have no
  indexed symbols to explore, and the plan index files define the row format this plan must add.
  kw-docs provenance: `root=<worktree>`, `branch=factory/feat/ci-content-hash-skip`,
  `rev=1405762e` — the dispatch's `RUN_BASE` revision and `RUN_BRANCH`.
- One read artifact to note: the Syft cache keys in `ci.yml` and `release.yml` came back redacted
  by the harness; workers must not treat that redaction as the file's content.

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.RepoPolicy.Tests/CiContentHashSkipTests.cs` (new class `KyberWeave.Tests.CiContentHashSkipTests`, corpus read pattern copied from `CiAlwaysRunJobsTests`) | `dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release` then `dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~CiContentHashSkipTests"` | For every eligible ci.yml job — the four security scans included: the fingerprint/restore/verdict steps exist with a `receipt-` key of the right shape; the scans hash `W` and `codeql`'s leg is `${{ matrix.language }}`; the `W` argument list carries every tracked top-level entry — directories as `<dir>/**`, root files by exact name — with no bare `'*'`/`'.*'` and no `hashFiles('**')`; the `D` arguments include `global.json` and `Directory.Build.props`, the `T` arguments include `dash/.nvmrc`, and the `S` arguments include `**/Directory.*.props` and cover each alternation of the `changes` job's sbom regex; no receipt restore declares `restore-keys`; every receipt step declares `shell: bash`; the verdict step compares the restored receipt's `saved_at_epoch` against `RECEIPT_MAX_AGE_SECONDS` (`14400`) before any `skip` output is set, treats a stale receipt as a miss whose summary line names the age and the ceiling, and treats a missing, unparsable, non-numeric or future `saved_at_epoch` — and a receipt whose `input_hash` differs from the fingerprint step's `hash` — as a miss; no cache key carries an age; heavy steps gate on `steps.verdict.outputs.skip`; save and attest steps carry the same-repo guard, and the save steps are miss-only with `continue-on-error: true`; attest steps appear only in jobs whose `permissions` include `id-token: write` with a comment, each with `subject-path` naming its product (`artifacts/nupkg/*.nupkg` for `build-test`, the three published binaries for `publish-smoke`, the cdx.json for `sbom`) and no `sbom-path`, so an SBOM-mode attestation fails the test; no job-level `if:` was added; no `${{ github.` appears in the receipt steps' `run:` blocks | The class fails against today's `ci.yml`: the assertions report the missing `receipt-` steps, the `W`/`D`/`T`/`S` argument-list requirements (the enumerated top-level entries, `global.json`, `Directory.Build.props`, `dash/.nvmrc`, `**/Directory.*.props`), the missing verdict-step age comparison and receipt validation, the missing `shell: bash` declarations, and the attest steps' missing provenance shape, before any workflow edit | The same filtered run passes with the workflow edited, and `actionlint .github/workflows/ci.yml` and `git diff --check` are clean |
| T2 | `tests/KyberWeave.RepoPolicy.Tests/ReleaseReceiptSkipTests.cs` (new class, same pattern) | `dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release` then `dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ReleaseReceiptSkipTests"` | Every release build/SBOM job restores the same version-bound chain key and gates its heavy steps on the verdict step's `skip` output; the workflow's `RECEIPT_MAX_AGE_SECONDS` is `14400` and the verdict step compares the chain receipt's `saved_at_epoch` against it before the chain can skip; the chain receipt save exists only in the `release` job after its verify steps and is miss-only — no save step is reachable when the verdict skipped, so a hit never refreshes the receipt's age; `release` and `gh-packages` carry verify-on-hit steps and never gate their verification steps, and those steps name concrete fail-closed commands: the asset inventory compared as sorted sets with exit 1 on mismatch, the published `SHA256SUMS.txt` compared with `diff` (exit 1 on a byte difference), the package-version lookup through the GitHub Packages API exiting 1 when a recorded version is absent, and `gh attestation verify` per archive for both the CycloneDX SBOM predicate and the SLSA provenance predicate; the provenance attestations for every archive exist in `attest-sbom` beside the SBOM attestations, under the existing permission block, each with `subject-path` over the full archive set and no `sbom-path`; no `needs` edge or display name changed. The fail-closed exit behaviour of those commands is not statically observable and is review scope (see Review) | The class fails against today's `release.yml`: the chain key, the `14400` ceiling, the miss-only chain save, the verify-on-hit steps and the provenance attestations are all absent | The same filtered run passes with the workflow edited, and `actionlint .github/workflows/release.yml` and `git diff --check` are clean |
| T3 | Documentation gates (the docs corpus is the test subject; no new test class — the prose scans in `KyberWeave.RepoPolicy.Tests` run over it in the policy gate) | `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` | The runbook section states the mechanism, the per-job fingerprint sets, the freshness rule with the single uniform `RECEIPT_MAX_AGE_SECONDS` value and the fork behaviour exactly as the workflows implement them; the standard section states the eligibility policy and the maximum-age rule; both state the fingerprint enumeration completeness rule (every tracked top-level entry, directories as `<dir>/**`, root files by name, no bare root globs), the receipt validation rule, the `shell: bash` declaration on receipt steps, the miss-only failure-tolerant save with its sanctioned `continue-on-error` exception, and the corrected cache-scope wording — a pull request's saves are scoped to that pull request, and only default-branch runs populate the readable-everywhere scope; both pass the docs gates with zero findings | Not a RED/GREEN task: the docs gates pass only after the sections are written; the RED equivalent is a `docs validate` failure naming what the sections are missing | Both docs commands report no findings |

## Tasks

### T1: Content-hash receipts in `ci.yml`

- **Objective:** Add the fingerprint, receipt lookup, freshness verdict, skip gating, attestation
  and receipt save to the fourteen eligible ci.yml jobs — the ten build and gate jobs plus the four
  security scans — with the new policy test class pinning every invariant.
- **Capabilities:** GitHub Actions workflow authoring, with xUnit repository-policy tests that read
  workflow YAML through the corpus seam
- **Difficulty:** hard
- **Technology:** github-actions
- **Session:** `ci-receipts`
- **Depends on:** none
- **Files:**
  - `.github/workflows/ci.yml`: the workflow-level `env:` entry `RECEIPT_MAX_AGE_SECONDS: "14400"`;
    jobs `build-test`, `integration-test`, `squad-filesystem-contract`, `sbom`, `publish-smoke`,
    `update-loop`, `skill-docs-gate`, `ts-gates`, `dash-e2e`, `tray-rust`, `codeql`, `trivy`,
    `semgrep`, `gitleaks` — the six-step receipt pattern in the contract below; `id-token: write`
    permission blocks (with the comment naming why) only on the jobs that attest
  - `tests/KyberWeave.RepoPolicy.Tests/CiContentHashSkipTests.cs`: new class
    `KyberWeave.Tests.CiContentHashSkipTests`, corpus pattern copied from `CiAlwaysRunJobsTests`
- **Receipt pattern (this task's whole contract; no other section is needed):**
  - Step 1 `Compute input fingerprint` (`id: fp`): the `hashFiles()` value to `$GITHUB_OUTPUT` as
    `hash`.
  - Step 2 `Look up the receipt` (`id: receipt`): `actions/cache/restore` at the repository's
    existing pin `55cc8345863c7cc4c66a329aec7e433d2d1c52a9 # v6.1.0`, `path: .receipt`, key
    `receipt-<job-key>-<leg>-<hash>`, no restore-keys. `<leg>` is `${{ matrix.rid }}` or
    `${{ matrix.os }}` for matrix jobs, `${{ runner.os }}` otherwise, and `${{ matrix.language }}`
    for `codeql`.
  - Step 3 `Decide the verdict` (`id: verdict`): age = now minus the restored
    `.receipt/receipt.json`'s `saved_at_epoch`; a hit is fresh only when the age is at most
    `env.RECEIPT_MAX_AGE_SECONDS`. Sets `skip` only on a fresh hit; writes the receipt to
    `$GITHUB_STEP_SUMMARY` on a fresh hit; a stale hit writes the receipt's age and the ceiling;
    a plain miss writes "no prior receipt; running in full"; a fork pull request's line names
    cache isolation. A missing, unparsable, non-numeric or future `saved_at_epoch` (a negative
    age), or an `input_hash` that differs from the fingerprint step's `hash`, is a miss with its
    own line — the receipt is validated before it is trusted.
  - Step 4: heavy steps gain `if: steps.verdict.outputs.skip != 'true'`, composed with their
    existing conditions.
  - Step 5 `Digest and attest` (miss path, same-repo guard): SHA-256 digests of the uploaded
    artifacts; attest build products only — `build-test`'s nupkg, `publish-smoke`'s RID binaries,
    `sbom`'s cdx.json — with `actions/attest` at the existing pin
    `1e69f48acb82d1966a394da916b4c1698aa569d6 # v4.2.2` in provenance mode, each attestation
    carrying `subject-path` naming the product and no `sbom-path`, under `id-token:
    write`, `attestations: write`, `artifact-metadata: write`, `contents: read` with a comment
    naming why. Test reports and SARIF are digest-only, never attested.
  - Step 6 `Save the receipt` (miss path only — never reachable on a hit, so a hit never
    refreshes `saved_at_epoch`; `success()`, same-repo guard), under `continue-on-error: true`:
    write `.receipt/receipt.json` via `jq` from values passed through `env:` — fields `schema`
    (`"1"`), `job`, `input_hash`, `commit_sha`, `run_id`, `run_url`, `artifact_digests` (object
    or `null`), `attested`, `saved_at` (ISO-8601 UTC), `saved_at_epoch` (epoch seconds) — then
    `actions/cache/save` at the cache pin. A cache-reserve race must never fail a green job; the
    failure mode is fail-open to the next run re-executing.
  - Fingerprint sets (the corrected D17 sets): `W` (whole tracked tree — the four scans,
    `build-test`, `integration-test`, `squad-filesystem-contract`, `publish-smoke`,
    `update-loop`, `skill-docs-gate`):
    `hashFiles('.agents/**','.apm/**','.claude/**','.codex/**','.commandcode/**','.cursor/**','.devin/**','.factory/**','.github/**','.kilo/**','.kyber-weave/**','.opencode/**','.pi/**','.playwright-cli/**','.vscode/**','dash/**','docs/**','homebrew/**','npm/**','products/**','samples/**','scripts/**','src/**','templates/**','tests/**','.editorconfig','.gitattributes','.gitignore','.gitleaks.toml','.mcp.json','AGENTS.md','CLAUDE.md','CODE_OF_CONDUCT.md','CONTRIBUTING.md','Directory.Build.props','HANDOVER-WAVE4.md','HANDOVER.md','KyberWeave.Unit.slnf','KyberWeave.sln','KyberWeave.sln.DotSettings','LICENSE','NOTICE','README.md','REFACTOR-LAYERS.md','REVIEW.md','SECURITY.md','apm.yml','global.json','kilo.json')`
    — every tracked top-level entry, directories as `<dir>/**` and root files by name, no bare
    root globs; the completeness test enumerates the tracked tree and fails when an entry is
    missing; `S` (`sbom`): `hashFiles('**/package-lock.json','**/Cargo.lock','**/packages.lock.json','**/*.csproj','**/Directory.*.props','global.json','scripts/generate-sbom.sh','scripts/validate-sbom.py','scripts/syft-bootstrap.sh','dash/tray/vendor/**','.github/workflows/release.yml','.github/workflows/ci.yml')`;
    `D` (`ts-gates`, `dash-e2e`): `hashFiles('dash/**','global.json','Directory.Build.props','.github/workflows/ci.yml')`;
    `T` (`tray-rust`): `hashFiles('dash/tray/**','dash/.nvmrc','.github/workflows/ci.yml')`.
  - Every receipt step declares `shell: bash` — three eligible jobs have windows legs whose pwsh
    default breaks `date +%s` and the verdict arithmetic.
  - Same-repo save/attest guard: `github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository`;
    no `${{ github.* }}` inside any `run:` block — values travel through `env:`.
- **Commands:**
  - `dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`
  - `dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~CiContentHashSkipTests"` (task-gate run, first with `--red` before the workflow edit, then passing)
  - `dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~CiAlwaysRunJobsTests|FullyQualifiedName~CiSkillGateCoverageTests|FullyQualifiedName~PrNamingWorkflowTests|FullyQualifiedName~SbomTests"`
  - `actionlint .github/workflows/ci.yml`
  - `git diff --check`
- **Acceptance:**
  - Every observable behavior in the T1 Test-contract row holds, on the passing filtered run
  - The `changes` job, `pr-naming` and `ci-summary` are untouched; the four security scans gain
    only the receipt steps and the freshness verdict — their scanning steps, SARIF uploads and
    `continue-on-error` state are unchanged (a skipped scan is a skipped step set inside a
    successful job, so `CI Summary` still reads it as success)
  - A stale receipt (older than `14400` seconds) is a miss: the verdict step does not set `skip`,
    and the summary names the age and the ceiling
  - Every new `uses:` pins a full commit SHA with a version comment (reuse the repository's
    existing `actions/cache` and `actions/attest` pins; do not add a new action)
  - No `${{ github.* }}` reaches a `run:` block; values travel through `env:`
- **Hard-rating design (decided here, not by the worker).** Failure mode: an incomplete
  fingerprint silently skips the tests a commit needed, and CI reports green over untested inputs.
  Mitigations, all already fixed by this plan:
  - Jobs that read anywhere hash the enumerated whole tree `W`, and a test walks the corpus and
    fails when a top-level entry is missing from the enumeration — the set cannot rot silently.
  - Every set includes the workflow file itself, so any workflow edit invalidates every key.
  - Restore is exact-key only; no `restore-keys` can substitute a near-miss receipt, and no key
    carries an age — the verdict step reads the workflow's current `RECEIPT_MAX_AGE_SECONDS`, so
    the ceiling can be tightened without touching a key.
  - Receipts save only on job success, so a re-run re-executes exactly what failed.
  - Every failure mode of the mechanism itself (eviction, fork isolation, quota) misses and runs
    the job in full: it fails open to running, never to skipping.

### T2: Chain receipts and verify-on-hit in `release.yml`

- **Objective:** Give the release build chain one version-bound receipt, skip the chain as a unit
  on a hit, and make the publish jobs verify the published state instead of re-publishing.
- **Capabilities:** GitHub Actions workflow authoring for a publishing pipeline, with xUnit
  repository-policy tests
- **Difficulty:** hard
- **Technology:** github-actions
- **Session:** `release-receipts`
- **Depends on:** none (parallel with T1; the receipt fields, the freshness ceiling and the
  verify-on-hit contract it needs are restated inside its own files and acceptance below)
- **Files:**
  - `.github/workflows/release.yml`: the workflow-level `env:` entry
    `RECEIPT_MAX_AGE_SECONDS: "14400"` (four hours, the same value as `ci.yml`); jobs `build`, `build-kyberdash`,
    `pack-squad`, `build-tray`, `sbom`, `artifact-sboms`, `attest-sbom` (chain restore, freshness
    verdict and skip gating — `attest-sbom` skips with the chain on a hit, because the hitting
    run builds nothing to attest); `release` (a miss-only chain save after its verify
    steps, never reachable on a hit, plus the verify-on-hit path: the asset inventory compared
    as sorted sets with exit 1 on mismatch, the published `SHA256SUMS.txt` compared with `diff`
    exiting 1 on a byte difference, `gh attestation verify` per archive for both the CycloneDX
    SBOM and the SLSA provenance predicates, and the prior `run_url` reported); `gh-packages`
    (verify-on-hit: the GitHub Packages API queried for the receipt's recorded package
    versions, exiting 1 when one is absent, instead of re-packing and re-pushing); `attest-sbom`
    (provenance attestations for every archive beside the existing SBOM attestations —
    `subject-path` over the archive set, no `sbom-path`)
  - `tests/KyberWeave.RepoPolicy.Tests/ReleaseReceiptSkipTests.cs`: new class
    `KyberWeave.Tests.ReleaseReceiptSkipTests`
- **Commands:**
  - `dotnet build tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release`
  - `dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ReleaseReceiptSkipTests"` (task-gate run, first with `--red`, then passing)
  - `dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~ReleaseTests|FullyQualifiedName~ReleaseNotesTests|FullyQualifiedName~SbomTests|FullyQualifiedName~UpstreamSeveranceTests"`
  - `actionlint .github/workflows/release.yml`
  - `git diff --check`
- **Acceptance:**
  - Every observable behavior in the T2 Test-contract row holds, on the passing filtered run
  - `authorize`, `version`, `approve` are untouched; no `needs` edge or display name changes
  - The chain receipt is written only after the `release` job verified the published release
  - The chain's `RECEIPT_MAX_AGE_SECONDS` is `14400`, the same value as `ci.yml`; a chain receipt
    older than that is a miss and the whole chain rebuilds
  - On a hit, `release` and `gh-packages` run their verification — sorted-set asset comparison,
    manifest `diff`, both attestation predicates per archive, package-version lookups — with each
    command exiting non-zero on a mismatch; the hit path does not re-run the macOS signature and
    notarization assertions, which are carried by the digest match; on a miss they behave
    exactly as today
  - The chain save is miss-only (`steps.verdict.outputs.skip != 'true'`) and carries
    `continue-on-error: true`: a hit never refreshes the receipt's age, and a cache-reserve
    race never fails a green publish
- **Hard-rating design (decided here, not by the worker).** Failure mode: a chain hit skips builds
  whose outputs the publish path then cannot find, reporting green for a release that is missing
  assets or was never published. Mitigations:
  - One shared chain key, no per-job keys: the chain skips or runs as a unit, so no job can skip
    while a sibling still builds.
  - The chain receipt exists only after the `release` job verified the published release — a
    half-published release has no receipt and the re-run re-publishes.
  - The publish jobs never skip verification; the verify-on-hit path fails closed on a missing
    asset, a digest mismatch, or a deleted release.
  - The `version` job still refuses a tag that already exists, so a fresh dispatch for the same
    tag cannot reach the chain-skip path at all; only a re-run of the same run can.
  - The chain save is miss-only and failure-tolerant, so a hit never refreshes the receipt
    into permanence and a cache-reserve race never fails a green publish.

### T3: Document the mechanism and its policy

- **Objective:** Write the durable behaviour into the CI runbook and the GitHub Actions standard,
  so the next workflow edit inherits the policy instead of rediscovering it.
- **Capabilities:** documentation authoring under the Kyber-Weave governed-docs standard
- **Difficulty:** easy
- **Technology:** github-actions
- **Session:** `receipts-docs`
- **Depends on:** T1 (the implemented ci.yml shape), T2 (the implemented release.yml shape)
- **Files:**
  - `docs/ci-pipelines/workflows-runbook.md`: new section `Content-hash receipts and skips` — the
    step pattern, the per-job fingerprint sets, the cache key and scope rules with the corrected
    wording (a pull request's saves are scoped to that pull request; only default-branch runs
    populate the readable-everywhere scope), the freshness rule with the single uniform
    `RECEIPT_MAX_AGE_SECONDS` value (`14400`, both workflows) and the re-run tradeoff the owner
    accepted, the receipt validation
    and `shell: bash` rules, fork behaviour, and the release chain's verify-on-hit shape with
    what it carries rather than re-checks
  - `docs/standards/github-actions/github-actions.standards.md`: new rules — the three eligibility
    conditions, the maximum-age rule (a receipt's age bounds how long a skip is honoured — one
    uniform `14400` in both workflows), the fingerprint enumeration completeness rule
    (every tracked top-level entry, directories as `<dir>/**`, root files by name, no bare root
    globs), the receipt validation rule and the `shell: bash` declaration, the key format with no
    restore-keys, the same-repo save guard, the miss-only failure-tolerant save and its sanctioned
    `continue-on-error` exception, the attestation placement rule, and the requirement that the
    fingerprint enumeration stays pinned by the policy test
- **Commands:**
  - `dotnet run --project src/KyberWeave.Cli -- docs validate .`
  - `dotnet run --project src/KyberWeave.Cli -- docs drift .`
- **Acceptance:**
  - Both docs commands report no findings (the standards warning period excepted), run plain —
    `--merge-ready` is the pull-request gate, not a task command
  - The runbook and standard sections state exactly what T1 and T2 implemented — no aspiration
    prose; anything the workflows do not do does not appear in them

## Dependency graph

```
T1 (ci.yml) ──┐
              ├──> T3 (docs)
T2 (release) ─┘
```

T1 and T2 are parallel (different workflows, different test files, one shared data model defined
by this plan). T3 documents their final shapes and depends on both.

## Risks

- **Runner-image and floating-toolchain drift** (D13): a hit proves the tree passed under the
  toolchains of the moment, not the same runner image, not the same floating Rust `stable`, and
  not the same .NET patch within the `rollForward: latestFeature` band. Accepted residual,
  bounded by the freshness ceilings; the toolchains pinned in the tree are hashed.
- **A green `CI Summary` over a skipped job is only as sound as the fingerprint.** The three
  eligibility conditions, the freshness ceiling and the whole-tree enumeration are the
  mitigation, and the policy tests keep them true; the residual is D13.
- **Skipped security scans upload no SARIF, and scanner rules can change inside the window — the
  owner's accepted tradeoff (D8, D16).** A scan that skips on a receipt up to four hours old can
  miss a vulnerability or a rule published inside that window, and during the skip window GitHub
  code scanning's diff against main sees no new analysis for that scan — the same informational
  "configuration not found" state the `codeql` job's comment already documents for a skipped
  matrix leg; it cannot fail `CI Summary`. Mitigation: the four-hour ceiling bounds the exposure,
  and the weekly schedule run re-scans the quiet tree and refreshes the receipts. Recorded as the
  owner's chosen tradeoff, not an oversight.
- **AI-graded integration tests** (D20): `ProseRuleGradingTests` can change `integration-test`'s
  verdict with no tree change (grader outage, confidence floor, secret presence), straining the
  same-inputs-same-verdict condition. The owner accepted the exposure at the gate-riding
  decision; it is bounded by the four-hour ceiling, under which a skip never carries the
  AI-graded verdict.
- **Release-chain age ceiling (AP2, resolved).** The release chain honours the same four-hour
  ceiling as CI, by the owner's decision over a recommended seven days. The consequence, stated
  plainly: a release re-run inside four hours of the failed attempt skips the build chain and
  verifies the published state; a re-run after that — the re-run-the-next-morning case after a
  flaky macOS notarization failure, which was the chain-skip's main win — rebuilds the whole
  chain, RID matrix included. The owner chose this knowingly; it is recorded here, not hidden.
  On a hit the path re-checks inventory,
  manifest and both attestation types; the macOS signature and notarization assertions are
  carried by the digest match, not re-run (D19).
- **Cache budget**: ~14 keys per tested content state, one small file each; a content state that
  stops being tested ages out in 7 days. Far inside the 10 GB repository limit; eviction fails
  open to running.
- **Rare-hit honesty**: the wins are re-runs, revert/reopened PRs and release re-runs inside
  the four-hour ceiling, not everyday pull requests. Recorded as a finding so the owner approves
  the mechanism for what it actually saves.

## Out-of-scope boundaries

- `pr-naming-recheck.yml` — one API POST that re-runs CI's own naming job; receipt plumbing would
  change nothing about its cost or verdict.
- `gate-catalogue-toolchains.yml` — the workflow's whole value is executing real toolchains
  against fixture projects; it is not a required check and its own `paths:` filter already
  selects its runs.
- `templates/github-actions/` copy-ready workflows — a product contract hosts copy; content-hash
  receipts are this repository's internals. `PR note: templates/github-actions/ — hosts copying
  the review-gate template do not gain receipt skips — a host's CI has different jobs and cache
  usage; revisit only if a host asks.`
- Branch-protection configuration — unchanged; `CI Summary` stays the single required check and
  no new check names are introduced.
- Extending `ci-summary`'s table with per-job prior-run links — each job's own summary carries
  them. `PR note: .github/workflows/ci.yml ci-summary — a "carried from" column is possible later
  if the owner wants the aggregator to show skips; left out to keep the aggregator unchanged.`
- A CI-side `docs drift` gate and any CodeGraph provisioning for it — already declined by the
  standard; unchanged here.

## Verification gates

- The two task-gate runs per developer task (test-first: `--red`, then passing), with the filtered
  policy classes from each task's Commands.
- `actionlint` on both workflow files, and `git diff --check`, per task.
- The existing classes that read the two workflows (`ReleaseTests`, `SbomTests`, `CiAlwaysRunJobsTests`
  and the rest named in the findings), run filtered by T1 and T2.
- `kyber-weave docs validate .` and `kyber-weave docs drift .` after T3 (plain, not
  `--merge-ready`, while this plan is open).
- CI itself is the live proof: the pull request carrying this change edits both workflow files,
  which invalidates every fingerprint (`W` hashes `.github/**`), so the run exercises the miss path
  everywhere and saves the first receipts. `PR note: the hit path (a skip) cannot be observed in
  this change's own CI run — it first appears on a later identical-input re-run; the policy tests
  are the pre-merge proof of its shape.`

## Review

The end-of-run review council derives its command from the `code-review` skill, measured against
the change with `--base` and admitted to the full-suite gates with `--council`; this change spans
two workflows and two new test classes, so it exceeds the quick-review line and takes the full
pre-push council. The reviewer checks the three eligibility conditions and the freshness ceiling hold for every job
that gained a skip, and that no excluded job gained one. The fail-closed behaviour of the
verify-on-hit commands is review scope, not test scope: a static YAML assertion can show the
commands exist, not that they exit non-zero on a mismatch. The reviewer reads each verify
command's exit path and exercises the comparison logic locally — `bash
scripts/verify-release-checksums.sh --list` against the expected asset set, and a `diff` against
a fixture manifest — so the property is judged in review rather than asserted unobservably. The
reviewer also confirms the `continue-on-error` flag appears on exactly the receipt save steps and
nowhere else.

## Completion summary

Verified by docs-dev at closeout against the run branch `factory/feat/ci-content-hash-skip` at
`a6d372ac` (four commits after the plan-finalization commit `b575dd04`). Each criterion below was
checked against the shipped workflow and the policy test class that pins it; the workers' own
summaries were not taken as evidence. The plan's task list carries no `- [ ]` checkboxes, so there
was nothing to tick.

| Task | Evidence |
|---|---|
| T1 | `.github/workflows/ci.yml` declares `RECEIPT_MAX_AGE_SECONDS: "14400"` at workflow level, and its fourteen eligible jobs — the ten build and gate jobs plus `codeql`, `trivy`, `semgrep` and `gitleaks` — each carry `Compute input fingerprint` → `actions/cache/restore` at the `55cc834…` v6.1.0 pin with `path: .receipt` and `receipt-<job>-<leg>-<hash>` → `Decide the verdict` → skip-gated work steps → digest and attest → miss-only save. The verdict enforces the plan's order: a miss (a fork's line names cache isolation), an absent or unparsable receipt, a non-numeric epoch, a future epoch, an `input_hash` mismatch, then the ceiling; a fresh hit sets `skip=true` and writes the receipt JSON (including `run_url`) to the summary. No receipt restore declares `restore-keys`, no job gained a job-level `if:`, no receipt `run:` block interpolates `${{ github.* }}`, and exactly three jobs attest build products under `id-token: write`. `CiContentHashSkipTests` pins every one of these across 17 tests, including the whole-tree argument list compared against the tracked top-level entries. |
| T2 | `.github/workflows/release.yml` declares the same `14400`, and its nine chain and publish jobs restore one `release-chain-<version>` key from `.release-chain` and run one verdict step. The seven build and SBOM jobs gate every step on `steps.verdict.outputs.skip != 'true'` except the checkout, the restore, the verdict and `build-tray`'s `if: always()` cleanup. `release` and `gh-packages` gate only their publishing steps; their verify steps always run and fail closed — sorted-set asset comparison, `diff` of the published `SHA256SUMS.txt`, `gh attestation verify` per archive for `https://cyclonedx.org/bom` and `https://slsa.dev/provenance/v1`, and a GitHub Packages API lookup per recorded version. The chain receipt is recorded in `release` only, after every verification, on the miss path only; the save carries `continue-on-error: true`. `Report the run that published this release` runs under `if: always()`, so a hit still names the proving run when a verify step failed, and it claims only what happened: a chain hit with a recorded proving run, a chain hit whose receipt recorded no run, a green miss naming this run as the publisher, a miss whose publish step succeeded but whose verification failed reporting a live, unverified release, a miss whose publish step failed after creating the release reporting a live, incomplete release, or a miss where no release exists saying it published nothing. `ReleaseReceiptSkipTests` pins each of these in 11 tests — the future-dated receipt guard, the retry loops' initially unverified flag and the always-running report step among them. |
| T3 | [the runbook's section](../ci-pipelines/workflows-runbook.md#content-hash-receipts-and-skips) and [the standard's rules](../standards/github-actions/github-actions.standards.md#receipts-and-skips) were read line by line against both workflows and state only what the workflows do: the step pattern, the four fingerprint sets, the key and cache-scope rules, the single uniform ceiling, the verdict's validation order, `shell: bash`, fork behaviour, and the chain's verify-on-hit shape with what it carries by byte-identity rather than re-checks. `docs validate` and `docs drift` are clean. |
| Closeout | [ADR 0095](../adr/0095-ci-jobs-skip-on-content-hash-receipts-only-with-a-complete-fingerprint.md) written and registered in the [ADR index](../adr/README.md); [ADR 0042](../adr/0042-release-sbom-cyclonedx-syft.md) restated to the attestation job as shipped (25 per-artifact SBOM attestations, one aggregate, one provenance step at `actions/attest` v4.2.2, 51 manifest entries); [the install runbook](../install.md#verifying-build-provenance) gained the provenance verification path users need. `kyber-weave docs closeout` reports nothing missing, and both docs gates are clean. |

**Recorded deviation, for the reviewer.** The job-inventory table, the fingerprint-sets paragraph,
the cache-keys paragraph and D10 described the chain key as tree plus version plus tag. What shipped
— and what the T2 task contract, the T2 test contract, the runbook and the standard all state — is a
version-bound key, `release-chain-<version>`, which carries no tree hash. Those four passages are
corrected above to the shipped key: this plan now names no tree hash in the chain key anywhere.
That is sound for the shipped trigger set: the `version` job refuses a dispatch whose tag
already exists, and a tag push names the version in the ref, so only a re-run of the same run
reaches a hit, and a re-run reuses the same commit. The residual is an administrator rewriting a
tag, which the repository's ruleset blocks except for administrators.

NOT VERIFIED: a real cache hit (a skip) on a re-run of identical inputs — needs a GitHub Actions
run; the policy tests are the pre-merge proof of its shape and the plan's own PR note says so.
NOT VERIFIED: the verify-on-hit path against a published release — needs a live release with its
attestations and package versions.
NOT VERIFIED: `actionlint` on both workflow files and `git diff --check` — worker-run evidence,
not re-run in this closeout.

## Closeout mapping

| ID | Destination | Documentation impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| H1, CI Pipelines, D3, D15, D16, D17, D18 | [CI runbook, Content-hash receipts and skips](../ci-pipelines/workflows-runbook.md#content-hash-receipts-and-skips) — the four receipt steps, the verdict's validation order, the four fingerprint sets and the enumeration-completeness rule, the keys and cache scope, the single uniform ceiling, `shell: bash`, fork behaviour, and the release chain's hit path with what it carries by byte-identity | extends-component | CI Pipelines | 2026-11-26 | harvested |
| H2, D2, D12, D16, D18 | [GitHub Actions standard, Receipts and skips](../standards/github-actions/github-actions.standards.md#receipts-and-skips) — the three eligibility conditions, the test-ratified workflow-pinning list, the maximum-age rule, the enumeration-completeness rule, the receipt-validation rule, the exact-key rule, the same-repository save guard, the miss-only failure-tolerant save as the one sanctioned `continue-on-error`, and the attestation placement rule | extends-component | CI Pipelines | 2026-11-26 | harvested |
| H3, D1, D2, D8, D9, D13, D19, D20, AP2 | [ADR 0095](../adr/0095-ci-jobs-skip-on-content-hash-receipts-only-with-a-complete-fingerprint.md), registered in the [ADR index](../adr/README.md) — the decision in twelve clauses, the accepted risks (security scans inside the ceiling, runner and floating-toolchain drift, AI-graded integration tests, the four-hour release ceiling over seven days, rare hits), and every rejected alternative | extends-component | CI Pipelines | 2026-11-26 | harvested |
| D7 | [ADR 0095](../adr/0095-ci-jobs-skip-on-content-hash-receipts-only-with-a-complete-fingerprint.md) (Decision 10) and [ADR 0042](../adr/0042-release-sbom-cyclonedx-syft.md) (D6–D8, updated to the shipped attestation job) — build products attested in provenance mode under `id-token: write`, test reports and SARIF digested only, and the release job's provenance step beside its SBOM attestations | extends-component | CI Pipelines | 2026-11-26 | harvested |
| D4, D5, D6, D10, D11, D14 | [CI runbook, The release chain](../ci-pipelines/workflows-runbook.md#the-release-chain-one-receipt-for-the-whole-build) and [distribution, Re-running a release](../distribution.md#re-running-a-release-the-chain-receipt) — the chain key and path, the miss-only save after verification, the publish jobs' verify-on-hit table, and the step-level skip that keeps the required check green | extends-component | CI Pipelines | 2026-11-26 | harvested |
| D7 (user view) | [Install, Verifying build provenance](../install.md#verifying-build-provenance) — the `gh attestation verify … --predicate-type https://slsa.dev/provenance/v1` command, and why the SBOM and provenance predicates cannot satisfy each other | extends-component | Distribution | 2026-11-26 | harvested |
| AP1 | Documentation waiver — the approve-and-execute record is run provenance: the owner's approval gate and its date have no surviving architectural, operational or behavioural invariant for a future reader to consult, and the two substantive items it carried are harvested under AP2 and D20 | none | CI Pipelines | 2026-11-26 | waived |

## docs-dev closeout

Harvest per the Design harvest table: the runbook section, the standard rules, and the new ADR
(`docs/adr/`, indexed in the ADR index). Write the ADR with the decision and the rejected
alternatives exactly as the Decisions table records them. Run `kyber-weave docs closeout` until
its mapping is complete, archive the plan with its task file removed, and confirm the corpus is at
zero findings with `docs validate . --merge-ready` and `docs drift .` before the pull request
merges.
