Task: fix issue #1143 (read: gh issue view 1143). Branch: create fix/1143-quit-feedback off main.

## Behavior to build
Pressing q during the post-paint background index currently drains the fill silently (~16.5s deterministic on a 21k-file corpus) before exiting. Wanted, exactly:
1. First q during an active fill: begin the graceful drain AND immediately render a status line in the dashboard footer area: "Finishing background index so the next launch starts warm - press q or Ctrl+C again to quit now" (no em-dashes; match existing footer styling).
2. A second q (or Ctrl+C at any time) during the drain: immediate exit through the same abrupt path Ctrl+C already uses - #1109 made abrupt exit kill-safe (nothing marked seen without being parsed; resume converges), so this is safe by design. Do not weaken those invariants.
3. q when NO fill is active: exits immediately as today (no status line flicker).
4. Ctrl+C semantics unchanged (immediate, always).

## Where
src/dashboard.tsx useInput (the q/Ctrl+C handler shipped in #1142) + wherever fill-active state is visible to the dashboard (the #1109 fill exposes indexing progress for the banner - reuse that signal; do not invent new global state if a fill-status already flows to the UI).

## Tests (repo conventions - see tests/dashboard-exit.test.ts from #1142)
- first q during active fill: no exit, status line rendered;
- second q: exits;
- Ctrl+C during drain: exits;
- q with no active fill: exits immediately, no status line.

## Gates
npx vitest run full suite green (~3708); npx tsc --noEmit clean. CHANGELOG: add to the section "## 0.9.22 - 2026-08-25" (NOT Unreleased - this ships in 0.9.22) under Fixed (TUI), house style, cite #1143. Commit on branch, push, STOP (no PR). Do not commit BRIEF.md. Only src/dashboard.tsx-adjacent + tests + CHANGELOG; do not touch the save/fill machinery itself.
