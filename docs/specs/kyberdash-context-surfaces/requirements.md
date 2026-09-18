---
id: specs/kyberdash-context-surfaces/requirements
title: KyberDash context surfaces requirements
doc-type: requirements
status: draft
owner: dpalfery
last-reviewed: 2026-09-18
component: KyberDash
keywords:
  - codeburn fork
  - menubar
  - system tray
  - tauri
  - context report
  - deep links
---

# Requirements Document

## Introduction

KyberDash began as a soft fork of `getagentseal/codeburn`, vendored under `dash/` and kept
mergeable through a merge zone. This specification ends that relationship and refocuses the
product's glanceable surfaces on context troubleshooting.

It does four things. It takes the current code as a **one-time fork**: the upstream remote,
the merge-zone rules and their tests go, and `dash/` becomes first-party code judged on its
own merits. It **deletes the upstream features that are not about context** — spend
commands, quota and subscription tracking, the Swift menu bar, the Electron app, the GNOME
extension and the Ink spend dashboard — while keeping the provider session parsers that feed
the Synthesizer, which are the part of the fork that would be expensive to rebuild. It
**replaces both native surfaces with one Tauri 2 + React tray application** for macOS and
Windows that shows the current session's context state and the findings that keep recurring,
and opens the web dashboard at the exact view a click refers to. And it **rewrites the CLI
report** as a non-interactive context diagnosis that a person or a coding agent can read and
act on.

Three measured facts about the current tree motivate the scope. No workflow builds or ships
the menu bar, tray or desktop app: `kyberdash menubar` installs AgentSeal's signed CodeBurn
app from `getagentseal/codeburn` releases, and `install.sh --with-menubar` invokes
`kyber-weave menubar`, a command the .NET CLI does not have. The Swift and Tauri clients look
up `kyber-weave` and `codeburn` binaries, neither of which is the shipped `kyberdash`. And the
optional `kyber` field of the status contract, meant to carry context analyses to native
clients, is never populated — so no native surface has ever shown context data.

The engine stays TypeScript. The REST API under `/api/kyber/*` is the seam every surface
reads, which leaves a later move of the engine to .NET free to happen without touching the
tray or the web dashboard.

Figures shown by the new surfaces follow the existing display rules:
[honest unobservability](../../rules/honest-unobservability.md),
[secondary cost display](../../rules/secondary-cost-display.md),
[the composite efficiency ban](../../rules/composite-efficiency-ban.md) and
[relocation over deletion](../../rules/relocation-over-deletion.md).

## Requirements

### Requirement 1 — Sever the upstream relationship

**User Story:** As the KyberDash maintainer, I want `dash/` to be first-party code with no
tie to `getagentseal/codeburn`, so that a change is judged on its merits rather than on its
merge cost against an upstream we no longer track.

#### Acceptance Criteria

1.1. WHEN any test, script, CI workflow or documented procedure in the repository runs THEN
it SHALL NOT require a `codeburn` git remote, a subtree merge commit, or a
`git subtree pull`.

1.2. WHEN this specification is delivered THEN `tests/KyberWeave.Tests/MergeBoundaryTests.cs`
and the upstream read-only and adapter-seam rules in `dash/kyber/tools/boundary.ts` SHALL be
removed.

1.3. WHEN ADR 0020 is accepted THEN it SHALL record the one-time fork — the commit the fork
was taken at, and that no further upstream change is merged — and SHALL restate the three
ADR 0006 decisions that survive (the embedded OTLP receiver, the single span-shaped
canonical model, and distribution through `scripts/install.sh` as SEA binaries), so that no
current decision depends on ADR 0006.

1.4. WHEN `AGENTS.md` and `docs/dash/architecture.md` are read THEN neither SHALL describe
`dash/` as a vendored soft fork or any path as a merge zone, and the architecture SHALL no
longer carry the merge-zone ownership table or the deliberate merge-zone edits table.

1.5. IF code derived from `getagentseal/codeburn` is distributed THEN AgentSeal's MIT
copyright and permission notice SHALL be retained in `dash/LICENSE` and in the third-party
notices shipped with the `kyberdash` binary and the tray.

1.6. WHEN a tracked file outside `docs/`, other than the attribution files named in 1.5,
contains `getagentseal`, `agentseal.org` or `codeburn.app` THEN a test SHALL fail and name
the file. `docs/` is exempt because the decision records and specifications that document
the fork have to name its origin.

1.7. WHEN the fork is recorded THEN existing git history SHALL be preserved without rewrite,
and the KyberDash runbook SHALL tell existing clones to remove their local `codeburn` remote.

1.8. WHEN ADR 0020 is accepted THEN it SHALL re-decide the engine language on current
grounds, because ADR 0006 rejected a C# core only on the grounds that a cross-language fork
cannot be merged, which no longer applies.

1.9. WHEN ADR 0020 is accepted THEN ADR 0006 SHALL move to `docs/archive/adrs/` with status
`superseded`, every live link to it SHALL be repointed to the archived path, and ADR 0020
SHALL name it in prose rather than in `supersedes`, because archived records are outside the
corpus and a `supersedes` reference to one fails `KW-DOC-SPEC-006`.

1.10. WHEN ADR 0006 is archived THEN the ADR index SHALL no longer claim that a superseded
record keeps an id its citing documents can resolve, since an archived id does not resolve.

### Requirement 2 — Remove upstream-only features

**User Story:** As the maintainer, I want every inherited feature that does not serve context
troubleshooting deleted, so that the code I now own is only the code the product uses.

#### Acceptance Criteria

2.1. WHEN the `kyberdash` CLI is built THEN it SHALL NOT register any of: `share`, `devices`,
`identity`, `overview`, `budget`, `today`, `month`, `currency`, `plan`, `model-alias`,
`price-override`, `model-savings`, `model-flat-rate`, `proxy-path`, `codex-tps`, `compare`,
`yield`, `spend`, `antigravity-hook`, `agy-statusline-hook`, `optimize`, `serve`, `status`,
`context`, `mcp`, `audit`, `sessions`, `models`, `export`.

2.2. WHEN the `kyberdash` CLI is built THEN it SHALL register `report` (Requirement 11),
`web` (Requirement 5), `menubar` (Requirement 12), `doctor`, and the existing KyberDash
commands — the `kyber` group, `dash refresh`, `otel` and `cursor-hook` — with their behaviour
unchanged except where this specification says otherwise.

2.3. WHEN a module or test under `dash/` is reachable only from a deleted command or surface
THEN it SHALL be deleted.

2.4. WHEN this specification is delivered THEN `dash/mac/`, `dash/windows/`, `dash/app/` and
`dash/gnome/` SHALL be deleted, after the files Requirement 6.11 salvages are copied into the
tray.

2.5. WHEN this specification is delivered THEN upstream project files with no KyberDash role
SHALL be deleted — `BRIEF.md`, `SUBMISSION.md`, `RELEASING.md`, `CHANGELOG.md`,
`CONTRIBUTING.md`, `SECURITY.md`, `codeburn-desktop-wireframes.html`, the marketing images
and star history under `dash/assets/`, and end-to-end specs of deleted surfaces — and
`dash/README.md` SHALL be replaced by a short pointer to `docs/dash/`.

2.6. IF a provider session parser is consumed by the Synthesizer or the harness-source
refresh registry THEN it SHALL be retained with its tests and the modules it depends on.

2.7. WHEN retained code runs THEN it SHALL NOT read third-party credentials, SHALL NOT call
third-party account, quota or usage APIs (Anthropic OAuth usage, ChatGPT backend, GitHub
Copilot internal, Cline, Google Cloud Code), and SHALL NOT call a currency-exchange service.

2.8. WHEN cost is computed THEN the bundled pricing table and its cached refresh SHALL be the
only pricing source, and a model absent from it SHALL carry cost status `no_rate`.

2.9. WHEN the web dashboard is served THEN the Usage tab and the Share chrome SHALL be absent,
and the header tabs SHALL be Context Doctor, Quarantine and Problems.

### Requirement 3 — Layout and identity

**User Story:** As a contributor, I want one source tree with one product name, so that
nothing in the layout or the running product implies a relationship that no longer exists.

#### Acceptance Criteria

3.1. WHEN the restructure is delivered THEN `dash/kyber/**` and `dash/src/**` SHALL be one
source tree at `dash/src/`, organised by concern with at least `providers`, `canon`,
`analysis`, `otel`, `refresh`, `server` and `cli`, and no directory or import path inside
`dash/` SHALL be named `kyber`.

3.2. WHEN the restructure is delivered THEN the React web dashboard SHALL live at
`dash/web/` and the tray application at `dash/tray/`.

3.3. WHEN the package is built THEN its npm name SHALL be `kyberdash`, its only bin SHALL be
`kyberdash`, and its author, repository and issue-tracker metadata SHALL point to
`dpalfery/kyber-weave`.

3.4. WHEN the CLI or the tray reads an environment variable of its own THEN the name SHALL
carry the `KYBERDASH_` prefix, and a `CODEBURN_*` variable SHALL have no effect.

3.5. WHEN the CLI writes cache, configuration or state THEN it SHALL write under
`~/.kyberdash/`, and it SHALL neither read nor delete an existing CodeBurn-named directory.

3.6. WHEN the tray is built THEN its bundle and application identifier SHALL be
`io.github.dpalfery.kyberdash` and its display name SHALL be `KyberDash`.

3.7. WHEN any surface renders user-visible text THEN the text SHALL NOT name CodeBurn except
in the attribution notices of Requirement 1.5.

### Requirement 4 — Gates cover every shipped surface

**User Story:** As the maintainer, I want the repository's gates to cover the forked code I
ship, so that owning it means checking it like the rest of the repository.

#### Acceptance Criteria

4.1. WHEN CI runs THEN typecheck, lint and tests SHALL cover `dash/src`, `dash/web` and the
React UI of `dash/tray`.

4.2. WHEN CI runs THEN `cargo fmt --check`, `cargo clippy` with warnings denied, and
`cargo test` SHALL cover the tray's Rust crate.

4.3. WHEN CodeQL runs THEN `dash/**` SHALL be analysed as first-party code, with no
exclusion that exists because a path was vendored.

4.4. WHEN `AGENTS.md` lists the KyberDash gates THEN it SHALL list the tray's commands with
the existing npm commands.

4.5. IF enabling a gate on previously excluded code reports findings THEN the findings SHALL
be fixed, and any that are not SHALL have a recorded reason.

### Requirement 5 — Addressable web dashboard views

**User Story:** As a developer arriving from the tray or a report, I want every dashboard view
to have a URL, so that a link lands me on the exact finding, session, run or harness.

#### Acceptance Criteria

5.1. WHEN the web dashboard renders Context Doctor, a harness, a run, a session, a turn of a
session, a finding, a run comparison, Quarantine or Problems THEN the browser URL SHALL
identify that view and the entity ids it shows.

5.2. WHEN such a URL is opened directly or reloaded THEN the dashboard SHALL render that view
with the same spine ancestry as navigating to it would produce.

5.3. WHEN the user navigates back or forward in the browser THEN the dashboard SHALL move
through the spine history accordingly.

5.4. IF a URL names an entity id the store does not hold THEN the dashboard SHALL render a
not-found state inside the shell naming the id and linking to Context Doctor.

5.5. WHEN the web server serves any request THEN it SHALL keep its loopback-only binding and
its rejection of non-loopback `Host` and cross-origin `Origin` headers.

5.6. WHEN `kyberdash web` is given a view path (for example `--view finding/<id>`) THEN it
SHALL open the browser at that view.

5.7. WHEN `kyberdash web` has bound its port THEN it SHALL print the URL it serves on stdout
in a stable, machine-readable line.

### Requirement 6 — Tray application: platform and lifecycle

**User Story:** As a developer on macOS or Windows, I want a small KyberDash app in my menu
bar or notification area, so that context state is visible without opening a dashboard.

#### Acceptance Criteria

6.1. WHEN the tray is built THEN one Tauri 2 application with a React UI, from the single
source tree `dash/tray/`, SHALL target darwin-arm64, darwin-x64 and win-x64.

6.2. WHEN the tray runs on macOS THEN it SHALL appear only as a menu-bar status item, with
no Dock icon and no application-switcher entry.

6.3. WHEN the tray runs on Windows THEN it SHALL appear as a notification-area icon.

6.4. WHEN the user clicks the status item THEN the tray SHALL open its popover anchored to
the item, and SHALL close it when it loses focus or the user presses Escape.

6.5. IF a second tray instance is launched THEN it SHALL show the running instance's popover
and exit.

6.6. WHEN the tray starts THEN it SHALL resolve the `kyberdash` binary from
`KYBERDASH_BIN`, then the kyber-weave install directory, then `PATH`, and SHALL spawn only a
validated absolute path, with bounded payload size and timeouts.

6.7. IF `kyberdash` cannot be resolved, or reports a version below the tray's declared
minimum, THEN the tray SHALL show a setup state naming the locations it probed and the
command that installs or updates `kyberdash`, and SHALL NOT present earlier data as current.

6.8. WHEN the user enables Launch at login in settings THEN the tray SHALL register itself to
start at login; the setting SHALL default to off.

6.9. WHEN the user quits the tray THEN it SHALL stop every server and receiver process it
started, and an in-flight refresh SHALL either finish or stop without leaving `canon.db`
partially written.

6.10. WHEN the tray runs THEN it SHALL open no network connection except to loopback, and it
SHALL contain no update checker, analytics, or access to third-party credentials.

6.11. WHEN the tray is created THEN the hardened process spawning of
`dash/windows/src-tauri/src/cli.rs`, the Windows login-item registration of `autostart.rs`,
the icon badge rendering of `tray_badge.rs`, and the monitor-aware popover positioning
(`position_popover` in `lib.rs`) SHALL be carried into its Rust crate, renamed to the
KyberDash identity; no other code of the deleted native apps SHALL be. `autostart.rs` has no
macOS path, so the macOS login item is new code.

### Requirement 7 — Tray data source

**User Story:** As the maintainer, I want the tray to read the same REST API as the web
dashboard, so that there is one data contract and opening the dashboard lands on the server
the tray already runs.

#### Acceptance Criteria

7.1. WHEN the tray needs data THEN it SHALL read it from the `/api/kyber/*` REST API of a
`kyberdash web --no-open` server it started, and SHALL NOT spawn a CLI process per poll or
open `canon.db` itself.

7.2. WHEN the tray has started its server THEN it SHALL reuse that server for its lifetime
and restart it if it exits, with bounded backoff.

7.3. IF the preferred port is in use THEN the tray SHALL use the URL the server reports
(Requirement 5.7).

7.4. IF the server is unreachable THEN the tray SHALL keep showing the last data it received,
labelled with its age and the error, and SHALL NOT present it as current.

7.5. IF the popover needs a figure no endpoint serves THEN the figure SHALL be added to the
REST API and computed by the engine, and the tray SHALL hold no analysis logic.

7.6. WHEN this specification is delivered THEN `status --format menubar-json`, the `kyber`
field of the status payload, and the status contract and MCP parity tests SHALL be deleted.

7.7. WHILE the popover is open THEN the tray SHALL poll at most every 15 seconds, and WHILE
it is closed at most every 60 seconds.

### Requirement 8 — Popover content

**User Story:** As a developer in the middle of a task, I want the popover to show what is in
my current session's context and what keeps going wrong, so that I can decide whether to act
before the window fills.

#### Acceptance Criteria

8.1. WHEN the popover opens THEN it SHALL show, in order: the harness selector, the latest
session panel, the findings list, the data-health footer, and the actions Open dashboard,
Refresh now, Settings and Quit.

8.2. WHEN the latest session panel renders THEN it SHALL show, for the most recently active
session in the selected harness scope: the harness, the project (working-directory name),
the age of its last activity, its turn count, its latest turn's context pressure as a
percentage of the model's context window, and that turn's composition across the five
canonical buckets (`system_prompt`, `tool_definitions`, `instruction_context`,
`conversation_history`, `tool_result_content`) plus the unattributed residual.

8.3. IF the latest turn is flagged as a cache invalidation THEN the session panel SHALL say
so.

8.4. IF the context window, a bucket or any other figure is not measurable for the harness
THEN the popover SHALL render `—` with the stated reason and SHALL NOT render `0`.

8.5. WHEN the findings list renders THEN it SHALL show up to three findings from sessions
active within the findings window (default 7 days), ordered by rank score, each with its
title, measurement class, and estimated recoverable tokens with its error bar.

8.6. WHEN the user clicks the session panel, a finding, a harness, the quarantine count or
the problems count THEN the tray SHALL open that view's URL (Requirement 5) in the default
browser.

8.7. WHEN the user chooses Open dashboard THEN the tray SHALL open Context Doctor.

8.8. WHEN the harness selector renders THEN it SHALL list All plus every canonical harness
with a session in the findings window, and choosing one SHALL scope the session panel, the
findings list and the status item, and SHALL persist across restarts.

8.9. WHEN cost is shown THEN it SHALL be one secondary figure — the scope's cost over the
findings window with its basis — placed after the token figures and never used as a sort
key.

8.10. WHEN the popover renders THEN it SHALL NOT show quota, subscription plans, capacity,
currency selection, spend budgets, or any composite score or grade.

8.11. IF the store holds no session THEN the popover SHALL show an empty state explaining
that data arrives through refresh or the OTLP receiver, with Refresh now.

8.12. WHEN the tray's UI renders THEN it SHALL use the web dashboard's design tokens for
colour, type scale and density.

### Requirement 9 — Status item

**User Story:** As a developer, I want the menu-bar or tray icon itself to tell me when my
context is filling, so that I notice without opening anything.

#### Acceptance Criteria

9.1. WHEN the scoped latest session's latest-turn context pressure is measurable THEN the
status item SHALL show it as a whole percentage — as title text beside the icon on macOS and
as a badge on the icon on Windows.

9.2. WHEN that pressure reaches the attention threshold (default 70%) THEN the icon SHALL
take its attention state, and at the critical threshold (default 90%) its critical state;
both thresholds SHALL be configurable in settings.

9.3. IF pressure is not measurable, or no session exists in scope, THEN the status item SHALL
show the neutral icon with no number.

9.4. IF the data shown is stale because the server is unreachable or the last refresh failed
THEN the status item SHALL show a stale state distinct from every pressure state.

9.5. WHEN the pointer rests on the status item THEN its tooltip SHALL name the harness, the
project, the pressure and the age of the data.

9.6. WHEN the status item renders THEN it SHALL NOT show cost.

### Requirement 10 — Data freshness

**User Story:** As a developer, I want the tray to keep the store current by itself, so that
I never troubleshoot against stale data without knowing it.

#### Acceptance Criteria

10.1. WHILE the tray runs THEN it SHALL run an incremental `kyberdash dash refresh` when it
starts and then at the configured cadence (default 5 minutes).

10.2. WHEN the user chooses Refresh now THEN the tray SHALL start a refresh immediately
unless one is already running.

10.3. IF a refresh is already running, whether the tray or a terminal started it, THEN the
tray SHALL NOT start another and SHALL show that a refresh is in progress.

10.4. WHEN `dash refresh` starts while another refresh holds the store's refresh lock THEN it
SHALL exit without writing, with a message naming the holding process and a distinct,
documented exit code.

10.5. IF a refresh fails THEN the data-health footer SHALL show when it failed and why, and
SHALL keep showing the time of the last successful refresh.

10.6. WHEN the data-health footer renders THEN it SHALL show the age of the last successful
refresh, the OTLP receiver's status (reachable, not reachable, hosted by the tray, or
unknown), the quarantine count and the problem count.

10.7. WHEN the user enables Host OTLP receiver in settings (default off) THEN the tray SHALL
start `kyberdash otel` as a child process and restart it with bounded backoff if it exits.

10.8. IF port 4318 is already bound when the tray would host the receiver THEN the tray SHALL
NOT start one, SHALL report that another process holds the port, and SHALL NOT retry in a
loop.

10.9. WHEN ADR 0021 is accepted THEN it SHALL record the tray's ownership of the web server,
its scheduled refresh, the refresh lock and the new exit code, and SHALL supersede only
ADR 0016's statement that a refresh button is not part of the refresh lifecycle, leaving
ADR 0016 current and unedited, as ADR 0008 did for ADR 0007 D4.

### Requirement 11 — CLI context report

**User Story:** As a developer or a coding agent troubleshooting context, I want
`kyberdash report` to print a non-interactive diagnosis of the store, so that I can read it
in a terminal or hand it to an agent to act on.

#### Acceptance Criteria

11.1. WHEN `kyberdash report`, or `kyberdash` with no command, runs THEN it SHALL print a
report to stdout and exit without an interactive UI, and the Ink dashboard SHALL be deleted.

11.2. WHEN the report is produced THEN its sections SHALL appear in this order: data
coverage, findings, harness dimensions, latest session context, cost.

11.3. WHEN the coverage section renders THEN it SHALL show the store path, the time and age
of the last successful refresh, and for each harness whether it was detected, its session
count in the window and its measurability, followed by the quarantine and problem counts.

11.4. IF the store holds no session, or the last successful refresh is more than one hour
old, THEN the coverage section SHALL say so and give the command that remedies it.

11.5. WHEN the findings section renders THEN it SHALL list up to `--limit` findings (default
5) from the window by rank score, each with every Finding Contract field — title,
measurement class, confidence and its basis, mechanism, at least two evidence ids,
recommendation, estimated recoverable tokens with error bar, and outcome-risk caveat — and
the `kyberdash web --view` command that opens it.

11.6. WHEN the harness section renders THEN it SHALL show each harness's six dimensions —
Context Hygiene, Cache Efficiency, Tool Yield, Skill Utilisation, Delegation Overhead and
Continuity — as separate values, with `—` and a reason where a dimension is unmeasurable.

11.7. WHEN the latest-session section renders THEN it SHALL show the figures of Requirement
8.2 through 8.4, the tool-definition sources ranked by resident tokens, and the turns flagged
as cache invalidations.

11.8. WHEN `--format` is `text` (the default) THEN the output SHALL be aligned plain text,
coloured only when stdout is a terminal and `NO_COLOR` is unset.

11.9. WHEN `--format` is `markdown` THEN the output SHALL be GitHub-flavoured Markdown
containing no terminal escape sequences.

11.10. WHEN `--format` is `json` THEN the output SHALL be one JSON document carrying a
`schemaVersion` field, in which an unmeasurable value is `null` with a reason and never `0`.

11.11. WHEN `--harness`, `--session`, `--run` or `--days` is given THEN every section SHALL
be scoped to it, and `--days` SHALL accept a positive integer defaulting to 7.

11.12. IF an argument is invalid THEN the report SHALL exit 2 before opening the store; IF
the store cannot be opened or read THEN it SHALL exit 1; otherwise it SHALL exit 0, including
when the store is empty or findings exist.

11.13. WHEN the report runs THEN it SHALL only read the store, performing no ingest and no
network access.

11.14. WHEN the report and the REST API are computed from the same store and scope THEN
their findings, dimensions and session figures SHALL be equal, and a test SHALL prove it.

11.15. WHEN `kyberdash doctor` runs THEN it SHALL keep its provider detection output, and the
coverage section SHALL use the same detection code.

### Requirement 12 — Distribution and installation

**User Story:** As a user, I want to install the KyberDash tray from kyber-weave's own
releases, so that what I run is what this repository builds.

#### Acceptance Criteria

12.1. WHEN a release is cut THEN `release.yml` SHALL build the tray for darwin-arm64,
darwin-x64 and win-x64 and publish each artifact with a SHA-256 checksum, at the same version
as `kyberdash`.

12.2. WHEN the release job builds a macOS tray artifact THEN it SHALL sign it with a
Developer ID Application identity of Apple team `J2UNNQ466J`, notarize it, and staple the
notarization ticket.

12.3. IF the macOS signing or notarization credentials are absent or rejected THEN the release
job SHALL fail rather than publish an unsigned or unnotarized tray.

12.4. IF a Windows code-signing certificate is configured THEN Windows installers SHALL be
Authenticode-signed; IF it is not THEN the release notes SHALL say that SmartScreen will warn.

12.5. WHEN `kyberdash menubar` runs on macOS or Windows THEN it SHALL download the tray
artifact for its own version and platform from `dpalfery/kyber-weave` releases, verify its
checksum, install it and launch it, and SHALL refuse to install on any verification failure.

12.6. WHEN `kyberdash menubar` installs on macOS THEN it SHALL additionally verify the code
signature, that the signing team is `J2UNNQ466J`, and Gatekeeper's assessment before placing
the app, and SHALL NOT remove the quarantine attribute.

12.7. WHEN `install.sh --with-menubar` runs on macOS THEN it SHALL invoke
`kyberdash menubar --force`, and IF `--no-kyberdash` was also given THEN it SHALL fail with a
message that the tray requires `kyberdash`.

12.8. IF `kyberdash menubar` runs on Linux THEN it SHALL exit non-zero with a message that
the tray supports macOS and Windows only.

12.9. WHEN the tray release job references a third-party GitHub Action THEN the reference
SHALL be pinned to a full commit SHA that resolves in that action's repository.

12.10. WHEN `kyberdash menubar` resolves its download origin THEN it SHALL honour
`KYBER_WEAVE_RELEASE_ORIGIN` as the self-updater does, so a local release server can serve
tray artifacts.

12.11. WHEN `kyberdash menubar` installs the tray THEN it SHALL record the install location
under `~/.kyberdash/`, so later updates find the tray without guessing paths.

### Requirement 13 — Documentation and decisions

**User Story:** As a reader of the governed corpus, I want the documentation to describe the
product as it now is, so that no document quotes the soft fork or the retired surfaces as
current.

#### Acceptance Criteria

13.1. WHEN this specification is delivered THEN `docs/dash/README.md`, `architecture.md` and
`runbook.md` SHALL describe three surfaces — the CLI report, the web dashboard and the tray —
and SHALL NOT describe the Electron app, the Swift menu bar, the GNOME extension, the Ink
dashboard or the status contract as current.

13.2. WHEN this specification is delivered THEN the KyberDash row of `docs/catalog.md` SHALL
reflect the three surfaces.

13.3. WHEN any document changes THEN `docs validate` and `docs drift` SHALL report zero
findings.

13.4. WHEN this specification is delivered THEN the deferred work it names — a Linux tray,
and Windows code signing while no certificate is configured — SHALL be recorded as todos
under `docs/todo/`.

### Requirement 14 — Display rules on the new surfaces

**User Story:** As a developer relying on these figures, I want the tray and the report to
obey the same honesty rules as the web dashboard, so that a glance never tells me something
the telemetry does not support.

#### Acceptance Criteria

14.1. IF a figure is not measurable for a source THEN the tray and the text and Markdown
report SHALL show `—` with the reason, and the JSON report SHALL carry `null` with the
reason; neither SHALL show `0`.

14.2. WHEN the tray or the report shows cost THEN cost SHALL appear only as a secondary figure
after the token figures, carrying its basis, and SHALL NOT lead a section or order a list.

14.3. WHEN the tray or the report presents harness or session health THEN it SHALL present
separate dimension values and SHALL NOT compute or show a composite score, index or grade.

14.4. WHEN the tray or the report shows a finding's recommendation THEN it SHALL show the
engine's recommendation text unchanged, and SHALL NOT rephrase a relocation as a deletion.

### Requirement 15 — The tray updates with `kyber-weave update`

**User Story:** As a user, I want `kyber-weave update` to update the tray along with the CLI,
so that the tray and the `kyberdash` it talks to never drift apart.

#### Acceptance Criteria

15.1. WHEN `kyber-weave update` installs release V and a tray is installed THEN, after
updating `kyberdash`, it SHALL update the tray to V by invoking the updated `kyberdash`'s tray
update, so that tray download, verification and installation are implemented once.

15.2. IF no tray is installed THEN the update SHALL NOT install one, and SHALL log that the
tray is not installed and the command that adds it.

15.3. WHEN `--no-menubar` or `--no-kyberdash` is given THEN the update SHALL leave the tray
unchanged and log which flag caused it.

15.4. IF release V predates the first release that publishes tray artifacts THEN the update
SHALL leave the tray unchanged and log the first version that carries them.

15.5. WHEN the tray is updated while running THEN it SHALL be quit gracefully, stopping its
child processes as Requirement 6.9 requires, then replaced and relaunched.

15.6. IF any step of the tray update fails THEN the previously installed tray SHALL remain
installed and runnable, and `kyber-weave update` SHALL name the failed step and exit
non-zero.

15.7. WHEN the self-updater changes THEN `scripts/update-loop.sh` SHALL still pass, the tray
update path SHALL be covered by automated tests against a local release origin, and the
loop's inability to build the tray SHALL be recorded in the existing
`kyberdash-local-release-loop` todo.
