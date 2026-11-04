---
id: adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys
title: The KyberDash Capture Command Owns Harness Telemetry Keys, with Key-Level Edits, Receipt Restore, and a Tray-Hosted Receiver
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-04
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
(appData injected by the caller, else `APPDATA`, else `<home>\AppData\Roaming`), and OpenCode's
`<home>\.config\opencode\opencode.jsonc` as the target of the one-time restore in the 2026-10-31
amendment below, with XDG variables ignored on every OS. The route
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

Amended 2026-10-26 (OpenCode capture deploy plan, decisions D4 and D11): the managed writer may
own a whole plugin file, and the install path has a preserve mode. `ManagedHarnessWriter` gains an
optional `filePlugin` slot, which OpenCode uses for `<root>/plugins/kyberdash-opencode.js`, so
KyberDash owns that file, its receipt, and the consent file under the same one-owner rule as the
Kilo file plugin. `kyber-weave` (Squad and `kyber-weave update`) never writes those files itself. It
calls `kyberdash kyber capture plugin install --harness opencode --preserve`, and the verb owns the
decision. Strict mode (`capture enable`) refuses a foreign or edited file with exit 1. Preserve mode
(Squad and update callers) keeps such a file with a warning and exits 0, and creates the consent file
with `content: false` only when it is absent. `capture enable` and `disable` flip that consent through
the same `runEnable` and `runDisable` paths as the Settings switch, so the decision stays in the
capture engine (ADR 0033). The departure is still scoped to KyberDash's own plugin file. Squad and
update never edit `opencode.jsonc` or the owner's `plugin` array, and every key rule above is
unchanged.

Amended 2026-10-31 (OpenCode V2-only plugin pipeline, [ADR 0048](0048-opencode-capture-is-a-v2-only-plugin-pipeline.md)):
OpenCode writes no telemetry key. Its capture is the plugin file
`<root>/plugins/kyberdash-opencode.js` and the consent file `~/.kyberdash/opencode-plugin.json`, both owned by
KyberDash under the 2026-10-26 rules, with the plugin's receipt at `~/.kyberdash/opencode-plugin-receipt.json`.
`enable` runs a strict plugin install and sets consent on. `disable` sets consent off and removes the plugin
file. Both also run a one-time migration: they restore the `experimental.openTelemetry` value that earlier
builds wrote into `opencode.jsonc`, from the key receipt, and drop that receipt entry. A value changed since is
left in place with a warning. The OpenCode harness declares no key, so `status` renders no OTLP environment
snippet for it. The key-level rules of decisions 1 to 3 still govern every other harness and this one-time restore.

Amended 2026-11-02 (Factory (Droid) capture plugin, [ADR 0055](0055-factory-capture-is-a-hooks-plugin-pipeline.md)):
Factory joins the plugin-only writers. Its capture writer owns a whole plugin **tree** — the
Kyber-owned marketplace tree it publishes — with its receipt at
`~/.kyberdash/factory-plugin-receipt.json` and its consent file at
`~/.kyberdash/factory-plugin.json`. It declares **no config key**, so nothing is read, written or
receipted in a config file. Two things differ from the OpenCode writer and are deliberate: the Factory
install is a **tree**, so its receipt records a path and a SHA-256 per file and removal requires
every digest to match; and the Factory **Settings switch never turns the content consent on** —
the switch installs the capture, and only the explicit consent verb
(`kyberdash kyber capture plugin consent --harness factory --on|--off`) or the owner editing the
consent file records the decision to capture the prompt text. Nothing else in this record moves:
the key-level rules of decisions 1 to 3 still govern every other harness, and the departure from
ADR 0026 stays scoped to KyberDash's own plugin tree.

Amended 2026-11-04 (the Factory content consent, [ADR 0055](0055-factory-capture-is-a-hooks-plugin-pipeline.md)):
the 2026-11-02 amendment above is **withdrawn** on the one point it names. The Factory
**Settings switch is the content consent**: a confirmed `enable` turns the prompt text on with
the capture, and the confirm states what the switch does to the owner's text before it is
applied, because the owner has already confirmed the switch and a second gate on the same
decision is friction rather than privacy. The explicit consent verb
(`kyberdash kyber capture plugin consent --harness factory --on|--off`) stays, as the way to
turn the prompt text off without disabling the capture, and the consent file still records the
decision. Nothing else in that amendment or in this record moves.

Amended 2026-11-02 (the Factory install route, measured against Factory Droid CLI 1.1.0): the
Factory install is **delegated to the harness's own commands**, and the owned unit is the
Kyber-owned **marketplace tree** KyberDash publishes and then asks the harness to install from.
The route this record first recorded for Factory — KyberDash writing a plugin directory into the
Factory root, `~/.factory/plugins/kyberdash-factory/`, and stopping there — **never worked**,
and that was measured rather than assumed: after that install `droid plugin list` reported
"No plugins installed", and a real Droid turn produced no plugin spans at all. Factory loads a
plugin that came from a **marketplace** — it copies the plugin into its own cache and enables it
through the `enabledPlugins` setting — so the plugin that runs is the harness's **cached copy** of
the Kyber-owned tree, and the install is `droid plugin marketplace add <the Kyber-owned
marketplace tree>` followed by `droid plugin install kyberdash-factory@kyberdash --scope user`.
The teardown is `droid plugin uninstall kyberdash-factory@kyberdash --scope user` followed by
`droid plugin marketplace remove kyberdash`, and KyberDash removes its own tree afterwards,
because a local marketplace's source directory survives the harness's removal.

**KyberDash still never writes the owner's settings or hooks file.** The letter of the promise
survives, and it is the stronger position: `enable`, `disable`, Squad and `kyber-weave update`
never create, read, merge, rewrite or restore `~/.factory/settings.json`,
`~/.factory/hooks.json` or their project equivalents. What changes is *who* changes the owner's
settings file during an install KyberDash requested — the harness does, through its own
commands, which write the `extraKnownMarketplaces` and `enabledPlugins` entries into it. That is
a **delegated write**, and this record's own mechanism is what makes it safe: the Factory writer
owns a whole separate file — the marketplace tree — and never a shared settings file, so
KyberDash holds no merge of the owner's settings, no partial edit and no schema knowledge of
it. The harness's edit is a whole-file edit, made by the harness, in the harness's own format.

**The restore criterion is semantic, not byte-identity.** Because the harness's own commands
change the owner's settings file during an install KyberDash requested, byte-identity on that
file does not hold and is withdrawn. What holds is: no entry the install added remains; no entry
the owner had is lost; and every other key is unchanged in content. The second half needs care,
because it is **measured** that `droid plugin uninstall` deletes the `enabledPlugins` entry
outright — it does not set it to `false` — and leaves the marketplace registration in place. An
owner who had that plugin listed as disabled before the install therefore has that key removed
by the harness's uninstall rather than restored to `false`, and the uninstaller must handle that
case itself rather than assume the harness's commands satisfy the criterion on their own. Any
residue the harness's uninstall leaves behind is **reported with the exact command that removes
it**, and is never patched by hand.

Three further facts about the harness are measured on Factory Droid CLI 1.1.0, and this record is
true about them: the install **auto-enables** the plugin — the `enabledPlugins` entry appears
with value `true` without being asked for; the registered marketplace name is the **source
directory's basename**, not the `name` field inside the marketplace manifest, so the tree's
directory name is load-bearing and the local-source object written into
`extraKnownMarketplaces` is exactly the documented shape; and `droid plugin marketplace remove`
deletes the marketplace registration and leaves `enabledPlugins` alone.

**A name the harness holds that KyberDash does not own is refused, and the harness's own output
decides it.** The harness resolves a plugin identity through the **registered marketplace name**,
not through the tree KyberDash published: measured, a marketplace named `kyberdash` registered
from a *different* directory makes `droid plugin install kyberdash-factory@kyberdash --scope
user` **succeed** and install **the other tree's payload**. Nothing in the harness's answers says
which payload is running. So the harness's registry is read as the harness reports it — the
marketplace list with its registered **SOURCE**, the plugin list with its **STATUS** — and a
marketplace or plugin identity KyberDash does not own is a **conflict**: strict mode refuses with
nothing written, preserve mode keeps it with a warning, and the report names the colliding
entry, the registry it lives in and the exact `droid plugin …` command that removes it. The
teardown never removes an entry that is not KyberDash's.

**The harness's list output is a wrapped table, and it is read as one.** The harness sizes its
columns to the content and **truncates** a cell that does not fit, printing the overflow at the
same offset on the lines below, so a registered path longer than its column arrives in pieces.
The reader joins a row's pieces back into the column they start in; a reader that took one line
per row would compare a **truncated** source against the tree KyberDash published and report a
conflict that does not exist — on every install, on every machine whose path is long enough.

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
- The owner's Factory settings file changes during an install KyberDash requested, and not by
  KyberDash's hand. The harness's own commands write the `extraKnownMarketplaces` and
  `enabledPlugins` entries and remove the `enabledPlugins` entry on uninstall, so KyberDash's
  restore criterion is semantic rather than byte-identity, and any residue the harness leaves
  behind is reported with the command that removes it.
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
- [ADR 0055: Factory (Droid) Capture Is a Hooks-Plugin Pipeline](0055-factory-capture-is-a-hooks-plugin-pipeline.md)
  — the Factory writer this record's plugin-tree rules now cover
- [KyberDash architecture](../dash/architecture.md)
