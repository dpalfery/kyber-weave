---
id: todo/factory-parent-child-lineage-not-projected
title: A Factory child session's parent is discovered but never linked
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# A Factory child session's parent is discovered but never linked

A Factory child (subagent) session is a first-class session on disk: it has its own transcript and
its own `<child-id>.settings.json` companion file, and the file source discovers it as a session of
its own. Its counters are paired from that companion file, so a child session's tokens and cost
are its own and never the parent's (`dash/src/providers/droid.ts`,
`dash/src/canon/droid-pairing.ts`).

Nothing links that child to its parent. The plugin cannot supply the link: Factory's documented
`SubagentStop` payload names `task_name`, `task_result`, `task_error` and `stop_hook_active` and
carries no child session id, so the event produces no span at all
(`dash/src/capture/harnesses/factory-plugin/mapper.ts`). The factory transcript's own `parentId`
does carry the linkage, and the file source reads the transcript — but it reads the session id
and the messages, and nothing projects a parent from it.

The consequence is that a Factory child session appears in the dashboard as an unlinked session:
its own turns, tools, tokens and cost are all present, and nothing says it ran under a parent.

**What a decision must settle.** Where the lineage should come from, and whether it should be
projected at all. The candidates:

- Project it from the file source's transcript, where the evidence already is. The file source
  reads the transcript today, so this is a small addition at that seam rather than a new
  collection path. It would give the child a `parent_session` the projection already knows how to
  read (`dash/src/canon/sessions.ts`).
- Project it from the plugin, by giving the child-session span a parent attribute. This needs a
  child identity the hook payload does not carry, so it is not available as the contract stands.
- Leave the lineage unprojected, and say so in the governed documents — that a Factory child is a
  session without a parent, and that its spend is counted at the child rather than folded into
  the parent. The plan already decided the second half of that (a child's usage is never folded
  into the parent's totals), so this option only needs the first half stated.

Whichever is chosen, the honest-unobservability rule applies: a session with no linkable parent
must read as an unlinked child, never as a fabricated one, and a parent with children it cannot
see must not be reported as a session with no subagents.

**Deliberately not done here.** The plugin's child-session span was withdrawn in this delivery
because the payload carries no child identity, and no file-source change was made. The lineage
question was left open rather than answered by guesswork, because the evidence lives in the
transcript and the transcript is the file source's business, not the plugin's.
