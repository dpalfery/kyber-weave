# Spine e2e host + gate evidence

## Host before

`127.0.0.1:4747` (node 49189) served:

`Dashboard not built yet` / `cd dash && npm install && npm run build`

Playwright `reuseExistingServer` would have reused that placeholder.

## Rebuild / restart

```
npm --prefix dash run build:dash   # exit 0, vite production bundle to dash/dist/dash/
kill 49138 49189                   # placeholder stopped; port free
npm --prefix dash run dev -- web --port 4747 --no-open
```

Curl after restart: real `kyberDash - Local Dashboard` HTML; `GET /api/kyber/harnesses` 200.

`~/.kyberdash/canon.db` read-only (size unchanged by this task).

## Playwright browsers

`npx --prefix dash playwright install chromium` (needed; first run failed with missing headless shell).

## Spec change (only `dash/e2e/spine.spec.ts`)

Q5Q6 rails (`nav-rail-compare` / Sessions / Attention) were already in the product; this spec never clicked `nav-tab-compare`. G2 failed on live data:

1. First matrix `drill-harness-*` is `aider` (sampleCount 0) — no runs.
2. First Claude Code run often has executions but **0 turns** on the run API; `drill-turn-*` never appears.

G2 now clicks `drill-harness-claude-code` and walks up to 15 runs until `drill-turn-*` is visible. Gemini **selector** left in G4a.

## Final command

```
npx --prefix dash playwright test e2e/spine.spec.ts
# exit 0
# 8 passed: G1 G2 G3 G4 G4a G5 G6 G7
```
