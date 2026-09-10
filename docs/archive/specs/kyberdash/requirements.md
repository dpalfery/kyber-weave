---
id: archive/specs/kyberdash/requirements
title: KyberDash requirements
doc-type: requirements
status: archived
owner: dpalfery
last-reviewed: 2026-08-29
component: KyberDash
---

# Requirements Document

## Introduction

KyberDash is a locally-run product that answers three questions about coding agents: what
they cost, what filled their context windows, and whether a change to either actually helped.

It is formed by merging two working codebases. `getagentseal/codeburn` (MIT, TypeScript)
reads the session files that 41 agent tools already write to disk and ships the surfaces — an
Ink terminal dashboard, a React web dashboard, an Electron application for macOS and Windows,
a Swift menu-bar application, and an MCP server — but has no OpenTelemetry support.
`agent-session-analysis-dashboard` (Python) analyses OpenTelemetry GenAI spans and holds the
correctness work — disjoint token classes with per-harness semantics, cost basis that travels
with every figure, tool-schema cost ranking, context composition, quarantine — but reads no
session files, ships no native surfaces, and routes every live ingest through a separately
installed .NET Aspire dashboard.

These requirements describe the union: codeburn's breadth and surfaces over the Python
pipeline's depth and rigor, with an embedded OTLP receiver so nothing external is required.

Several requirements below are unusual in citing a measurement rather than a preference. Each
such measurement is a failure that has already occurred in the Python pipeline, and a
reimplementation that drops the requirement reproduces the failure. They are recorded so a
later reader cannot mistake a correctness constraint for a style choice.

## Requirements

### Requirement 1 — Local session-file ingestion

**User Story:** As a developer using several coding agents, I want KyberDash to read what
those tools already write to disk, so that I can see my spending without configuring
telemetry, installing a proxy, or supplying an API key.

#### Acceptance Criteria

1.1. WHEN KyberDash runs for the first time with no configuration THEN the system SHALL
discover and parse session files for every provider the upstream parser supports.

1.2. WHEN a provider's session store is absent from the machine THEN the system SHALL omit
that provider silently and SHALL NOT report it as an error.

1.3. WHEN a provider's session store exists but cannot be parsed THEN the system SHALL record
a problem naming the provider and the file, and SHALL continue parsing every other provider.

1.4. WHEN session files are ingested THEN the system SHALL NOT require an API key, a proxy, a
network call, or a wrapper around the agent tool.

1.5. WHEN a corpus large enough to trigger upstream's parallel cold-parse path is present THEN
the system SHALL produce output identical to serial parsing.

### Requirement 2 — OpenTelemetry ingestion without external infrastructure

**User Story:** As a developer whose harness emits OpenTelemetry, I want to point it at
KyberDash directly, so that I get span-level analysis without running an Aspire dashboard, a
container runtime, or a collector.

#### Acceptance Criteria

2.1. WHEN KyberDash starts THEN the system SHALL listen for OTLP over HTTP on port 4318 at
`POST /v1/traces`.

2.2. WHEN a request arrives with content type `application/json` THEN the system SHALL accept
it. The existing collectors hand-roll OTLP JSON to that exact port, so this encoding is in
active use and not merely permitted by the specification.

2.3. WHEN a request arrives with content type `application/x-protobuf` THEN the system SHALL
accept it, because an OTel SDK exporter emits protobuf by default.

2.4. IF the configured port is already bound THEN the system SHALL report the conflict and the
occupying process where discoverable, and SHALL NOT fail silently or select a different port
without saying so.

2.5. WHEN spans arrive faster than they can be persisted THEN the system SHALL batch writes
and SHALL NOT drop spans.

2.6. WHERE a .NET Aspire dashboard is already in use THEN the system SHALL offer an optional
ingest source that reads spans exported from it, so an existing corpus stays readable.

2.7. WHEN KyberDash operates without Aspire THEN the system SHALL NOT lose parent-child span
relationships to buffer eviction. In the Aspire-mediated pipeline this is a measured loss: 25
of 1,009 spans had already lost their parent, and 17 sessions held 27 run identifiers against
only 20 surviving run spans.

### Requirement 3 — One session identity across both ingest paths

**User Story:** As a developer whose harness both writes session files and emits telemetry, I
want each session counted once, so that my totals are not silently doubled.

#### Acceptance Criteria

3.1. WHEN the same session is observed through both the file path and the OTLP path THEN the
system SHALL count its turns, tokens and cost exactly once.

3.2. WHEN deduplication occurs THEN the system SHALL extend the cross-provider deduplication
mechanism that already exists upstream, and SHALL NOT introduce a second mechanism beside it.

3.3. WHEN a session is observed through both paths and the two disagree on a value THEN the
system SHALL prefer the richer source and SHALL record the disagreement as a problem rather
than discarding it.

### Requirement 4 — Token accounting that survives harness disagreement

**User Story:** As a developer comparing harnesses, I want token figures that mean the same
thing across tools, so that a comparison is a comparison and not an artefact of whose counter
I read.

#### Acceptance Criteria

4.1. WHEN a span or session record is normalized THEN the system SHALL store token classes
disjointly, such that `fresh_input + cache_read + cache_creation` is always the true input.

4.2. WHEN an adapter normalizes a harness's counters THEN the system SHALL convert that
harness's convention on the way in. Copilot's `gen_ai.usage.input_tokens` *includes* cache
read and cache creation; pi's *excludes* both — the same attribute key with opposite meanings,
which no reading of the OpenTelemetry specification distinguishes. Applying Copilot's
convention to pi produced negative fresh input on 293 of 307 measured spans; applying pi's to
Copilot double-counts input by up to 2×.

4.3. WHEN any record is normalized, including an orphan with no resolvable parent, THEN the
system SHALL validate its token decomposition.

4.4. IF a decomposition yields negative fresh input, or does not add back up to what the
harness reported, THEN the system SHALL fail loudly and record a problem rather than storing
the record.

4.5. WHEN a harness reports its own total THEN the system SHALL reconcile the per-turn sum
against it and SHALL expose the result as a per-request match indicator.

4.6. WHEN token counts are derived by tokenizing content rather than read from a counter THEN
the system SHALL present them as a lower bound and SHALL name the model. The `o200k_base`
proxy carries a 2.8–4.4% unattributed residual on one measured model against 35–41% on
another; the difference is the tokenizer, not missing content.

### Requirement 5 — Cost attribution that never blends its bases

**User Story:** As a developer looking at a dollar figure, I want to know where it came from,
so that I can tell a published rate from a harness's own arithmetic and neither is silently
substituted for the other.

#### Acceptance Criteria

5.1. WHEN the system presents any cost figure THEN it SHALL carry the basis it was derived
from, and SHALL NOT blend figures of different bases into one total without saying so.

5.2. WHEN a harness reports its own cost THEN the system SHALL carry that figure verbatim in
preference to deriving one.

5.3. IF a published rate table does not name a harness in its applicability list THEN the
system SHALL NOT price that harness from the table. Unguarded, this scoping failure would have
priced 143 pi turns at GitHub's credit rate and totalled $0.27 against the $1.57 actually
charged — wrong by 5.8×, in the understating direction, and entirely plausible-looking.

5.4. IF a model has no published rate THEN the system SHALL render "no published rate" and
SHALL NOT render `$0.00`.

5.5. WHERE a model is explicitly not billed THEN the system SHALL distinguish that from an
absent rate.

5.6. WHEN a rate table encodes context tiers THEN the system SHALL select the tier by measured
input size.

### Requirement 6 — Telemetry the system declines to interpret stays visible

**User Story:** As a developer adding a new harness, I want to see what KyberDash could not
classify, so that unrecognized telemetry is a diagnosable gap rather than silent absence.

#### Acceptance Criteria

6.1. WHEN a span matches no adapter with sufficient confidence THEN the system SHALL
quarantine it with its observed attribute namespaces and SHALL NOT guess at its harness.

6.2. WHEN harness attribution is performed THEN the system SHALL attribute by attribute
fingerprint and SHALL NOT attribute by the telemetry source name, which carries per-instance
suffixes, does not track content, and is not stable across reconfiguration.

6.3. WHEN quarantined spans exist THEN the system SHALL expose them in a view with the counts
and namespaces needed to write the missing adapter.

6.4. WHEN validation problems are recorded THEN the system SHALL expose them in a view rather
than only in logs.

### Requirement 7 — Context composition and pressure

**User Story:** As a developer whose session burned an unexpected number of tokens, I want to
see what was resident in the context window, so that I can act on the largest contributor
instead of guessing.

#### Acceptance Criteria

7.1. WHEN a turn's input is analysed THEN the system SHALL bucket it by part type — system
prompt, tool definitions, instruction and workspace context, conversation history, and file
contents arriving through tool results.

7.2. WHEN bucketing is performed THEN the system SHALL bucket on part type and SHALL NOT
bucket on message role.

7.3. WHEN buckets do not account for the whole input THEN the system SHALL show the residual
explicitly and SHALL attribute it to tokenizer drift only where that is the actual cause.

7.4. WHEN a session is displayed THEN the system SHALL present context headroom, token
accumulation rate, and context pressure across turns.

7.5. WHEN fresh input rises sharply between consecutive turns THEN the system SHALL flag the
turn, because that is the visible signature of cache invalidation.

7.6. IF a data source cannot supply message structure THEN the system SHALL report context
composition as not measurable for that source and SHALL NOT chart a residual that would be
read as tokenizer drift.

### Requirement 8 — Tool and schema cost

**User Story:** As a developer with several MCP servers configured, I want to know what their
tool definitions cost me every turn, so that I can remove the ones I never invoke.

#### Acceptance Criteria

8.1. WHEN tool definitions are present THEN the system SHALL rank them by resident cost,
computed as schema tokens multiplied by turns resident.

8.2. WHEN a tool was offered but never invoked THEN the system SHALL report its cost
separately.

8.3. WHEN tools originate from MCP servers THEN the system SHALL group them by server and
SHALL resolve server names against ground truth rather than by splitting a prefixed
identifier.

8.4. WHEN the cost of unused schemas is presented THEN the system SHALL express it as a range
bounded by the cache-read floor and the fresh-input ceiling, because the true figure depends
on cache behaviour the telemetry does not report.

8.5. IF a data source reports invocations but no definitions THEN the system SHALL report
schema ranking as not measurable for that source and SHALL NOT report zero.

### Requirement 9 — Execution structure

**User Story:** As a developer debugging a slow or looping agent workflow, I want to see the
call tree, so that I can find the bottleneck or the circular delegation.

#### Acceptance Criteria

9.1. WHEN a session's spans form a tree THEN the system SHALL render tool executions and
subagent invocations hierarchically over a timeline.

9.2. WHEN a span is selected THEN the system SHALL expose its attributes for inspection.

9.3. WHEN a session contains subagent activity THEN the system SHALL identify subagent
sessions and their parent.

9.4. WHEN a session contains auxiliary activity such as title generation THEN the system SHALL
separate it from the primary conversation while still reporting its spend.

### Requirement 10 — Comparison across harnesses

**User Story:** As a developer choosing between tools, I want a comparison that distinguishes
what a harness did not do from what it does not report, so that I do not read an absent metric
as a good score.

#### Acceptance Criteria

10.1. WHEN a metric is presented for a harness THEN the system SHALL declare whether that
metric is measurable for that harness, separately from its value.

10.2. IF a metric is not measurable for a harness THEN the system SHALL render it as not
measurable and SHALL NOT render zero.

10.3. WHEN corpora differ in size THEN the system SHALL lead with per-turn ratios rather than
totals, because totals measure how long each harness was left running.

10.4. WHEN cost is compared THEN the system SHALL compare only through a declared basis.

### Requirement 11 — Surfaces

**User Story:** As a developer, I want the same figures wherever I look — terminal, browser,
desktop window, menu bar — so that I do not have to learn which surface is authoritative.

#### Acceptance Criteria

11.1. WHEN any surface displays data THEN the system SHALL derive it from one data path that
covers both file-sourced and OTLP-sourced sessions.

11.2. WHEN the terminal dashboard runs THEN the system SHALL present period reports, breakdown
tables and daily activity.

11.3. WHEN the web dashboard runs THEN the system SHALL present the analyses of Requirements 7
through 10.

11.4. WHEN the macOS menu-bar application or the Electron application requests data THEN the
system SHALL supply it through a machine-readable status format produced by the CLI.

11.5. WHEN a new analysis is added THEN extending that status format SHALL be sufficient to
carry it into the native clients, without modifying the clients themselves.

11.6. WHEN the MCP server runs THEN the system SHALL expose the same figures to an agent.

### Requirement 12 — Local-only data handling

**User Story:** As a developer whose telemetry embeds real repository content, I want certainty
that none of it leaves my machine, so that I can run this against work that is not mine to
disclose.

#### Acceptance Criteria

12.1. WHEN KyberDash operates THEN the system SHALL NOT transmit session content, span
content, or derived figures off the machine.

12.2. WHERE the product fetches reference data such as pricing THEN the system SHALL send no
user data in that request, and SHALL function from cached data when the fetch fails.

12.3. WHEN artifacts are produced for version control THEN the system SHALL NOT write captured
content into tracked files, because git history is permanent and one commit survives every
later cleanup.

12.4. WHEN the system stores raw telemetry THEN it SHALL bound the cost of doing so. The
current store holds 37,623 spans in 2.9 GB — roughly 78 KB per span — because raw content
attributes are persisted uncompressed.

### Requirement 13 — Installation and distribution

**User Story:** As a developer installing KyberDash, I want the same single verified command
that installs the rest of the toolchain, so that there is one path to learn and one to trust.

#### Acceptance Criteria

13.1. WHEN a user installs KyberDash THEN the system SHALL be installable through the
repository's existing `install.sh`, verified against published checksums.

13.2. WHEN a release is produced THEN the system SHALL ship a self-contained artifact for each
supported runtime identifier.

13.3. WHEN the installer runs THEN the system SHALL NOT require a package manager, a language
runtime, or elevated privileges.

13.4. WHEN the user installs the macOS menu-bar application THEN the system SHALL install a
signed application and SHALL verify it before placing it.

### Requirement 14 — The fork stays mergeable

**User Story:** As the maintainer, I want upstream's provider coverage to keep arriving, so
that supporting 41 agent tools does not become my maintenance burden.

#### Acceptance Criteria

14.1. WHEN upstream publishes changes THEN the system SHALL be able to take them through a
three-way merge rather than a manual port.

14.2. WHEN KyberDash code is added THEN it SHALL live outside upstream's directories and
SHALL consume upstream's output rather than modifying its internals.

14.3. IF a change inside upstream's directories is unavoidable THEN the system SHALL record
that file and the reason in this specification, so a future conflict arrives with its
rationale attached.

14.4. WHEN an upstream directory serves a surface KyberDash does not ship THEN the system
SHALL leave it in place unmodified, because deleting it manufactures a conflict on every
future merge.

### Requirement 15 — Migration from the Python pipeline

**User Story:** As the maintainer, I want proof that the new pipeline computes what the old one
computed, so that retiring the old one is a verified step rather than an act of faith.

#### Acceptance Criteria

15.1. WHEN the ported pipeline processes the same span corpus as the Python pipeline THEN it
SHALL reproduce that pipeline's content-free parity digest exactly.

15.2. IF the digests differ THEN the Python pipeline SHALL remain authoritative and the
difference SHALL be resolved before migration proceeds.

15.3. WHEN migration completes THEN the system SHALL NOT require the existing derived store to
be carried forward; the corpus SHALL be re-ingested from existing span exports and live OTLP.

15.4. WHEN the Python project is retired THEN its documented rationale — the measurements
cited throughout these requirements — SHALL survive in this repository's documentation.

## Traceability

Each requirement above is realized by the design in [design.md](design.md) and delivered by
the tasks in [tasks.md](tasks.md). Task entries cite requirement numbers in the form `4.2`,
so a criterion can be traced to the task that satisfies it and back.

## Open questions — ✅ Resolved (2026-08-29)

These were genuine ambiguities, not deferred decisions. Each named how it would be resolved; all four are now resolved.

- **Single-executable packaging for an ESM bundle — ✅ Resolved.** Confirmed: single-executable packaging with a CommonJS entry alongside the existing ESM bundle. Requirement 13 assumes a self-contained artifact is producible; upstream has zero native modules, which holds. The runtime's single-executable support takes a CommonJS entry while upstream is ESM — Task 1 will validate against the real bundle. The fallback remains a pinned-runtime archive with a launcher.
- **Does the CodeGraph index cover TypeScript? — ✅ Resolved.** Yes.
- **Is OTLP over gRPC in scope? — ✅ Resolved.** Deferred — HTTP only. Requirement 2 specifies HTTP, which covers the existing collectors and the common SDK configuration.
- **Which technologies the ontology gains — ✅ Resolved.** Both TypeScript and Swift are needed.
