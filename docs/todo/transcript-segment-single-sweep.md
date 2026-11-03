---
id: todo/transcript-segment-single-sweep
title: segmentTranscript filters every step once per call
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# segmentTranscript filters every step once per call

`segmentTranscript` (`dash/src/providers/antigravity-transcript.ts`, lines 239 to 255) runs one filter
over all transcript steps for each segment. The cost is O(calls x steps) for a conversation. The
conversations seen so far are small, so this is not a measured problem.

If it becomes one, replace the per-segment filter with a single sweep over the steps sorted by index,
advancing a pointer through the ascending `last_step_index` list. Keep the coverage and single-result
rules unchanged (plan D4), and pin the output with the existing segmentation tests before changing
the code.
