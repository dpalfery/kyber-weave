---
id: plans/tasks/T6-left-edge-artifact
title: T6 — Find what renders outside the shell
doc-type: plan
status: needs-review
owner: dpalfery
last-reviewed: 2026-09-06
component: KyberDash
---

# T6 — Find what renders outside the shell

**Model:** Haiku. **Run after:** T1–T4 merge. **Report before editing.**

---

TASK: Clipped glyphs and partial boxes render down the full page height at x < 20px, outside the app shell — visible in the current screenshot as fragments along the left edge. Find the cause.

DONE WHEN THIS PASSES (it fails right now — run it first and read the failure):

```bash
npm --prefix dash run dev &
npx --prefix dash playwright test e2e/spine.spec.ts -g "G7"
```

PHASE 1 — REPORT ONLY. Do not edit anything yet.

Run the gate, read the element list it prints, and report:

- Which element or component is laying out off-canvas.
- Whether it is a stray absolute/fixed position, a negative margin, a transform, an off-canvas mobile drawer that is not `visibility: hidden` when closed, or something else.
- Which file owns it.

`App.tsx` contains a mobile sidebar that slides off-canvas via `max-md:-translate-x-full` and a `max-md:invisible` guard. If the cause is there, **stop and report** — `App.tsx` belongs to T8 and a fix there must be folded into that task.

PHASE 2 — only if the owning file is not `App.tsx`, and only after reporting.

Narrowest possible fix. One file if you can.

CONSTRAINTS:

- Do not add fixtures or mocks. The gate runs against the real dev server and the developer's real `~/.kyberdash/canon.db`.
- Do not write a new unit test as evidence of completion. The gate above is the only evidence that counts.
- If the gate cannot pass for a reason outside your owned files, STOP and report what blocks it. Do not widen your file list.
- Do not "fix" this with `overflow: hidden` on a container. That hides the symptom and will break a legitimate scroll or popover later. Find the element.
- Verify in a browser at 1280px and 1920px, and once below 768px to confirm you have not broken the mobile drawer.

REFERENCE: `docs/plans/2026-09-06-kyberdash-spine.md` Phase A.
