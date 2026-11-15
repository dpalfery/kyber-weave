---
id: adr/0081-the-conductor-delivers-a-run-through-one-docs-tool
title: The conductor delivers a run through one docs server tool
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-13
component: DocGraph
decided-by: []
supersedes: []
code-refs:
  - RunDelivery
  - PullRequestDescription
  - RunEvidence
  - DocsTools
---

# ADR 0081: The conductor delivers a run through one docs server tool

## Status

Accepted, 2026-11-13. Decided by the owner.

## Context

A finished run's delivery (commit, push the run branch, open a draft pull request) was a model
dispatch: `github-devops` following the `create-pull-request` skill. In a live autonomous run it took
3.6 minutes, created the pull request and then edited it three more times, tried to write the body
file into the repository's canonical checkout (the write guard refused it), and the conductor spent
another 1.3-minute dispatch re-checking the pull request read-only. Every step but the description's
wording is mechanical, and the wording can be assembled from what the run already recorded. On
GitHub Copilot CLI the conductor has no shell, only the `kw-docs` server's tools and sub-agent
dispatch, so the step has to be a docs server tool, as finalize and archive are
([ADR 0078](0078-the-conductor-finalizes-and-archives-plans-through-two-docs-tools.md)).

## Decision

1. **One tool, one CLI twin.** `docs_push_run_branch_and_open_draft_pull_request` and
   `kyber-weave run deliver` run the same Core step, `RunDelivery`. Inputs: `projectPath` (required,
   the run's worktree), `summary` (one to three plain sentences from the conductor), and optional
   `notes`, `title`, `draft` (default true), `documentationImpact` and `planFile`.
2. **Inside the autonomous delivery approval, and no further.** It commits only the plan files the
   archive left staged, pushes only the current run branch and never forces, and creates the pull
   request as a draft assigned to the signed-in user (the autonomy contract assigns the pull request
   to the invoking user), or updates the open one for that branch once. It never marks a pull
   request ready, merges, requests reviewers, or waits for CI.
3. **Workers commit their own work.** Deliver requires a clean tree outside the plans and
   archived-plans folders and refuses otherwise, naming the paths; it does not commit task or
   documentation changes for a worker.
4. **Refuse rather than guess.** Every check that needs nothing outside the machine runs first, and a
   refusal changes nothing: a branch that is not `[<owner>.<agent>.]<harness>/<type>/<slug>`, no run
   record naming the base, the base or default branch, a plan added but not archived, several
   archived plans with none named, a title that breaks the convention, or an open pull request from
   the branch into another base.
5. **One description format.** The `create-pull-request` skill's
   `assets/pull-request-description.md` is the format both the skill and the tool fill. The binaries
   embed no Squad corpus, so Core.Docs ships a copy and its build fails when the two differ. A host
   pull request template, when the repository has one, decides the sections; the not-verified,
   decisions-to-check and pending-review sections follow it when it has none of its own.
6. **The description comes from the run's records.** Changes and `NOT VERIFIED` items come from the
   archived plan, and the decisions to check are its `autonomous: low-confidence` rows, with a count
   of the other autonomous recommendations. Per-task evidence comes from the `task-gate.json` reports
   written since the run started. The review line and its open findings come from the last review
   cycle tied to this branch, its verdict recomputed with the verdict engine. The
   documentation-impact outcome exists only in docs-dev's reply, so the conductor passes it as
   `documentationImpact`. A piece the run did not record is reported as missing, never invented.
7. **No file is written for the body.** The description goes to `gh` on standard input
   (`--body-file -`).
8. **GitHub only; the dispatch stays as the fallback.** A remote not on github.com, or a `gh` that is
   missing or signed out, is refused with `KW-DELIVER-005`, and only that refusal sends the conductor
   to `github-devops` with the `create-pull-request` skill.

## Consequences

- Delivery is one tool call and the conductor reports its result; there is no re-check dispatch.
- No Arbiter rule changes. The tool is not a dispatch, and no rule expects a delivery dispatch. The
  conductor calls it with the worktree as `projectPath` while no worker is dispatched, so the
  canonical-checkout write guard (`KW-ARB-CHECKOUT-001`) and the git-diff attribution rules never see
  its writes. Shell guards on agents' commands are unchanged. The Copilot conductor guard already
  allows every `kw-docs` tool.
- The tool is the first `kw-docs` tool that reaches outside the machine (`OpenWorld = true`). It joins
  the `kw-docs` roster every role is entitled to (ADR 0050); its refusals keep that harmless, since
  it can push only the checkout's own run branch.
- Marking the draft ready once required CI passes is no longer done on the default path, because the
  tool does not wait for CI. The report says what CI is waiting for. Only the fallback skill keeps
  its 60-minute wait.
