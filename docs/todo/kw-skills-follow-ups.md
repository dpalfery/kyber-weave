---
id: todo/kw-skills-follow-ups
title: Follow-ups found while delivering KW skills
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-24
---

# Follow-ups found while delivering KW skills

Found during the run of the plan [Kyber-Weave skills that agents load on demand](../archive/plans/2026-11-24-kw-skills-load-on-demand.md).
None blocks it; each is a separate small change.

- **KyberDash technology gate.** `.kyber-weave/kyber-weave.yml` has no gate for `dash/server`, so
  `review gates --task` on a KyberDash task runs nothing of its own (the plan verified T11 by its dash runner).
- **RED classifier paths and arity.** The RED proof reads `tsc` paths relative to `dash/` while own files are
  repository-relative, so a new TypeScript test file is never "own"; and it rejects a CS1729 constructor-arity error as
  a RED reason.
- **`review gates` and `--artifacts-path`.** The verb rejects the flag, so a worker cannot share a root build output.
- **Developer-tier task gates and RED.** The `dev-easy` task gates do not cover `KyberWeave.Contracts` and
  `KyberWeave.Application`, so a RED run for work there compiles nothing of its own.
- **`PricingFixture` under a root `--artifacts-path`.** It walks up to an ancestor folder named `server` and breaks
  when the build output moves.
- **Stale constant.** `DocsLayout.ScopeFolderFiles` still names the retired skills file shape.
- **Skill list problems are not surfaced.** `KwSkillList.Problems` (a malformed `.skills.yml`) is not shown in the
  task prompt or the coding-standard answer; only `docs validate` reports it.
- **Antigravity marker gate.** It blocks a role dispatch that carries no marker, which the role-only KW skills
  insertion makes more visible.
- **Converting a legacy `.skills.md`.** `docs standards migrate` only warns (`KW-DOC-SKILL-005`); a conversion could
  follow. A global KW skills view across sessions in KyberDash is also not built.
