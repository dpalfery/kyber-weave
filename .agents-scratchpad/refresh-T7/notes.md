# T7 — Deterministic CLI acceptance (integration tests)

## Result

7/8 integration tests GREEN. One remains RED by contract: `--history-weeks 6` re-upserts already-covered records (`Updated=1` on `pi`).

## Product bug (do not patch in T7)

`dash/kyber/refresh/orchestrator.ts`

- `:65` / `:173–186` — jobs always iterate the full `utcHistoryWindow` DateRange.
- `:337` — `previousFingerprintsFor` omits fingerprints when `uncoveredIntervals` is non-empty, so the unit is re-read as new.
- `:241–244` — existing span ids in that full window are counted as `updated`.

Fix: restrict native-unit slicing/ingest to uncovered intervals (T2 already computes them) so only newly uncovered records are `created` and already-covered rows stay `Updated=0`.

## CLI

`npm --prefix dash run build:cli` succeeded.
`node dash/dist/cli.js dash refresh --help` shows `--history-weeks` default 2, no public `--provider`.
