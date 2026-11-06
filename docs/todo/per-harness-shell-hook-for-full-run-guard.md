---
id: todo/per-harness-shell-hook-for-full-run-guard
title: Close the full-run guard with a shell hook in each harness
doc-type: todo
component: ReviewCouncil
status: draft
owner: dpalfery
last-reviewed: 2026-11-06
---

# Close the full-run guard with a shell hook in each harness

[ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) refuses a full test
run through `scripts/proc-guard.sh`, but only when the command is started through that wrapper and
the caller is not a terminal or the council. A raw `dotnet test` typed into a harness's shell
bypasses it. The strongest closure is a shell hook in each harness that runs
`scripts/proc-guard.sh classify -- <argv>` before every shell command and refuses a `full` result.

**Work.** Add a pre-tool-use style hook for each harness the Squad target catalogue lists
(`src/KyberWeave.Core/Squad/Deployment/SquadTarget.cs`), which includes Claude Code, Copilot CLI,
Factory, Pi, OpenCode and Antigravity. The hook replaces the soft caller test (the terminal check)
with the classifier's answer. Each harness has its own hook format, so the shape of each hook is a
per-harness decision and is not assumed here.

**Three open classifier gaps from the council.** Fix them in the same change:

- `dotnet run --project <test project>` classifies as partial. It should classify as full.
- Wrapper launchers (`nohup`, `caffeinate`, `sudo`, absolute-path wrappers) are not unwrapped.
- `npm --prefix <absolute path> run test` classifies as partial. It should classify as full.

The likely fix is to classify by the first `dotnet`, `npm` or `cargo` word after the wrappers, and
to match a wrapper by its basename.

**Acceptance.** On each listed harness, a developer agent's full-suite command is refused before the
shell runs it, and the refusal names a targeted alternative. The three classifier gaps have failing
rows first and then pass. The ADR 0059 known-limits list is updated to drop the items this closes.
