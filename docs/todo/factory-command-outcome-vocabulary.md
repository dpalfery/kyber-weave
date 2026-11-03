---
id: todo/factory-command-outcome-vocabulary
title: The Factory capture command's output does not distinguish the outcomes a user must tell apart
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-02
---

# The Factory capture command's output does not distinguish the outcomes a user must tell apart

`kyberdash kyber capture plugin install --harness factory` reports what it did
(`dash/src/capture/harnesses/factory-plugin/install-command.ts`). Its stdout carries a small
vocabulary — `factory plugin: installed`, `unchanged`, `kept-foreign`, `kept-edited`,
`kept-unreadable`, `would-install`, `skipped-not-installed` — and its refusals go to stderr with
exit 1.

Four outcomes a user has to tell apart do not have a word each:

- **A refusal.** The refusal text is on stderr, so the outcome is visible, but the stdout
  vocabulary does not name it and the exit code is the only machine-readable signal. A caller that
  reads stdout alone sees nothing that says the install did not happen.
- **A residue the harness left behind.** The uninstall drives the harness's own uninstall and
  marketplace removal (`dash/src/capture/harnesses/factory-plugin/marketplace.ts`,
  `marketplaceUninstallCommands`), and the harness's uninstall does not clean everything it
  touched. ADR 0031 requires the residue to be **reported with the exact command that removes
  it**, and no such line is printed.
- **A name collision kept in preserve mode.** The `kept-foreign` token says the tree was not
  written by KyberDash; it does not say that a marketplace or plugin of the same name already
  exists under another owner, which is the case the capture plan's D46 names and refuses.
- **A plugin that is installed but whose hooks the organisation's settings will not load.** The
  command reports the install as done, and the status row reports the plugin as enabled. Nothing
  says the hooks will never fire.

**What a decision must settle.** What the command should say for each outcome. The vocabulary has
to serve two readers at once — a person reading the terminal and a caller reading the output — so
the decision is whether each outcome gets a token, a line, an exit code, or some combination, and
whether the same vocabulary is used on stdout and on the status surfaces. The exit-code contract
is already published
([`docs/cli-command-surface.md`](../cli-command-surface.md)), so changing what an exit code means
is a change to a published contract.

**Deliberately not done here.** The command's output vocabulary was not changed. The install and
uninstall routes, the refusal reasons and the exit codes were settled in this delivery, and a
vocabulary is a published surface that the callers (Squad, `kyber-weave update`) already parse.
The gap is recorded so that the next change to the command's output starts from a list of the
outcomes that need words rather than from the tokens that happen to exist.
