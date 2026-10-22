---
id: adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys
title: The KyberDash Capture Command Owns Harness Telemetry Keys, with Key-Level Edits, Receipt Restore, and a Tray-Hosted Receiver
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-21
---

# ADR 0031: The KyberDash Capture Command Owns Harness Telemetry Keys, with Key-Level Edits, Receipt Restore, and a Tray-Hosted Receiver

## Status

Accepted, 2026-10-07. Records decisions D3, D9 and D11 of the KyberDash context-capture plan.

This is a deliberate departure from
[ADR 0026](0026-kyber-utilities-owned-files-not-settings.md), scoped to KyberDash. ADR 0026
remains in force for Kyber Utilities, which still never reads or writes a shared harness
settings file. ADR 0026 is not edited. This record borrows its receipt-and-digest pattern.

Amended 2026-10-10 ([issue #319](https://github.com/dpalfery/kyber-weave/issues/319)): the
"tray-hosted receiver" reading of decision 4 and of the Context paragraph below no longer
holds as written. The `kyberdash web` server now hosts the receiver (`ReceiverHost`,
`dash/src/jobs/receiver-host.ts`) and owns it through the shared setting
`settings.receiver.hosted`, so a "host receiver" switch is now the engine's switch rather
than tray-local state, and the tray's former `host_receiver` setting is no longer read
([ADR 0033](0033-kyberdash-surfaces-are-display-layers.md),
[`rules/kyberdash-display-layer`](../rules/kyberdash-display-layer.md)). Capture's own
position — it installs no service, it writes no telemetry key beyond the export keys above —
is unchanged, and so is the liveness probe this record depends on.

Amended 2026-10-11 (the harness-OTel settings-drawer plan): decision 1's "The user runs it" is
extended rather than contradicted, decision 4 gains the route's receiver coupling, and the
"a green `capture enable` is not capture" consequence below is revised; the departure's scope
and its receipt, drift and restore contracts are unchanged. The `kyberdash web` server now
serves the capture engine in process behind `/api/kyber/capture` — `GET` returns structured
status, `POST` takes `{action, harness, dryRun}` — and the dashboard's Settings drawer uses it
for enable and disable of the same engine: one harness per explicit user action, a dry-run
preview first, and never a spawned CLI or script, so
[ADR 0033](0033-kyberdash-surfaces-are-display-layers.md) and
[`rules/kyberdash-display-layer`](../rules/kyberdash-display-layer.md) stand unbroken — a
click in the dashboard is the user running the same engine. Capture now runs on Windows as
well, in process in TypeScript, with no `.bat`, `.cmd` or `.ps1` shipped and the previous
`win32` refusal withdrawn: Claude resolves at `<home>\.claude\settings.json`, VS Code at
`<appData>\Code\User\settings.json` and `<appData>\Code - Insiders\User\settings.json`
(appData injected by the caller, else `APPDATA`, else `<home>\AppData\Roaming`), and OpenCode
at `<home>\.config\opencode\opencode.jsonc`, with XDG variables ignored on every OS. The route
couples the receiver to a successful enable where the CLI deliberately does not (D5, D7, D20):
a non-dry enable that ends in `wrote` or `noop`, or that wrote at least one file before a later
file refused (a partial write), turns `settings.receiver.hosted` on when it is off, after the config write succeeds, and a setting write that fails is still a 200 carrying
`receiver.enableFailed: true`; disable never touches the receiver, including when the last
managed harness is turned off — the dashboard says the receiver is still hosted — and
`capture enable` at the terminal keeps printing the receiver remedy. The route also filters
applicability (D19): it writes only config files that already exist or whose host application
is detected, so enabling Copilot never creates a phantom VS Code Insiders profile directory,
while the CLI keeps writing every declared file. The harness write scope is unchanged — the
managed harnesses stay claude-code, copilot and opencode, with codex, pi and antigravity
status-only — and [ADR 0026](0026-kyber-utilities-owned-files-not-settings.md)'s bar on Kyber
Utilities writing harness settings is untouched.

Amended 2026-10-21 (the Kilo Code collection plan): the harness scope gains `kilo` as a
**status-only** harness, appended last in `CAPTURE_HARNESS_REGISTRY`. Kilo is the first harness
whose absence from the managed set is itself the decision rather than an unmapped surface. It has
no config key that turns on local telemetry export — `OTEL_EXPORTER_OTLP_ENDPOINT` is
environment-only and no config field sets it — and its only on/off key,
`experimental.openTelemetry`, gates Kilo's PostHog product analytics, defaults to `true`, and is
not a local export switch. Writing that key would breach decision 1's "only the keys that enable
telemetry export" and could re-enable vendor analytics for an owner who opted out. `kilo`
therefore joins `codex`, `pi` and `antigravity` as a `kind: 'pending'` writer: `status` reports its
install evidence, its fixed status lines and one manual step (`kilo-folder-import`, with no `env`),
`enable` and `disable` write nothing and leave no receipt, and `POST /api/kyber/capture` refuses it
with **400** (both points are superseded by the Kilo capture plugin amendment below: Kilo now
installs a plugin file and the route accepts it). The row states that Kilo's OTLP export is env-gated and carries no model spans,
usage or content, and that Kilo usage comes from kilo.db through folder import. Nothing else in
this record moves: the managed write scope stays `claude-code`, `copilot` and `opencode`, decision
4's receiver coupling is untouched because a status-only row never couples to the receiver, and a
future config-level Kilo export key would be an additive change rather than a revision of this
paragraph.

Amended 2026-10-21 (Kilo capture plugin): the owned unit may be a whole file, not only keys.
`kilo` stays `kind: 'pending'` for the key machinery but gains a `filePlugin`, which makes the
Settings route accept it (`POST /api/kyber/capture` switches it through the same preview and
confirm flow, and `GET` reports it as `managed` with state `on`, `off`, `partial` for an older
plugin of ours, or `unknown` for an edited or foreign file). A file plugin never turns the OTLP
receiver on, and never counts toward "the last managed harness was disabled". The writer:
`enable` installs `~/.config/kilo/plugin/kyberdash-capture.js` and `disable` removes it only while
its SHA-256 still matches the digest in `~/.kyberdash/kilo-plugin-receipt.json`. An owner edit is
left in place and reported, a file KyberDash did not write is never overwritten, and
`kilo.jsonc` is never read or written. The plugin records Kilo's assembled system prompt, tool
definitions and skills catalog to local files under `~/.kyberdash/capture/kilo` and has no network
code; this replaces the OTLP framing of the earlier amendment, because Kilo's OTLP stream cannot
carry those buckets and its plugin hooks can. Nothing in decisions 1 to 4 moves: no telemetry
export key is written, and the receiver coupling is untouched.

## Context

KyberDash can only analyse the context it receives. Most of that context reaches it through each
harness's OpenTelemetry export, and every harness switches that export on through a key in its
own user-level config:

- Claude Code: `env` in `~/.claude/settings.json`
- Codex: `[otel]` in `~/.codex/config.toml`
- OpenCode: `opencode.json`
- Copilot: VS Code settings
- Pi: files under `~/.pi/agent/`
- Antigravity: its settings file

Leaving those edits to the user, as ADR 0026 does for status lines, means six harnesses, each
with its own user-level config location and key names. A single wrong key fails silently: the harness runs,
nothing is exported, and the dashboard shows a coverage gap that looks the same as a harness that
cannot export. Agents editing the files by hand is worse. Nobody can inspect what changed or
undo it.

ADR 0026's objections still apply. These files belong to the user, their schemas drift between
harness versions, and a wrong merge is hard to undo. Owning the keys is only acceptable if each
edit is narrow, recorded and reversible.

Exporting also needs something listening. The receiver now runs under
[ADR 0033](0033-kyberdash-surfaces-are-display-layers.md) as a child of the `kyberdash web`
server rather than of the tray, which attaches to that server. The "host receiver" setting
this record describes has become the shared `settings.receiver.hosted`
(`dash/src/settings/shared-settings.ts`), still off by default. The receiver answers a liveness
probe at `OTLP_HEALTHZ_PATH` (`dash/src/otel/receiver.ts`).

## Decision

1. **A KyberDash CLI command owns the harness telemetry keys.** `kyberdash kyber capture`
   (status | enable | disable) writes the per-harness user-level config listed above, and only
   the keys that enable telemetry export.
   It is:
   - inspectable: `capture status` reports each harness's state, and a dry run prints the
     key-level change without writing;
   - reversible: `capture disable` restores what was there before;
   - testable against a temporary `HOME`.

   The user runs it. Since the 2026-10-11 amendment a click in the dashboard is the user running
   it too: the Settings drawer asks `/api/kyber/capture` on the `kyberdash web` server, which
   runs the same engine code in process — one harness per explicit user action, behind a
   dry-run preview — and never spawns a CLI or a script. Agents never hand-edit the user's
   harness config.
2. **Edits are key-level, and a KyberDash-owned receipt records them.** For each file, the
   receipt records every key KyberDash wrote, together with that key's prior value or the fact
   that it was absent. It also records the file's SHA-256 before and after the edit.
3. **`disable` restores only what is still KyberDash's.** It restores the prior value, or removes
   the key, only where the key still holds what KyberDash wrote. If any written key has drifted,
   it refuses, prints the drift, and exits non-zero. This is ADR 0026's receipt-and-digest
   pattern, applied to keys rather than whole files.
4. **The engine hosts the receiver; capture does not install a service.** Keep-alive reuses
   the existing "host receiver" and "launch at login" switches, which stay off by default;
   as of [ADR 0033](0033-kyberdash-surfaces-are-display-layers.md) the first of those is the
   shared `settings.receiver.hosted`, owned by the `kyberdash web` server rather than the
   tray, while `launch_at_login` remains the tray's own autostart switch.
   `capture status` and `capture enable` probe `GET 127.0.0.1:4318/healthz` and print the exact
   remedy when nothing answers. The route couples where the CLI does not (2026-10-11
   amendment): a non-dry enable over `/api/kyber/capture` that ends in `wrote` or `noop` turns
   `settings.receiver.hosted` on when it is off, after the config write succeeds, and a
   setting write that fails is still a 200 carrying `receiver.enableFailed: true`. The CLI
   stays uncoupled: `capture enable` keeps printing the receiver remedy. Capture installs no
   LaunchAgent and no systemd unit.
5. **The departure is scoped to KyberDash.** It applies only to the telemetry keys the capture
   command owns. ADR 0026 continues to govern Kyber Utilities, and nothing here licenses any
   other component to write harness settings.

## Alternatives Considered

- **Manual activation, as ADR 0026 does.** Rejected for KyberDash. There are six harnesses,
  each with its own user-level config location and key names, and a mistake fails silently. Status lines are a convenience, but telemetry
  export is the data KyberDash runs on. The tool also cannot report what it never reads.
- **Whole-file backup and restore.** Rejected. Restoring a file snapshot discards every edit the
  user, or the harness itself, made after `enable`. Key-level restore preserves those edits and
  refuses only when its own keys have changed.
- **Restore regardless of drift.** Rejected. Overwriting a key the user has since changed
  destroys the user's state, which is the harm ADR 0026 was written to prevent.
- **A LaunchAgent or systemd unit to keep the receiver alive.** Rejected. It would create a
  second supervisor alongside the tray's ownership of the receiver (ADR 0023), with
  platform-specific install and removal to own as well.
- **Let agents edit harness config during a session.** Rejected. Those edits leave no receipt,
  cannot be inspected and cannot be reversed. That is the opposite of every property decision 1
  requires.

## Consequences

- KyberDash now reads and writes files it does not own. It has to track each harness's config
  format and key names as they change, and the tests have to pin that against a temporary
  `HOME`.
- `disable` can refuse. When it does, the user resolves the drift by hand, guided by what it
  printed. That is the cost of never overwriting a user's change.
- The receipt is new KyberDash state. It is separate from Kyber-Squad's receipts and from Kyber
  Utilities' receipt, and none of them reads another's.
- A green enable is not capture. With `settings.receiver.hosted` off, which is the default,
  nothing listens on 4318; an ensure-if-absent endpoint the file already holds is kept, not
  overwritten, so when it points elsewhere the enable response carries an `endpoint-differs`
  warning, and receiver liveness is reported separately, never assumed. At the terminal the
  remedy is still the user's to apply; over the route, a successful enable turns the receiver
  on itself, and a setting write that fails is `receiver.enableFailed: true` on a 200 rather
  than a failed request (2026-10-11 amendment).
- Two components now take opposite positions on harness settings. The boundary between them is
  this record's scope clause. A future component that wants to write harness settings needs its
  own record.
- Content is captured in full — system prompt, user prompts, tool schemas, tool arguments and
  output, skills and all turn data — with identity fields (`user.email`, account, organization
  and uuid ids) dropped at the receiver.

## Related

- [ADR 0026: Kyber Utilities Deploys Owned Files and Never Owns Harness Settings](0026-kyber-utilities-owned-files-not-settings.md)
  — the boundary this record departs from for KyberDash, and the receipt-and-digest pattern it
  borrows
- [ADR 0023: One KyberDash Report Model, Rust-Side HTTP, and the Tray's Ownership of Server, Refresh and Receiver](0023-kyberdash-report-model-and-tray-ownership.md)
  — the tray's ownership of the receiver
- [ADR 0029: Per-Bucket, OTel-First Content Precedence](0029-kyberdash-per-bucket-otel-first-content-precedence.md)
  — how captured content is reconciled with file content
- [KyberDash architecture](../dash/architecture.md)
