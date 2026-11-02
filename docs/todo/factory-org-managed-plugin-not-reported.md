---
id: todo/factory-org-managed-plugin-not-reported
title: An organisation-controlled Factory plugin that will never load is not reported
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# An organisation-controlled Factory plugin that will never load is not reported

An organisation's settings can refuse the marketplace KyberDash publishes and refuse the
enablement of the plugin it installs. The refusals are visible: the harness's own commands fail,
and KyberDash's install reports the refusal
(`dash/src/capture/harnesses/factory-plugin/marketplace.ts`,
`dash/src/capture/harnesses/factory-plugin/install.ts`).

The case that fails **silently** is different. An organisation's settings can load only
organisation-managed hooks and hooks from organisation-enabled plugins. A user-scope plugin then
installs, registers as enabled, and its hooks never fire: the install succeeds, the plugin list
says the plugin is installed, and the capture produces nothing. KyberDash reads the harness state
through `droid plugin list --scope user` and `droid plugin marketplace list`
(`readFactoryHarnessState`), and neither command reports whether the hooks will load.

The status surface therefore reports a plugin as installed and enabled while the capture is inert.
A reader of the Settings row, and of `capture status`, has no way to tell a working capture from
one the organisation's policy has disabled.

**What a decision must settle.** What the surfaces report when the hooks will not load, and how
that state is known.

- Whether the harness's own output carries the information. If `droid plugin list --scope user`
  or `droid plugin marketplace list` reports the organisation's policy — an approved marketplace
  list, an enabled-plugin allowlist, or which hooks a scope loads — then KyberDash can read it
  the same way it reads everything else about the harness, through the harness's own CLI. That
  question is answerable only against the real CLI.
- Whether the state is detectable at all from outside. If the harness exposes no such report,
  then the honest position is that KyberDash cannot tell, and the surfaces must say so rather than
  report "installed" as though it meant "capturing".
- What the report is called. The existing vocabulary has words for the states KyberDash can
  observe (`plugin: installed`, `plugin: not installed`, `factory harness: plugin-installed`); a
  state meaning "installed, and the hooks will not load" needs its own, and it must not read as
  a fault in the capture.

Whichever is chosen, the honest-unobservability rule applies: a capture that will not run must
never be reported as one that is running, and an absence of knowledge must be reported as an
absence of knowledge rather than as a healthy state.

**Deliberately not done here.** The status surface was not changed and no state was added. The
information the report would need is the organisation's policy, which KyberDash can only read
through the harness's own commands, and the harness's answer is not known. This todo records the
gap so that a user whose capture is silently inert has a place to find that it is a known state
rather than a mystery.
