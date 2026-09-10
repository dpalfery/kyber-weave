# Handover: KyberDash rebuild-phase performance

## What this session is for

Make `kyber build` / the rebuild phase of `dash refresh` fast. Ingestion was fixed in a
previous session and is no longer the bottleneck; the derived-table rebuild now is.

Everything below was measured on this machine's real corpus, not estimated.

## Repo state — READ FIRST

- Repo: `/Users/dave/git/personal/kyber-weave`, branch `feat/kyberdash-version-1`.
- **29 files are modified and uncommitted.** They are a previous session's work: Claude Code
  ingestion, cross-path dedup, an OOM fix, harness taxonomy, and dashboard changes. All
  4,686 tests pass. **Do not revert or "clean up" these changes** — start from the working
  tree as it is. Read `git diff` before touching anything.
- Read [`AGENTS.md`](AGENTS.md) before you start. `TreatWarningsAsErrors` is on with
  `AnalysisMode=all`; a warning fails the build.
- The dash TypeScript project lives under `dash/`. Run tests with `cd dash && npx vitest run`.
  The suite takes ~40-60s. `npx tsc --noEmit -p tsconfig.json` has **5 pre-existing errors**
  in `dash/dash/src` (App.tsx findLastIndex, Attention.tsx, RunDetail.tsx) — those are the
  baseline, do not count them as yours and do not fix them as a side quest.

## The problem

A full rebuild processes ~1,900 sessions at roughly **one session per second** — around 30
minutes. Ingestion of the same corpus (2,252 sources, 85,604 records, 12 harnesses) takes
about **3 minutes**. The rebuild is now ~10x the cost of the ingest it follows.

Profiled on 40 real sessions (4.3M chars, 1,695 tokenizer calls):

```
record load only : 4283 ms
+ tiktoken       : 8154 ms   (tokenizing ≈ 3871 ms)
```

Roughly half decompression, half tokenization. Treat those as indicative, not precise — the
measurement had OS page-cache contamination between loops. **Re-profile properly before
optimizing.**

## Two identified causes

### 1. The tokenizer cache is never used

`dash/kyber/canon/tokens.ts:186` exports `tokenize(text, model, store, countTokens)`, which
memoizes into the `token_cache` table — this exists specifically for R4.6.

`dash/kyber/canon/sessions.ts:289` instead calls `loadO200kCounter()` and passes the raw
counter into `buildSessionRow`, bypassing the cache entirely.

Evidence: `sqlite3 ~/.kyberdash/canon.db "select count(*) from token_cache;"` returns **0**.
It has never held a row.

Caveat worth testing before committing to this: hashing the text plus a SQLite lookup may not
beat tiktoken for short strings, and a *first* build pays full cost either way. The win is on
rebuilds, which is the common case here. Measure both.

### 2. The corpus is decompressed four times per rebuild

`recordsForSession` decompresses `parts_json` (zlib; ~1.4 GB compressed, several times that in
memory). It is called once per session or execution from four places in one rebuild:

- `dash/kyber/canon/sessions.ts:294` — buildSessions
- `dash/kyber/canon/runs.ts:409` — buildRuns pass 1 (grouping)
- `dash/kyber/canon/runs.ts:629` — buildRuns pass 3 (outcome derivation)
- `dash/kyber/canon/findings.ts:40` — buildFindings

**Important constraint:** `runs.ts:629` is deliberate. `buildRuns` used to hold every session's
records in memory at once and died with a heap OOM on this corpus. It was restructured to keep
only a light projection during grouping (`toLinkageRecord`) and re-read full records per run
for `deriveOutcome`. Measured after that fix: 1,748 runs, 137s, **773 MB peak**. Any change
that reintroduces holding the whole corpus is a regression — verify peak RSS, not just wall
clock.

A promising direction: `buildSessions` → `buildRuns` → `buildFindings` each walk the same
records. A single pass that feeds all three, or a bounded LRU keyed by session id, would cut
decompressions without unbounded retention. `deriveOutcome` needs `content`/`parts`/`raw`;
`linkExecutions` needs only `spanId`/`parentSpanId`/`source`/`measurability`.

## How to measure honestly

Do not trust wall clock alone — the OOM fix traded memory for time on purpose, so a change
that "speeds things up" by holding more may just be re-breaking it.

```bash
cp ~/.kyberdash/canon.db /tmp/bench.db
cd /Users/dave/git/personal/kyber-weave/dash
NODE_OPTIONS=--no-deprecation npx tsx src/cli.ts kyber build --db /tmp/bench.db
```

Report before/after for: wall clock, peak RSS (`process.memoryUsage().rss`), and
`token_cache` row count. Write throwaway probes into the scratchpad, not the repo.

The live DB `~/.kyberdash/canon.db` currently holds ~85,600 records across 12 harnesses and is
a good benchmark corpus. A refresh may still be running against it when you start — check
`ps aux | grep "dash refresh"` and either wait or work on a copy.

## Definition of done

1. Rebuild is materially faster, with before/after numbers for time **and** peak RSS.
2. Peak RSS during `buildRuns` has not regressed past ~800 MB on this corpus.
3. `cd dash && npx vitest run` — all 4,686 pass, no new tsc errors beyond the 5 baseline ones.
4. New tests covering whatever you change. If you wire in `token_cache`, assert it is
   populated and that a second build hits it.
5. Output of `kyber build` is unchanged: same session, run, rollup and finding counts on the
   same input. This is the real safety property — it is a cache rebuild, so identical input
   must give identical derived tables.

## Known-good baseline on the current corpus

```
Sources: 2252   Synthesized: 84990   Accepted: 61909   Problems: 219
Sessions: ~1895 built   Rollups: 14   Findings: 543
Harnesses with data: cursor, antigravity, copilot, opencode, codex, pi,
                     kilo-code, claude-code, gemini, droid
```

## Out of scope

- The dashboard UI. It is working and verified in a browser.
- Ingestion speed. Already fixed (52M upserts → 61.6K; >13 min → 76s on the same input).
- Cross-path dedup. Already fixed with tests over all nine harness alias pairs.
- The 5 pre-existing tsc errors.
- Committing. The previous session left everything uncommitted deliberately so the whole
  change set can be reviewed as one.
