---
id: todo/kyberdash-copilot-env-override-decision
title: Decide whether capture enable should manage Copilot's environment override
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-16
---

# Decide whether capture enable should manage Copilot's environment override

`enable` now writes the half of Copilot's OTLP contract that was missing. It adds
`github.copilot.chat.otel.dbSpanExporter.enabled`, writes its keys as the flat dotted form
VS Code reads rather than the nested `github` tree it silently ignores, and `status`
resolves the exporter Copilot will actually get instead of merely checking that keys exist.
Before that, every key could be present and correct while the extension handed the SDK a file
exporter pointed at `os.devNull`, discarding every span — and `status` called it a pass.

What `enable` still does not do is reach the environment. Copilot's pipeline additionally
requires `COPILOT_OTEL_ENABLED`, and a GUI-launched VS Code does not inherit a shell profile,
so on macOS it has to arrive through `launchctl setenv`. Setting it automatically means a
login-scoped system mutation with no per-key inverse — a different kind of change from editing
a config file this writer can receipt and restore — so it was left to the owner, and `status`
now names the exact command instead.

That leaves a consequence worth a decision. The status route runs under `kyberdash web`, not
under the editor, so it cannot see the editor's environment. It therefore withholds the
verdict — reporting the resolved line without raising a warning — rather than warning about an
override it never saw. The dashboard can say the settings are right; it cannot say Copilot is
delivering.

Choose how to close that gap: manage the environment (and have `disable` undo it), surface the
override as unverifiable in the UI rather than silent, or keep the CLI as the diagnostic
surface and accept that the dashboard is settings-only. All three are defensible. Leaving it
undecided means the tray can show a healthy Copilot row while the editor discards every span,
which is the failure this change exists to make visible.
