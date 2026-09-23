---
id: specs/kyberdash-context-surfaces/index
title: KyberDash context surfaces specification
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-09-18
---

# KyberDash context surfaces specification

End KyberDash's soft-fork relationship with `getagentseal/codeburn`, delete the inherited
features that are not about context, and rebuild the glanceable surfaces around context
troubleshooting: one Tauri 2 + React tray for macOS and Windows, URL-addressable web
dashboard views the tray links into, and a non-interactive `kyberdash report` a person or a
coding agent can act on.

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 15 requirements in EARS form |
| [Design](design.md) | Architecture, one `ContextReport` model with its renderers, tray internals, installer and updater, error handling, testing, and a delegation plan by workstream |
| [Tasks](tasks.md) | 41 tasks in 10 groups plus a closeout task, test-first, each tagged with the stream and harness it is delegated to |

## Decisions settled before writing

These were put to the owner and answered on 2026-09-18. The requirements encode them; a
change to one is a change to the requirements, not an implementation choice.

| Decision | Outcome | Requirements |
|---|---|---|
| Upstream relationship | One-time fork. The remote, merge zone, boundary rules and `MergeBoundaryTests` go. | 1.1–1.7 |
| ADR 0006 | Only its soft-fork decision is obsolete. ADR 0020 restates the three that survive (embedded receiver, span-shaped canonical model, SEA distribution through `install.sh`) and re-decides the engine language, whose original rationale was mergeability. ADR 0006 then moves to `docs/archive/adrs/`, as the ADR index prescribes. | 1.3, 1.8–1.10 |
| Gates | Every shipped surface under `dash/` is subject to the repository's gates and CodeQL. | 4 |
| Layout | `dash/kyber` and `dash/src` merge into `dash/src`; `dash/dash` becomes `dash/web`; the tray is `dash/tray`. | 3.1, 3.2 |
| Inherited features | Deleted now, not deferred: spend commands, quota and subscription tracking, Swift menu bar, Electron, GNOME, Ink dashboard, upstream MCP. `doctor` stays. Provider parsers stay. | 2 |
| Identity | npm `kyberdash`, `KYBERDASH_*` variables, `~/.kyberdash/`, bundle id `io.github.dpalfery.kyberdash`, MIT attribution kept. | 1.5, 3.3–3.7 |
| Native surfaces | Start over: one Tauri 2 + React app for macOS and Windows, carrying over only the hardened spawn, autostart and badge code. Linux is out. | 6, 13.4 |
| Engine language | Stays TypeScript. The REST API is the seam; a .NET port would be its own specification. | 7.1, 7.5 |
| Tray data | The tray owns one `kyberdash web --no-open` server and reads its REST API. `status --format menubar-json` and the `kyber` status field are retired. | 7 |
| Spend | Context first. Cost is one secondary figure; no quota, plans, capacity or currency. | 8.9, 8.10, 9.6, 14.2 |
| Popover anchor | The latest session above, recurring findings over a window below. | 8.1–8.5 |
| Freshness | The tray runs incremental refresh on a cadence plus Refresh now; hosting the OTLP receiver is opt-in. ADR 0021 records it and supersedes only ADR 0016's refresh-button clause, because ADRs are never edited. | 10 |
| Full dashboard | Opened in the browser against the tray's server, with deep links into specific views. | 5, 8.6, 8.7 |
| CLI report | Non-interactive text, Markdown or JSON; the Ink TUI is retired. | 11 |
| Signing | macOS: Developer ID of team `J2UNNQ466J`, notarized and stapled; a release without the credentials fails rather than shipping unsigned, and install verifies the team id. Windows: Authenticode if a certificate is configured, otherwise unsigned with a todo. | 12.2–12.6, 13.4 |
| Updates | `kyber-weave update` updates an installed tray by delegating to the updated `kyberdash`, with a `--no-menubar` opt-out. | 15 |

## Defaults chosen without a question

The owner accepted the recommended answers and asked to proceed before these branches were
put to them. Each is a default the requirements state explicitly so it can be reviewed, not
an assumption hidden in the design.

| Default | Where |
|---|---|
| Popover order and content: harness selector, latest session panel, three findings, data-health footer, actions | 8.1–8.8 |
| Status item shows the latest turn's context pressure; attention at 70%, critical at 90%, both configurable | 9.1, 9.2 |
| Poll every 15 s while the popover is open, 60 s while closed; refresh every 5 minutes | 7.7, 10.1 |
| Findings window of 7 days; three findings in the tray, five in the report | 8.5, 11.5, 11.11 |
| Report section order, and a staleness hint when the last refresh is over an hour old | 11.2, 11.4 |
| `dash refresh` gains an exclusive lock and a new exit code | 10.4 |
| `kyberdash web --view <path>` and a machine-readable URL line on stdout | 5.6, 5.7 |
| Launch at login defaults to off | 6.8 |
| One specification for all of the work, with tasks sequencing the severance first | — |

## Out of scope

- A Linux tray (recorded as a todo, 13.4).
- Porting the KyberDash engine to .NET.
- Exposing context troubleshooting over MCP. The upstream `mcp` command is deleted.
- Sharing usage between devices.

## Status

**Draft**: requirements, design and tasks await approval. Stream A starts once they are approved.
