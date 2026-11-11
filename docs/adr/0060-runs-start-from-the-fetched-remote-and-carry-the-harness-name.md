---
id: adr/0060-runs-start-from-the-fetched-remote-and-carry-the-harness-name
title: Runs start from the fetched remote and carry the harness name
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-07
code-refs:
  - RunStartCommand
  - RunStartPlanner
  - RunStartRunner
  - TriggerClassifier
---

# ADR 0060: Runs start from the fetched remote and carry the harness name

## Status

Accepted, 2026-11-07.

## Context

A Kyber-Squad run used to begin wherever the operator's checkout happened to be. The base branch
was assumed to be `main`, the fetch was a step somebody remembered, and nothing recorded which
harness had written the change: the diff says nothing about whether a `claude` run or a
`commandcode` run produced it, so a maintainer reading a pull request had no way to know. Those
facts were then reconstructed later, or not at all.

The decisions a run needs before it investigates anything are small and total: the base it is
compared against, the branch its commits belong on, the directory the work happens in, and the name
the harness is recorded under. A run that works any of that out mid-investigation does it against a
checkout somebody has already moved.

## Decision

1. **Every run fetches `origin` and branches from the remote base (D8).** The step runs
   `git fetch origin --prune`, then starts the branch from `run.base-branch` when the repository
   set it and from `origin/HEAD` otherwise. `main` is never assumed, because a repository whose
   default branch is `develop`, or whose operator named a release branch, would otherwise be
   compared against a branch that may not exist while the answer looks authoritative. Resuming a
   plan or a pull request keeps that branch rather than recreating it.
2. **The harness name is carried in every branch and pull request title (D9).** The branch is
   `[<owner>.<agent>.]<harness>/<type>/<slug>` and the title ends `[<harness>]`. The two shapes
   share one vocabulary with `scripts/check-pr-name.sh`, which CI runs, so a branch a run creates is
   a branch whose pull request can merge. A pull request authored by `dependabot[bot]` is exempt,
   because GitHub generates its branch and title and no human can conform them; the exemption
   follows the author, not the branch prefix. `manual` names work done under no harness.
3. **A title edit is checked without touching branch protection (D14).** `ci.yml` gains no
   `edited` event: every description edit — which the review council makes when it writes its
   verdict — would cancel and restart CI. Instead the naming rule is a `pr-naming` job that
   `CI Summary` needs, reading the live title, branch and author through the GitHub API rather than
   the event payload. A separate `pr-naming-recheck.yml` runs on `pull_request: edited`, only when
   the title changed, finds the latest completed CI run for the head commit, and re-runs that run's
   naming job and then `CI Summary` — which is the only way a corrected title can turn the required
   check green with nothing to push.
4. **One command does the whole step (D21).** `kyber-weave run start --harness <token> --type
   <type> --slug <slug> [--resume <branch>] [--failing-version <version>] [--json]` names the
   branch, resolves the base, reads the three versions and the divergence *before* it switches
   anything, and creates the branch with `--no-track`. Its exit codes separate the outcomes a
   caller acts on differently: `0` ready, `1` a tool failure while `stop-on-tool-failure` is on,
   `2` refused input, `3` the `codegraph` binary is missing. `--json` prints the facts a dispatching
   caller needs — `base`, `baseRef`, `branch`, `worktree`, `location`, `revision`, `resumed`,
   `versions`, `serverAlignment` — so it reads a result rather than parsing prose. A clean checkout
   with no second worktree is branched in place; a dirty or shared one gets a worktree beside the
   repository. Where the harness gives the conductor no shell, it dispatches `github-devops` with
   the exact command and a `RUN-START: true` marker, which the Arbiter's `TriggerClassifier`
   classifies as `investigate` so `KW-ARB-PLAN-001` does not refuse the route the marker exists for.
5. **The name format (D42).** Branch `[<owner>.<agent>.]<harness>/<type>/<slug>`, for example
   `commandcode/fix/arbiter-global-block-identity`; title `<type>(<scope>): <summary> [<harness>]`,
   for example `fix(arbiter): qualify global block identity by target [commandcode]`. The tokens
   are the thirteen catalogue tokens plus `manual`, with `agy` written `antigravity`. The suffix
   keeps the conventional prefix first, which is what the tag history and the changelog read.

## Alternatives Considered

- **Check out `main` and pull first.** Rejected. It moves the operator's checkout as a side effect
  of starting a run, it assumes the base's name, and it carries whatever the working tree held onto
  a branch those edits were not made for.
- **Detect the harness from the folder name.** Rejected. The folder a repository sits in is a
  location, not a fact about who is working in it: a Command Code run was once inside a `Devin`
  checkout. A rename would also change the branch prefix and the title suffix together, and
  nothing would record that the value was inferred rather than given. The harness is supplied — as
  `--harness` at run start, and as the `{{harness}}` placeholder the shared render pipeline
  substitutes from the target catalogue.
- **Detect the harness from an environment variable.** Rejected for the same reason, with more
  force: an `OPENCODE_CONFIG` variable was set inside a Claude session. Every child process
  inherits an environment variable, so a run dispatched from within another run would report the
  outer harness.
- **Leave the title to convention and enforce only the branch.** Rejected. Branch protection
  patterns match a branch name, not a pull request title, so the harness would be enforced in one
  place and merely hoped for in the other. Both are checked where both are visible, by a job that
  fails in seconds.

## Known Limits

- `--resume` accepts a local branch or `origin/<branch>`. A numeric pull request reference is
  refused with exit `2` rather than resolved: no pull request is fetched or looked up, so the
  caller passes the branch the pull request already owns.
- Presence of `codegraph` on `PATH` is what exit `3` measures, not its version. A binary that is
  present and misbehaves is a tool failure (exit `1`), because only an absent binary has an install
  as its remedy.
- A pull request from a fork gets a read-only token, so a title edit on a fork's pull request is
  checked at its next push rather than by the recheck.
- Whether `actions: write` on `GITHUB_TOKEN` reaches the job re-run endpoint is not documented by
  GitHub; the recheck reports its own failure rather than claiming the route works.
- The `RUN-START: true` route is a dispatch, so it is subject to the roster the dispatching skill
  declares. Where the roster does not name `github-devops`, the dispatch is escalated and the run
  records `run-start: not run` rather than refusing the work.
- The naming rule is enforced on the pull request, not on the local branch, so a branch created by
  hand is refused only when it reaches CI.

## Consequences

- A run never investigates against a stale base or an assumed default branch; the fetch and the
  base it resolved are in the run record, and every later dispatch of the run works in the location
  the step printed.
- The harness is readable in the branch and the title, so a maintainer sees it before reading a
  line of the diff, and the pull request cannot reach the default branch without it.
- Two shapes have one vocabulary: `kyber-weave run start` composes what `scripts/check-pr-name.sh`
  checks, so a change to one that misses the other fails the naming job rather than drifting.
- A tool that fails at run start stops the run by default. A repository that would rather continue
  from the base it last fetched sets `stop-on-tool-failure: false` and gets a warning instead.
- The two workflows are joined by each job's display name, because the API identifies a job by its
  `name:`. Renaming `pr-naming` or `CI Summary` in `ci.yml` means editing `NAMING_JOB` or
  `SUMMARY_JOB` in `pr-naming-recheck.yml` in the same change.
