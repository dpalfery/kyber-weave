# T7 — Are Antigravity's token counts absent, or dropped in ingest?

**Model:** strong (not Haiku — this is an empirical question with a judgement call). **Blocks:** T2, T4. **Time:** ~30 min. **Investigation only — no production edits.**

---

TASK: Session `rd-pi` (Antigravity, Gemini 3.8 Flash) reports `SPANS 5`, `TURNS 5`, `REQUESTS 5`, `DURATION 2.5s` — and `TOTAL INPUT 0`, `CACHE READ 0`, `CACHE CREATION 0`, `TOTAL OUTPUT 0`. Five requests did not consume zero tokens. Establish which of these is true:

- **(a)** Antigravity does not emit token counts. → T2's formatter fix is the whole answer.
- **(b)** It emits them and ingest drops them. → an adapter bug; T2 is still correct but insufficient, and the missing-data story across all eleven harnesses needs re-surveying.
- **(c)** It emits them under attribute names the reader does not recognise. → a mapping fix, and probably not only for Antigravity.

DONE WHEN: you can state which, with the evidence that decides it, and name the file that would change.

WHERE TO LOOK:

1. The raw artifact on disk for `rd-pi` — whatever Antigravity writes, before KyberDash touches it. Does a token count appear anywhere in it?
2. `dash/kyber/synth/readers/` — the Antigravity reader. Which attributes does it map to `usage.input_tokens` / `output_tokens` / `cache_read` / `cache_creation`?
3. `dash/kyber/canon/measurability.ts` — is `token_usage` declared `measured` for this harness? If it is declared measured and arrives as 0, that is a contradiction the store should already be flagging.
4. `records` in `~/.kyberdash/canon.db` for that session — is the value 0, or NULL? **This is the decisive check.** NULL that renders as 0 is a formatter bug. 0 in the store is a reader or upstream bug.
5. OTel GenAI semconv naming — if Antigravity emits `gen_ai.usage.input_tokens` and the reader looks for `gen_ai.usage.prompt_tokens` (or vice versa, across the semconv rename), that is (c).

DELIVERABLE: append a short section to `docs/dash/telemetry-inventory.md` recording, for Antigravity: whether token counters are emitted, under what names, and whether they survive ingest. Mark each claim verified / assumed. Then post the verdict — (a), (b) or (c) — so T2 and T4 can start.

CONSTRAINTS:

- **Investigation only. No production code edits.** If you find the bug, report it; do not fix it. The fix belongs to T2 (formatter) or a new task (reader), and mixing them makes both unreviewable.
- Do not generalise from one session. Check at least two Antigravity sessions and one from a harness whose counts *do* populate, so you know what a working path looks like.
- If the answer is (b) or (c), also state whether the same defect plausibly affects Cursor, Copilot or KiloCode — a reader-level naming bug rarely affects one harness alone.

REFERENCE: `docs/plans/2026-09-06-kyberdash-spine.md` Q1.
