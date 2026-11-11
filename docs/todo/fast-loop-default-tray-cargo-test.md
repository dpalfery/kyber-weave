---
id: todo/fast-loop-default-tray-cargo-test
title: The fast loop runs an unfiltered cargo test of the tray crate
doc-type: todo
component: ReviewCouncil
status: draft
owner: dpalfery
last-reviewed: 2026-11-06
---

# The fast loop runs an unfiltered cargo test of the tray crate

When a tray Rust file changes, the default path of `scripts/fast-loop.sh` (no argument) runs the
tray's Rust checks. Its last step is a bare `cargo test` of the whole tray crate:

```text
(cd dash/tray/src-tauri && cargo fmt --check && cargo clippy -- -D warnings && cargo test)
```

This step does not go through `scripts/proc-guard.sh`, so the full-run refusal in
[ADR 0059](../adr/0059-full-test-runs-are-reserved-for-the-council-and-ci.md) never sees it. A bare
`cargo test` is a full run under that ADR, so a developer agent can run one through the fast loop.
It is listed as a known limit in the ADR.

**Decision needed.** Choose one, and record it in the ADR's known-limits list:

- filter the step to the tests that cover the tray change;
- route the step through `scripts/proc-guard.sh` so the classifier applies;
- or keep it as the documented exception, with the reason written next to the step.

**Acceptance.** The decision is recorded. If the step is filtered or routed, a tray Rust change no
longer runs an unfiltered `cargo test` through the default fast-loop path, and the targeted
fast-loop tests pin that. If it is kept, the ADR 0059 known-limits entry names the exception and
its reason.
