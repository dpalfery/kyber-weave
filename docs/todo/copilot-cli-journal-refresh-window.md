---
id: todo/copilot-cli-journal-refresh-window
title: Copilot CLI journal messages outside the refresh window
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# Copilot CLI journal messages outside the refresh window

The remaining 1,784 journal messages on the owner's store are dated 2026-06-01 to 2026-09-02. The
refresh window (2026-10-11 to 2026-10-18 in the T0b trace) drops them in `sliceCallsToWindow`
(`dash/src/refresh/source-reader.ts`, around line 232). They are never read, so they produce no
records and no per-message tool or skill attribution.

This is operational rather than a code defect. The choice is between a wider one-off re-import and
documented guidance on the window. Either way the outcome depends on
[copilot-cli-output-tokens-skipped-messages](copilot-cli-output-tokens-skipped-messages.md), because
the messages that the window keeps are also the ones the `outputTokens` default skips.
