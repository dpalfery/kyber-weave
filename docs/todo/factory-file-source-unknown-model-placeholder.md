---
id: todo/factory-file-source-unknown-model-placeholder
title: The Factory file source publishes the literal string "unknown" as a model
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# The Factory file source publishes the literal string "unknown" as a model

The Factory capture plan's decision **D40** already names this as a follow-up, in its own words:
the placeholder is "the file source's own placeholder, not a measured model", and changing it
"would be a file-source change, which D21's boundary excludes; the follow-up is recorded, not
done". Its risk table names the same finding: "A reader takes the file source's `unknown`
placeholder for a measured model". This todo is the reminder the plan's harvest requires; it does
not restate D40, and it adds the question D40 leaves open.

When a Factory session's companion `<session-id>.settings.json` names no model, the file source
substitutes the literal string `unknown` for it (`dash/src/providers/droid.ts`, the
`sessionModelDisplay` assignment), and a shipped characterization test pins exactly that
(`dash/src/providers/droid.test.ts`). The substituted string travels into the synthesized record's
raw attributes, and the session reads its model from those attributes
(`dash/src/canon/sessions.ts`).

So a session whose plugin turn names no model and whose companion file names none publishes the
string `unknown` as its model. A reader of the dashboard sees a model called `unknown` and cannot
tell it from a model Factory actually reported.

The placeholder is the file source's own, and it predates this work: it is pre-existing
behaviour, outside the capture plugin's scope, and the capture plan deliberately did not change it.
The governed documents name it as the file source's own literal rather than a measurement
([telemetry inventory](../dash/telemetry-inventory.md#the-model-a-factory-session-publishes)),
so a reader is not misled — but the string is still in the data.

**What a decision must settle.** Whether the placeholder should become an absent model, and what
that would change for every file-source harness.

- Replace the string with an absent model. The session then publishes no model, and the honest
  reading is that the model was not measured. The cost is that the file source's parser contract
  changes for every harness that reads it, and the characterization test that pins `unknown`
  changes with it.
- Keep the string and make it unmistakable in the surfaces — a display that renders the
  placeholder as "not reported" rather than as a model name. This keeps the parser contract
  untouched and confines the change to the display layer, but it leaves the literal in the data
  and in the exported rows.
- Keep it as it is, on the grounds that the string is established and the plan's model chain
  already names it as the file source's own. That is defensible only while every reader of the
  value knows what it is.

**Deliberately not done here.** The parser was not changed. The placeholder is a file-source
behaviour that predates the capture work, and the plan's scope boundary excluded changing it; the
follow-up was recorded there rather than done here. This todo carries the reminder, and states
the blast radius — the string is in the file source, not in the plugin path, so a fix is a
file-source change and reaches every harness that reads that provider.
