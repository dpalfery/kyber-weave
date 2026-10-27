---
id: todo/review-council-rust-tauri-go-standards
title: Declare Rust, Tauri and Go coding standards for the review council
doc-type: todo
component: ReviewCouncil
status: draft
owner: dpalfery
last-reviewed: 2026-10-27
---

# Declare Rust, Tauri and Go coding standards for the review council

The review council's technology checklists for Rust, Tauri and Go are pointer checklists. Each
one names the repository's coding standard for that technology and states no rule of its own.
Those three have no standard declared in `.kyber-weave/kyber-weave.yml`, so the checklists say
"no standard is declared" and carry only non-authoritative "Review traps".

PR #440 (still a draft) added the checklists at
`products/kyber-squad/skills/code-review/references/{typescript,rust,tauri,go}.md`. The
TypeScript checklist points at `docs/standards/typescript/README.md`, which is declared. The
other three have nothing to point at.

Kilo bot review comments on PRs #407 to #439 hit Rust, Tauri and Go code. The council cannot
cite an authoritative standard for those findings until one is declared.

## Done when

- `.kyber-weave/kyber-weave.yml` declares coding standards for `rust`, `tauri` and `go`, using
  the same mechanism the TypeScript standard uses.
- Each declared standard is backed by a document under `docs/standards/<tech>/`.
- The Rust, Tauri and Go checklists point at those standards instead of saying none is declared.
- `ReviewGuidanceCouncilContractTests` is updated to match.
- `docs validate` and `docs drift` are clean.

## Evidence

The reasoning and the Kilo findings are in
[the archived council plan](../archive/plans/2026-10-26-council-catches-kilo-findings.md),
Appendix A and Appendix B.

## Note

The standards content is a design task, not a quick edit. Each standard needs to decide what it
requires and what it forbids, and the checklists must not invent rules. Start with the
architect or a plan.
