---
id: todo/review-council-rust-tauri-go-standards
title: Declare Rust, Tauri and Go coding standards for the review council
doc-type: todo
component: ReviewCouncil
status: superseded
owner: dpalfery
last-reviewed: 2026-10-27
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# Declare Rust, Tauri and Go coding standards for the review council

**Superseded** on 2026-10-27 by the plan [Declare Rust, Tauri and Go coding standards](../plans/2026-10-27-declare-rust-tauri-go-standards.md)
(`plans/2026-10-27-declare-rust-tauri-go-standards`, archived 2026-10-27, branch `feat/declare-rust-tauri-go-standards`).
That plan carried the work below and was archived with this todo.

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
[the archived council plan](../plans/2026-10-26-council-catches-kilo-findings.md),
Appendix A and Appendix B.

## Note

The standards content is a design task, not a quick edit. Each standard needs to decide what it
requires and what it forbids, and the checklists must not invent rules. Start with the
architect or a plan.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| DW1 | Are `rust`, `tauri` and `go` declared in `ontology.technologies`? | Yes, all three, with `docs init` regenerating the Config Reg block. | The full decision record is in the archived plan's Decisions table (Q1, Q3). | archived with plan, autonomous: recommendation accepted (2026-10-27) |
| DW2 | Is each declared technology backed by a `current` standard under `docs/standards/<tech>/`? | Yes. Each standard is grounded in the repository's own gates, files and settings. | Archived plan Q2, D4-D6, D11. | archived with plan, autonomous (2026-10-27) |
| DW3 | Do the Rust, Tauri and Go checklists point at those standards? | Yes. Each names its in-repo path and keeps its not-declared fallback. | Archived plan D7. | archived with plan, autonomous (2026-10-27) |
| DW4 | Is `ReviewGuidanceCouncilContractTests` updated to pin all of this? | Yes, one theory with four rows (typescript, rust, tauri, go). | Archived plan D2 and §5. | archived with plan, autonomous (2026-10-27) |

## Design harvest

| ID | Target component | Target doc type | Target doc | Durable item |
|---|---|---|---|---|
| DW1 | ReviewCouncil | index | [coding standards index](../../standards/README.md) | The declared list names Rust, Tauri and Go, and the rules for what a standard holds |
| DW2 | ReviewCouncil | coding-standard | [Rust](../../standards/rust/README.md), [Tauri](../../standards/tauri/README.md), [Go](../../standards/go/README.md) | The standards themselves, with the Rust and Tauri split and the Go no-code scope |
| DW3 | ReviewCouncil | architecture | [review guidance](../../code-review/architecture.md#review-guidance) | Each checklist names its declared standard; the not-declared fallback stays for host repositories |

## Closeout mapping

| ID | Destination | Impact component | Last-reviewed | Status |
|---|---|---|---|---|
| DW1 | [`.kyber-weave/kyber-weave.yml`](../../../.kyber-weave/kyber-weave.yml) `ontology.technologies`; [coding standards index](../../standards/README.md) "This repository declares" | ReviewCouncil | 2026-10-27 | archived |
| DW2 | [Rust](../../standards/rust/README.md), [Tauri](../../standards/tauri/README.md) and [Go](../../standards/go/README.md) coding standards | ReviewCouncil | 2026-10-27 | archived |
| DW3 | `products/kyber-squad/skills/code-review/references/{rust,tauri,go}.md`; [review guidance](../../code-review/architecture.md#review-guidance) | ReviewCouncil | 2026-10-27 | archived |
| DW4 | [coding standards index](../../standards/README.md) (the invariant it pins: each declared standard is current and named by its checklist); test pin in `tests/KyberWeave.Tests/ReviewGuidanceCouncilContractTests.cs` | ReviewCouncil | 2026-10-27 | archived
