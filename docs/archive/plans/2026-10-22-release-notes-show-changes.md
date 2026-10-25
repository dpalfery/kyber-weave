---
id: plans/2026-10-22-release-notes-show-changes
title: "Release notes show what changed: RC since the previous release, stable since the last stable"
doc-type: plan
status: archived
component: Distribution
owner: dpalfery
last-reviewed: 2026-10-22
development-mode: test-first
---

# Release notes show what changed for every RC and every stable release

**Status: Completed — archived 2026-10-22, with one item open: T4 (live check on real releases) is
`[NOT LIVE-VERIFIED]` and is a follow-up for the owner. Approved for execution 2026-10-22 (user
chose "approve and execute", message "execute", relayed by the conductor). Branch:
`feat/release-notes-show-changes`.**

`development-mode: test-first` (default; no opt-out supplied). Autonomy: supervised for this
plan — the packet did not carry `autonomy: autonomous`, so every decision below waits for an
explicit answer even though the Config Reg default is `autonomous`.

## 1. Problem and goal

When a release candidate (for example `v0.1.7-rc.10`) is published, the GitHub Release must
show what changed since the previous release. When a stable release (for example `v0.1.7`) is
published, it must show everything that changed since the last stable release — cumulative
across all of that line's RCs, not just the delta from the last RC.

Today `.github/workflows/release.yml` (step "Create tag and GitHub Release", lines 793–869)
runs `gh release create --notes "<static install table>" --generate-notes` with no start tag.
Two problems:

1. **The comparison base is undefined.** GitHub documents neither for the REST
   `generate-notes` endpoint nor for `gh release create` which previous tag is used when none
   is given (the UI calls it "Previous tag: auto"). A stable release may therefore show only
   the changes since the last RC — a near-empty list — which defeats the goal.
2. **Changes are buried.** `gh` prepends `--notes` to the generated notes, so the pre-release
   banner, the asset table, the SBOM paragraph, the install command and the Windows signing
   note all come before "What's Changed".

Goal: a deterministic, tested rule picks the start tag; the release body leads with the
changes; a post-publish step proves the range that was published.

## 2. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| Q1 | Layout: put the change list first and the install/asset section after it? | Yes: build the body via the `generate-notes` API and publish with `--notes-file`, changes first. | Recommended option accepted. Build the body ourselves: call `gh api repos/{owner}/{repo}/releases/generate-notes` (fields `tag_name`, `target_commitish`, `previous_tag_name`), then write pre-release banner (one line) → generated changes → existing install/asset/SBOM/Windows section to a file and pass `--notes-file`; drop `--generate-notes`. Alternative: keep `--generate-notes --notes-start-tag`, which fixes the range but leaves changes under the install table (gh docs: `--notes` is prepended). | ANSWERED — User decision 2026-10-22 (relayed by conductor) |
| Q2 | If GitHub's notes generation fails, should the release stop before publishing? | Yes: fail closed after one retry. | Recommended option accepted. The step runs before `gh release create`, so no tag or release exists yet and a re-run is clean (it does re-request the `release` environment approval). Alternative: publish with a "notes unavailable" line and a compare link — never blocks a release, but can ship a release with no change list, which is the defect being fixed. | ANSWERED — User decision 2026-10-22 (relayed by conductor) |
| Q3 | Group changes into categories (features, fixes, dependencies) with `.github/release.yml`? | No: grouping is out of scope; no `.github/release.yml` is added. | Recommended option accepted. GitHub categories key on PR **labels**, not PR titles; recent merges use conventional-commit titles (`feat:`, `fix:`, `chore(deps):`) and label use was not verified (see GAPS). Without labels every PR lands in one catch-all bucket — no gain. Can follow as a todo if wanted. | ANSWERED — User decision 2026-10-22 (relayed by conductor) |
| AP1 | Approve and execute this plan? | Yes, including baseline decisions D1–D6 and the test contract in §4. | The user's chat message "execute", relayed by the conductor at the approve-and-execute gate. | APPROVED — User approval 2026-10-22 |
| D1 | Start-tag rule | Stable `X.Y.Z`: highest **stable** tag strictly lower (cumulative across that line's RCs; `v0.1.7` → `v0.1.6`). Prerelease (`-rc.N`, also `-dev.N` tag pushes): highest stable-or-RC tag strictly lower (`rc.10` → `rc.9`; `rc.1` → last stable). The target tag itself is always excluded (it already exists on the tag-push path). No lower tag → empty output, notes generated without `previous_tag_name`. | SemVer 2.0.0 precedence, so `rc.10` > `rc.9`. `-dev.*` and non-rc prereleases are never a base. Major filter: ignore tags whose major is above `max(highest stable major, target major)` — the existing filter alone would drop `v1.0.0-rc.1` when computing the base for `v1.0.0-rc.2`. Source: user goal statement; existing `next-release-version.sh` tag policy. | APPROVED — User approval 2026-10-22 at the approve-and-execute gate ("execute"), relayed by conductor |
| D2 | Where the rule lives | New mode in `scripts/next-release-version.sh`: `--notes-base <version> [tags...]` printing `NOTES_START_TAG=<tag or empty>`. | One SemVer comparator and one tag-collection policy (origin `ls-remote`, which excludes stray local `v0.9.*` tags). A separate script would be a third copy of `semver_compare` that must stay in lockstep with `install.sh`. The script is CODEOWNERS-protected, so the change gets owner review either way. | APPROVED — User approval 2026-10-22 at the approve-and-execute gate ("execute"), relayed by conductor |
| D3 | Which job computes what | `version` job (has `fetch-depth: 0`, tags, read-only) computes and outputs `notes_start_tag`; `release` job (has `contents: write`, which `generate-notes` requires) calls the API. | The `release` job's checkout has no tags, so it cannot compute the base itself. Version job writes the chosen range to the step summary so it is visible at the approval click. | APPROVED — User approval 2026-10-22 at the approve-and-execute gate ("execute"), relayed by conductor |
| D4 | Post-publish proof | New step after publish: `gh release view "$TAG" --json body,isPrerelease`; assert the body contains the compare link `compare/<start>...<tag>` when a start tag exists, that the generated section precedes the `## Kyber-Weave <version>` install heading, and that `isPrerelease` matches the version. Write release URL + range to the step summary. | Detective only (release is already public), matching the existing asset verification step and ADR 0027's pre/post split. | APPROVED — User approval 2026-10-22 at the approve-and-execute gate ("execute"), relayed by conductor |
| D5 | Tests | xUnit tests that run the bash script through `ProcessRunner` and assert `release.yml` structure, the same pattern as `ReleaseTests.VerifyReleaseChecksumsListModeOutputsAssetNames`; plus new `--self-test` cases. | No bats in the repo; adding it is a new dependency. Windows skips POSIX shell tests (`SkipOnWindows`), as existing script tests do. | APPROVED — User approval 2026-10-22 at the approve-and-execute gate ("execute"), relayed by conductor |
| D6 | ADR | No ADR — ADR waiver: refines the existing release flow; no new dependency, boundary or schema. The start-tag rule is recorded in `docs/distribution.md`. | Q2 was answered fail-closed, so no release-integrity trade arises that would need a record. | APPROVED — User approval 2026-10-22 at the approve-and-execute gate ("execute"), relayed by conductor |

## 3. Investigation findings

Discovery: codegraph 1, docs 1, direct reads 9 (`release.yml` and `next-release-version.sh`
are workflow/shell — CodeGraph indexes C#/TS, not these; `ReleaseTests.cs` lines 1170–1259
read to confirm the script-test pattern after codegraph returned unrelated files; plan index,
an archived plan and `distribution.md` frontmatter for format; architect references). Web:
GitHub REST docs, GitHub "automatically generated release notes" docs, `gh release create`
manual, one web search — all self-gathered (no research-agent call needed for three pages).

- `release.yml` release job (762–869): body built in `NOTES`, `--generate-notes`, `--prerelease`
  when version contains `-`, `--target "${TARGET_SHA}"` only on `workflow_dispatch` (the tag
  does not exist yet; `gh release create` mints it). Asset verification step at 871+.
- `version` job (77–148): dispatch path runs `next-release-version.sh --self-test`, then the
  chosen mode; tag-push path takes `GITHUB_REF_NAME`. Checkout uses `fetch-depth: 0`,
  `fetch-tags: true`.
- `next-release-version.sh`: `semver_compare` (awk, lockstep with `install.sh`),
  `collect_tags` (origin `ls-remote`, or argv for tests), `is_stable`, `is_rc`, major filter,
  `--self-test` with 8 cases. Usage text is `sed -n '2,22p'` of the header — header edits
  must keep that range correct.
- GitHub REST `POST /repos/{owner}/{repo}/releases/generate-notes`: `tag_name` (existing or
  new), `target_commitish` (required if the tag does not exist), `previous_tag_name`
  (starting point), `configuration_file_path` (defaults to `.github/release.yml`). The
  default previous-tag choice is **not documented** anywhere checked — hence D1/D2 always
  pass it explicitly.
- `gh release create`: `--notes` is prepended to generated notes; `--notes-start-tag` exists.
- Generated notes list merged PRs and new contributors plus a "Full Changelog" compare link;
  commits pushed without a PR appear only through the compare link.
- No `CHANGELOG`, no `.github/release.yml`. `docs/distribution.md` line 182 says the release
  ships "changelogs" and line 205 describes the pre-release banner — both need updating.
- Tests: `tests/KyberWeave.Tests/ReleaseTests.cs` asserts `release.yml` text and runs
  `verify-release-checksums.sh` via `/bin/bash` + `ProcessRunner`; `SbomTests.cs` likewise.

## 4. Test contract (test-first)

Runner for every row: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseNotes"`
New file: `tests/KyberWeave.Tests/ReleaseNotesTests.cs` (POSIX-only cases skip on Windows).

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 (script) | `ReleaseNotesTests.cs` | as above | `next-release-version.sh --notes-base <v> <tags...>` prints `NOTES_START_TAG=`: `0.1.7-rc.10` + {v0.1.6, v0.1.7-rc.9} → `v0.1.7-rc.9`; `0.1.7-rc.1` + {v0.1.6} → `v0.1.6`; `0.1.7` + {v0.1.6, rc.1…rc.10} → `v0.1.6`; `0.1.8-rc.1` + {v0.1.7, v0.1.7-rc.9} → `v0.1.7`; `0.1.8` + {v0.1.7} → `v0.1.7`; `0.1.7-rc.11` + {rc.9, rc.10} → `v0.1.7-rc.10`; target tag present in list is excluded; `-dev` and mistyped-major tags ignored; `1.0.0-rc.2` + {v0.9.0, v1.0.0-rc.1} → `v1.0.0-rc.1`; no lower tag → empty value, exit 0; missing/invalid version → non-zero with `usage:` and no `unbound variable`. `--self-test` still exits 0. | Tests fail because `--notes-base` is an unknown option (`die "unknown option"`). | Same tests pass unmodified. |
| T1 (workflow) | `ReleaseNotesTests.cs` | as above | `release.yml`: `version` job exposes `notes_start_tag` output from `--notes-base`; release step calls `releases/generate-notes` with `previous_tag_name` from that output and `target_commitish`; no `--generate-notes` flag remains; the generated body is written before the `## Kyber-Weave ${VERSION}` heading in the notes file and passed with `--notes-file`; a post-publish step reads `gh release view ... --json body` and checks the compare link; the file still parses as YAML (existing test). | Tests fail on current `release.yml` (no output, `--generate-notes` present, no body check). | Same tests pass; existing `ReleaseTests` and `SbomTests` stay green. |
| T3 (docs) | none — docs only | `docs validate . --merge-ready`, `docs drift .` | `distribution.md` states the start-tag rule and the body order. | n/a — no-test task; verified by the two doc checks and review. | Both checks report zero findings. |
| T4 (live) | none — owner gate | manual | The next real RC and the next real stable release show the expected range. | n/a — cannot be tested before a real cut. | Owner confirms on the published Release pages (record as `[NOT LIVE-VERIFIED]` until done). |

## 5. Tasks

**T1 — RED tests** (skill: `test-dev`). Files: `tests/KyberWeave.Tests/ReleaseNotesTests.cs`
(new). Write the script and workflow contract tests in §4. Acceptance: tests compile, fail
for the stated reason, RED output captured. Depends on: none.

**T2 — GREEN script and workflow** (skill: `github-devops`). Files:
`scripts/next-release-version.sh` (`--notes-base` mode, reuse `semver_compare`/`collect_tags`,
widened major filter for this mode only, new `--self-test` cases, header/usage range),
`.github/workflows/release.yml` (version job output + step-summary line; release step builds
the notes file per Q1, retry-then-fail per Q2, `--notes-file`; new post-publish notes check per
D4; header comment). Must not change `next-release-version.sh` default or `--prod` output.
Acceptance: T1 tests GREEN unmodified; `bash scripts/next-release-version.sh --self-test`
passes; full `ReleaseTests`/`SbomTests` green; `actionlint` clean if available. Depends on: T1.

**T3 — Docs** (skill: `docs-dev`, via `app-docs-standard`). Files: `docs/distribution.md`
(Release flow and Tag conventions: replace "changelogs" with the actual rule — RC vs previous
release, stable vs last stable, changes first, PR-based list plus compare link, fail-closed
behaviour per Q2). Acceptance: `docs validate . --merge-ready` and `docs drift .` zero
findings. Depends on: T2 (documents the shipped rule).

**T4 — Live verification** (owner). After merge, the next RC cut and the next stable cut:
confirm range and order on the Release page and the new step summary. Depends on: T2 merged.

**Review** — `code-review` over the whole change, then `docs-dev` closeout: archive this plan,
map D1–D6 and Q1–Q3 to `distribution.md` (or record the waiver), add an index archive row.

## 6. Dependency graph

```text
T1 (RED) -> T2 (GREEN) -> T3 (docs) -> review -> closeout
                       \-> T4 (owner, after merge)
```

## 7. Risks

- **Live behaviour cannot be proven before a real cut.** Mitigation: script logic is unit
  tested; the post-publish check (D4) fails loudly on the first wrong range.
- **Commits without a PR do not appear in the list.** Mitigation: compare link is always in
  the body; documented.
- **Approval re-request on retry** (Q2 fail-closed): a failed notes call after approval needs
  a re-run and a second approval click.
- **Body length.** GitHub release bodies cap at 125,000 characters; a long stable cumulative
  list plus the install section is far below that today. Not mitigated further.
- **`semver_compare` lockstep** with `install.sh` is unchanged; the new mode only calls it.

## 8. Out of scope

Categories/labels and `.github/release.yml` (Q3), a committed `CHANGELOG.md`, editing past
releases' notes, changing how versions are chosen, signing (ADR 0027).

## 9. Verification gates

Declared suite (`dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`),
`bash scripts/next-release-version.sh --self-test`, `docs validate . --merge-ready`,
`docs drift .`. `scripts/update-loop.sh` is not required (no install/self-updater change).

## 10. GAPS

- Could not see past Releases (the public Releases page shows none; `gh` not run by the
  planner), so the current "auto" range on real RCs is unverified.
- PR label usage unverified (moot: Q3 put grouping out of scope).
- `docs validate` / `docs drift` were not run by the planner (no process tool in this
  session). Conductor-reported result before approval: drift clean; `validate --merge-ready`
  reports only `KW-DOC-LIFECYCLE-003` (this open plan), which is expected until closeout
  archives it. Re-run both after these Ready edits.
- T4 (owner live check) is not done. The next real release candidate and the next real stable
  release have not been checked on their published Release pages. Record stays
  `[NOT LIVE-VERIFIED]` until the owner confirms the range and order.

## 11. Amendment from whole-change review

- Review finding: a mistyped higher-major tag (for example `v1.0.6-rc.6` while releasing
  `v0.1.7-rc.10`) could be picked as the comparison base. Fixed: the base is computed per release
  tag with `scripts/next-release-version.sh --notes-base --for <tag>`. Only tags lower than the
  released tag are candidates, so a higher-major tag is never the base.
- The shipped interface supersedes the form written in D2 and T1: `--notes-base --for <released-tag> [tags...]`
  prints `PREVIOUS_TAG=<tag>` (empty when there is no earlier tag). The D1 rule is unchanged.
  The Decisions table above is kept as approved.

## 12. Closeout

Verification (code review APPROVE; 281 `Release|Sbom` tests pass):
- T1 and T2 are done. The release workflow and the script contain the behaviour in D1–D4.
- T3 is done: `docs/distribution.md` describes the rule, the order and the failure policy.
  A Q3 sentence (no categories, no `.github/release.yml`) was added at closeout.
- T4 is open (see GAPS).
- `docs validate . --merge-ready` and `docs drift .` were not run in this closeout pass. The owner
  runs them after the archive move.

Closeout mapping (durable decision to canonical destination, all in `docs/distribution.md` "Release notes"
unless stated):

| Decision | Destination |
|---|---|
| Q1 changes first, `--notes-file` | "Order on the page" paragraph; first paragraph |
| Q2 fail closed after one retry | "Failure is fail-closed" paragraph (two attempts, then stop) |
| Q3 no grouping | First paragraph (added at closeout) |
| D1 start-tag rule | Start-tag table and the rules list under it |
| D2 rule lives in the script | "computed by the version job" paragraph (names `--notes-base --for`) |
| D3 version job computes, release job calls the API | "computed by the version job" paragraph; "Failure is fail-closed" |
| D4 post-publish proof | "Post-publish check" paragraph |
| D5 tests | Documentation waiver: test-fixture scoped (xUnit cases and script self-test harness). The behaviour it checks is documented under D1–D4. |
| D6 ADR | no ADR — ADR waiver: refines the existing release flow; no new dependency, boundary or schema. The start-tag rule is recorded in `docs/distribution.md`. |
| AP1 approval | Documentation waiver: a one-time approval event with no surviving invariant. |

Archive step: the file moves from `docs/plans/` to `docs/archive/plans/` by the owner's command
(this closeout pass did not run shell commands). The plan index's active table is emptied and an
archived row is added with the same path.
