---
id: plans/2026-11-01-factory-droid-capture-plugin
title: "Factory (Droid) capture — a hooks-based Factory plugin and a KyberDash Settings row for Factory"
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
last-reviewed: 2026-11-02
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberDash, KyberSquad]
---

# Factory (Droid) capture — a hooks-based Factory plugin and a KyberDash Settings row for Factory

**Status: Archived — complete 2026-11-02.** Approved for execution 2026-11-01 (D22,
`AUTONOMOUS_APPROVAL: approve-and-execute`, provenance `autonomous: recommendation accepted`) and
delivered on the branch `feat/factory-droid-capture-plugin`. The closeout mapping, the harvest
verification and the documentation-impact review are in `## Closeout mapping`; frontmatter
`status: archived` follows the closed status vocabulary (`KW-DOC-LIFECYCLE-004`'s C1). Every
decision row below is answered, and the table is preserved intact by the archive move as the
repository's closeout policy requires; the amendments D28–D67 amended answers in place, and each
correction carries its reason and its provenance in the row's status column. Execution authority
was the task list in §5: T1–T21, plus the rework tasks T22–T33 added by the D28–D37 amendments below, each
with its RED/GREEN split, acceptance criteria and test command, plus the tasks T34–T46 added by
the D43–D56 amendment, T47–T48 added by the D57–D64 amendment, and T49–T50 added by the D65
amendment. The
conductor dispatches them; no task runs before it does. The reconciliation of the external
evidence digest and the local-environment digest (D12, D23–D26, A1–A10) is folded into this
version; the two documentation checks must be re-run by the conductor after these edits, because
the architect session cannot run them (§2.1). **Amendment 5 (D38–D42) adds no task**: it reconciles
this plan's decision text with the code that shipped, and closes the one open question the
earlier amendments left.

**Lifecycle note (D27).** The frontmatter `status` is `current`, not `ready`: the ontology's
status vocabulary is closed (`current`, `draft`, `needs-review`, `superseded` —
`src/KyberWeave.Core/Configuration/OntologyConfig.cs:30-31`), and any other value is a
`docs validate` error. The body status is authoritative when the two disagree
(`products/kyber-squad/agents/architect/references/plan-authoring.md`, "Start or resume"), so
**Ready** is this plan's lifecycle state, and the `## Active Plans` row in
`docs/plans/README.md` states it. **AMENDED at closeout (2026-11-02):** the plan is archived, so
the frontmatter `status` is `archived` — the closed vocabulary's value for closed work, and the
one `KW-DOC-LIFECYCLE-004` accepts — and the body status line above now reads **Archived**.

**Amendment (D28–D30, T22–T26).** The pairing test written under T13
(`dash/src/canon/sessions.droid-pairing.test.ts`) proved three gaps in this plan's accounting
rules, and this amendment decides them. **D28** states the Factory source-precedence rule — in a
`droid` session holding both plugin rows and file rows, the plugin rows are the session's turns
and the file rows are its counters, and neither source is counted twice. **D29** moves the child
session's token source from the parent's `childInclusiveTokenUsageBySessionId` map to the child's
**own** companion file, which changes D25's meaning. **D30** requires the plugin adapter's four
`not_measurable` reasons to name the file-source pairing, which D8 and D9 already required and the
shipped wording did not. D21's scope boundary is amended by the same stroke: a droid-scoped change
to the shared projection's session derivation is now in scope, and nothing else is. The plan's
lifecycle state is unchanged (**Ready**): this is the scope decision T13's acceptance criteria
required before a projection change was made, recorded as a decision and dispatched as ordinary
tasks. The `## Design harvest` table keeps every row it had and gains two (H13, H14). Provenance:
**autonomous: recommendation accepted**.

**Amendment 2 (D31, D32, T27–T30).** The native-OpenTelemetry group (T15/T16) is implemented and
green, and the implementer flagged two seams where the production path differs from the tested
path. **D31** decides where Factory's native OTLP metrics GO: a new canon store table,
`otel_metric` — **D38 corrects the shipped name to `metric_point`**, and **D42** the point's
identity — holding the decoded metric points, written by the production sink through a new
canon-layer module; the canon projection does **not** consume metrics in this branch (a todo); and
the writer's "never accepted-and-dropped" rule does **not** change. **D32** decides the status
rule at the normalization seam: an error status is never downgraded, so the `droid` adapter's
`error.type` status survives into the stored record — a rule that is inert for every other
harness, because the `droid` adapter is the only adapter that assigns a status. The amendment also
records two further divergences the implementer did not flag (the persisted-fact hook omits
metrics, so a metrics-only commit never moves the receiver's last-persisted fact; and the metrics
path has no test below the receiver's decode seam), and the harvest table gains two rows (H15,
H16). The plan's lifecycle state is unchanged (**Ready**). Provenance:
**autonomous: recommendation accepted**.

**Amendment 3 (D33, D34, T31).** The `csharp-dev` worker for T18 escalated with
`CIRCUIT_BREAKER_TRIGGER: INVARIANT_CONTRADICTION`: three legacy fixtures in
`tests/KyberWeave.Tests/UpdateCommandTests.cs` pin the reconciliation sequence as
`["engine","tray","plugin"]`, `["engine","tray","plugin"]` and `["plugin"]`, and the Factory design
adds a second plugin call. **D33** resolves it: the escalation is a **legacy detail in a coupled
fixture, not an architectural contradiction** — those fixtures pin WHICH reconciliation kinds run
and in what ORDER, and the count of plugin entries was incidental to what they were written to
prove. The step is harness-aware and runs one plugin call per capture-plugin harness in a fixed
order, and the three fixtures are updated **in place** to state that substance. **D34** answers
the flag question: the updater's Factory call **carries `--preserve`** (D11 already mandates
preserve mode for `kyber-weave update`, and the OpenCode call has carried it all along), and the
RED test pins the flag's **presence**; D18's and T18's wording is amended to be unambiguous, because
"the same with `--if-installed`" never said whether the mode travelled with it. **No production
change is required — the worker's implementation stands.** The plan's lifecycle state is unchanged
(**Ready**). Provenance: **autonomous: recommendation accepted**.

**Amendment 4 (D35–D37, D14 amended, T32, T33).** The documentation harvest surfaced a real
inconsistency and three smaller ones. **D35** decides the `--config-dir` flag on the Factory
plugin install: the flag is the **OpenCode** config root, the Factory path takes **no root** from
it, the Squad step stops passing it for Factory, and the Factory verb **refuses** it with a usage
error instead of ignoring it; the Factory installer's root stays `$FACTORY_DIR` else `~/.factory`
— the resolution the Factory file provider uses — so no precedence rule is added and the plugin
and the file source always pair from the same root. **D36** requires the harness-collection rule's
Factory row to state the D28 split (the plugin rows are the session's turns, the file rows are its
counters, neither is counted twice) — the row as written claims only the exact-id join. **D37**
keeps the plan's native-availability reporting as a **requirement** (D26 stands) while saying
plainly that §3.3 describes a design the shipped row does not yet implement. **D14 is amended in
place**: the shared-transport extraction did **not** keep the OpenCode bundle byte-stable — the
bundle's module paths changed and the shared body builder gained an identity argument — while the
OpenCode **wire** stayed unchanged and the drift guard still holds in its real sense (the committed
artefact is current, not unchanged). The plan's lifecycle state is unchanged (**Ready**).
Provenance: **autonomous: recommendation accepted**.

**Amendment 5 (D38–D42, no new tasks).** The second documentation harvest (commit `54b31d6`)
compared this plan's decision text against the shipped code and reported four places where the
plan no longer matched what was built, plus one question that had never been decided. Every
claim below was checked against the code as it stands on `feat/factory-droid-capture-plugin`;
where the harvest's report was itself imprecise, the plan records the code's truth. **D38**
corrects the metric destination's **table name**: the shipped table is `metric_point`, created by
the store's own `CREATE ... IF NOT EXISTS` schema and written through the production sink's
`upsertMetrics` and the `ingestMetricBatch` seam — not the `otel_metric` this plan named, which
no code ever carried. **D39** corrects the **model chain**: the plugin reads the hook payload
only, so a plugin turn names a model only when the payload carries one; the FILE SOURCE reads
the session's `model` from the companion `<session-id>.settings.json` beside the transcript and
publishes it wrapper-stripped; and the session publishes the model its turn records name, else
the counter record's model, and none where no source named one — never a default, never a guess.
`~/.factory/settings.json` is never a model source. **D40** records that the literal `unknown`
the file source publishes for a companion file that names no model is the **file source's own**
placeholder — pre-existing behaviour outside this plan's scope (D21), which this plan does not
change and which a reader must not mistake for a measured model. **D41** removes a **stale
marker**: D37's "not shipped yet" note was right when it was written and is wrong now — the
native-export availability reporting ships on every status surface, and the status row ships it
(commit `e140472`). **D42** settles the open question D31 left open: a metric point's identity
is its **content digest**, so the plan's "one row per decoded point" is corrected to "one row per
DISTINCT decoded point", and the duplicate case — reachable, and always the same point delivered
twice — is stated plainly. **No implementation change is required or made by this amendment**:
the shipped code is right, and it is this plan's text that was wrong. The plan's lifecycle state
is unchanged (**Ready**). Provenance: **autonomous: recommendation accepted**.

**Amendment 6 (D43–D56, T34–T46).** A live check against a real Factory installation (Factory
Droid CLI 1.1.0, a real KyberDash receiver on the loopback OTLP port) produced one decisive
positive and **two blocking defects**, and this amendment decides both. **The positive:** the
plugin's hooks **do** fire in real Droid sessions, and a captured real plugin span is **accepted**
by the branch's production ingest path — the wire contract, the adapter and the canonical
projection are correct as shipped. **The first defect:** the shipped installer writes a plugin
directory and stops, and **Factory never loads it** — `droid plugin list` reports "No plugins
installed", and a real turn produced no plugin spans at all. Factory loads a plugin only from the
cache that `droid plugin install` fills, and only a plugin installed from a registered
marketplace reaches that cache. **D43** replaces the install route: KyberDash drives the harness's
own `droid plugin marketplace add` / `droid plugin install` / `droid plugin uninstall` /
`droid plugin marketplace remove` commands, owns a **marketplace tree of its own** under
`~/.kyberdash/marketplaces/kyberdash/`, and **never reads and never writes** the owner's
`~/.factory/settings.json` — the harness's own commands maintain that file, and KyberDash learns
the harness's state only through the harness's CLI. **D44–D47** fix the rest of the install
contract: what the installer writes and the uninstaller removes, what the prior state is and how
it is restored, what happens when a marketplace or plugin of the same name already exists, and what
a partial or interrupted install leaves. **D48** is the consequence for D4: D4's **letter** stands
(KyberDash neither reads nor writes the owner's settings or hooks files) and D4's **effect** clause
does not (the file's bytes change across an install, by the harness's hand) — the byte-identity
criterion is replaced by D45's semantic criterion. **D49** records the ADR consequence: ADR 0026 and
ADR 0031 stand in their letter; ADR 0031's Factory amendment is corrected in place and gains the
delegated-install clause. **The second defect:** the native OpenTelemetry path is **inert**. The
default `legacy` format emits content-shaped spans no adapter may claim, and the `genai` format's
trace is claimed only in part — the registry's group vote divides the winner's evidence by the
group's span count, so a one-turn trace scores `1/(1+k)` against a `0.6` threshold, and every real
trace (`k ≥ 1`) falls below it and is quarantined (measured: **accepted 0, quarantined 2**).
**D50** withdraws the native **span** claim from this branch and keeps the native **metrics** path,
with the rejected alternative (a group-scoped claim) and the arithmetic stated; **D51** makes the
status surface report the native export's effective shape — the format switch, the granularity, the
content switch, and the fact that KyberDash claims no native span. **D52** answers the content
question: KyberDash's own consent governs **every** route by which Factory content could reach
the store, including the quarantine packet, so with consent off no Factory prompt, reply, tool
argument or tool result is written anywhere. **AMENDED (D57): that sentence is WITHDRAWN as
false** — the consent governs the **plugin's prompt text only**, the file source's first-message
fragment is stored whatever the consent says, and the plugin's tool arguments and results are
byte counts rather than content; see Amendment 7 below. **D53** aligns the plugin's `hooks/hooks.json` with
the documented shape (the shipped file used the `settings.json` wrapper and worked only by
undocumented tolerance), and **D54** withdraws the plugin's `SubagentStop` child-session span,
because the documented payload carries no child identity. **D55** corrects the root resolution:
`FACTORY_DIR` does not redirect `~/.factory/settings.json`, and under the new route the install
writes nothing under the Factory root at all. **D56** records the lifecycle: the plan stays
**Ready**, the shipped tasks T1–T33 stand as shipped, and the new tasks T34–T46 are dispatched
only once the conductor accepts this amendment. Provenance: **autonomous: decided in the
D43–D56 amendment session; no hard block, no escalation**.

**Amendment 7 (D57–D64, T47–T48).** A follow-up audit of this plan against the shipped code and
against Factory's own published documentation found **one high-severity error** and a set of
smaller ones. The high-severity error is in the **consent's scope**: D52, the status row's fixed
words, the harvest row H24 and the live check all claim that with KyberDash's consent off **no
Factory content is stored anywhere in the store** — but the consent file is read only by the
capture plugin's hook runner and gates only the plugin's own prompt text, while the Factory
**file source** captures the first user message fragment (≤ 500 characters) into every
file-sourced record's raw attributes, which the store projects; and the plugin's tool hooks
publish **byte counts only**, so the plugin never carries a tool argument or a tool result at
all. **D57** narrows the consent's scope to what the code implements and to what this branch has
the authority to change, and **T47/T48** correct the shipped status wording that overstated it.
**D58** completes the effective shape the row must report — the organisation's `enabled` master
switch, which the shipped reader does not resolve — and states the granularity and content
wording with Factory's own citations. **D59** reconciles the receipt with the prior state the
install found and restates the restore criterion as a property of **KyberDash's own actions**,
with the harness's own deletion of an owner entry **reported, never patched**. **D60** records
Factory's organisation-level hard controls (`strictEnabledPlugins`, `strictKnownMarketplaces`,
`allowManagedHooksOnly`), which can refuse the user-scope route outright, and forbids working
around them. **D61** corrects this plan's factual statements about the shipped code. **D62** records
the **four** follow-ups the audit surfaced — the quarantine table's unbounded growth, consent as a
capture switch rather than a purge or a retention control, the harness's teardown dropping a
pre-existing owner entry, and an organisation's `allowManagedHooksOnly` making the capture silently
inert — and names the todo that carries each. **D63** re-bases the plan's **task accounting** on the
shipped code, because the branch moved on while this amendment was being written. **D64** records
the lifecycle: the plan stays **Ready**, and the new tasks T47–T48 are dispatched only once the
conductor accepts this amendment. Provenance: **autonomous: decided in the D57–D64 amendment
session; no hard block, no escalation**.

**Amendment 8 (D65, T49–T50).** The shipped Factory installer can install the capture plugin and
can leave it alone, but it **cannot upgrade it**. The tree classifier's `same` means "the tree
matches the receipt", and a tree KyberDash wrote with an **older bundle** still matches its
receipt — so the install reports `unchanged`, the Kyber-owned tree is never rewritten, and the
harness's cached copy keeps running the superseded bundle. Nothing on the branch ever replaces a
bundle that a newer KyberDash has superseded. **D65** decides the upgrade path. A tree the
**receipt proves** KyberDash wrote, whose bytes still match that receipt, and whose content is
not the current bundle, is an **upgrade** — not an owner edit and not `unchanged`; a tree no
receipt proves stays **foreign**, because matching an older bundle's bytes never establishes
ownership, and the existing strict/preserve behaviour applies to it unchanged. The upgrade
rewrites the Kyber-owned tree and its receipt with the current bundle and then refreshes the
**harness's cached copy** through the harness's own `droid plugin update kyberdash-factory@kyberdash
--scope user`; the refresh command is chosen from the **harness's own registry**, an installed
plugin is never refreshed with `plugin install` (Factory 1.1.0 refuses it), and an installed
plugin whose marketplace registration is missing gets the registration restored first.
`--if-installed` decides from the harness's registry, as D11 requires, and the refresh happens
even when KyberDash's own tree is absent and must be written for the first time. The reporting
gains the `upgraded` and `would-upgrade` tokens, the strict/preserve refusal behaviour is
unchanged, and **a harness refusal is never reported as a success**. `unchanged` now means
**both** facts — the tree is the current bundle **and** the harness holds the plugin — so a
routine install mutates nothing and runs no harness command. The **receipt keeps its shape**
(`{version, marketplace, plugin, root, files[]}`): its per-file digests already prove the
installed tree's identity and ownership, so no bundle-identity field is added and the receipt
forms the plan already requires the installer to read stay readable. The plan's lifecycle state
is unchanged (**Ready**); **T49/T50** are dispatched only once the conductor accepts this
amendment. Provenance: **autonomous: decided in the D65 amendment session; no hard block, no
escalation**.

**Development mode:** `test-first` (supplied in the packet). **Autonomy:** `autonomous` (supplied
in the packet; matches the repository root `AGENTS.md` Config Reg `<autonomy>`).

Issue: [#485](https://github.com/dpalfery/kyber-weave/issues/485) — a hooks-based capture plugin
and a Settings row for Factory (Droid) in KyberDash.

Branch: `feat/factory-droid-capture-plugin`. **AMENDED (D63): the branch now exists** — it was
cut from `origin/main` `7b307703` and has since taken the marketplace route, the `SubagentStop`
withdrawal, the native-claim withdrawal and the export's effective shape (D63). The branch line
above is a record of where the plan was written, not of the current checkout.

## 1. Problem and goal

### 1.1 Problem, as it stands in this checkout

**AMENDED (D63): this table records the problem as it stood when the plan was written — before
this branch's own work landed.** On the current branch the Factory Settings row, the plugin, the
marketplace route and the native metrics path **exist**; what remains open is the consent gate
(T42/T43), the hooks file's shape, the name-conflict refusal and the residue report (T36/T37),
and the status row's corrected wording (T47/T48). The table is kept, because a plan that rewrites
its own problem statement loses the record of what it was written against; the state of the branch
is **D63's task accounting**, not this table.

Factory (`droid`) is a declared Kyber-Squad target (`docs/kyber-squad/onboarding.md:109`, target
`factory`) and KyberDash already ingests it — but only as a **file** source, and it has no
Settings row and no capture plugin:

| Gap | Evidence in this checkout |
|---|---|
| No Settings row: `CAPTURE_HARNESS_REGISTRY` has seven writers and Factory is not one of them | `dash/src/capture/harnesses/registry.ts:205-213` — `copilotHarness`, `claudeCodeHarness`, `codexHarness`, `opencodeHarness`, `piHarness`, `antigravityHarness`, `kiloHarness` |
| No plugin, no OTLP path for Factory | `dash/src/capture/harnesses/` has no `factory-plugin/`; the receiver serves `POST /v1/traces` and `POST /v1/logs` only (`dash/src/otel/receiver.ts:62-63`, `:1379-1386`) |
| The file path loses turn and tool timing, and the model attribution is a session-level guess | `dash/src/providers/droid.ts:113-290` — tokens come from the companion `*.settings.json` `tokenUsage` and are **split evenly** across assistant calls (`:196-232`); the model is the session's `settings.model`, never the per-turn model |
| The harness id the file source stores is `droid` | `dash/src/providers/droid.ts:384` (`name: 'droid'`), `dash/src/refresh/registry.ts:153,406-410` (`harnessId: 'droid'`, `sourceRootLabel: '~/.factory/sessions'`) |

The precedent to follow is the OpenCode capture plugin: one Kyber-owned plugin file, one receipt,
one consent file, one managed writer row, and a plugin pipeline from the host's hooks to
contract-3 OTLP spans to a canon adapter to the shared projection
(`dash/src/capture/harnesses/opencode-plugin/`, `dash/src/canon/adapters/opencode.ts`,
[ADR 0048](../../adr/0048-opencode-capture-is-a-v2-only-plugin-pipeline.md)).

### 1.2 Goal

Make KyberDash fully operational for the Factory harness:

1. A Kyber-owned Factory **hooks plugin** (`kyberdash-factory`) that emits turn, tool and session
   spans as OTLP/HTTP to the loopback receiver, with content off by default behind a consent
   file, and that never fails the host and never writes the owner's Factory settings.
   **AMENDED (D43):** the plugin is installed through **Factory's own marketplace and plugin
   commands**, from a Kyber-owned marketplace tree under `~/.kyberdash/`, and the owned unit is no
   longer a directory under the Factory root.
2. A **Settings row** for Factory (`displayName: 'Factory (Droid)'`) whose Enable installs the
   plugin and whose Disable removes it, reporting plugin state, receipt and consent state.
   **AMENDED (D47):** the row reports **two** states — the Kyber-owned tree's and the harness-side
   install's — read through the harness's own CLI.
3. A canon **adapter** that claims the plugin's spans, so a live Droid turn produces a
   dashboard-visible session with model and tool attribution.
4. Honest token accounting: paired from the existing file source when the session ids agree,
   otherwise `not_measurable` with a stated reason — never a fabricated zero.
5. An **authorized, additive** native-OpenTelemetry branch (`POST /v1/metrics` and a
   `gen_ai.agent.name=droid` claim), because Factory's own documentation confirms its endpoint is
   user-configurable (`OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"` is Factory's own example).
   It complements the plugin rows — it carries no tokens and no subagent lineage — and never
   replaces them (D12, D23).

### 1.3 Explicitly not the end state

A status-only row (the same gap in another shape), native Factory OpenTelemetry as the primary
path, merging hooks into `~/.factory/hooks.json`, and file-only ingest are rejected for the
reasons recorded in D2 and in the issue.

## 2. Investigation findings

### 2.1 Discovery tooling — unavailable in the architect session, checks run by the conductor

The repository asks for `codegraph_explore` and the Kyber-Weave MCP documentation tools
(`docs_explore`, `docs_for_symbol`, `docs_glossary`) before text search, and for
`docs_validate` / `docs_drift` after a plan write (root `AGENTS.md`, "Exploration order" and
"Non-negotiables"). **Neither tool family is loadable in the architect session**: the only
deferred tools are `WebSearch` and `FetchUrl`, and every `ToolSearch` query — including the
exact-name form `select:docs_validate,docs_drift,docs_explore,codegraph_explore` — returned
"Every tool this session can load is named in the Deferred tools reminder … the capability is
unavailable in this session". The plan was therefore authored from **direct reads and targeted
greps** of the named files, and that discovery record stands.

**The documentation checks were run, by the conductor, and both passed.** Against this saved plan
and its index registration, over a CodeGraph-indexed checkout:

- `docs validate .` (mergeReady false, the planning gate while a plan sits in the active folder):
  **pass — 0 errors, 0 warnings**, 150 documents.
- `docs drift .`: **pass — 0 errors, 0 warnings**, 150 documents.

So the plan and its index row are schema-conformant and drift-free. The two checks must be re-run
after every further change to this plan or to the index, because the architect session cannot
run them itself. **The D28–D30, D31–D32, D33–D34, D35–D37, D38–D42, D43–D56, D57–D64 and D65
amendments are such changes**: the two checks
must be re-run by the conductor against this amended plan, for the same reason and with the same
`--merge-ready` gate (the plan still sits in the active plans folder, so `mergeReady` stays false).

### 2.1a The D43–D56 amendment session — no semantic index, no documentation tools, and no shell

The amendment session that produced D43–D56 had **neither tool family and no process-execution
tool**. The CodeGraph MCP tool (`codegraph_explore`) and the Kyber-Weave MCP documentation tools
(`docs_explore`, `docs_for_symbol`, `docs_glossary`, `docs_validate`, `docs_drift`) were **not
loadable**: every `ToolSearch` query — the exact-name form
`select:codegraph_explore,docs_explore,docs_for_symbol,docs_glossary,docs_validate,docs_drift`
and four keyword forms — returned "Every tool this session can load is named in the Deferred
tools reminder; if none provides this, the capability is unavailable in this session and no
ToolSearch query will change that." The session's deferred tools were `WebSearch` and `FetchUrl`
only, and **no shell was exposed**, so the `codegraph explore` CLI fallback, the two documentation
gates and `git` were all unavailable. The amendment was therefore authored from **direct reads,
targeted greps and Factory's own published documentation** (fetched over HTTP with `FetchUrl`),
and **the two documentation checks could not be re-run in this session** — the conductor must run
them against this amended plan, for the same reason and with the same `--merge-ready` gate.

### 2.1b The D57–D64 amendment session — no semantic index, no documentation tools, and no shell

The amendment session that produced D57–D64 had **neither tool family and no
process-execution tool**, exactly as the D43–D56 session did (§2.1a). The CodeGraph MCP tool
(`codegraph_explore`) and the Kyber-Weave MCP documentation tools (`docs_explore`,
`docs_for_symbol`, `docs_glossary`, `docs_validate`, `docs_drift`) were **not loadable**: every
`ToolSearch` query — the exact-name form
`select:docs_validate,docs_drift,docs_explore,docs_for_symbol,docs_glossary,docs_status` and
keyword forms — returned "Every tool this session can load is named in the Deferred tools
reminder; if none provides it, the capability is unavailable in this session and no ToolSearch
query will change that." The session's deferred tools were `WebSearch` and `FetchUrl` only, and
**no shell was exposed**, so the `codegraph explore` CLI fallback, the two documentation gates and
`git` were all unavailable. The amendment was therefore authored from **direct reads, targeted
greps and Factory's own published documentation** (fetched over HTTP with `FetchUrl`), and **the
two documentation checks could not be re-run in this session** — the conductor must run them
against this amended plan, for the same reason and with the same `--merge-ready` gate.

**One further difference from the earlier sessions, and it matters.** The branch had **moved on**
since the D43–D56 amendment was written: the marketplace route, the `SubagentStop` withdrawal,
the native span claim's withdrawal and the export's effective-shape work had all landed. The
amendment's task accounting was therefore re-based on the working tree rather than on the earlier
amendment's text (D63), because a task list that reports shipped work as pending sends the
conductor to re-do it.

**The reconciliation pass that closed this amendment had the same limits, and re-confirmed them.**
It ran with the same two tool families absent and no shell: `ToolSearch` was asked for
`docs_validate`, `docs_drift`, `docs_explore` and `codegraph_explore` by exact name and by keyword,
and every query returned "Every tool this session can load is named in the Deferred tools
reminder; if none provides it, the capability is unavailable in this session and no ToolSearch
query will change that." **A `bash`/`shell`/`execute`/`git` capability was searched for the same
way and is not in this session's list**, so the `codegraph explore` CLI fallback, the two
documentation gates and `git` remained unavailable to it as well. **The pass therefore could not
run `docs validate` or `docs drift`, and could not commit** — the gates are the conductor's, and
they must be run against this amended plan and this amended index row, with `mergeReady` false,
before anything is committed (§13, item 11).

### 2.1c The D65 amendment session — no semantic index, no documentation tools, and no shell

The amendment session that produced D65 had **neither tool family and no process-execution
tool**, exactly as the D43–D56 and D57–D64 sessions did (§2.1a, §2.1b). The CodeGraph MCP tool
(`codegraph_explore`) and the Kyber-Weave MCP documentation tools (`docs_explore`,
`docs_for_symbol`, `docs_glossary`, `docs_validate`, `docs_drift`) were **not loadable**: the
`ToolSearch` query for them returned "Every tool this session can load is named in the Deferred
tools reminder; if none provides this, the capability is unavailable in this session and no
ToolSearch query will change that." The session's deferred tools were `WebSearch` and `FetchUrl`
only, and **no shell was exposed**, so the `codegraph explore` CLI fallback, the two
documentation gates and `git` were unavailable as well. The amendment was therefore authored from
**direct reads, targeted greps and Factory's own published documentation** (fetched over HTTP
with `FetchUrl`), and **the two documentation checks could not be re-run in this session** — the
conductor must run them against this amended plan and this amended index row, with `mergeReady`
false, for the same reason and with the same gate (§13, item 12).

### 2.2 What the repository already provides (the reusable seams)

| Fact | Where |
|---|---|
| The registry holds one writer per harness; a managed writer may own a whole plugin file through `filePlugin` | `dash/src/capture/harnesses/registry.ts:151-203` (`ManagedHarnessWriter`, `ManagedFilePlugin`) |
| A plugin-only managed writer is the OpenCode shape: `kind: 'managed'`, `desiredKeys: () => ({})`, a `filePlugin`, no config file | `dash/src/capture/harnesses/opencode.ts:239-246`; `dash/src/capture/index.ts:268-280` (`isPluginOnly`) |
| Enable = strict install then consent; disable = consent off then uninstall | `dash/src/capture/index.ts:927-979` (`enableFilePlugin` / `disableFilePlugin`) |
| The Settings drawer is generic: it renders whatever the registry reports, so a new row needs **no web change** | `dash/web/src/components/settings/HarnessCollectionList.tsx:292`; `dash/web/src/lib/kyberApi.ts:1694,1947` (`CAPTURE_MAX_HARNESSES = 64`) |
| The capture route accepts any writer with a `filePlugin` | `dash/src/server/capture-routes.ts:81` (`MANAGED_IDS`) |
| Install modes: strict refuses a foreign or edited file (exit 1); preserve keeps it with a warning (exit 0) and creates the consent file with content off only when absent | `dash/src/capture/harnesses/opencode-plugin/install.ts:239-334` |
| The receipt is `{ version: 1, path, sha256 }`; the consent file is `{ version: 1, content: <boolean> }` | `install.ts:79,221-227`; `consent-write.ts:16-40` |
| A one-shot installer verb exists for out-of-drawer callers | `dash/src/capture/harnesses/opencode-plugin/install-command.ts`; registered at `dash/src/cli/register.ts:460-486` |
| Squad and `kyber-weave update` install the OpenCode plugin in preserve mode through that verb | `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs:161-169`; `src/KyberWeave.Cli/Update/SelfUpdater.cs:425-460` |
| The receiver decodes OTLP spans and hands them to a store port; adding a path means touching the route table and the pause/error-window predicates | `dash/src/otel/receiver.ts:62-63,1119-1121,1370-1386` |
| An adapter claims a harness by attribute fingerprint only, never by source name | `dash/src/canon/adapters/base.ts` (`HarnessAdapter`), `dash/src/canon/adapters/registry.ts:106-141` |
| The OpenCode plugin envelope is claimed only under a contract version; an unknown version quarantines | `dash/src/canon/adapters/opencode-envelope.ts:47-79`; `dash/src/canon/adapters/opencode.ts:281-300` |
| The plugin bundle is committed text, regenerated by esbuild and drift-checked | `dash/scripts/bundle-opencode-plugin.mjs`; `dash/src/capture/harnesses/opencode-plugin/bundle.test.ts`; `dash/src/architecture/opencode-leaves.test.ts` (leaf rule) |
| The file source for Factory and its token pairing already exist | `dash/src/providers/droid.ts:68-69` (`FACTORY_DIR` → `~/.factory`), `:129-135` (companion `*.settings.json`), `:196-232` (even split of `tokenUsage`), `:295-299` (internal-session skip) |
| The collection standard's mechanism hierarchy: plugin hooks are a PRIMARY mechanism; file ingest is fallback where a PRIMARY mechanism is declared | `docs/rules/kyberdash-harness-collection.md` ("Mechanism hierarchy") |

### 2.3 The display-layer constraint

`dash/web` and `dash/tray` are display layers: no feature logic, no schedulers, no jobs, one
datastore, one `/api/kyber/*` API (`dash/AGENTS.md`, `docs/rules/kyberdash-display-layer.md`,
[ADR 0033](../../adr/0033-kyberdash-surfaces-are-display-layers.md)). This plan therefore adds
**zero** web code: the Factory row appears because the engine's registry reports it. The
`dash/src/architecture/display-layer.test.ts` scan stays green.

### 2.4 The ownership constraint

ADR 0026 ("Kyber Utilities deploys owned files and never owns harness settings") and
[ADR 0031](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) (as amended
for the OpenCode and Kilo file plugins) set the bar: KyberDash may own a **whole file or file
tree** of its own, with a receipt and a digest, but it never edits a harness's shared settings
file. A Kyber-owned plugin directory under `~/.factory/plugins/` is the same shape as the OpenCode
plugin file: Kyber owns the tree, the receipt records every file's digest, and the owner's
`~/.factory/settings.json` and `~/.factory/hooks.json` are never read, written, merged or
restored. **AMENDED (D43, D48): the plugin directory's LOCATION is wrong — that directory is not
a discovery path (A1, §2.6) — and the owned unit becomes a Kyber-owned marketplace tree under
`~/.kyberdash/`. The ownership rule itself stands unchanged: Kyber owns a whole tree of its own,
the receipt records every file's digest, and the owner's settings and hooks files are still never
read, written, merged or restored by KyberDash.**

### 2.5 Local-environment evidence (read-only inspection of this machine)

Empirical facts about the Factory installation this work runs against, from the local-environment
digest. They correct two premises of the issue text and they sharpen the model, token and
child-session rules.

| Fact | Consequence in this plan |
|---|---|
| `~/.factory/plugins/` **exists** and holds only `sync_stamp.json`; no Factory plugin is installed. Squad owns `~/.factory/droids/` and `~/.factory/skills/`, so the plugin subtree is a third, distinct, collision-free subtree | **AMENDED (D43):** the directory's existence is **not** evidence of a discovery path — the live check disproved it (A1, §2.6). The owned unit leaves the Factory root entirely and becomes the Kyber-owned marketplace tree under `~/.kyberdash/` (D44), so D18's no-collision claim now concerns a tree under KyberDash's own state directory, and the harness's own cache is the harness's file |
| **No `~/.factory/hooks.json`** exists on this machine, and there is no `~/.factory/plugins/hooks` | The byte-identity criterion must still be proven for an owner who *does* have one: the test **creates** `~/.factory/hooks.json` (and a project `.factory/hooks.json`) and proves both are byte-identical after install, enable, disable and uninstall (T7) — **AMENDED (D48): the hooks file's criterion is unchanged and still holds, because the plugin route does not touch it; the SETTINGS file's byte-identity criterion is withdrawn (D45), because the harness's own commands change that file** |
| `~/.factory/settings.json` exists and its keys are `compactionModel`, `compactionTokenLimit`, `keepAwakeEnabled`, `logoAnimation`, `missionModelSettings`, `sessionDefaultSettings`, `subagentModelSettings`, `trustedFolders`. **There is no top-level `model` key**; the model lives in nested blocks (`sessionDefaultSettings.model`, `missionModelSettings.workerModel`, `subagentModelSettings.{light,medium,heavy}Model`, `compactionModel`) | **The issue's premise is wrong about this file's shape.** The model-resolution chain is corrected in D24; no nested settings block is ever read as "the model" |
| Every session directory holds a `<session-id>.settings.json` beside `<session-id>.jsonl`, with keys `model` (`minimax-m3.1-flash`), `tokenUsage` (`inputTokens`, `outputTokens`, `cacheCreationTokens`, `cacheReadTokens`, `thinkingTokens`, `factoryCredits`), `lastCallTokenUsage`, `inclusiveTokenUsage`, `childInclusiveTokenUsageBySessionId`, `providerLock`, `apiProviderLock`, `toolExecutionModeModelId`, `reasoningEffort`, `interactionMode`, `autonomyLevel`, `artifacts`, `allowSubagentsInScripts`, `compactionThresholdCheckEnabled`, `specModeModel` | The companion file — **not** `~/.factory/settings.json` — is the token and model source, which is what `dash/src/providers/droid.ts:129-135` already reads. D9 stands and is strengthened. `childInclusiveTokenUsageBySessionId` gives child-session tokens keyed by child session id (D25) |
| A Factory **child (subagent) session is a first-class session on disk**: the parent session `5f50fd96-…`'s companion file names the child `bfde2f1a-…` with input 368047 / output 41735 / cacheRead 1617280, and the child's **own** `<child-id>.settings.json` beside its own `<child-id>.jsonl` carries exactly those counters. The child's transcript's `session_start` names the child's id and its `callingSessionId` names the parent; the parent's `inclusiveTokenUsage` (369300 / 47551 / 1938106) equals the parent's own `tokenUsage` (1253 / 5816 / 320826) plus every child's entry | The child session is discovered by the file source as its own session, so a child's tokens come from **its own** companion file through the ordinary exact-id pairing (D29) and **no file-import change is needed**; the parent's `childInclusiveTokenUsageBySessionId` map is the parent's aggregate view of its children, never a token source, and a child's usage is never folded into the parent's totals. The characterization test that pins the map as ignored stays correct and unchanged (A9, A10) |
| Each transcript record carries `type` (`session_start`), `id`, `timestamp`, `message`, `seq`, `parentId`, `messageIndex`, `title`, `cwd`, `hostId`, `turnId`, `reason`, `resultKind`; an assistant `message` carries `modelId` (`minimax-m3.1-flash`), `apiProvider` (`fireworks`), `reasoningEffort`, `userMessageSource`, `openaiMessageId`. **No `usage` field exists on any message** | The per-message model is `message.modelId`, which D24 listed as a link of the chain. **AMENDED (D39): no shipped reader reads it** — the file source names the session's model from the companion file, and nothing reads the transcript's per-assistant model. `parentId` gives parent/child turn linkage that the native `genai` OTLP format does not carry (D2) |
| `~/.factory/org-managed-settings.cache.json` holds `factoryTier` (`team_annual`) and a `managedSettings` object with **only** `modelPolicy` and `routedModelVisibilityPolicy` — **no `telemetry` block** | For this user no organisation pins an endpoint, so a machine environment variable would take effect. The org-override caveat (D12, D26) is real but not universal |
| The installed Factory CLI reports version **1.1.0** | The version is recorded in the status surface and the live check so a future Factory change is diagnosable |

### 2.6 The live check's evidence (real Factory 1.1.0, a real receiver on the loopback OTLP port)

The live check that produced the D43–D56 amendment ran against a real Factory installation with a
real KyberDash receiver listening. Its findings are facts, and they are recorded here before the
decisions that answer them. Each row names its evidence: **measured** on this machine, or
**documented** by Factory's own published documentation (fetched over HTTP).

| Fact | Consequence in this plan |
|---|---|
| **MEASURED — the plugin's hooks fire in real Droid sessions.** With the plugin installed and enabled, real `factory.tool` spans arrived at the receiver carrying a live session id and naming real tools. Fed through the branch's production ingest path, a captured real plugin span is **accepted**: the droid adapter's detect score is 1, the confidence is 1.000, and the record lands as harness `droid`, op `tool.invoke`, with the correct session id | The wire contract, the adapter and the canonical projection are **correct as shipped**; D5, D6, D8, D9 and D28 stand, and the plugin path is the primary mechanism (D2) |
| **MEASURED — the shipped installer never registers the plugin.** It writes `~/.factory/plugins/kyberdash-factory/` and stops. After that install, `droid plugin list` reports "No plugins installed", and a real Droid turn produced **no plugin spans at all** | **A1 is disproven.** The install route changes (D43); the owned unit moves out of the Factory root (D3, D44) |
| **DOCUMENTED — plugins are installed from a marketplace.** A marketplace is a directory holding `.factory-plugin/marketplace.json`, whose `plugins[]` entries carry a `name` and a `source` (a relative path for a local marketplace; absolute paths and paths that escape the marketplace directory are rejected). Droid copies the plugin into its cache unchanged; the plugin identity is `pluginName@marketplaceName`; there is no `droid plugin enable` or `disable` command, and enablement is stored in the `enabledPlugins` setting | The install route is the harness's own commands (D43), and the identity and the enablement key are named (D44) |
| **MEASURED — the working route.** `droid plugin marketplace add <path>` registers a marketplace; for a local path the **registered marketplace name is derived from the path's directory basename**, not from the `name` field inside `marketplace.json`. `droid plugin install <plugin>@<marketplace> --scope user` copies the plugin into `~/.factory/plugins/cache/<marketplace-hash>/<plugin-hash>/<version>/`, writes a record under `~/.factory/plugins/installed_plugins/`, sets `enabledPlugins` in `~/.factory/settings.json`, and the plugin then reports STATUS "Enabled". `${DROID_PLUGIN_ROOT}` expands to the **cached** path, not the source path | The marketplace tree's directory name is load-bearing (D44, A12); the plugin's script path stays portable because the hook command uses `${DROID_PLUGIN_ROOT}` |
| **MEASURED — `FACTORY_DIR` does NOT redirect `~/.factory/settings.json`.** With `FACTORY_DIR` pointing elsewhere, `droid plugin marketplace add` wrote to the **real** `~/.factory/settings.json` | The root resolution is corrected per path (D55); the plan's "FACTORY_DIR else `~/.factory`" rule survives only for the **sessions** tree |
| **MEASURED — the native export in the default (`legacy`) format emits content-shaped spans.** `droid.message.user` and `droid.message.assistant` carry `model.id`, `message.role`, `message.content`, `session.id` and `prompt.id` under the scope `cli-customer-events`. The droid adapter scores them 0, no other adapter scores them, and both are **quarantined as `unclaimed`** | The legacy surface is not claimable and is not claimed (D50) |
| **MEASURED — the native export in the `genai` format emits a flat trace.** The `invoke_agent` span carries `gen_ai.agent.name=droid`, `gen_ai.operation.name=invoke_agent`, `gen_ai.conversation.id` and `session.id`; the `chat` span carries `gen_ai.operation.name=chat`, `gen_ai.request.model`, `gen_ai.conversation.id`, `gen_ai.response.finish_reasons`, `server.address` and `session.id`. The droid adapter scores them **[1, 0]** — it recognises a native span only by `gen_ai.agent.name === 'droid'`, which the `chat` span does not carry — so the group normalises to **0.500**, below the registry's **0.6** threshold, and **both spans are quarantined**. The probe reported **accepted 0, quarantined 2** | The native span path is **inert by construction**, not by accident (D50, A15) |
| **MEASURED — the content switch is what makes text present.** With `OTEL_LOG_MESSAGE_CONTENT=true` the same spans additionally carry `gen_ai.input.messages` (the user's prompt) and `gen_ai.output.messages` (the assistant's reply), and separate `droid.message.*` content spans appear as well | The content switch is a **harness-side** switch that KyberDash does not control, so the consent gate must cover the native route (D52) |
| **MEASURED — metrics go to `{endpoint}/v1/metrics` with DELTA temporality.** The release build installed on this machine returns 404 for that path; the branch's receiver now serves it | The metrics path stays as shipped (D50, D31) and is not affected by the span withdrawal |
| **DOCUMENTED — the export's effective shape is decided by four switches, and a fifth decides whether anything is sent at all.** The format (`legacy` default, or `genai`) is set by the organisation's `telemetry.format` or by `OTEL_TELEMETRY_FORMAT`, and the organisation's value wins; the granularity (`user` default, or `aggregate`) resolves **most-restrictive-wins**, and **a granularity value that cannot be read resolves to `aggregate`** — Factory's own words, on the privacy page; `aggregate` + `legacy` produces **no spans at all** while `aggregate` + `genai` keeps the full trace with the four content attributes stripped; message-content logging is **off by default** and is forced on by the organisation's `telemetry.logMessageContent: true`, forced off by `false`, and overridden by `aggregate`; the two formats **replace each other and do not stack**; and the organisation's `telemetry.enabled` is a **master switch** — "`true` keeps it on even where `OTEL_CUSTOMER_ENABLED=false`; `false` keeps it off even where `OTEL_CUSTOMER_ENABLED=true`" | The status surface must report the effective shape, not only the endpoint (D51, **amended by D58**: the `enabled` switch joins the shape, and the granularity's unreadable value resolves to `aggregate`, not to `user` — the shipped reader does the latter, which is the fail-open direction on a privacy control) |
| **DOCUMENTED — the plugin's `hooks/hooks.json` is keyed directly by event name**, and the top-level `hooks` wrapper is the shape documented for the `hooks` key **inside `settings.json`**. The shipped plugin file uses the wrapper; it was observed working, so it relies on undocumented tolerance | The plugin's hooks file is rewritten in the documented shape (D53) |
| **DOCUMENTED — the documented hook payload.** Every hook receives `session_id`, `transcript_path`, `cwd`, `permission_mode`, `hook_event_name` and an optional `message_id`; tool hooks add `tool_name`, `tool_input` and `tool_response`; `UserPromptSubmit` adds `prompt` and `has_images`; `Stop` adds `stop_hook_active`, `tool_execution_count` and `elapsed_time`; `SubagentStop` adds `task_name`, `task_result`, `task_error` and `stop_hook_active`; `SessionEnd` adds `reason`, `session_duration_ms` and `message_count`. **No model field is documented**, and **`SubagentStop` carries no child session id** | The model chain (D39) is confirmed; the plugin's child-session span is withdrawn (D54); the tool-call-id question is recorded (A3) |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D0 | Development mode | `test-first`. Every unit below has a RED task and a GREEN task, separated adversarially. | Supplied in the packet; repository default. | ANSWERED — packet |
| D1 | Autonomy | `autonomous`. Open items are decided and recorded here; only a hard block would escalate, and none did. | Supplied in the packet; matches Config Reg `<autonomy>` in the root `AGENTS.md`. | ANSWERED — packet |
| D2 | Which collection path is primary? | **The hooks-based Factory plugin.** Not a status-only row, not native Factory OpenTelemetry, not file-only ingest, and not a merge into `~/.factory/hooks.json`. | It is the only path that yields turn, tool and session spans with per-event content gating and no dependency on org telemetry settings. The OpenCode plugin is the established precedent for a hook-based harness (`dash/src/capture/harnesses/opencode-plugin/`). The file source stays as the token source (D9). The external evidence strengthens this: the native `genai` format carries **no subagent lineage** — Factory's data reference states that subagent turns are separate trace roots and that the `parent_session.id` linkage "has no equivalent in the GenAI conventions", so a subagent's trace cannot be joined to its parent's (§2.5) — whereas the transcript's own `parentId` does carry that linkage, and the plugin's `SubagentStop` event carries the child session id. Native export is also **tokenless by documentation** (D12), so it can never be the primary path. **AMENDED (D50): the native path is now MEASURED to be inert as shipped** — the default `legacy` format emits content-shaped spans no adapter may claim, and the `genai` format's trace is claimed only in part, so every native span is quarantined (accepted 0, quarantined 2; §2.6). The primary-path choice is unchanged and is now stronger: the plugin path is the only path that works at all. **AMENDED (D54): the plugin's `SubagentStop` event does NOT carry a child session id** — the documented payload names `task_name`, `task_result`, `task_error` and `stop_hook_active`, and the child's identity comes from the child's own transcript — so the plugin's lineage clause rests on the file source's transcript (`parentId`, `callingSessionId`), not on the hook payload; a child session's own turns still arrive as the child's own plugin spans, so the child pairing (D29) is unaffected. | ANSWERED — autonomous: decided from the issue's rejected alternatives, the repository precedent, and Factory's own data reference; **amended** by D50 and D54 |
| D3 | What exactly is the owned unit? | **AMENDED (D43) — the owned unit is a Kyber-owned MARKETPLACE TREE, and it no longer lives under the Factory root.** The unit is `~/.kyberdash/marketplaces/kyberdash/`, holding `.factory-plugin/marketplace.json` (the marketplace manifest) and the plugin payload under `kyberdash-factory/` (`.factory-plugin/plugin.json`, `hooks/hooks.json`, `hooks/kyberdash-factory.mjs`), installed and removed as one unit with one receipt. The plugin is installed into the harness by the harness's own `droid plugin install`, which copies it into `~/.factory/plugins/cache/…` — the harness's cache is the harness's file and is never written by KyberDash. | A directory is required because Factory's plugin shape is a manifest plus a hooks directory (issue body, confirmed by Factory's own documentation). **The original clause — "the unit is `~/.factory/plugins/kyberdash-factory/`" — is disproven by measurement:** that directory is not a discovery path (`droid plugin list` reported "No plugins installed" and a real turn produced no plugin spans; A1, §2.6). A marketplace needs a directory too, so the shape survives; the location changes, because a marketplace must be a **directory the harness can be pointed at**, and the harness's own copy of it is the cache. Project scope (`.factory/plugins/`) is not used: the capture is per-user and the owner may have many projects. The unit is Kyber-owned, so it is reversible; the owner's settings and hooks files are never read or written by KyberDash (D48). | ANSWERED — autonomous: decided from Factory's documented plugin layout and the observed local plugin root; **amended** by D43 — the residual live confirmation is no longer A1's, because A1 is now disproven |
| D4 | Does anything write the owner's Factory files? | **AMENDED (D48) — the LETTER stands, the EFFECT clause is withdrawn.** `~/.factory/settings.json`, `~/.factory/hooks.json`, `.factory/settings.json` and `.factory/hooks.json` are still **never created, read, merged, rewritten or restored by KyberDash** — not by `enable`, not by `disable`, not by `squad`, not by `update` — because the install route is the harness's own commands (D43) and the harness's state is read through the harness's own commands. **What changes is the file's fate, not KyberDash's hand:** the harness's own `droid plugin marketplace add` and `droid plugin install` write `extraKnownMarketplaces` and `enabledPlugins` into the owner's settings file, so the file's bytes are **not** byte-identical across an install. The byte-identity criterion is therefore withdrawn and replaced by D45's semantic criterion ("no entry KyberDash added remains; no entry the owner had is lost or altered; every other key unchanged in content"). **AMENDED (D39, still standing): there is no read-only exception to state.** This row once excepted the companion per-session file `<session-id>.settings.json`, on the ground that the plugin reads it for one key — the session's `model` (D24). The shipped plugin reads **no** file at all: the committed bundle takes the hook payload from stdin and nothing else, so the exception described a reader that does not exist. The companion file is read by the **file source** (`dash/src/providers/droid.ts:122-134`), which is a different reader of the same file and never writes it. | ADR 0026 (never own harness settings) and ADR 0031's file-plugin amendments (own a whole file, never a shared settings file) — D49 records why the marketplace route needs **no exception**: KyberDash does not touch the file at all, so the file's changing is the harness's act, not KyberDash's. `resolvePath` no longer points at `~/.factory/settings.json` at all: the file is neither a write target, nor a read target, nor install evidence (D16, D44); the install's evidence is the harness's own registry. `~/.factory/hooks.json` and the project equivalents remain untouched in every sense — the plugin route does not use them. | ANSWERED — autonomous: decided from ADR 0026 / ADR 0031; **amended** by D39 and **by D48** |
| D5 | Wire contract: reuse the OpenCode contract-3 keys? | **No. A new Factory contract, version `1`, with `factory.*` attribute keys and its own span names**, in its own leaf module `dash/src/capture/harnesses/factory-plugin/contract.ts`. | The OpenCode adapter claims any span carrying the contract-3 markers (`dash/src/canon/adapters/opencode-envelope.ts:47-79`), so reusing `opencode.*` keys would attribute Factory rows to OpenCode — a cross-harness mis-attribution, which the fingerprint rule (R6.2) exists to prevent. A separate contract also lets the two plugins evolve independently. | ANSWERED — autonomous: decided from the adapter fingerprint rule |
| D6 | Which canonical harness id do the plugin's rows carry? | **`droid`** — the id the file source already stores (`dash/src/providers/droid.ts:384`, `dash/src/refresh/registry.ts:406`). The plugin adapter is therefore named `droid`. | One harness bucket for one harness. The dashboard shows Factory once, with the plugin's turns and the file source's tokens in the same bucket. Display name: `Factory (Droid)`. | ANSWERED — autonomous: decided from the existing file-source id |
| D7 | One adapter or two? | **One adapter, named `droid`.** **AMENDED (D50): it claims (a) the Factory plugin envelope (`factory.contract_version = 1`) and NOTHING ELSE.** The earlier text of this row also gave it (b) native `gen_ai.*` spans carrying `gen_ai.agent.name=droid` — that half is **withdrawn**: the shipped adapter carries no native claim, and its own header says so ("ONE claim path reaches this adapter: the Kyber-owned Factory plugin envelope", `dash/src/canon/adapters/droid.ts:6-21`). The plugin envelope is checked; a span is claimed by one rule only. | `AdapterRegistry.register` throws on a duplicate name (`dash/src/canon/adapters/registry.ts:73-79`), so two adapters named `droid` are impossible. Precedence is explicit so the plugin path's rows never depend on a native claim, so a native span and a plugin row for one session are never summed (D23). | ANSWERED — autonomous: decided from the registry's uniqueness rule; **amended by D50** |
| D8 | How do plugin turns and file tokens reach one session? | **By exact session-id equality only.** The plugin stamps the hook's `session_id`; the file source stores the `session_start` entry's id (`dash/src/providers/droid.ts:150-153`). When they are equal the shared projection groups them into one session and the file row carries the tokens; when they differ, the plugin records carry `not_measurable` with a reason naming the file-source pairing. **No fuzzy join, no time-window join, no token fabrication.** | **Assumption A2** (below): the hook's `session_id` equals the jsonl `session_start` id. The rule is exact-match, so a wrong assumption degrades to `not_measurable`, never to a wrong number. | ANSWERED — autonomous: low-confidence, no authoritative source (cost of wrong: sessions split in two; mitigated by the exact-match rule and the live check) |
| D9 | Where do tokens come from? | **Only from the file source**, through D8's exact-id pairing. The plugin **does not** read `transcript_path` and does not read `~/.factory/settings.json`; it never writes a zero. A plugin record with no paired file row declares `token_usage` (and the counter classes) `not_measurable` with a reason that names the file-source pairing, the way the OpenCode adapter declares an absent counter. | The hook payload carries no counters (issue body, and Factory's own data reference: token and cost data "are not exported as customer OTEL metrics"). The local evidence confirms where the counters actually live: the **companion per-session file** `<session-id>.settings.json` beside the transcript, whose `tokenUsage` carries `inputTokens`, `outputTokens`, `cacheCreationTokens`, `cacheReadTokens`, `thinkingTokens` and `factoryCredits` — which is exactly what `dash/src/providers/droid.ts:129-135` already reads, and **not** `~/.factory/settings.json`. The plugin never reads the transcript, which would be a second, ungated content reader. Honest unobservability is the rule (`docs/rules/honest-unobservability.md`). | ANSWERED — autonomous: decided from the issue's token rule, the local environment evidence, and the honest-unobservability rule |
| D10 | Content consent | **Off by default and never turned on by enabling the plugin.** The consent file is `~/.kyberdash/factory-plugin.json` (`{ "version": 1, "content": <boolean> }`). The install creates it with `content: false` only when it is absent. `capture enable` (the Settings switch) installs the plugin and **does not** change consent. The owner turns content on explicitly, through a new verb `kyberdash kyber capture plugin consent --harness factory --on|--off`, or by editing the file. `KYBERDASH_FACTORY_CONTEXT=1|0` overrides it, mirroring the OpenCode override. | The issue's acceptance criterion: "enabling the plugin never turns content on". This **differs** from OpenCode, whose Settings switch also sets consent (`dash/src/capture/harnesses/opencode.ts:63-66`), and the difference is deliberate: for Factory the switch is "install the capture", not "start recording my prompts". The verb is additive and cheap to drop if the owner prefers editing the file. | ANSWERED — autonomous: decided from the issue's acceptance criterion |
| D11 | Install modes and refusal | **Strict** for `capture enable` (a foreign or owner-edited plugin directory is refused with exit 1 and nothing is written) and **preserve** for `squad` / `kyber-weave update` (kept with a warning, exit 0). The receipt records every installed file's path and SHA-256; removal requires every digest to match, so an edited file is left in place. An absent plugin with `--if-installed` writes nothing. **AMENDED (D43, D46, D47):** the modes now cover **two** things — the Kyber-owned marketplace tree and the **harness-side state**. Strict refuses a foreign or edited tree, a marketplace name or a plugin identity the harness already holds that KyberDash does not own (D46), and a harness registry that cannot be read; preserve keeps each of those with a warning and exits 0. `--if-installed` now means **"the harness's own registry reports the plugin installed"** — not "the tree exists on disk" — and it writes nothing when it does not. | The OpenCode installer's exact semantics (`dash/src/capture/harnesses/opencode-plugin/install.ts:239-334`), extended from one file to a tree. The receipt shape grows from `{version,path,sha256}` to `{version, root, files:[{path,sha256}]}` — a superset, not a replacement. **AMENDED (D44):** the receipt grows once more, to `{version, marketplace, plugin, root, files[]}` — the marketplace name and the plugin identity are load-bearing (the marketplace's registered name is the tree's directory basename, and the identity is `pluginName@marketplaceName`), so a receipt that did not name them could not say what it had installed. | ANSWERED — autonomous: decided from the OpenCode installer; **amended** by D43, D44, D46 and D47 |
| D12 | Is Factory's native OpenTelemetry a second path? | **ANSWERED — branch A: the endpoint IS user-configurable to an arbitrary local address, so the native group (T15/T16) is AUTHORIZED.** The endpoint is set by **machine environment variables** or by an **org-managed `telemetry` block**; there is **no personal `settings.json` telemetry key** (a `telemetry` block in a user or project settings file is ignored with a warning, "read from the organization level only"). The documented machine variables are `OTEL_TELEMETRY_ENDPOINT` (base URL; metrics → `{endpoint}/v1/metrics`, spans → `{endpoint}/v1/traces`; falls back to `OTEL_EXPORTER_OTLP_ENDPOINT`), `OTEL_TELEMETRY_HEADERS` (falls back to `OTEL_EXPORTER_OTLP_HEADERS`), `OTEL_LOG_MESSAGE_CONTENT`, `OTEL_METRIC_EXPORT_INTERVAL` (default 60000), `OTEL_CUSTOMER_ENABLED`, `OTEL_TELEMETRY_FORMAT` (`legacy` \| `genai`), `OTEL_TELEMETRY_GRANULARITY` (`user` \| `aggregate`), `OTEL_RESOURCE_ATTRIBUTES` and `DROID_PARENT_SESSION_ID`. **Factory's own documented example is `export OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"`** — the KyberDash receiver address. The endpoint must be `http`/`https` with no query string or fragment. **No collector is bundled or required**: Droid pushes OTLP/HTTP directly to the endpoint, so KyberDash is simply the customer's own collector. Only OTLP/HTTP is documented — port 4318, `/v1/metrics` and `/v1/traces`; no gRPC path, no `OTEL_EXPORTER_OTLP_PROTOCOL`, no port 4317. **Signals:** default is metrics only, delta temporality, 60-second flush. Under `legacy`, spans exist only as the content spans `droid.message.user`, `droid.message.assistant`, `droid.tool.call`, `droid.tool.result`, and only when `OTEL_LOG_MESSAGE_CONTENT=true`; `aggregate` + `legacy` is metrics only, zero spans. Under `genai`, the **trace structure is emitted whenever a customer endpoint is configured** and the content flag gates only the four content attributes; `aggregate` + `genai` keeps the full trace with the content attributes stripped. **Logs are never exported to a customer endpoint**, so KyberDash's `POST /v1/logs` receives nothing from Droid. **Tokens and cost are never exported** — the documentation states affirmatively that they "are not exported as customer OTEL metrics" and directs users to Factory's hosted org-level Analytics API; there is no `gen_ai.usage.*` metric and no token attribute. **AMENDED (D50, D51): the org block decides MORE than the endpoint.** The organisation's `telemetry` block carries `enabled`, `endpoint`, `headers`, `metricExportIntervalMs`, `logMessageContent`, `granularity` and `format`, and where it pins `format` the machine's `OTEL_TELEMETRY_FORMAT` is **not read at all** (an unrecognised value falls back to `legacy`); `granularity` resolves **most-restrictive-wins**, not by precedence, and `aggregate` overrides every content opt-in; and `logMessageContent` in the organisation's block is authoritative (`true` forces content on for every member, `false` forces it off). **The two formats replace each other and do not stack** — under `genai` the collector receives no `droid.*` metric and no `droid.message.*` / `droid.tool.*` span at all. **AMENDED (D50): the native SPAN path is withdrawn from this branch** — the authorization above stands for the METRICS path, which is what remains. | Consequences of branch A, all now binding: the receiver grows a third signal path (`POST /v1/metrics`, T15/T16); the `droid` adapter gains a native claim with the exact vocabulary in §3.4 — **AMENDED (D50): that claim is withdrawn**, because the group vote cannot clear the threshold for a real trace (A15); native spans are **tokenless**, so they can never replace the plugin or the file pairing (D9); the `genai` format carries **no subagent lineage**, which the plugin path does carry (D2); and the native path is **advisory** on the developer machine but **silently unavailable** where an organisation pins an endpoint (D26). The plugin path is unaffected by every one of these facts. | ANSWERED — autonomous: accepted from the research digest (Factory's own documentation, verified twice); the digest's branch A is confirmed by Factory's documented `OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"` example; **amended** by D50 and D51 |
| D13 | How is the plugin script delivered? | **One self-contained ESM file**, `hooks/kyberdash-factory.mjs`, bundled by esbuild from `dash/src/capture/harnesses/factory-plugin/entry.ts` and committed as generated text (`factory-plugin/generated/plugin-source.ts`), exactly like the OpenCode bundle. Its first line is the ownership marker `// kyberdash-factory-plugin v1`. | A plugin tree cannot resolve `node_modules`, so the script must be self-contained. The committed text keeps npm and SEA builds on the same bytes, and the existing drift-test pattern (`bundle.test.ts`) proves it. | ANSWERED — autonomous: decided from the OpenCode bundle contract |
| D14 | Shared code: transport and consent writes | **Extract the generic parts into shared leaves and keep the old paths as re-export shims.** `dash/src/capture/harnesses/transport/{outbox.ts,otlp-sender.ts}` and `dash/src/capture/consent-file.ts` **became** the shared modules; `opencode-plugin/transport/*.ts` and `opencode-plugin/consent-write.ts` **became** thin re-exports, so the OpenCode entry's imports and its tests are unchanged. **AMENDED: the shims did NOT keep the committed bundle byte-stable** — the shared modules' module paths changed inside the bundle, and the shared OTLP body builder gained an **identity argument** (`OtlpTracesIdentity`: `serviceName`, `scopeName`, `scopeVersion`) whose defaults are the OpenCode ones. The committed artefact was regenerated; the **OpenCode wire is unchanged** (the emitted body still names `kyberdash-opencode-plugin` and the OpenCode scope), the OpenCode suite is green, and the drift guard still holds **in its real sense**: it asserts the committed source EQUALS a fresh bundle of the entry, so it proves the committed bytes are CURRENT, not that they never changed. | Copying the outbox would fork a bounded, security-relevant component; the repository's review lenses flag duplication. **AMENDED:** the original claim — "the shim pattern keeps the OpenCode bundle byte-stable (its drift test proves it)" — was **wrong**: the drift test proves the committed artefact is current, not unchanged, and the extraction regenerated it. What the shim pattern did deliver is the intent: no duplicated transport or consent code, the OpenCode entry's imports unchanged, the wire unchanged, and the suite green. The cost was a regenerated committed artefact — caught and re-committed by the drift guard rather than shipped silently, which is what the guard exists for. The Factory plugin adds a **file-backed** spool (D15) rather than reusing the in-memory outbox, so the shared module contributes the OTLP body builder, the delivery error type and the retry/429 policy, not the queue. | ANSWERED — autonomous: decided from the duplication rule; **amended** — the byte-stability claim was wrong and is corrected here; the cost of wrong was a regenerated committed artefact, caught by the drift guard |
| D15 | How does a one-shot hook process deliver a span? | **A bounded synchronous POST plus a bounded file-backed spool.** The hook script reads stdin, maps the event to at most one span, POSTs it to `http://127.0.0.1:4318/v1/traces` with a ≤ 2 s abort deadline, and on failure appends it to `~/.kyberdash/capture/factory/spool.jsonl` (bounded: 500 events / 4 MB, drop-oldest). Each run drains the spool first. Every error is swallowed, stdout carries only the hook protocol, diagnostics go to a bounded 64 KB tail file, and the script always exits 0. **AMENDED (D61): the file's name is corrected from `outbox.jsonl` to `spool.jsonl`** — that is the name the shipped spool writes (`dash/src/capture/harnesses/factory-plugin/spool.ts:24`, `SPOOL_FILE_NAME`), and the earlier name was this plan's own, never a shipped one. | The OpenCode plugin is a long-lived process with an in-memory outbox (`transport/outbox.ts:1-20`); a Factory hook script is one process per event, so an in-memory queue would lose the span when the process exits. The file spool is the file-backed analogue of the same policy (bounded, drop-oldest, retry with backoff, 429 halves the batch). Never fail the host is the issue's hard requirement. | ANSWERED — autonomous: decided (cost of wrong: a lost span on a transient receiver outage; mitigated by the spool) |
| D16 | The registry row's shape | **`kind: 'managed'`, `id: 'factory'`, `displayName: 'Factory (Droid)'`, `desiredKeys: () => ({})`, a `filePlugin`.** Because it declares no config key and no `configFiles`, `isPluginOnly` is true, so `enable` = install, `disable` = uninstall, and no key is read, written or receipted. **AMENDED (D43, D48, D55):** `resolvePath` no longer points at `~/.factory/settings.json` — that file is neither a write target, nor a read target, nor install evidence. It points at the **Kyber-owned marketplace tree** (`~/.kyberdash/marketplaces/kyberdash/`), which is the unit the receipt digests; the install's evidence is the **harness's own registry**, read through `droid plugin list --scope user` and `droid plugin marketplace list`. `FACTORY_DIR` is no longer honoured by the installer, because the installer writes nothing under the Factory root any more. | The OpenCode shape (`dash/src/capture/harnesses/opencode.ts:239-246`). **AMENDED (D55):** `FACTORY_DIR` is honoured only by the **file source**, for the **sessions** tree (`dash/src/providers/droid.ts:68-69`); it was measured NOT to redirect `~/.factory/settings.json` (§2.6), so the install's root independence is what keeps the plugin and the file source from drifting apart — D35's rule ("the plugin and the file source always read the same root") is satisfied vacuously now, because they no longer share a root. Install detection keeps its own probe group: the `~/.factory` directory and the harness's own plugin registry, because a `.factory` tree exists for a Droid that never wrote a settings file (`dash/src/capture/detect.ts:80-140`). | ANSWERED — autonomous: decided from the OpenCode row and the detect seam; **amended** by D43, D48 and D55 |
| D17 | Does the Settings drawer need a change? | **No.** The drawer renders the registry's rows generically; a managed row with a plugin state gets Enable and Disable. The plan pins the parity with a test rather than with web code. | `dash/web/src/components/settings/HarnessCollectionList.tsx`, `dash/web/src/lib/kyberApi.ts:1947`, `dash/src/server/capture-routes.ts:81`. ADR 0033 and the display-layer rule forbid moving logic into the surfaces. | ANSWERED — autonomous: decided from the display-layer rule |
| D18 | Does Squad / `kyber-weave update` install the plugin? | **Yes, in preserve mode, exactly as the OpenCode plugin is installed.** `KyberDashCapturePluginStep` runs `kyberdash kyber capture plugin install --harness factory --preserve` after a global Factory deploy — **with no root** (D35): the Factory installer's root is `$FACTORY_DIR` else `~/.factory`, and the step hands it none. The step is **harness-aware**: one plugin call per capture-plugin harness, in a fixed order with the OpenCode call first and the Factory call following it, and the **OpenCode** call is handed its own resolved global root. `SelfUpdater` runs `kyberdash kyber capture plugin install --harness factory --preserve --if-installed` — the same verb in the same preserve mode, with `--if-installed` and **no root**, because the plugin is installed where the harness already has it — and the OpenCode call it already made is unchanged (D33, D34). The Settings row carries a manual note naming both. | The Factory global root is `~/.factory` with no environment override (`src/KyberWeave.Core/Squad/Deployment/SquadGlobalRoots.cs:24,54,124`), and Squad already owns `~/.factory/droids/` and `~/.factory/skills/` — the plugin directory is a third, distinct subtree, so no collision (`docs/kyber-squad/onboarding.md:576`). The step never changes Squad's exit code. **AMENDED (D33, D34):** the step's shape (one call per capture-plugin harness, in a fixed order) and the updater's exact verb are now stated here, because "the same with `--if-installed`" did not say whether the preserve mode travelled with it — and D11 had already decided that mode. **AMENDED (D35):** the Factory call is handed **no root**: `--config-dir` is the OpenCode root and the Factory verb refuses it, so the plugin is always installed under the root Factory itself loads it from. **AMENDED (D43, D55): the call's argument list is unchanged, but what it now does is different** — the verb drives the harness's own marketplace and plugin commands and writes nothing under the Factory root, so the "no root" rule is now a consequence of the route rather than a convention. The step's outcome is still never Squad's exit code, and a missing KyberDash is still a skip. | ANSWERED — autonomous: decided from the OpenCode step (`KyberDashCapturePluginStep.cs:161-169`) and the Factory global-root rule; **amended** by D33, D34, D35, D43 and D55 |
| D19 | Documentation impact | **`extends-component`; `impact-components: [KyberDash, KyberSquad]`.** A new ADR records the Factory capture decision; ADR 0031 is amended in place for the Factory row. | The choice — a hooks plugin and a Kyber-owned plugin directory rather than a settings edit or a native-OTel dependency — constrains future Factory work and had viable rejected alternatives. The KyberSquad component is touched only by the global-install step and its documentation. | ANSWERED — autonomous: decided from the ADR criteria and the repository's scale vocabulary |
| D20 | Live verification | **A live Droid turn is an owner live check, not a CI gate.** The automated proof is the unit suite plus a replay e2e through the production pipeline. The live procedure (install, one scripted turn, read the dashboard) is documented in the runbook and executed by the owner. | The OpenCode live gate is likewise owner-run and never part of the test suite (`docs/dash/runbook.md` §1b). A live check that calls a model cannot be a CI gate. | ANSWERED — autonomous: decided from the OpenCode precedent |
| D21 | Scope boundaries | **Out of scope:** the sibling coverage gap named in the issue (Cursor, Warp, ZCode, Devin, Command Code) — captured as a todo under the todo index, not implemented here; a Factory *file* import rewrite (the even token split in `providers/droid.ts` stays as it is, with a follow-up todo filed — see the supporting column); any change to `dash/web` or `dash/tray`; Factory's own `settings.json`/`hooks.json` content. **AMENDED (D28): any change to the shared projection's session-derivation rules** is no longer out of scope without limit — **one droid-scoped change is now in scope**, the Factory source-precedence rule (D28), and it is inert for every other harness. Every other projection change stays out of scope. | The issue names the sibling gap as a separate concern. On the even split: the local evidence shows the companion `<session-id>.settings.json` carries **`lastCallTokenUsage`** and **`childInclusiveTokenUsageBySessionId`** beside `tokenUsage`, so the even split of the session total across assistant calls (`dash/src/providers/droid.ts:196-232`) is a **known lossy approximation** that better per-call data exists for — not an accident. It stays out of scope for this plan (the plugin work does not depend on it), but T20 must file a todo under the todo index recording the loss and the two fields that would remove it, so the limitation is tracked rather than forgotten. **AMENDED (D29):** of those two fields, `lastCallTokenUsage` is the better per-call data; `childInclusiveTokenUsageBySessionId` is the parent's aggregate of its children (A9), not a per-call breakdown, so the todo's evidence is the `lastCallTokenUsage` field. The projection carve-out is narrow on purpose: the T13 test proved that the plugin's and the file's rows cannot be reconciled by any join, so one harness needs one explicit rule, and the alternative — making every file-sourced record in the corpus declare `token_usage` as measured — would change every harness's published declarations to fix a Factory problem. | ANSWERED — autonomous: decided; the even-split follow-up is recorded as a todo requirement in T20; the projection carve-out is **amended** by D28 |
| D22 | The plan's own approval gate | **APPROVED. The plan moves to Ready and is executable; execution authority is the task list in §5.** Scale `extends-component` with `impact-components: [KyberDash, KyberSquad]`, `development-mode: test-first` and `autonomy: autonomous` are confirmed. Every autonomous row D0–D27 is accepted as recorded, including the five rows added by the reconciliation (D23–D27) and the correction of the model-resolution rule (D24). No task runs until the conductor dispatches it. | The conductor supplied explicit `AUTONOMOUS_APPROVAL: approve-and-execute` under resolved autonomy `autonomous` (repository root `AGENTS.md` Config Reg). `products/kyber-squad/agents/architect/references/plan-authoring.md` ("Ready and finalization"). The two documentation checks had already been run by the conductor against this plan and index (`docs validate .` and `docs drift .`: 0 errors, 0 warnings, 150 documents) and must be re-run after the reconciliation edits, since the architect session cannot run them. | ANSWERED — autonomous: recommendation accepted (`AUTONOMOUS_APPROVAL: approve-and-execute`) |
| D23 | Double counting between native spans and plugin rows in the same `droid` bucket | **Precedence rule, in this order: (1) the plugin envelope is claimed first; (2) a native `genai` span is claimed only when the session has no plugin rows; (3) a native span for a session that already has plugin rows is NOT summed with them — it is attributed to the session as corroborating evidence and excluded from the session's turn, tool and duration totals.** The adapter distinguishes the two sources by their attribute fingerprints, never by name: the plugin envelope carries `factory.contract_version = 1` plus a `factory.` marker; a native span carries `gen_ai.agent.name = 'droid'` and `gen_ai.operation.name` and **no** `factory.` attribute. **AMENDED (D50): the rule is RETAINED but is INERT in this branch** — no native span is claimed, so there is nothing for it to govern; it is kept as the rule that applies to any native claim that is ever restored, and the ingest's native-supersede logic goes with the claim it served. | The native `gen_ai.conversation.id` **is** the Droid session id, and `session.id` is present on every native data point, so a native span and a plugin row are joinable to the same session and the same turn — summing them would double count turns, tools and duration. The native format also has no per-turn identity that matches the plugin's, and no subagent lineage at all (D2). The rule is deterministic, testable at the adapter seam, and stated in the ADR before the branch ships (H11). The consequence for the dashboard: a session with plugin rows shows the plugin's turns and tools; a native span for that session is visible as evidence (the claim is recorded) but adds no turn, no tool and no duration. **AMENDED (D50): note what the rule would have cost even if the native spans were claimable** — a claimed native span for a session that already has plugin rows is excluded from the session's model as well, so the native path's one unique contribution (the per-turn model) would not have reached the dashboard without a further amendment to the model chain. That is part of why the native span path is withdrawn rather than repaired. | ANSWERED — autonomous: decided from the native attribute vocabulary (D12) and the existing no-double-count rule the OpenCode pipeline follows; **amended** by D50 |
| D24 | How is the model resolved? (The issue's premise is wrong about the settings file's shape.) | **AMENDED (D39) — this chain is superseded by the one that shipped; D39 states it in full.** The chain as written gave link (2) to the plugin and link (3) to the transcript, and neither reader does that: the plugin reads the hook payload only, and no shipped reader reads the transcript's per-assistant `message.modelId`. **As shipped:** (1) the PLUGIN names a model only when the hook payload carries one; (2) the FILE SOURCE reads the companion per-session file `<session-id>.settings.json`'s `model` key and publishes it **wrapper-stripped**; (3) the SESSION publishes the model its **turn records** name, else the **counter record's** model, and **none** where no source named one — never guessed, never defaulted, and never taken from a nested block of `~/.factory/settings.json`. | The local evidence shows `~/.factory/settings.json` has **no top-level `model` key** — the model lives in nested blocks (`sessionDefaultSettings.model`, `missionModelSettings.workerModel`, `subagentModelSettings.{light,medium,heavy}Model`, `compactionModel`), so the issue's "otherwise the `model` in `~/.factory/settings.json`" would have read a key that does not exist and, worse, would have had to guess among nested blocks (a subagent's `mediumModel` is not the parent's model). That part of this row stands and is confirmed by the shipped code: no reader opens `~/.factory/settings.json` for a model. **AMENDED (D39):** the companion file's `model` key **is** the session's actual model (`minimax-m3.1-flash` on this machine), and it is the **file source** that reads it (`dash/src/providers/droid.ts:122-134`), not the plugin — the committed bundle contains no settings read at all. The transcript's `message.modelId` is **not** a link in the shipped chain: the file source names the session's model from the companion file and prices against it, and nothing reads `message.modelId`. The token counters stay the file source's job (D9), and the plugin reads no file of the owner's at all (D4). | ANSWERED — autonomous: decided from the local environment evidence (the actual file shapes); **superseded** by D39 |
| D25 | Child (subagent) sessions and their tokens | **AMENDED (D29) — this row's subject changes: a child session's tokens are paired from the child's OWN companion file, not from the parent's child-usage map.** A child session's tokens are paired from **its own** `<child-id>.settings.json`, through D8's exact session-id pairing: a Factory child session is a first-class Factory session with its own transcript and its own companion file, the file source discovers it as its own session, and the plugin's `SubagentStop` span carries the same child session id — so the child's plugin rows and the child's file row pair by exact id, exactly as any other Factory session pairs. A child session whose own companion file is absent (or carries no `tokenUsage`) reads `not_measurable` with a stated reason; it never borrows the parent's counters and never borrows another child's. The parent's `childInclusiveTokenUsageBySessionId` map is **not** a token source, and a child's usage is never folded into the parent's session totals. **AMENDED (D54): the plugin's `SubagentStop` event does NOT supply the child session id** — Factory's documented payload for that event names `task_name`, `task_result`, `task_error` and `stop_hook_active`, and the common `session_id` is the session the hook fires in, which for a subagent's stop is the **parent's** session; the span that claimed the child is withdrawn (D54). **A child session's plugin rows are unaffected:** the child's own lifecycle events fire in the child's session, so the child's turns, tools and session spans carry the **child's** session id and pair with the child's own companion file exactly as this row says. The parent-child linkage stays where the evidence is — the file source's transcript — and it is exact-match only, and a child with no linkable parent stays an unlinked child, never a fabricated one. | The original row's subject was the parent's `childInclusiveTokenUsageBySessionId` map, on the evidence that the companion file carries it. That evidence holds, and it shows something the original row missed: the map is the parent's **aggregate view** of its children, and on this machine each entry is byte-for-byte the child's own companion-file counters, while the parent's `inclusiveTokenUsage` is the parent's own `tokenUsage` plus every child's entry (A9). The child session is therefore already a session the file source discovers, with its own usage already stated by its own file — so the child pairing needs **no file-import change at all**, and D21's boundary holds untouched. Folding a map entry into the parent would double count the child's tokens (they are the child session's own totals); folding it into the child would give the child a second source for a number its own file already states. The native `genai` format carries no subagent lineage at all (D2), which is a further reason the plugin path is primary. `DROID_PARENT_SESSION_ID` is a machine environment variable that adds `parent_session.id` for linking headless/CI sub-sessions **across processes** — it is not a subagent-to-parent link within a session, so it is not used for this. The characterization test that pins the map as ignored (`dash/src/providers/droid.test.ts:274`) stays exactly as it is: under this rule its title and its assertions are both correct. | ANSWERED — autonomous: **amended** by D29; the original answer (the parent's child-usage map as the source) is superseded by the child's own companion file, on the local evidence in A9 |
| D26 | What does KyberDash report when an organisation pins a telemetry endpoint? | **The native path is reported as unavailable, never as working.** The Factory row's status states that native OpenTelemetry export is configured by machine environment variables, that an organisation-managed `telemetry` block **overrides** those variables, and that where it does, Droid sends nothing to the local receiver — so the plugin path is the only capture path in that deployment. The status never claims the native path is available; it reports what is observed (native spans arriving or not) and names the override as the reason a deployment sees none. **AMENDED (D50, D51): the rule is REPLACED, because the shipped row's silence was the wrong silence.** "What is observed (native spans arriving or not)" is no longer the right question: no native span is claimed at all (D50), so the absence of native spans says nothing about the export. The row now reports the export's **effective shape** — the endpoint verdict, the **format** (`legacy` or `genai`) and its source, the **granularity** (`user` or `aggregate`), and the **message-content switch** — and states in fixed words what KyberDash does with it: *Factory's native metrics are persisted; Factory's native spans are not claimed; while KyberDash's consent is off, KyberDash's own Factory capture stores no prompt text.* **AMENDED (D57): the fixed words are corrected** — the earlier *no Factory content is stored while KyberDash's consent is off* was false, because the Factory **file source** stores the first user-message fragment whatever the consent file says (D57). The row never says the native path is "working". | Org-managed values override the machine environment variables, and a missing `${VAR}` reference disables the customer sink for that session — so a developer whose organisation pins an endpoint gets **no local export, silently**. The local evidence shows this user's org cache holds only `modelPolicy` and `routedModelVisibilityPolicy` and **no `telemetry` block**, so the caveat is real but not universal on this machine. The machine variables are documented as advisory ("Environment variables live on the developer's machine, which makes them advisory"), and only the org-managed `telemetry` block is a manager capability (`Settings → Enterprise Controls → Raw Configuration`). KyberDash cannot read an organisation's managed settings, so the honest report is the observed behaviour plus the named cause, not a guess about the organisation's configuration. **AMENDED (D51):** the same reasoning now covers the format, the granularity and the content switch — each is pinned by the organisation where the organisation pins it, and each changes what the export sends, so each is part of the row's report rather than an inference from a missing span. **AMENDED (D58):** the organisation's `telemetry.enabled` master switch joins them, so a row cannot report an export as configured while the organisation has switched the pipeline off. | ANSWERED — autonomous: decided from the research digest's org-override rule and the local org-cache evidence; **amended** by D50, D51, D57 and D58 |
| D27 | How is the Ready lifecycle state represented, given the ontology's closed status vocabulary? | **The frontmatter `status` stays inside the vocabulary — `current` — and the plan's lifecycle state is stated in the body, which is authoritative.** The body status line reads "Ready — approved for execution 2026-11-01 (D22)", and the `## Active Plans` row in `docs/plans/README.md` shows Ready. A `status: ready` frontmatter would be a `docs validate` error, because the ontology's vocabulary is `current`, `draft`, `needs-review`, `superseded` and any other value is `InvalidVocabulary` at Error severity. | `src/KyberWeave.Core/Configuration/OntologyConfig.cs:30-31` (the default status vocabulary) and `src/KyberWeave.Core/Docs/Validation/DocSpecValidator.cs:203-212` (the closed-vocabulary check); `products/kyber-squad/agents/architect/references/plan-authoring.md` ("Reconcile body status, frontmatter lifecycle, and index status in the same save. The body status is authoritative when they disagree."). Archived plans use `archived`/`complete` only because the loader excludes the archive folder from the corpus. The choice keeps `docs validate` at zero findings while the lifecycle state stays unambiguous to a reader of the plan and of the index. | ANSWERED — autonomous: decided from the validator's closed vocabulary and the plan-authoring rule that the body status is authoritative |
| D28 | How a `droid` session that holds **both** plugin rows and file rows is accounted for — the paired session's token totals and its turn count | **The Factory source-precedence rule: each source is used for what only that source can measure, and neither source is counted twice.** In a `droid` session holding at least one plugin row and at least one file row: (1) the **turn records** are the **plugin rows** — the turn count, the timeline's turns, the request roots, the per-turn model, the tool invocations, the duration and the context turns are read from the plugin, because the plugin is the PRIMARY mechanism (D2) and it alone observes the turn boundary, the tools and the timing; (2) the **counter records** are the **file rows** — the session's token totals (input, output, cacheRead, cacheCreation) and the availability that gates them are read from the file rows, because the file source is the only source of counters (D9) and its counters are a **session-level** total split evenly across assistant calls, so they belong to the session and never to a turn; (3) the file row in such a session contributes **nothing else** — it is not a turn, not a timeline entry, not a request root, not a tool invocation, and its duration is not added to the session's duration; it contributes its counters and its cost; (4) the session's **cost** is summed over the turn records and the counter records together (the two sets are disjoint, so no cost is counted twice), and the cost-basis verdict is computed over that same union; (5) the **context analysis** reads the turn records, and where no turn record carries a measured input it reads the counter records, so a Factory session keeps the context figures the file source gave it before the plugin arrived. A `droid` session with plugin rows and **no** file row keeps today's behaviour: the plugin rows are both the turn records and the counter records, so the plugin's `not_measurable` declarations and their reasons gate the totals. A `droid` session with file rows and **no** plugin row keeps today's behaviour: the file rows are both. Every other harness is untouched — the rule is inert outside `droid`. The rule is exact-match only: no fuzzy join, no time window, no counter-equality join. It never fabricates: a paired session's totals are the file row's counters, and a plugin-only session's totals are absent, never zero. | This generalizes D23's precedence principle — the plugin envelope wins and the other source is evidence only — from the native export to the file source, with the one difference the local evidence forces: the file row is **not** quarantined, because it carries the only counters and the only priced cost, so it stays as a counter carrier rather than being dropped. A turn-level **join** was rejected and cannot work: the twin-join rule pairs a file row with a non-file row only over **identical reported counters** (`dash/src/canon/twin-dedupe.ts:293-320`), the plugin row carries zeros, and an all-zero row is excluded from the counter clustering because it identifies nothing (`dash/src/canon/twin-dedupe.ts:191-196`); and because the file row's counters are a session total split evenly across assistant calls, there is **no turn-level correspondence** between a plugin turn and a file row to join over — a join would both mis-attribute the split and drop the counters of the rows it did not join. Before this rule the paired session read its totals as `not_measurable` — the plugin row's declaration won the merge because the file row's declaration map lists only what a file source **cannot** measure (`dash/src/canon/measurability.ts:425-486`, `dash/src/synth/synth.ts:534-537`) and says nothing about `token_usage` — and it counted two turns for one Factory turn. The alternative fix — making every file-sourced record declare `token_usage` as `measured` — was rejected: it would change what every file-sourced session in the corpus declares, for every harness, to fix a Factory problem, and it would have to guess whether a stored zero is a measured zero or an absent counter. The file source's rows carry no content and no tool records of their own (the Factory reader yields no reader turn, so `synthesizeToolCalls` returns none — `dash/src/synth/synth.ts:845-847`), so demoting the file row to a counter carrier loses no text, no tool and no attribution. | ANSWERED — autonomous: recommendation accepted |
| D29 | Where a child (subagent) session's tokens come from | **From the child's own companion file, through D8's exact session-id pairing — never from the parent's `childInclusiveTokenUsageBySessionId` map.** The map is the parent's aggregate view of its children, not the child's own usage, and the child's own companion file already states the child's own usage. A child session with no companion file of its own reads `not_measurable` with a stated reason; it never borrows the parent's counters and never borrows another child's. A child's usage is never folded into the parent's session totals. The parent-child linkage stays exact-match only, and a child with no linkable parent stays an unlinked child, never a fabricated one. | This is the amendment of D25, and the evidence is local and read-only (A9): the parent session's companion file names the child with counters that are exactly the child's own companion-file counters, the child's own transcript's `session_start` names the child's id (and its `callingSessionId` names the parent), and the parent's `inclusiveTokenUsage` is the parent's own `tokenUsage` plus every child's entry. So the child session is already a session the file source discovers, with its own usage already stated by its own file: the pairing is the ordinary D8 exact-id pairing and **no file-import change is needed**, which is what lets D21's boundary stand. The characterization test that pins the map as ignored (`dash/src/providers/droid.test.ts:274`) therefore stays exactly as it is — its title and its assertions are both correct under this rule — and no assertion is weakened anywhere. | ANSWERED — autonomous: recommendation accepted |
| D30 | Must the plugin adapter's `not_measurable` reasons name the file-source pairing? | **Yes, and D30 fixes the wording.** All four of the plugin adapter's counter-class reasons name the file-source pairing that would supply the counters, while still naming the harness and the metric. The reason is static — the adapter cannot know whether a file row will pair — so it names the **rule**, not the outcome: `token_usage` — "Droid telemetry did not report token usage for this span; the Factory session file's companion settings supply it when this session's id matches a file-sourced session."; `output` — the same sentence with "output token usage"; `cache_read` — "Droid did not export a cache read counter; the Factory session file's companion settings supply it when this session's id matches a file-sourced session."; `cache_creation` — the same sentence with "cache creation counter". | D8 and D9 already require the reason to name the file-source pairing ("a reason naming the file-source pairing", "a reason that names the file-source pairing"); the shipped wording — "Droid telemetry did not report token usage for this span." — did not, so the requirement was unmet rather than optional, and the adapter test only asserted the harness and the metric, which is why it passed. Under D28 the reason surfaces exactly where it should: a paired session's availability comes from the file rows, so the plugin's reason is published only when the pairing did **not** resolve — which is why a static rule-naming reason is accurate where it is published, and no projection-side rewrite is needed. The reason is never a claim that the tokens exist. | ANSWERED — autonomous: recommendation accepted |
| D31 | Where Factory's native OTLP metrics GO, and what "accepted" means for them | **AMENDED (D38, D42) — the destination is the canon store's own `metric_point` table, and a point's identity is its content digest.** The table is `metric_point`, created by the store's own schema and written through the production sink's `upsertMetrics` and the `ingestMetricBatch` seam; the `otel_metric` name this row carried was never in any code (D38). The canon projection does NOT consume metrics in this branch (a todo). The writer's "never accepted-and-dropped" rule does NOT change. **AMENDED (D50): the METRICS path is the whole of what remains of the native path** — the native SPAN claim is withdrawn, and nothing else about this row changes. (1) **The destination.** One row per **distinct** decoded metric data point — a point's identity is a **content digest** of everything the row carries, so two byte-identical decoded points collapse into one row and a re-delivered point lands on the row it already occupies instead of beside it (D42) — carrying the metric's name, its kind (`gauge`/`sum`/`histogram`/`exponentialHistogram`/`summary`), its unit, the aggregation temporality, the point's timestamp, the point's value — or, for a histogram or a summary, its count and its sum — the point's attributes, the resource attributes, the scope name, and the exporter's `service.name` as the row's **source**. A point that carries no value keeps it absent, never zero (`docs/rules/honest-unobservability.md`). The canon layer gains `dash/src/canon/metric-ingest.ts` with `ingestMetricBatch(metrics, canon)`, mirroring `ingestLogBatch(logs, canon)`. The table is added to `SCHEMA_SQL` as an `IF NOT EXISTS` statement, so **no schema-version bump and no migration step are needed** — the store applies the full schema on every open, and the `model_context_window_catalog` table landed the same way (`dash/src/canon/store.ts:553-565`, `:2983`). (2) **The production sink.** `dash/src/otel/service.ts`'s `toCanon` gains `upsertMetrics`, which calls `ingestMetricBatch` and then writes the fenced audit row `canon.logIngest('otlp:metrics', <points received>)` — the shape the span sink uses for its per-source rows and the log sink uses for its `otlp:logs` row. A metrics batch changes no projected record, so it requests no projection (the pending-log precedent). The metric rows carry the exporter's `service.name` as their source and **no harness attribution**: the source name is never harness evidence (R6.2), and a metric point has no span for the fingerprint vote to run on. The full corpus wipe clears the table; a scoped per-harness wipe does not, because those rows' harness attribution is unknown — the standing the unclaimed quarantine rows already have (`dash/src/canon/store.ts:145-151`). (3) **The writer's rule is unchanged.** It stays: a sink that cannot persist metrics does not offer the port, and the batch is refused rather than accepted-and-dropped (`dash/src/otel/writer.ts:50-58`, `:463-479`). The production sink now offers the port, so the refusal is unreachable in production and remains the writer's answer for a sink without one; the refusal path is currently untested and is pinned by T27. (4) **"Accepted and counted but not projected" is NOT compatible with that rule, and the rule is not weakened to accommodate it.** The rule's purpose is that the exporter's 200 means the batch was persisted; a count-only receipt keeps the batch's size and drops its values. An OTLP exporter pushes on its own cadence and does not resend a batch KyberDash answered 200 for, so the values would be gone permanently; and the repo has no "received and discarded" state — `ingest_log` records RECEIVED activity, the tables record STORED facts, and every other signal has a table (spans → `records`, logs → `pending_logs`/`quarantined_logs`). If a later build chooses to discard metric values, that is a change to the writer's rule and must be stated as one: the port's contract would become "durably receipted, values not persisted", the telemetry inventory would have to say so, and the loss would be accepted deliberately. | **AMENDED (D50): the "production consequence of the seam as it stands" paragraph below described the PRE-FIX state and is withdrawn as a statement of fact.** The shipped production sink **does** offer `upsertMetrics` (`dash/src/otel/service.ts:145` builds the sink object, and `upsertMetrics` is its third member at `:218`), so a metrics batch no longer finds no port: the writer's refusal is unreachable in production, the receiver answers 200, and the points land in `metric_point`. The live check confirms the shape from the other side: **the release build installed on this machine answers 404 for `POST /v1/metrics`, and the branch's receiver now serves it** — which is exactly the difference between the shipped release and this branch. The rest of this column stands as the history of the defect the tasks repaired: the receiver's metrics cases hand the route a `RecordingStore` that offers the port directly (`dash/src/otel/receiver.test.ts:1103-1143`), so **nothing below the receiver's decode seam is exercised at all** — not the writer's queue, not the writer's refusal, not the production sink, not the store's persistence; the `pending_logs`-style pattern was considered and rejected (a log may arrive before the model call it enriches; a metric point has no such dependency); a service-owned metrics ledger was rejected too (the canon database is the one datastore); and the two further divergences the implementer did not flag — the persisted-fact hook counts spans and logs but not metrics (`writer.ts:666-676`), so a metrics-only commit — which is exactly Factory's default export shape — never moved the receiver's `lastAcceptedAt`, and the writer's metrics refusal had no test — are the ones T27 repaired. **AMENDED (D38, D42):** the table's shipped name is `metric_point` (`dash/src/canon/store.ts:597-614`) and its key is a content digest under a `UNIQUE` constraint (`dash/src/canon/store.ts:593-600`, `:1762-1781`, `:3042-3060`), so "one row per decoded point" is "one row per **distinct** decoded point" — D42 states the consequence. | ANSWERED — autonomous: recommendation accepted; **amended** by D38, D42 and D50 |
| D32 | The rule for a normalized record's status at the receiver-timing seam | **An error status is never downgraded.** `growWithReceiverTiming` keeps the record's own `error` status and takes the exporter's OTLP status code for every other value: `record.status = record.status === 'error' ? 'error' : span.status.code`. The repair lives in `growWithReceiverTiming` (`dash/src/canon/ingest.ts:143-147`), whose doc is amended: it gives the record the timing, and the outcome **unless the adapter already determined one**. A native Factory span carrying `error.type` therefore keeps the status the `droid` adapter set (`dash/src/canon/adapters/droid.ts:307-313`), so the stored record reads `error` and the policy-denied tool call counts as an error in the session's tool error counts (`dash/src/canon/sessions.ts:1534`) and in the run outcome derivation (`dash/src/canon/outcome.ts:296`, `:504`) — which is the whole point of the adapter's rule. The error type itself stays in the record's raw attributes: it is the evidence of **why** the call was denied, and the status only says that it was. | **The rule is inert for every other harness, and the proof is in the code:** `baseRecord` stamps `status: 'unspecified'` (`dash/src/canon/adapters/copilot.ts:666-694`), and the `droid` adapter is the **only** adapter under `dash/src/canon/adapters/` that assigns `record.status` — every other adapter's record still carries `'unspecified'` when `growWithReceiverTiming` runs, so its stored status remains the exporter's status code, byte for byte. The three alternatives were considered and rejected: (a) repairing it at the call site (`dash/src/canon/ingest.ts:641`) would split the function's contract across two places and would still have to ask "did the adapter set a status?", which the call site cannot answer; (b) making the adapter's error a separate field the projection reads would leave `record.status` wrong for every consumer that reads it — the stored column (`dash/src/canon/store.ts:3116`), the session's tool error counts, the run row (`dash/src/canon/runs.ts:219`) and the outcome derivation — and would change the projection for every harness; (c) repairing it in the adapter's record shape is the same as (b). The defect is invisible at the adapter seam because the adapter-level case asserts the record the adapter returned (`dash/src/canon/adapters/droid.test.ts:540`), while the production path calls `growWithReceiverTiming` immediately afterwards (`ingest.ts:641`): the RED test must therefore pin the **stored** record, which is what the production path writes. | ANSWERED — autonomous: recommendation accepted |
| D33 | The capture-plugin step's shape, and what the legacy update fixtures pin | **The step is HARNESS-AWARE: it runs ONE plugin call per capture-plugin harness, in a fixed order — the OpenCode call keeps its place and the Factory call follows it — on both sides (the Squad deploy step and the self-updater). **AMENDED (D35):** the OpenCode call is handed its resolved global root; the **Factory call is handed no root at all** on both sides — the Factory installer resolves `$FACTORY_DIR` else `~/.factory` itself — so the plugin always lands under the root Factory loads it from.** The three legacy update fixtures are updated **in place** to the two-plugin sequences, and their substance is unchanged: the engine runs first, then the tray, then the plugin reconciliation; the "already on" path still reconciles and still does not download; `--no-restart` still skips the engine and the tray and still updates the plugin(s). The modernization **states that substance** rather than a bare count — each plugin entry names the harness it belongs to. | **The escalation is a legacy detail in a coupled fixture, not a contradiction between two invariants**, and the evidence is in the fixture's own code: the legacy helper records EVERY call on the one capture seam and names it "engine" when the first argument is `engine` and "plugin" otherwise (`tests/KyberWeave.Tests/UpdateCommandTests.cs:846-855`), so the OpenCode call and the Factory call are recorded under the SAME step name and the sequence's plugin COUNT grew from one harness to two. The fixtures' doc comments state propositions that are all still true — "the engine runs first, then the tray, then the plugin" (`:902`), "the 'already on' path still runs the engine, tray and plugin reconciliation, and still does not download" (`:923`), "--no-restart skips the engine and the tray, but the plugin still upgrades when it is installed" (`:986`) — and none of them is about how many plugin calls the update makes. The worker's claim is confirmed: under D18 (the updater always runs the Factory verb with `--if-installed`) no implementation can satisfy the legacy counts, because the updater's Factory call is unconditional — `--if-installed` decides what the VERB does, not whether the call happens, and the only way to avoid the call would be for the updater to read the owner's Factory tree itself, which would put KyberDash's plugin paths inside the updater and contradict D18. So the legacy counts are stale under the approved design and the fix is the fixture's, not the design's. The worker's refusal to work around it — by skipping the Factory step on the "already current" path or under `--no-restart`, which would leave the Factory plugin stale in exactly the cases the OpenCode plugin is refreshed — is right, and is now the plan's requirement. The other legacy sequence is unaffected: `--no-kyberdash` returns before any plugin call, so that fixture still asserts an empty list (`UpdateCommandTests.cs:1024`). | ANSWERED — autonomous: recommendation accepted; **amended** by D35 |
| D34 | Must the updater's Factory call carry `--preserve`, and must the RED test pin it? | **Yes to both: the call is `kyberdash kyber capture plugin install --harness factory --preserve --if-installed`, and the RED test pins the flag's PRESENCE.** The mode travels with the verb, the way it already does for the OpenCode call; the updater passes `--if-installed` and **no root**, because the plugin is installed where the harness already has it. | D11 already decided the mode: "**preserve** for `squad` / `kyber-weave update` (kept with a warning, exit 0)". The OpenCode updater call has carried `--preserve` since before this branch (`PluginArgs`, `tests/KyberWeave.Tests/UpdateCommandTests.cs:883-886`), and the Factory call is the same verb for the same optional surface, so it must be the same mode: without `--preserve` the verb's strict mode REFUSES an owner-edited plugin with exit 1, so every update would report a failed plugin step for an owner-edited Factory plugin — while the sibling OpenCode plugin, edited the same way, is kept with a warning. The worker's reasoning is right and its implementation stands. The RED test must pin the **presence** of `--preserve`: the current case deliberately asserts neither, so a later change could drop the flag and turn every edited-plugin update into a warning with no test failing. The pin is the exact argument list — `["kyber","capture","plugin","install","--harness","factory","--preserve","--if-installed"]` — mirroring the `PluginArgs` constant the OpenCode case asserts exactly; it subsumes the case's present partial assertions (the verb's first four arguments, `--if-installed` present, no `--config-dir`) and adds the mode, so nothing is weakened. D18's and T18's wording is amended to be unambiguous, because "the same with `--if-installed`" never said whether the mode travelled with it. | ANSWERED — autonomous: recommendation accepted |
| D35 | What `--config-dir` means on the **Factory** plugin install | **Option B: the flag is the OpenCode config root and the Factory path takes NO root from it. The Squad step does not pass it for Factory, and the Factory plugin-install verb REFUSES it with a usage error (exit 2) instead of ignoring it. No precedence rule is added: the Factory installer's root stays `$FACTORY_DIR` else `~/.factory` — the resolution the Factory file provider already uses.** The OpenCode call is unchanged and keeps its `--config-dir`. | **A is rejected on a functional ground**: honouring the flag for Factory would let a caller select a root Factory never reads, and Squad's Factory root is `~/.factory` with no environment override, so a flag that won over `FACTORY_DIR` would break the D28 pairing. **C is incoherent**: the receipt and consent live under KyberDash's state directory, not the harness root. **B keeps one meaning for the flag**: its help text already says "OpenCode config root" (`dash/src/cli/register.ts:470`) and the command surface already says it "does not move the Factory tree" (`docs/cli-command-surface.md:394`). **Documents that must change:** the CLI command surface's plugin-install row and its exit-code row; the runbook's Factory plugin paragraph; the plan's D18 and D33; the harvest rows H7, H17 and H18. | ANSWERED — autonomous: recommendation accepted |
| D36 | Does the harness-collection rule's Factory row need a wording change once D28 lands? | **Yes.** The row must state the split, not only the join: the plugin rows are the session's turns, the file rows are its counters and its cost, and neither source is counted twice; a plugin-only session's token classes render `not_measurable` with a reason naming the file-source pairing and the session-id equality (D30's wording); a near-miss id stays its own session. The row's existing statements stand. | The harvested row (`docs/rules/kyberdash-harness-collection.md:37`) says the pairing is "joined on exact session-id equality" with the file source as the counter source — true, but incomplete: it does not say the two sources are **split** rather than summed, so a reader could take a paired session's turn count to be the sum of both sources' turns, which is the double count D28 removes. A harness row is ground truth (`:15-18`), so it has to say which source owns which figure. A harvest edit, not a code change: H7 carries the requirement and T20 lands it. | ANSWERED — autonomous: recommendation accepted |
| D37 | Is the plan's §3.3 claim about the status row reporting native-export availability accurate? | **AMENDED (D41) — the reporting SHIPPED, so the "not shipped yet" marker is removed and §3.3 describes the row as it stands.** D26's requirement stands unchanged: the row reports the native path as **unavailable** with the named cause, never as working, and names the organisation's pin as the cause where there is one. **AMENDED (D51): and what shipped is now only PART of what the row must say.** The shipped row reports the **endpoint verdict** — available, unavailable with the named cause, or not configured — and nothing else about the export. Under D50 the native span claim is withdrawn, so the endpoint verdict alone is misleading: it reads as "the native path works" while KyberDash claims no native span at all. The row must therefore also report the **format**, the **granularity** and the **message-content switch**, and state what KyberDash does with the export (D51). | The shipped row reported the plugin, the receipt, the resolved plugin directory and the consent (`dash/src/capture/harnesses/factory.ts:66-77`); it did **not** report the native-export availability or the organisation override, so this row's caution was right when it was written — the closeout must not claim what the row does not say, and nothing about the native path may be inferred from the row's silence. **AMENDED (D41):** the reporting is now part of the row: the Factory row carries the native OpenTelemetry export's availability and its named cause — available, unavailable with the organisation's pin as the cause, or not configured — on every surface the row reaches, the printed status, the structured row and the API route, because they all read the row's own status description (`dash/src/capture/harnesses/factory.ts:71-85`, `:100-113`, `:121`). The requirement that the row **say** the state rather than stay silent (D26) is met and shipped (commit `e140472`), so the marker this row asked for is stale and §3.3 must now describe the shipped row. **AMENDED (D51):** "the endpoint verdict ships" is now "the endpoint verdict ships, and the effective shape does not" — the row's reader reads `dash/src/capture/harnesses/factory-plugin/native-export.ts`, which resolves the endpoint and nothing else. The switches that decide what the export sends — the format, the granularity, the message-content switch and the organisation's `enabled` master switch (D58) — are read from the same managed-settings cache the shipped reader already uses, so no new read of the owner's settings file is introduced. | ANSWERED — autonomous: recommendation accepted; **amended** by D41 and D51 |
| D38 | What is the name of the canon table the native OTLP metric points land in? | **`metric_point`.** The shipped table is `metric_point`, created by the store's own schema and written through the production sink's `upsertMetrics` and the `ingestMetricBatch` seam. This plan's earlier text named `otel_metric`; that name was never carried by any code, and the shipped implementation and the landed harvest documents both say `metric_point` (`docs/dash/architecture.md:689-691`, `docs/dash/telemetry-inventory.md:317-318`). **The plan's text is corrected to the shipped name; no code changes, and the table is not renamed.** | The table is the store's own, applied on every open with `CREATE TABLE IF NOT EXISTS`, so no schema-version bump and no migration step are needed (the `model_context_window_catalog` precedent). Its columns carry the point's name, kind, unit and aggregation temporality, the verbatim nanosecond timestamp and its ISO reading, the value — or the count and sum — the point's attributes, the resource attributes, the scope, and the exporter's `service.name` as the row's source; a point that carried no value keeps it absent, never zero (`dash/src/canon/store.ts:597-614`). The row names no harness: a metric point has no span, so a scoped per-harness wipe cannot claim it and only the full corpus wipe clears it (`dash/src/canon/store.ts:93-96`, `:5015`). | ANSWERED — autonomous: decided from the shipped schema and the landed harvest documents |
| D39 | How is the model resolved, as the shipped code actually resolves it? | **The shipped chain, in three steps.** (1) **The PLUGIN names a model only when the hook payload carries one** — the mapper reads the payload's `model` and emits `factory.model.id` only then (`dash/src/capture/harnesses/factory-plugin/mapper.ts:202-203`). The shipped bundle reads the hook payload and nothing else, and Factory's own hook payload documents no `model` field, so a plugin-sourced turn **generally names none** — and an absent model stays absent all the way down: the adapter never defaults one (`dash/src/canon/adapters/droid.ts:163-168`, `:327-340`). (2) **The FILE SOURCE reads the session's `model`** from the per-session companion file beside the transcript, `<session-id>.settings.json`, and publishes it **wrapper-stripped** (`dash/src/providers/droid.ts:122-134`, `:270-274`). (3) **The SESSION publishes one model**: the model its **turn records** name, and where no turn record names one, the **counter record's** model; where no source named one, it publishes **none** — never a default, never a guess (`dash/src/canon/sessions.ts:1758-1766`). **`~/.factory/settings.json` is never a model source.** | All three steps are pinned by the shipped pairing test: the file source's model stands in when the plugin turn names none (`dash/src/canon/sessions.droid-pairing.test.ts:813-830`); the turn's own model wins and the file source's model is not published beside it (`:832-853`); and a plugin-only session whose turn names none publishes none (`:855-873`). The plugin never opens the companion file (D4), so the plan's old link (2) — the plugin reading the companion file for the model — described a reader that does not exist, and the transcript's `message.modelId` is not a link at all: no shipped reader reads it. `~/.factory/settings.json` has no top-level `model` key — its model lives in nested per-role blocks — and reading one would attribute a subagent's model to its parent, so no reader reads it for a model. | ANSWERED — autonomous: decided from the shipped mapper, the shipped file source and the shipped session projection |
| D40 | Is the `unknown` a Factory session can publish a measured model? | **No: the literal `unknown` is the FILE SOURCE's own placeholder, not a measured model, and this plan does not change it.** The file source's parser substitutes the literal string `unknown` for a companion file that names no model (`dash/src/providers/droid.ts:134`), and the shipped characterization test pins exactly that (`dash/src/providers/droid.test.ts:271`). This is pre-existing file-source behaviour, outside this plan's scope (D21): the plugin path never produces it, and an absent companion model is not a measurement. | Where it can surface: the file source's parsed call carries `model: 'unknown'`, the synthesized record carries it in its raw attributes (`dash/src/synth/synth.ts:513-514`), and the session's model reads those attributes (`dash/src/canon/sessions.ts:276-300`) — so a session whose plugin turn names none and whose companion file names none publishes that literal as its model. That is the file source's long-standing standing, unchanged here, and it is why the harvest's model chain names the placeholder explicitly rather than leaving a reader to take it for a measurement. Changing it would be a file-source change, which D21's boundary excludes; the follow-up is recorded, not done. | ANSWERED — autonomous: decided from the shipped parser and its characterization test |
| D41 | Is §3.3's "not shipped yet" marker still true? | **No: the native-export availability reporting SHIPPED, and the marker is removed.** The Factory status row reports the native OpenTelemetry export's availability and its named cause — available, unavailable with the organisation's pin as the cause, or not configured — on every surface the row reaches: the printed status, the structured row and the API route. D26's requirement — the native path is reported as unavailable with its named cause, never as working — is met and shipped (commit `e140472`). | D37 answered this question correctly for its moment: the row then reported the plugin, the receipt, the resolved plugin directory and the consent, and not the native path, so the closeout had to refrain from claiming it. The reporting is now part of the row's own status description (`dash/src/capture/harnesses/factory.ts:71-85`, `:100-113`, `:121`), so the marker is stale. Nothing else in D37 changes: D26 stands, and the requirement that the row **name** the cause rather than stay silent stands. | ANSWERED — autonomous: decided from the shipped status row |
| D42 | Is a metric point's identity per decoded point, or per content? | **Option A: the content-digest identity is correct and intended, and this plan's "one row per decoded point" is corrected.** A metric point's identity is a **content digest** of everything the row carries — its name, kind, unit, aggregation temporality, nanosecond timestamp, value (or count and sum), point attributes, resource attributes, scope and source (`dash/src/canon/store.ts:1762-1781`). That digest is the row's key under a `UNIQUE` constraint, and the write is an upsert that restates the row it already occupies rather than replacing it (`dash/src/canon/store.ts:593-600`, `:3042-3060`). So the table holds **one row per distinct decoded point**: two byte-identical decoded points collapse into one row, and a re-delivered point lands on the row it already occupies instead of beside it. **No implementation change is required, and none is made here.** | **The duplicate case is reachable, and every reachable duplicate is the same point delivered twice.** The receiver hands the decoded metrics over as they arrived, with no point filtered or aggregated away (`dash/src/otel/receiver.ts:687-700`), and the receiver persists a batch before it answers it — so a batch KyberDash stored and then failed to answer for is delivered again by the exporter's own retry, and the digest is what makes that re-delivery idempotent instead of doubling the rows. Two genuinely distinct measurements cannot collide: the nanosecond timestamp and the value are both part of the digest, and Factory's native export stamps a point at its collection instant (a 60-second flush, A6). **Consequence for a reader:** the table's row count is the count of *distinct* points, while the receiver-activity row under `otlp:metrics` records the points the batch *carried* (`dash/src/canon/metric-ingest.ts:47-54`, `dash/src/otel/service.ts:218-238`) — the two numbers differ when a batch repeats a point, and that difference is the intended one between what arrived and what is stored. **What it costs:** the multiplicity of byte-identical points is not preserved, and no shipped test pins the collision (`dash/src/canon/metric-ingest.test.ts:214` writes three distinct points), so a later change to the identity would be unobserved. That gap is recorded as a follow-up for T20 — a todo, and the same correction to the two harvest documents — not as a change to this plan or to the code. | ANSWERED — autonomous: decided from the shipped identity, the receiver's hand-over and the OTLP retry rule |
| D43 | **How is the capture plugin installed at all?** | **Through Factory's own marketplace and plugin commands, driven by KyberDash — and through nothing else.** KyberDash writes a **Kyber-owned marketplace tree** and then runs the harness's CLI: `droid plugin marketplace add <marketplace root>`, then `droid plugin install kyberdash-factory@kyberdash --scope user`. The uninstall runs `droid plugin uninstall kyberdash-factory@kyberdash --scope user`, then `droid plugin marketplace remove kyberdash`. **KyberDash never reads and never writes the owner's `~/.factory/settings.json`** — the harness's own commands maintain that file, and KyberDash learns the harness's state only through the harness's own CLI (`droid plugin marketplace list`, `droid plugin list --scope user`). | **The measurement is decisive (A1, §2.6):** the shipped installer wrote `~/.factory/plugins/kyberdash-factory/` and Factory did not load it — `droid plugin list` reported "No plugins installed" and a real turn produced no plugin spans. Factory's own documentation states that plugins are installed from a **marketplace**, that Droid copies them into its cache unchanged, that the identity is `pluginName@marketplaceName`, and that "There is no `droid plugin enable` or `disable` command. Enablement is stored in the `enabledPlugins` setting." **The alternatives were weighed.** (a) **The user-scope hooks file `~/.factory/hooks.json`** is documented and "applies across your projects", so it would work — but it is the owner's own hand-edited file, so owning it means owning a file the owner edits (and merging into it is exactly what D2 and §1.3 rejected); it forfeits the plugin's versioned identity and its receipt; and its hook command must be an absolute path, because `${DROID_PLUGIN_ROOT}` exists only inside a plugin. (b) **Writing `~/.factory/settings.json` ourselves** is rejected outright: it is the shared-file ownership ADR 0026 and ADR 0031 forbid, and it would put KyberDash's merge semantics inside a file whose schema drifts between Factory versions — the failure ADR 0026 names. (c) **A plugin directory under the Factory root** is disproven by measurement. (d) **The user-scope `hooks` key inside `settings.json`** is the same shared-file problem as (b), with less to show for it. The marketplace route is the only one Factory actually loads, and it is the harness's own supported lifecycle — so it is also the route with the best odds of surviving a Factory upgrade. | ANSWERED — autonomous: decided in the D43–D56 amendment session, from the measurement and Factory's own plugin documentation |
| D44 | What does the installer write, what must the uninstaller remove, and where does the owned unit live? | **KyberDash owns exactly three things: the marketplace tree, the receipt, and the consent file.** The marketplace tree is `~/.kyberdash/marketplaces/kyberdash/` — `.factory-plugin/marketplace.json` (the marketplace manifest: `name`, `description`, and `plugins[]` with `name: 'kyberdash-factory'` and the **relative** `source: './kyberdash-factory'`) plus the plugin payload `kyberdash-factory/` holding `.factory-plugin/plugin.json`, `hooks/hooks.json` and `hooks/kyberdash-factory.mjs`. The receipt is `~/.kyberdash/factory-plugin-receipt.json` and now records the **marketplace name**, the **plugin identity**, the tree root and a SHA-256 per owned file (the marketplace manifest included). The consent file is unchanged (`~/.kyberdash/factory-plugin.json`). **The uninstaller removes the harness-side state first** (the harness's own uninstall, then the harness's own marketplace removal), **then the Kyber-owned tree and the receipt**; the consent file follows the consent rule, not the uninstall's. The harness's cache copy and its `installed_plugins` record are **the harness's files**: KyberDash never writes them and never deletes them by hand — it asks the harness to. | The marketplace's registered name is the **directory basename of the path that was added**, not the `name` field inside `marketplace.json` (measured, A12), so the tree's directory name is load-bearing: `kyberdash` yields the identity `kyberdash-factory@kyberdash`. Factory **rejects** an absolute `source` and a source that escapes the marketplace directory, so the plugin payload must live inside the marketplace tree. The marketplace manifest's `name` is metadata — "The registered name is derived from the source or set by the `extraKnownMarketplaces` key" — so the directory name, not the manifest, is the identity. The receipt's shape grows from the plugin tree's files to the marketplace tree's files: a superset, not a replacement (D11). | ANSWERED — autonomous: decided in the D43–D56 amendment session |
| D45 | What is the prior state, how is it captured, and how is it restored exactly? | **AMENDED (D59) — the capture half of this answer is WITHDRAWN as untrue of the shipped code, and the restore guarantee is narrowed.** This row originally answered that KyberDash "captures the harness-side state it depends on … recorded in the receipt as the state the install found". **The shipped receipt records no such state** — it is exactly `{version, marketplace, plugin, root, files[]}` (`install.ts:137-147`) — and capturing it would mean reading the owner's settings file or adding a harness-side prior-state record KyberDash does not own. **D59 is the operative answer:** the receipt records the owned tree and its digests and nothing about the harness-side state the install found, and the restore guarantee narrows to what KyberDash's own actions can promise. **The residue rule stands unchanged** (report, never patch), and it now says where a restoring command exists and says plainly where none does. **The original text below is kept for the record of what this row decided, and is superseded by D59 on the capture half and on the "no entry the owner had before is missing or altered" clause.** The original: **KyberDash captures the harness-side state it depends on, and restores it through the harness's own commands; it never edits the owner's settings file to clean up.** The capture is (i) the harness-side view before the change — the registered marketplaces (`droid plugin marketplace list`) and the installed plugins with their STATUS (`droid plugin list --scope user`) — recorded in the receipt as the state the install found; and (ii) the owned tree's own file digests, which is what makes an owner edit detectable. The restore is the harness's own uninstall and marketplace-removal commands. **The criterion is semantic, not byte-wise:** after the uninstall, **no entry KyberDash's install added remains** in the harness's registry, **no entry the owner had before is missing or altered**, and every other key of `~/.factory/settings.json` is unchanged **in content** — the file's bytes may differ, because the harness's own commands rewrite it in the harness's own format. **A residue is reported, never patched:** if the harness's uninstall leaves an entry behind, KyberDash names it (which registry, which entry) and prints the exact `droid plugin …` command that removes it, and does not write the file. | The old criterion — the settings file stays byte-identical through install, enable, disable and uninstall — **cannot survive** the marketplace route, because the harness's own commands rewrite the file in its own format; keeping it would make a correct install look like a defect, and dropping it silently would hide a real one. Byte-identity is therefore replaced by a rule KyberDash can actually keep: **KyberDash writes nothing to that file** (D48), and the harness's own writes are the harness's business. The residue rule keeps ADR 0026's and ADR 0031's letter intact even if the harness's cleanup turns out to be partial — which is exactly what T46 must **establish, not assume**. | ANSWERED — autonomous: decided in the D43–D56 amendment session; **amended by D59** |
| D46 | What happens if the owner's settings file already holds a marketplace or a plugin of the same name? | **KyberDash refuses; it never takes a name it does not own.** Before it writes anything, the install reads the **harness's own registry**: if a marketplace named `kyberdash` is already registered and it is not the one KyberDash registered (its registered path is not KyberDash's marketplace root), the install is **refused** with a named conflict in strict mode and **kept with a warning** in preserve mode (exit 0); the same holds when the plugin identity `kyberdash-factory@kyberdash` is already installed and enabled by something KyberDash did not install. A plugin named `kyberdash-factory` in a **different** marketplace is a different identity and is **not** a conflict. The refusal names the conflicting entry, prints the command that removes it, and leaves the owner's registry exactly as it found it. | The names are KyberDash's and the harness's registry is keyed by name, so two owners cannot share one; the only safe behaviour is to refuse. The check is made against the **harness's CLI** rather than against the settings file, so the decision needs no read of the owner's file (D48) and inherits the harness's own view of what it holds. The shipped installer's refusal vocabulary — `conflict` (not ours), `drift` (owner edit), `unreadable` — is reused, so the exit codes and the status lines stay one shape across the harnesses. | ANSWERED — autonomous: decided in the D43–D56 amendment session |
| D47 | What does a partial or interrupted install leave behind? | **Every partial state is a named state, and every one of them is resumable by re-running the install.** The states are: `marketplace-tree-written` (the tree landed; the harness does not have it yet), `marketplace-registered` (the harness registered the marketplace; the plugin is not installed), `plugin-installed` (the plugin is installed and enabled), and `unknown` (the harness's own state cannot be read — the CLI is missing, or its output is not understood). The tree write is atomic per file (staged and renamed), so no half-written file is ever left, and the receipt is written only after the tree landed. **An interrupted harness-side step is never repaired by hand:** the harness's cache copy and its `installed_plugins` record are the harness's files, and the repair is to re-run the harness's own command. **A missing `droid` CLI is reported as `unknown` with the named cause**, never as an install that succeeded. | The status surface is assembled from the **harness's own view** and the Kyber-owned tree's state, so an interrupted install is visible without KyberDash inferring anything from a directory it does not own. The shipped classifier's vocabulary is kept for the tree and extended with the harness-side states above, so the row's existing state words keep their meaning and gain one axis. **AMENDED (D61): the tree classifier's vocabulary is corrected against the shipped code.** The shipped `classify` returns exactly five kinds — `absent`, `unreadable`, `foreign`, `edited`, `same` (`dash/src/capture/harnesses/factory-plugin/install.ts:319-324`, `:341-377`) — and this row's earlier list, which named `older` and `newer`, named **two states the code has never had**. The `unreadable` state, which the row omitted, is a real one (a symlinked root, a non-regular file, an unreadable directory, an oversized tree). The vocabulary is therefore **`absent` / `unreadable` / `foreign` / `edited` / `same`** for the tree, and the harness-side axis adds `marketplace-tree-written` / `marketplace-registered` / `plugin-installed` / `unknown` on top of it. | ANSWERED — autonomous: decided in the D43–D56 amendment session; **amended** by D61 |
| D48 | **What does the marketplace route mean for D4, ADR 0026 and ADR 0031?** | **D4's letter stands; D4's effect clause is withdrawn — and no ADR exception is needed.** `~/.factory/settings.json`, `~/.factory/hooks.json` and the project equivalents are still **never created, read, merged, rewritten or restored by KyberDash**, because the install route is the harness's own commands and the harness's state is read through the harness's own commands. **What changes is the file's fate, not KyberDash's hand:** the harness's own `droid plugin marketplace add` and `droid plugin install` write `extraKnownMarketplaces` and `enabledPlugins` into the owner's settings file, so the file's bytes are **not** byte-identical across an install. The plan's byte-identity criterion is withdrawn and replaced by D45's semantic criterion. `~/.factory/hooks.json` remains untouched in every sense — the plugin route does not use it. | **ADR 0026** ("Kyber Utilities deploys owned files and never owns harness settings") governs another component and states the repository's ownership principle: a shared harness settings file is the user's state, and no tool merges into it. The marketplace route does not breach the principle, because **KyberDash does not read or write the file at all** — the harness maintains its own file with its own semantics, and KyberDash's success does not depend on the file's content. **ADR 0031**'s 2026-11-02 Factory amendment says the owner's settings and hooks files "are never created, read, merged, rewritten or restored" — that sentence stays true **of KyberDash**, and the amendment must add what it now omits: **the file changes, by the harness's own hand, across a marketplace install, and KyberDash is not the hand that changes it.** The alternative — a scoped exception in which KyberDash writes the two settings keys itself, merges the owner's other entries and restores them on uninstall — was considered and rejected: it puts KyberDash's merge semantics inside a file whose schema drifts between Factory versions, and it duplicates work the harness's own commands already do correctly, with the harness's own conflict handling. D49 carries the ADR amendment. | ANSWERED — autonomous: decided in the D43–D56 amendment session |
| D49 | What must change in the ADR record? | **ADR 0031 is amended in place; ADR 0026 needs no amendment; the new Factory ADR is amended with them.** ADR 0031's Factory amendment (2026-11-02) gains the **delegated-install clause** — an install the harness's own CLI performs is not KyberDash owning the harness's settings, and the file's changing across such an install is the harness's act — and its account of the Factory writer is corrected from "a plugin directory under `~/.factory/plugins/`" to the marketplace route (D43, D44). ADR 0026's rule is **unchanged and unsatisfied**: no tool merges into the owner's settings file, and the marketplace route keeps it that way. The new ADR (H1) carries the install route, the owned tree, the consent contract and the native path's withdrawal. | A waiver would have been the wrong instrument: there is no rule to waive, because the design was changed to fit the rule rather than the other way round. The ADRs are amended because their **Factory paragraph** describes a design the branch no longer has — the same reason D38–D42 corrected the plan's text — and because the delegated-install clause is a durable statement about how a capture may be activated on a harness whose plugin lifecycle the harness owns. | ANSWERED — autonomous: decided in the D43–D56 amendment session |
| D50 | **Is the native OpenTelemetry path worth shipping, and what happens to the native span claim?** | **The native METRICS path stays exactly as shipped; the native SPAN claim is WITHDRAWN from this branch.** Factory's customer-side export is **metrics only by default**, and its span surface is either content-shaped (`legacy`: the four `droid.message.*` / `droid.tool.*` content spans, produced only when message-content logging is on) or a generic GenAI trace (`genai`: the root `invoke_agent droid` plus the per-turn `chat` and the per-tool `execute_tool` children, all children of the root). **Neither is claimable as shipped.** The `legacy` spans carry no attribute fingerprint an adapter may claim — their span **names** are the only Factory-specific evidence — and claiming them would import content through a route the consent rule does not govern. The `genai` trace is claimed only in part: the `droid` adapter recognises a native span **solely** by `gen_ai.agent.name === 'droid'`, which Factory's own data reference puts on the **root only**, so the group's score is the winner's evidence divided by the group's span count — `1/(1+k)` for a trace of one root and `k` children — against the registry's **0.6** threshold (`dash/src/canon/adapters/registry.ts:34` — **AMENDED (D61): the constant lives at `:34`, not the `:38` this row cited**; `DEFAULT_CONFIDENCE_THRESHOLD = 0.6`, and the group's normalised score is the winning adapter's total over the group's **span count**, computed in `scoreGroup` at `:104-128`). A real turn's trace has `k ≥ 1`, so it scores at most **0.5** and is quarantined; the measured probe reported **accepted 0, quarantined 2**. The metrics path is unaffected: it is metrics-only in the default format, the branch's receiver serves it, and the points are persisted in `metric_point` (D31, D38, D42). | **What the native path would have added, and why it is not worth the machinery.** Its one unique contribution is the **per-turn model** (`gen_ai.request.model` on the `chat` span) — the plugin names no model (D39) and the file source names a session-level one. But (i) under D23's precedence a native span for a session that already has plugin rows is excluded from the session's turns, tools, duration **and model**, so the native model would never reach the dashboard without a further amendment to the model chain; (ii) the transcript's assistant records already carry `message.modelId` (D39), so the same fact is reachable from the file source the plan already reads; (iii) the only safe way to claim the generic `chat` and `execute_tool` spans is a **group-scoped** claim — a trace whose root declares `gen_ai.agent.name = 'droid'` is Factory's own trace — which means a change to the **shared** attribution contract (`dash/src/canon/adapters/base.ts`, `registry.ts`) whose only beneficiary would be this one harness; and (iv) the current claim's behaviour is also **batch-dependent**, because the registry's second pass lets an undecided group inherit an attribution from a confidently-mapped group of the same source — so one childless root trace could pull the source's other groups into `droid`, and a batch without one would not. **The rejected alternative is exactly that group-scoped claim.** The withdrawn path's measurement, the arithmetic and the conditions for restoring it are filed as a todo under the todo index, so the evidence is kept and the claim is not. | ANSWERED — autonomous: decided in the D43–D56 amendment session, from the measurement and the arithmetic |
| D51 | What must the status surface report about the native path? | **The row reports the native export's EFFECTIVE SHAPE, and it says plainly that KyberDash claims no native span.** The Factory row reports, as fixed facts and never as an echo of owner text: the **endpoint verdict** (available / unavailable with the named cause / not configured — as shipped, D26, D41); the **export format** (`legacy` or `genai`) and where it came from (the organisation's `telemetry.format` wins over `OTEL_TELEMETRY_FORMAT`); the **granularity** (`user` or `aggregate`, resolved most-restrictive-wins); the **message-content switch** (`OTEL_LOG_MESSAGE_CONTENT`, or the organisation's `telemetry.logMessageContent`, with the organisation's value winning and `aggregate` overriding both); and the consequence in fixed words: *Factory's native metrics are persisted; Factory's native spans are not claimed; while KyberDash's consent is off, KyberDash's own Factory capture stores no prompt text.* **AMENDED (D57): the fixed words are corrected, and the correction is the point of D57.** The earlier wording — *no Factory content is stored while KyberDash's consent is off* — was **false**: the Factory **file source** captures the first user-message fragment (≤ 500 characters) out of the owner's own transcript and stores it on every file-sourced Factory record's raw attributes, and the store projects `userMessage` as a named attribute (`dash/src/providers/droid.ts:165-176`, `:287`; `dash/src/canon/store.ts:2433`), so that text is in the store whatever the consent file says. The consent file is read in **one** place — the plugin's hook runner (`dash/src/capture/harnesses/factory-plugin/entry.ts:132-136`) — and governs **one** emission: the plugin's prompt text. The corrected wording therefore says what the consent governs, and the row names the file source's route separately as a route the consent does **not** govern (D57, D62). The row never says the native path is "working" — it says what the export is configured to send and what KyberDash does with it. | The shipped row reports the endpoint verdict alone (`dash/src/capture/harnesses/factory-plugin/native-export.ts`), which is true and **incomplete**: the format decides which metric catalog arrives (`droid.*` under `legacy`, `gen_ai.*` under `genai` — the two replace each other and do not stack), the granularity decides whether the legacy format produces spans at all (`aggregate` + `legacy` produces **no spans**), and the content switch decides whether content-bearing spans are produced. The organisation's telemetry block is read from the same managed-settings cache the shipped reader already uses, so no new read of the owner's settings file is introduced. The row's reader must resolve the switches with Factory's own precedence rules — the organisation's value wins for the format and the endpoint, most-restrictive-wins for the granularity, and `aggregate` overrides every content opt-in — because a row that resolved them wrongly would state a shape the export does not have. **AMENDED (D58): the organisation's `telemetry.enabled` master switch is added to the reported shape.** Factory's own schema gives the block a master switch — "`true` keeps it on even where `OTEL_CUSTOMER_ENABLED=false`; `false` keeps it off even where `OTEL_CUSTOMER_ENABLED=true`. Omit it to fall back to `OTEL_CUSTOMER_ENABLED`" (Factory's *Enterprise Controls & Managed Settings*, the `telemetry` block) — and the shipped reader does not resolve it, so a row can report an export as configured while the organisation has switched the pipeline off. The row therefore reports the **enabled** switch with its source, beside the endpoint verdict, and states the consequence of the organisation's `false` in the same fixed words it already uses for the endpoint's cause: the export is **off**, not merely pointed elsewhere. | ANSWERED — autonomous: decided in the D43–D56 amendment session; **amended** by D57 and D58 |
| D52 | **Does the native path's content flow into KyberDash's store regardless of the consent?** | **AMENDED (D57) — the rule is narrowed to the routes KyberDash's own capture controls, and the blanket claim is withdrawn.** KyberDash's consent governs **every route by which KyberDash's own capture of Factory content reaches the store**: the plugin path (the prompt text the plugin puts on the wire) and, once T42/T43 land, the native OTLP route's records and quarantine packets. **It does not govern the Factory FILE source**, and the plan's earlier claim that "no Factory prompt … is written anywhere in the KyberDash store" is **withdrawn as false**. **The gate has a FAIL-CLOSED ARM, and it is deliberate:** a span that belongs to **no** harness has an **unknown origin**, so its content is stripped too, which is what covers a Factory child that arrives **before its root** and crosses a batch boundary. A **claimed** span's content is never touched, so another harness's own `gen_ai.input.messages` is untouched. The residual is named in the code: a Factory child that arrives before its root **and** is claimed by another harness keeps its content, which needs a source-name collision and the child-first ordering together. The file provider reads the first user message fragment out of the owner's own transcript — up to 500 characters (`dash/src/providers/droid.ts:165-176`, `:287`) — and carries it on every file-sourced Factory record's raw attributes; the store projects `userMessage` as a named attribute (`dash/src/canon/store.ts:2433`), so that fragment is in the store whatever the consent file says. **The consent file is read in exactly two places — the plugin's own hook runner (`dash/src/capture/harnesses/factory-plugin/entry.ts:132-136`) and the canon seam's gate (`dash/src/canon/droid-content.ts`, read at `dash/src/canon/ingest.ts:400`) — and it governs both routes KyberDash's own capture controls: the plugin path (the prompt text the plugin puts on the wire) and, once T42/T43 land, the native OTLP route's records and quarantine packets.** | **The hole is real and measured, and it is the file source's, not the plugin's.** The shipped adapter copies a span's attributes **verbatim** into `record.raw` (`baseRecord` → `raw: raw.attributes`, `dash/src/canon/adapters/copilot.ts:692`), `canonicalParts` reads `gen_ai.input.messages` as the record's prompt content (`dash/src/canon/adapters/copilot.ts:164`, `:405-460`), and an **unclaimed** span's whole decoded packet is written into the quarantine row as the audit evidence (`dash/src/canon/ingest.ts:401`, `:461-471`). So with the native export pointed at the receiver and message-content logging on, Factory's prompts would land in the store — through a route the consent file does not govern, and (under D50) through a route that **claims nothing at all**. **What the audit corrected is the SCOPE, not the hole:** the native route's hole is real and T42/T43 still close it, but the plugin's mapper never emits tool arguments or tool results as content at all — it emits **byte counts** (`factory.tool.input_bytes`, `factory.tool.result_bytes`; `dash/src/capture/harnesses/factory-plugin/contract.ts:32-33`), so the claim "no tool argument or tool result is stored" was never a property of the consent gate; it was a property of the plugin's wire contract. **What is genuinely outside the consent is the file source's own first-message fragment**, which the file provider reads from the owner's transcript without consulting any consent file. That route is pre-existing file-source behaviour, outside this plan's scope (D21), and it is now named rather than papered over. | ANSWERED — autonomous: decided in the D43–D56 amendment session; **amended** by D57 |
| D53 | Should the plugin's `hooks/hooks.json` be aligned to the documented shape? | **Yes — the shipped wrapper is removed, and the file is keyed directly by event name.** The shipped file wraps its events inside a top-level `hooks` key; that is the shape Factory documents for the `hooks` key **inside `settings.json`**, not for a plugin's `hooks/hooks.json`. Factory's own documentation shows a plugin's `hooks/hooks.json` keyed directly by event name (`{ "PostToolUse": [ { "hooks": [ { "type": "command", "command": "${DROID_PLUGIN_ROOT}/hooks/format.sh" } ] } ] }`), and the standalone `hooks.json` files at every scope use the same direct-keyed shape. The wrapped form was **observed working** — it relies on undocumented tolerance, which a Factory upgrade may remove — so the shipped bytes are corrected rather than kept. | The correction changes one owned file's bytes, so the receipt's digest changes with it and the bundle is regenerated; the wire contract's span names and attribute keys are untouched, so the contract version stays `1` and the adapter's envelope check is unchanged. The inner shape (an array of matcher groups, each carrying a `hooks` array of command objects) is already what the shipped file uses per event, so **only the outer wrapper is removed**. The alternative — keeping the working bytes and documenting the tolerance — was rejected because it buys nothing: the documented shape is a strict subset of what the wrapper contains, and there is no cost to carrying it. | ANSWERED — autonomous: decided in the D43–D56 amendment session, from Factory's own hooks documentation |
| D54 | **The plugin's `SubagentStop` span names a child session it cannot know.** | **The `SubagentStop` event produces no span in contract 1, and the child-session rule is corrected.** Factory's hooks documentation lists `SubagentStop`'s fields as `task_name`, `task_result`, `task_error` and `stop_hook_active` — **no child session id** — and the common payload's `session_id` is the session the hook fires in, which for a subagent's stop is the **parent's** session. The shipped mapper reads `session_id` as the child's id and looks for a `parent_session_id` the payload does not carry, so the span it produced named the parent as the child. The span is withdrawn. **A child session's plugin rows are unaffected:** the child's own lifecycle events fire in the child's session, so the child's turns, tools and session spans carry the **child's** session id and pair with the child's own companion file exactly as D29 says. The parent-child linkage stays where the evidence is — the file source's transcript (`parentId`, `callingSessionId`) — and the plugin path contributes no linkage of its own. | This corrects D2's and D25's clause that the plugin's `SubagentStop` event carries the child session id: the documented payload does not, and the plan's own local evidence (§2.5) puts the linkage in the transcript. The plan's primary-path choice does not rest on the withdrawn clause — the native `genai` format still carries no subagent lineage (Factory's own data reference), so the file source's transcript remains the only lineage source, and it is the plugin's **turn, tool and timing** coverage that makes the plugin the primary mechanism. The withdrawal removes a span kind from the contract's **emitted** set; it does not change the contract version, because no consumer's claim changes when a span is absent. | ANSWERED — autonomous: decided in the D43–D56 amendment session, from Factory's own hooks documentation |
| D55 | How is the Factory root resolved, per path? | **`FACTORY_DIR` does not redirect `~/.factory/settings.json`, and the install no longer writes under the Factory root at all.** Measured: with `FACTORY_DIR` pointing elsewhere, `droid plugin marketplace add` wrote the **real** `~/.factory/settings.json`. The plan's root resolution is therefore corrected **per path**: the **file source** keeps its `FACTORY_DIR` else `~/.factory` resolution for the **sessions** tree (where the transcripts and their companion files live); the **plugin payload** is no longer under the Factory root at all (it is the marketplace tree under `~/.kyberdash/`); and the **settings file** is neither read nor written (D48). | D35's rule is superseded in its root half — the installer's tree is not under the Factory root — and stands in its other half: the verb still **refuses** `--config-dir` with a usage error (exit 2), and the Squad step and the updater still hand the Factory call **no root**, because the verb now needs no root at all. The practical consequence is a simplification: the plugin and the file source no longer share a root, so the pairing rule D35 protected cannot drift apart. | ANSWERED — autonomous: decided in the D43–D56 amendment session, from the measurement |
| D56 | What is the plan's lifecycle after this amendment? | **The plan stays Ready; the shipped tasks T1–T33 stand as shipped; the new tasks T34–T46 are dispatched only once the conductor accepts this amendment.** The amendment changes the install route (D43–D49), withdraws the native span claim (D50–D52), and corrects two contract-level facts (D53, D54) — changes to work that has already shipped, which is why the closeout must not claim the old design. The plan's lifecycle state is unchanged (**Ready**): the amendment is a decision set, not a re-scoping of the branch. The closeout mapping gains a row per new decision, and T20's harvest carries the ADR 0031 amendment (D49) and the new ADR's corrected account of the install route (H20). | The plan's own approval (D22) covered the tasks then listed. This amendment adds tasks and supersedes decisions, so it is presented for acceptance as a whole; until it is accepted, the closeout cannot claim the marketplace route, and the shipped native-claim code is still in the tree awaiting T40–T41. The two documentation checks must be re-run against this amended plan (mergeReady false, the planning gate) — the amendment session could not run them, because it had no process-execution tool (§2.1a). | ANSWERED — autonomous: decided in the D43–D56 amendment session; the conductor's acceptance of the amendment is the gate for the new tasks |
| D57 | **What exactly does KyberDash's consent govern?** | **It governs the plugin's own prompt text on the wire — and only that. The plan's earlier claim that "no Factory prompt, reply, tool argument or tool result is written anywhere in the KyberDash store" while the consent is off is WITHDRAWN as false.** The consent file (`~/.kyberdash/factory-plugin.json`, with the `KYBERDASH_FACTORY_CONTEXT` override) is read in exactly **one** place — the plugin's own hook runner (`dash/src/capture/harnesses/factory-plugin/entry.ts:132-136`) — and it reaches exactly **one** emission: the plugin's `factory.turn.prompt.text` attribute, which the mapper adds only when the flag is true (`mapper.ts:202-207`). **Three routes carry Factory content outside the consent's reach, and the plan names them instead of claiming a store-wide gate:** (1) **the Factory file source**, which reads the first user message fragment out of the owner's own transcript — up to 500 characters (`dash/src/providers/droid.ts:165-176`, `:287`) — and carries it on every file-sourced Factory record's raw attributes, which the store projects as a named attribute (`dash/src/canon/store.ts:2433`); that route is pre-existing file-source behaviour outside this plan's scope (D21). (2) **the native OTLP route**, whose content-bearing attributes the harness sends when its own content switch is on (Factory's `telemetry.logMessageContent` or `OTEL_LOG_MESSAGE_CONTENT`), and whose quarantine packet keeps the whole decoded span as the audit evidence (`dash/src/canon/ingest.ts:401`, `:461-471`) — the gate T42/T43 add is what closes that route, and it is scoped to Factory's own spans. (3) **the plugin's tool arguments and tool results**, which the plugin **never** carries as content at all: the wire contract publishes **byte counts** (`factory.tool.input_bytes`, `factory.tool.result_bytes`; `contract.ts:32-33`), so there is no tool content to gate. **The status surface's fixed words change accordingly** (D58), and the row names the file source's route separately rather than implying a store-wide silence. | The claim being withdrawn was never a property of the code — it was a property of the consent **file**'s reach, mistaken for a property of the **store**. The consent file is read by the plugin process, in the plugin's own state directory, on the plugin's own machine; it has no authority over the file source, which reads the owner's transcript directly, and none over the native route, whose content the harness decides. **A store-wide gate would be a different and much larger design** — a gate at the ingest seam that strips Factory content from every record and quarantine packet, plus a change to the file source, which is out of scope (D21), and the metric route, which this plan has not measured (A22). The honest position is the narrower one: the consent governs KyberDash's own capture, and the plan says so. **What is NOT withdrawn:** the native route's hole is real and measured, and T42/T43 still close it — the correction is to the SCOPE of the claim, not to the requirement. **And the file source's fragment is not new:** it predates this branch, and the plan's own §3.5 already says the file source "observes no turn boundary and **no content**" — a sentence this decision corrects, because the file source's `userMessage` **is** content, up to 500 characters of it (D61). | ANSWERED — autonomous: decided in the D57–D64 amendment session, from the shipped code and the follow-up audit |
| D58 | What must the status surface say, in fixed words, about content and about the export's shape? | **The fixed words become a pair, one per consent branch.** *Off:* *Factory's native metrics are persisted; Factory's native spans are not claimed; while KyberDash's consent is off, KyberDash's own Factory capture stores no prompt text.* *On:* *Factory's native metrics are persisted; Factory's native spans are not claimed; while KyberDash's consent is on, KyberDash's own Factory capture stores the plugin's prompt text.* **Both branches name the capture, not "Factory content"** — the phrase *"Factory content"* is the false word, and the shipped ON branch carries it too (*"Factory content is stored while KyberDash's consent is on"*, `factory.ts:147-151`). The row names the **file source** separately as a route the consent does **not** govern, and it names the native route's content consequence from the harness's own switch rather than from the consent. **The reported shape gains the organisation's `enabled` master switch** and the granularity's unreadable-value fallback is corrected, both per Factory's own published rules (D61). | The shipped wording — *"no Factory content is stored while KyberDash's consent is off"* — is **false**, and a status surface that says a false thing about privacy is worse than one that says nothing (D57). The corrected wording is equally fixed, equally owner-text-free, and equally checkable in a test. **The ON branch is corrected in the same change**, because the same two words make it false: the file source stores its fragment whether the consent is on or off, so a row that says "Factory content is stored" while the consent is on overstates in the other direction, and a reader who reads only that line would conclude the opposite of the truth. **The organisation's `enabled` switch:** Factory's schema gives the `telemetry` block a master switch — "`true` keeps it on even where `OTEL_CUSTOMER_ENABLED=false`; `false` keeps it off even where `OTEL_CUSTOMER_ENABLED=true`. Omit it to fall back to `OTEL_CUSTOMER_ENABLED`" (*Enterprise Controls & Managed Settings*, the `telemetry` block) — and the shipped reader does not resolve it (`native-export.ts:274-286`), so the row can report an export as configured while the organisation has switched the pipeline off. **The granularity fallback:** Factory's privacy page states that granularity "is resolved most-restrictive-wins against `OTEL_TELEMETRY_GRANULARITY`, so either source asking for `aggregate` yields `aggregate`" and that **"a value that cannot be read resolves to `aggregate`"** (*Telemetry Privacy Controls*, *Data granularity*) — and the shipped reader resolves an unrecognised value to `user` instead (`native-export.ts:312-335`, and the test pins it at `native-export.test.ts:117`), which is the **fail-open** direction on a privacy control. The reader therefore resolves an unreadable organisation or machine granularity to `aggregate`, and the row reports the source of that resolution. | ANSWERED — autonomous: decided in the D57–D64 amendment session, from Factory's published schema and privacy rules against the shipped reader |
| D59 | **What does the receipt capture, and what does the teardown promise about the owner's prior state?** | **The receipt records the Kyber-owned tree and its digests, and NOTHING about the harness-side state the install found.** The shipped receipt is exactly `{ version: 1, marketplace, plugin, root, files[] }` (`dash/src/capture/harnesses/factory-plugin/install.ts:137-147`), and it stays that shape. **The restore guarantee is narrowed to what KyberDash's own actions can promise:** KyberDash adds no entry to the owner's registry that it does not remove, and it never writes the owner's settings file (D48) — so "no entry the owner had before is missing or altered" is **not** a promise KyberDash can make about the harness's own commands. Where the harness's own uninstall removes a pre-existing owner entry — measured: `droid plugin uninstall` deletes an `enabledPlugins: false` entry outright rather than restoring it, and `droid plugin marketplace remove` deletes the registration and leaves `enabledPlugins` alone (ADR 0031's 2026-11-02 amendment) — the teardown **reports** it, with the exact command that restores it where such a command exists, and says plainly where none does. **The prior state is NOT captured in the receipt**, because capturing it would mean reading the owner's settings file or adding a harness-side prior-state record KyberDash does not own; the mechanism is a filed todo, not this branch's work. | The mismatch the audit found was between a planned receipt and a shipped one: D45's answer said the capture is "recorded in the receipt as the state the install found", while the shipped receipt holds no such field. The honest resolution is to **not** promise the capture, to narrow the guarantee to KyberDash's own actions, and to name the one case the harness's own teardown breaks. ADR 0031's 2026-11-02 amendment already requires the uninstaller to "handle that case itself rather than assume the harness's commands satisfy the criterion on their own" and already states the residue rule — **report with the exact command, never patch by hand** — so the plan must say what "handle" means when no restoring command exists. The three options (capture and restore; report it; declare the criterion unachievable) are the decision the filed todo carries; this plan picks the **reporting** half as the behaviour the branch ships, and leaves the mechanism open (D62). | ANSWERED — autonomous: decided in the D57–D64 amendment session, from the shipped receipt and ADR 0031's measured amendment |
| D60 | **Can an organisation's settings make the marketplace route fail?** | **Yes — three documented hard controls can, and KyberDash must neither work around them nor report success through them.** (1) **`strictKnownMarketplaces`** — a higher-level list "is the ceiling and is not broadened by lower levels", so a user-scope marketplace outside the organisation's list is refused. (2) **`strictEnabledPlugins`** — the `enabledPlugins` maps at that level and higher become "the exhaustive plugin allowlist", and "enables and manual installs at lower levels are rejected unless the plugin is listed with value `true`", so the user-scope install is refused. (3) **`allowManagedHooksOnly`** — "only org-managed hooks and hooks from org-enabled plugins are loaded; hooks defined at the user and project levels are dropped", so a user-scope plugin can install, register as enabled, and **never fire a hook**. **KyberDash's behaviour is unchanged in kind and stated in full: it drives the harness's own commands, it reports the harness's own refusal with its stderr quoted (`install.ts:517-518`), it never edits the owner's settings file to widen the policy, and it never claims the capture is working when the harness's own answers do not say so.** The first two controls are **visible** — the harness's command fails and KyberDash reports the refusal. The third is **silent**: the install succeeds, the plugin list says the plugin is installed, and the capture produces nothing; neither `droid plugin list --scope user` nor `droid plugin marketplace list` reports which hooks a scope loads, so KyberDash **cannot** tell. That gap is filed as a todo, and the surfaces must not read "installed" as "capturing" (D62). | The controls are documented as **hard controls** whose whole point is that lower levels "can never weaken, remove, or re-enable" them (*Enterprise Controls & Managed Settings*, *How precedence works*). An organisation that forbids user-scope marketplaces or plugins therefore makes this branch's route unavailable on that machine, and the honest report is the harness's own refusal — which the shipped installer already produces, quoting the harness's stderr. **Working around a control is not available in principle:** the only way to add a marketplace or an enablement without the harness's commands is to write the owner's settings file, which D48 forbids and which ADR 0026 exists to prevent. **The silent third case is the dangerous one**, because it is the only one where the harness's own answers look healthy. The plan cannot fix that here — the information the report needs is the organisation's policy, which is only readable through the harness's own commands — so it is recorded as a known gap with a named todo rather than papered over with a guess. | ANSWERED — autonomous: decided in the D57–D64 amendment session, from Factory's published enterprise-controls schema |
| D61 | **Which of this plan's statements about the shipped code are wrong?** | **A correction list, each item checked against the code as it stands.** (a) The registry's confidence constant is at **`dash/src/canon/adapters/registry.ts:34`**, not `:38` — `DEFAULT_CONFIDENCE_THRESHOLD = 0.6` — and the group's normalised score is computed in `scoreGroup` at `:104-128`; the adapter's own fingerprint check was at `dash/src/canon/adapters/droid.ts:176` before T41 removed it, so that citation now points at a line that no longer holds the check. (b) The shipped tree classifier's vocabulary is **`absent` / `unreadable` / `foreign` / `edited` / `same`** (`install.ts:317-324`, `:337-377`) — this plan named `older` and `newer`, which the code has never had, and omitted `unreadable`, which it has. (c) The plugin's file-backed spool is **`spool.jsonl`**, not `outbox.jsonl` (`spool.ts:24`). (d) The plugin's **tool arguments and tool results are never carried as content**: the wire contract publishes byte counts (`contract.ts:32-33`), so §3.2's "tool arguments, gated" and "tool result, gated" are wrong — those attributes carry **sizes**, and the gate has nothing to gate. (e) **§3.2's child-session sentence is stale**: "`SubagentStop` carries the child session id" is false — the documented payload carries no child identity (D54), and the span is withdrawn. (f) The **quarantine packet's 256 KiB cap bounds one packet**, not the table: `MAX_QUARANTINE_RAW_PAYLOAD_CHARS` cuts a single stored payload (`dash/src/canon/ingest.ts:360-366`), and **no bounded retention or re-quarantine policy for the quarantine rows themselves was identified** — the rows are cleared only by a scoped or full wipe (`store.ts:154`, `:5008-5010`). (g) The investigation record's **external fetch count** was four; the session fetched five. (h) Factory's **granularity fallback** is `aggregate`, not `user`, for a value that cannot be read (D58). **(i) §3.4's "The shipped `droid` adapter recognises the native `genai` fingerprint by its agent identity alone" is HISTORICAL, not a statement of the shipped adapter** — the shipped adapter's own header says "ONE claim path reaches this adapter: the Kyber-owned Factory plugin envelope", and it states that Factory's own export "scores 0 whatever it carries" (`droid.ts:6-21`, `:243-252`). The paragraph is now labelled as the record of what the withdrawn claim would have matched. **(j) D7's "two claim rules" is withdrawn** — the adapter has one. **(k) T11, T15 and T16 still described the native claim as work to be written**; the native branch is not implemented, it is **removed**, and the D23 precedence rule now reads "a native span adds no turn, no tool, no duration" because there is no session it can contribute to at all. **(l) §1's own summary of D52 — "with consent off no Factory prompt, reply, tool argument or tool result is written anywhere" — is false** and is now marked withdrawn in place. **(m) The consent's ON branch is as false as its OFF branch**: the shipped row says *"Factory content is stored while KyberDash's consent is on"* (`factory.ts:147-151`), which is false for the same reason — the file source stores its fragment whatever the consent says — so the fixed words became a **pair**, one per branch (D58). **(n) D45's "recorded in the receipt as the state the install found" is untrue of the shipped receipt**, which holds `{version, marketplace, plugin, root, files[]}` and no harness-side state; D45 is now marked superseded by D59 on that clause and on the "no entry the owner had before is missing or altered" promise. **(o) The task ledger did not record what had shipped** — the plan said "T1–T33 dispatched" and listed T34–T46 as new work, while T34/T35, T38's `SubagentStop` half, T40/T41 and T44/T45 had in fact landed, and the §13 item 2 documentation pass had been measured against an **earlier revision** of the plan. | Each item was checked against the code rather than carried forward from the plan's prose, because a plan that states a shipped fact wrongly is a plan a reader cannot trust about anything else. (a) and (b) are citation-and-vocabulary errors with no behavioural consequence, but they are the kind that make a reader doubt the behavioural statements too. (d) and (e) are **behavioural statements about the shipped contract** and are corrected in the text itself, not only here. (f) matters because D50's withdrawal makes the quarantine the **only** place a native Factory span's content can land, so the growth of that table is now this branch's own consequence — and it is unbounded (D62). | ANSWERED — autonomous: decided in the D57–D64 amendment session, from the shipped code |
| D62 | **What follow-ups does this amendment file?** | **Four, none of them work in this branch. Three have a dedicated todo under the todo index; the fourth rides on an existing one.** (1) **The quarantine table's growth.** The quarantine packet keeps the whole decoded span, capped at 256 KiB each, and **no retention or re-quarantine policy for the rows themselves was identified**; with the native span claim withdrawn (D50), every native Factory span the harness sends lands there. The concern is unbounded row count, not unbounded packet size. **No dedicated todo exists for this** — the nearest is `kyberdash-quarantine-harness-index.md`, whose "neighbouring question" names payload retention ("If it is going to be large, a retention policy — or moving the payload out of the row — is worth more than an index") but whose title and scope are the harness index. **Filing a dedicated quarantine-retention todo is T20's work**, not this amendment's. (2) **Consent is not a purge or a retention control.** Turning the consent off does not remove what is already stored, and `records.raw` is never purged — a backfill re-derives content from it, so a row that holds content stays a content store and an ingest-time strip is **not retroactive**. **Todo: `canon-records-raw-never-purged.md`** (2026-11-02). (3) **The harness's teardown drops a pre-existing owner entry**, and no restoring command is known to exist (D59). **Todo: `factory-uninstall-drops-disabled-plugin-entry.md`** (2026-11-02), which carries the three candidate remedies and says the receipt does not record the harness-side state the install found. (4) **An organisation's `allowManagedHooksOnly` makes the capture silently inert**, and the harness's own CLI does not report which hooks a scope loads (D60). **Todos: `factory-org-managed-plugin-not-reported.md`** (the status surface does not report that state) **and `factory-command-outcome-vocabulary.md`** (the command's output does not distinguish "an install whose hooks the organisation's settings will not load" from the other outcomes a user must tell apart). | Each is a finding whose remedy is a decision about **other** files — the quarantine's retention, the store's raw retention, the harness's teardown semantics, and the status surface's vocabulary — and each is recorded with the evidence and the candidate remedies, so the next decision starts from a list rather than from a rediscovery. The plan's own out-of-scope section already names the mechanism (D21, T20 files the todos); this decision makes the list explicit and ties each item to the decision that raised it. **Naming the artifacts exactly matters:** a follow-up list that says "filed as a todo" for an item with no todo of its own sends the next session hunting for a document that does not exist. | ANSWERED — autonomous: decided in the D57–D64 amendment session |
| D63 | **Which of the amendment's tasks have shipped, and what does the branch carry now?** | **The branch has moved on since the D43–D56 amendment was written, and the plan's own task accounting is re-based on the code as it stands.** **Shipped:** T34/T35 (the marketplace route — `marketplace.ts`, `marketplace.test.ts`, and the installer's rewrite around it), T38's **SubagentStop** half (the mapper and the contract declare no child-session span), T40/T41 (the native span claim's withdrawal), T44/T45 (the native export's effective shape on the status surface — the format, the granularity and the content switch). **Partly shipped:** T36/T37 (**the settings-file non-interference shipped**, `marketplace.test.ts:223-249`; **the name-conflict refusal and the residue report did not**), T38 (**the `SubagentStop` half shipped; the hooks-file half did not** — the shipped hooks file still carries the `hooks:` wrapper and still registers the `SubagentStop` event), T44/T45's **D58 criteria did not ship** (the organisation's `enabled` master switch is unresolved and the granularity's unreadable value still resolves to `user`, `native-export.ts:334`), T46 (**some of the harness's answers are recorded** — ADR 0031's 2026-11-02 amendment and the filed todos carry the marketplace registration, the settings-entry shapes and the uninstall behaviour — **but no evidence directory was produced and the probe is not closed**). **Not shipped:** T42/T43 (the consent gate over the native and quarantine routes). **New work in this amendment: T47 and T48** — the status surface's corrected consent wording and the native reader's `enabled` switch and granularity fallback (D57, D58). | The task list is the plan's execution authority, and a task list that says a shipped task is pending sends the conductor to re-do work and a closeout that claims shipped work as new. Every entry was checked against the working tree rather than against the amendment that wrote it: the marketplace module and its test exist and pin the command order, the state vocabulary and the settings-file non-interference; the mapper's `SubagentStop` case returns no span and the contract declares no child-session kind; the adapter's `detect` claims the plugin envelope and nothing else; the native reader resolves the format, the granularity and the content switch and the row reports them. **What has NOT landed is named as precisely as what has**, because the pending half of T38 (the hooks file's shape) and the pending T42/T43 gate are real gaps the branch still carries. **A task that is partly shipped is marked partly shipped, not wholly pending and not wholly done** — the two errors are the same error in opposite directions, and both mislead the conductor. | ANSWERED — autonomous: decided in the D57–D64 amendment session, from the working tree |
| D64 | What is the plan's lifecycle after this amendment? | **The plan stays Ready. The shipped tasks stand as shipped. T36/T37's pending half (the name-conflict refusal and the residue report), T38's hooks-file half, T42/T43 and T46 remain pending and are dispatched as ordinary tasks. T47/T48 are new and are dispatched only once the conductor accepts this amendment.** The amendment corrects the **consent's scope** (D57), the **status surface's fixed words and the export's shape** (D58), the **receipt and the restore guarantee** (D59), the **enterprise-control position** (D60), the **plan's factual statements about the code** (D61) and the **follow-up list** (D62), and re-bases the **task accounting** on the shipped code (D63). None of it changes the branch's shape: the capture is still one hooks-plugin pipeline, the owned unit is still the Kyber-owned marketplace tree, the native span claim is still withdrawn, and the native metrics path is still the whole of what remains of the native path. | The plan's approval (D22) covered the tasks then listed; every amendment since has been a decision set presented for acceptance rather than a re-scoping. **This amendment touches work that has already shipped** — the status surface's wording is live, and the false sentence in it is live — which is why the closeout must not claim the corrected wording before T48 lands, and why the branch carries a status line that overstates its own privacy behaviour until then. The two documentation checks must be re-run against this amended plan (mergeReady false, the planning gate) — the amendment session could not run them, because it had no process-execution tool and no documentation MCP server (§2.1b). | ANSWERED — autonomous: decided in the D57–D64 amendment session; the conductor's acceptance of the amendment is the gate for T47/T48 |
| D65 | **How does a superseded KyberDash bundle get upgraded?** | **A receipt-proven tree whose bytes are exactly what KyberDash wrote, and whose content is not the current bundle, is an UPGRADE — not an owner edit and not `unchanged`.** The tree classifier gains a **sixth** kind, `upgrade`, beside `absent` / `unreadable` / `foreign` / `edited` / `same` (D61's five). Three facts make it one: (1) the receipt names this tree's root and records a digest for **every** owned file; (2) every file on disk still matches its recorded digest, so the owner has not edited it; and (3) the tree's content differs from the current `FACTORY_MARKETPLACE_CONTENT`, so the bundle KyberDash wrote has been superseded. **A tree that fails (2) stays `edited`** — the owner's edit is the owner's, and the strict/preserve behaviour is unchanged. **A tree that fails (1) is `foreign`** — no receipt, an unreadable receipt, or a receipt naming another root — and **matching an older bundle's bytes never establishes ownership**, so it is refused in strict mode and kept in preserve mode, exactly as today. **AMENDED (D67):** a tree no receipt proves whose content is the CURRENT bundle is **not** foreign — it is KyberDash's own tree without the receipt that proves it, and the install writes that receipt, so the next upgrade is provable; a tree whose content is an OLDER bundle stays `foreign`. **The upgrade flow.** The Kyber-owned tree and its receipt are rewritten with the current bundle, and then the harness's **cached copy** is refreshed through the harness's own command. The command is chosen from the **harness's own registry** (the state D11 already reads), never from a guess: the plugin **installed** → `droid plugin update kyberdash-factory@kyberdash --scope user`; the plugin **not installed** and the marketplace **registered** → `droid plugin install kyberdash-factory@kyberdash --scope user`; the plugin **installed** and the marketplace **not registered** → `droid plugin marketplace add <the marketplace root>` and then the update; neither → the marketplace add and then the install. **`plugin install` is never used against an installed plugin**: Factory 1.1.0 refuses it, so the fallback would turn a working upgrade into a failure. **A refusal is the harness's answer and KyberDash reports it** with the harness's stderr quoted — strict refuses (exit 1), preserve keeps with a warning (exit 0) — and **no refusal is ever reported as a success**: an install the harness refused does not print `installed`, and an upgrade the harness refused does not print `upgraded`. **`--if-installed` decides from the harness's registry, as D11 requires.** When the harness's own registry reports the plugin installed, the install refreshes the harness's copy — **even when KyberDash's own tree is absent and must be written for the first time**, because the harness's cached copy is then the stale one. When the registry does not report it installed, nothing is written and nothing is run. **`unchanged` means two facts, not one:** the outcome is `unchanged` only when the tree is the **current** bundle **and** the harness holds the plugin. The harness is refreshed **only when the tree changed**, so a routine install — a Squad step, or the updater's `--if-installed` call — that finds the current tree and an installed plugin runs **no harness command at all** and mutates nothing. **The reporting.** `upgraded` for an actual upgrade and `would-upgrade` in a dry run, mirroring the OpenCode installer's existing vocabulary (`dash/src/capture/harnesses/opencode-plugin/install.ts:56-60`, `:317-333`); `installed`, `would-install` and `unchanged` keep their meanings, and a dry run writes nothing. **The receipt keeps its shape:** `{ version: 1, marketplace, plugin, root, files[] }`, unchanged. The per-file digests already prove the installed tree's identity and ownership, and they are exactly what the upgrade classification reads, so **no bundle-identity field is added**, and the receipt forms this plan already requires the installer to read — including the previous route's receipt, which names no marketplace and no plugin — stay readable. | **The defect is measurable in the shipped code.** `classify` returns `same` for a tree whose files match the receipt's digests (`dash/src/capture/harnesses/factory-plugin/install.ts:365-408`), and the install then returns `unchanged`/`noop` when the harness holds the plugin (`install.ts:645-651`) — so a tree written by an earlier KyberDash is never rewritten, and the harness's cached copy keeps running the old bundle. The tree is written **only when the tree is `absent`** (`install.ts:653-661`), so an upgrade needs a second write trigger. **The precedent is the OpenCode installer, which already has this shape:** it classifies `older` (with a `proven` flag) and `newer` beside `same`, upgrades a proven older file, keeps an unproven one with a warning, and reports `upgraded` / `would-upgrade` (`dash/src/capture/harnesses/opencode-plugin/install.ts:189-211`, `:292-333`). The Factory route differs in one respect, and the difference is deliberate: an OpenCode plugin file carries a content-version marker (`// kyberdash-opencode-plugin v1`) that an unproven file can be recognised by, while the Factory **tree** has no such marker, so an unproven tree cannot be told from an owner tree at all — hence "no receipt, no upgrade". **Factory's own documented command set** names `droid plugin update [plugin@marketplace] --scope <scope>` beside `droid plugin install` and `droid plugin marketplace update`, and states that Droid copies a plugin into its cache unchanged — which is why the harness's cached copy needs its own refresh command once the marketplace tree changes. **One honest limit:** the Factory tree carries no content-version marker a newer KyberDash would bump (its plugin manifest's `version` is the static `1.0.0`), so a tree from a **newer** KyberDash cannot be told apart from an older one; both are "not the current bundle", and the running build replaces the tree with its own bundle, which keeps the tree and the engine that owns it consistent. **One named consequence:** a refresh the harness refuses leaves the Kyber-owned tree current and the harness's cached copy stale, and because `unchanged` requires only a current tree and an installed plugin, **a later install does not retry the refresh** — so the refusal is reported with the exact command that completes it (`droid plugin update kyberdash-factory@kyberdash --scope user`), which is the residue rule D59 established: report, never patch. | ANSWERED — autonomous: decided in the D65 amendment session, from the shipped classifier, the OpenCode installer's precedent and Factory's own documented command set; the conductor's acceptance of the amendment is the gate for T49/T50 |
| D66 | **What has the branch landed since D65, and what may the closeout therefore claim?** | **The closeout's "must not claim" list is re-based on the branch as it stands: the hooks file's shape (T38), the consent gate over the native and quarantine routes (T42/T43), the corrected consent wording and the export's effective shape (T47/T48) and the name-conflict refusal's **decision** (T36/T37) HAVE LANDED — the harness-state reader parses the registry and decides the refusal — while the installer's own wiring of that refusal and the residue report are T60's and are **pending**; the closeout MAY describe the first four as shipped and must not claim the last two.** Two of them carry a property a reader of the closed plan cannot infer from the code, and the closeout must NAME both: the consent gate's **fail-closed arm** (a span that belongs to no harness has an unknown origin, so its content is stripped too, which is what covers a Factory child that arrives before its root across a batch boundary; a claimed span is never touched), and the harness's **wrapped** list table (it truncates a cell that does not fit and prints the overflow on the lines below, so the reader joins a row's pieces into the column they start in). **The upgrade path (T49/T50) is still pending, so the closeout must not claim it.** | The re-basing is a reconciliation against the working tree, not a new design question, and each claim is checked against the code and not against an amendment that wrote it: the hooks file is keyed directly by event name and `SubagentStop` is not registered — and it was verified **live**, because a fixture can only assert the bytes, so a real Droid turn produced the session, turn and tool spans through the corrected file with the consent off and no prompt text on the live span; the consent gate runs at the ingest seam before normalization and before the quarantine write; the status row's two consent branches name what KyberDash's own capture stores and the file source's route is named as one the consent does not govern; the organisation's `enabled` master switch and the granularity's unreadable-value fallback are reported with the level that pinned each; and the harness's registry is read as the harness reports it, so a marketplace or plugin identity KyberDash does not own is refused with the exact command that removes it. **The two named properties are properties of the IMPLEMENTATION, not of the plan**: the arm and the wrapped table are what make the gate and the collision refusal behave as the decisions require, and a closeout that described neither would leave a reader unable to account for why a span lost its content or why the reader joins a row's pieces. | ANSWERED — autonomous: decided by the conductor from the branch as it stands, against the working tree rather than against an amendment; the D65 clause stands, and the closeout's mapping gains a row for this decision |
| D67 | **What does the shipped upgrade path decide beyond D65, and what does the installer do with a name it does not own?** | **Three refinements, each closing a gap D65 left open.** (1) **A tree no receipt proves whose content is the CURRENT bundle is not foreign: the install writes the receipt that proves it**, so the next upgrade is provable. Without that, a tree whose receipt was lost would be `same` today and `foreign` after the next bundle change, and could never be upgraded (D65 is amended on that clause). A tree whose content is an OLDER bundle stays `foreign`, because matching an older bundle's bytes never establishes ownership. (2) **The harness-side commands are derived from the harness's own REGISTRY, not from a state name**, so "the plugin is installed but the marketplace is not registered" is expressible — and it is reachable, because `droid plugin marketplace remove` leaves the plugin installed. The sequence is: the marketplace is not registered → `plugin marketplace add` first, because `plugin update` fails without a registration; the plugin is installed → `plugin update`, because `plugin install` is refused for an installed plugin; the plugin is not installed → `plugin install`. (3) **The teardown removes KyberDash's own tree on a conflict and leaves the foreign entry alone**: KyberDash's tree is KyberDash's and the receipt proves it, while the entry that holds KyberDash's NAME is somebody else's — so no harness command is run against it, the conflict is reported with the exact command that removes it, and KyberDash's own tree still goes. | Each refinement closes a gap that would otherwise make the upgrade unreachable or the teardown unsafe. (1) The review found the receipt was never repaired: the tree matched the current bundle, was classified `same`, and the receipt was never rewritten, so the NEXT bundle change made that tree `foreign` and it was refused forever. (2) The state vocabulary names four states, and none of them says "installed but not registered" — a combination the harness produces and the state machine could not express, so the command it would have run (`plugin update`) would have failed with "Marketplace \"kyberdash\" not found". (3) The teardown's promise is narrowed to what KyberDash's own actions can promise (D59): it must not remove an entry that is not KyberDash's, and it must still be able to remove what IS KyberDash's. A teardown that refused everything would leave the owner unable to clean up their own state directory. | ANSWERED — autonomous: decided by the conductor from the review's findings and the measured harness behaviour, and verified live: the upgrade rewrote the tree and the harness's cached copy a second later |
| D68 | **Is the consent reader's duplication merged, and what ties the two copies together?** | **The duplication is KEPT, and the two copies are tied together by a parity test.** There are two production copies of the Factory consent reader: `dash/src/canon/droid-content.ts` (`readContentConsent`, `resolveContentConsent`) and `dash/src/capture/harnesses/factory-plugin/consent.ts` (the same two functions). The plugin's copy **must stay a leaf** — the plugin bundle inlines it, and the leaf rules in `dash/src/architecture/factory-leaves.test.ts` and `dash/src/capture/harnesses/factory-plugin/bundle.test.ts` forbid a value import from outside `src/capture/` — and the canon copy declares itself a node-built-ins-only leaf in its own header comment. A **parity test** runs BOTH readers over one corpus (valid, malformed, oversized, missing, unknown-version) and asserts identical verdicts, plus a round trip from the shared writer `dash/src/capture/consent-file.ts` into both readers. | **The merging option is REJECTED, in both directions.** Merging in the plugin's direction — the canon copy importing the plugin's reader — would break the plugin's leaf rule, because `src/canon/` is outside `src/capture/`. Merging in the canon's direction — the plugin's copy importing the canon reader — would break the canon leaf declaration, because the plugin bundle would then inline a `src/canon/` module. The two copies answer identically today, so the duplication is not yet a behavioural fork; the parity test is what keeps it from becoming one, because it fails the moment the two readers disagree on any input in the corpus. The **write** side is not duplicated: both sides already share the one writer (`dash/src/capture/consent-file.ts`). The parity test is being written in parallel. | ANSWERED — autonomous: decided by the conductor from the two copies' leaf rules; the parity test is in flight |
| D69 | **Is the harness-side lifecycle vocabulary surfaced by the status surfaces?** | **No.** The vocabulary exists and is the **install-side** state the installer classifies — `marketplace-tree-written`, `marketplace-registered`, `plugin-installed`, `conflict` and `unknown` — and `readFactoryHarnessState` returns it. It is **not** part of the status surface: reading it spawns the harness's own CLI, so a caller that must not spawn — a status read, for instance — does not call it. `factoryHarnessState` (`dash/src/capture/harnesses/factory-plugin/install.ts`) says exactly this in its own header comment, and `describeFactoryPluginStatus` prints the `factory harness: <state>` line only when a caller injects a harness CLI. No production caller injects one, so the **install path is the only place the vocabulary is reported today**, and a status row reports the **tree's** state only. The honest-unobservability gap stays open in `docs/todo/factory-org-managed-plugin-not-reported.md`: an owner whose organisation refused the route, or whose marketplace registration was removed, sees a row that says the plugin is installed with no harness-side fact to contradict it. | A status read must not spawn the harness's CLI: the status surface is rendered by the dashboard and printed by `capture status`, and a spawn there would make a read cost a subprocess against a CLI that may be missing, slow or refused. The row's contract is fixed words about what KyberDash owns — the tree, the receipt and the consent file — and the harness's registry is not KyberDash's to read on a status call. Surfacing the vocabulary would move it, not close the gap: the harness's own commands do not report whether the hooks will load, so the organisation-policy state stays unobservable either way, and that gap is carried as a todo. | ANSWERED — autonomous: decided by the conductor from the shipped call sites; the vocabulary's absence from the status surface is a documented limit, not a defect |
| D70 | **Does the shipped install path do what D65 and D67 decide?** | **It does now, and the three corrections that made it so are the code council's findings on the accumulated branch.** (1) **THE REFRESH.** D65 decides that a superseded bundle is upgraded by rewriting the tree **and** refreshing the harness's cached copy. The shipped code armed that refresh for an upgrade only, so a tree that was **absent** while the harness already held the plugin wrote the tree and the receipt and refreshed nothing: the harness kept running the bundle it had already cached while the install reported success. The refresh is armed for the freshly written tree as well, and the disposition and the printed line name what happened to the **tree** — `installed` and `factory plugin: installed <dir>` for a tree written fresh, `upgraded` and `factory plugin: upgraded <dir>` for a superseded one. (2) **THE OWNERSHIP.** The plugin's ownership fell back to the tree's presence, so an absent tree turned KyberDash's **own** installed plugin into a conflict, and the install refused with a command that told the owner to uninstall a plugin KyberDash itself installed — the refusal D67(2)'s own repair (`plugin marketplace add` first, then `plugin update`) could not get past. The **receipt** is now consulted: it proves the identity when it names **this** tree, **this** marketplace and **this** identity, and the install then runs D67(2)'s sequence. (3) **THE STATUS WORD.** The upgrade classification was reported as `unknown`, whose fixed words call a tree "modified since install or not written by KyberDash" — false for a tree KyberDash wrote and the owner never touched. It is now `partial`, with its own fixed line, `plugin: installed (an older version than this build)`; `partial` is the state name the OpenCode and Kilo plugin surfaces already use for an older plugin of ours. | **Each correction closes a silent failure this plan had already decided against.** (1) The refresh is the difference between a capture that works and one that looks installed and delivers nothing: the harness runs its **cached copy**, so a tree KyberDash rewrote under a copy the harness never re-read leaves the owner with the previous bundle and a successful install report. D65's promise had two halves — the tree and the receipt are rewritten, and the copy is refreshed — and only the first half shipped. (2) A false conflict is worse than no install: it refuses the owner's own capture and hands the owner a command that uninstalls a plugin KyberDash itself installed, and D67(2)'s repair sequence is unreachable because the refusal comes first. The receipt is the proof that survives the harness's own teardown — the harness removes a marketplace registration **without** removing the plugin — so consulting it restores the ownership decision D46 and D67 decided. (3) `unknown`'s fixed words accuse the owner of an edit they never made, and the fixed words are the whole contract of the status surface, so the superseded tree needed its own word rather than the owner-edit one. **All three are covered by tests that failed before the fix**: the four RED cases in `dash/src/capture/harnesses/factory-plugin/install.test.ts` (`253e2aa`) — the plain install over an absent tree the harness already holds the plugin for, the same install under `--if-installed`, the install whose receipt names the marketplace and the identity, and the status line of a superseded tree — each failed against the code before the fix for the reason it names, and the implementation that ships them is `9775b9b`. | ANSWERED — autonomous: decided by the conductor from the code council's findings on the accumulated branch; the corrections ship in `9775b9b` under the RED tests of `253e2aa`, and §10's review criteria name them |
| D71 | **What did the second code council find, and what does each fix decide?** | **Six findings, six decisions.** (1) **THE UNINSTALL PROVES THE PLUGIN WITH THE RECEIPT TOO.** D70 applied the receipt proof at the **install** site only; the **uninstall** site was left out. A plugin the receipt proves is ours was therefore classified as a **conflict** once the marketplace registration was gone, so the uninstall ran **no harness command**, **deleted the receipt** and reported success while the plugin stayed installed and kept capturing — and the next update then met an absent tree, no receipt and a held plugin. The uninstall now derives the proven identity from the receipt it **already reads**, under the same conditions the install uses — the receipt names **this** tree, **this** marketplace and **this** identity, and is not the previous route's legacy receipt — so the harness's own `plugin uninstall kyberdash-factory@kyberdash --scope user` runs. A marketplace named `kyberdash` that somebody else took over is still a conflict and is still left alone. (2) **THE ORGANISATION'S CACHED SETTINGS ARE READ FROM THE HARNESS'S OWN DIRECTORY.** D55 resolved the Factory root **per path**: `FACTORY_DIR` moves the sessions tree and **never** the harness's own directory (MEASURED: with it pointed elsewhere the harness still wrote the real `~/.factory/settings.json`). The organisation's cached telemetry block is written by the harness into **its own** directory, so reading it from the `FACTORY_DIR`-redirected root reported the **machine variable's** value where an organisation's pin should be. It is read from the harness's own directory now. (3) **ONE CONSENT FILE, ONE SIZE BOUND, ONE UNIT.** The engine-side state reader bounded the consent file by **UTF-16 code units** while every content reader bounded it by **UTF-8 bytes**, so a file under 4096 characters and over 4096 bytes was reported as **consent on** by the status row while both content readers read it as **off**. All of them count **UTF-8 bytes** now — the Factory plugin's reader, the OpenCode plugin's reader, the canon gate and the engine-side state reader — and the committed OpenCode plugin bundle was **regenerated** to match. (4) **THE OWNERSHIP RULE IS AN OR, AND THE DOCUMENTS SAY SO.** The tree's presence proves the identity ours when KyberDash wrote that tree, **and** the receipt proves it when the tree is gone but the receipt survives the harness's own teardown. The canonical architecture document previously claimed the receipt was the **only** proof; it now states the OR. (5) **THE `unchanged` VERDICT READS THE TREE, NOT THE HARNESS'S COPY.** KyberDash never inspects the harness's cached copy, so the command surface no longer claims `unchanged` means the harness's copy is current. (6) **THE CAPTURE-PLUGIN GUIDANCE RELAY RULE LIVES IN ONE PLACE.** The rule that relays a capture-plugin verb's guidance lines was written **twice** in the .NET CLI — in the self-updater and in the Squad deploy step — differing by a **single token**, so the duplicates gate never clustered them and a one-sided change would have made the two callers relay **different** lines. It now lives **once**, beside the existing OpenCode guidance helper, and a test drives both call paths over one verb output and asserts they agree. | **Each correction closes a failure this plan had already decided against, and each is covered by a test that failed before the fix.** (1) A teardown that reports success while the plugin keeps capturing is the worst shape a removal can take: the owner is told the capture is gone, the proof is thrown away, and the plugin goes on writing. The receipt is the proof that survives the harness's own teardown, so the uninstall reads it too, under the same conditions the install uses — one rule, two sites, so the two cannot drift apart again. (2) The organisation's pin was reported **wrong**, not missing: a machine variable's value was named where an organisation's pin belonged, so the row named the wrong cause for the export's availability. (3) One file, two bounds and two units is a disagreement about the owner's own consent: the status row claimed a capture that was not happening. **One bound and one unit — bytes — is the only answer that lets every reader agree**, and the committed bundle has to be regenerated when the reader it inlines changes. (4) The documents are the contract a reader consults, and a document that names one proof where the code accepts two sends the next engineer looking for a rule the code does not have. (5) The verdict is read from the tree and the harness's own **registry**, never from the harness's cached copy, and a document that says otherwise sends a reader looking for a file KyberDash never opens. (6) Two copies of one rule, differing by one token, are two rules: the duplicates gate cannot cluster them, so a change to one copy — a new guidance prefix, a changed verb output — would make `kyber-weave update` and the Squad deploy step relay **different** lines for the same harness, and only a test that drives both paths can hold them together. **The RED tests are `3d05a5e` (the uninstall proof, the organisation cache and the consent bound), `a33931a` (the OpenCode reader's byte bound) and the guard test in `ecb64d3`; the implementations that ship them are `df7ee07`, `199406d` and `ecb64d3`.** | ANSWERED — autonomous: decided by the conductor from the second code council's findings on the accumulated branch; the corrections ship under the RED tests named, and §10's review criteria name them |

## Design harvest

`documentation-impact: extends-component`; `impact-components: [KyberDash, KyberSquad]`.

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | KyberDash | adr | New ADR: Factory (Droid) capture is a hooks-plugin pipeline — the plugin directory, the receipt, the consent file, the `droid` wire contract, and the rejection of native OTel as the primary path (D2, D3, D5, D12). |
| H2 | KyberDash | adr | ADR 0031 amended in place: the Factory writer owns a plugin directory and a consent file, declares no config key, and never writes `~/.factory/settings.json` or `~/.factory/hooks.json` (D4, D10, D16). |
| H3 | KyberDash | architecture | `docs/dash/architecture.md`: the Factory capture pipeline (hooks → plugin → OTLP → `droid` adapter → store → projection → `/api/kyber/*`), the plugin directory layout, the file-backed spool, the consent contract, the install modes, and the `droid` harness id shared with the file source (D3, D6, D8, D11, D15). **AMENDED (D68): the consent contract's read side exists twice — the canon seam's gate and the copy the plugin bundle inlines — and the duplication is kept because each copy is a leaf, with a parity test holding the two readers to one behaviour.** |
| H4 | KyberDash | reference | `docs/dash/telemetry-inventory.md`: the Factory rows — what the plugin measures, what the file source measures, and the honest token position (D8, D9). |
| H5 | KyberDash | runbook | `docs/dash/runbook.md`: enable and verify Factory capture, the consent verb, the live check, and the uninstall procedure. |
| H6 | KyberDash | reference | `docs/cli-command-surface.md`: `kyber capture enable/disable/status --harness factory`, `kyber capture plugin install --harness factory`, and the new `kyber capture plugin consent` verb (D10). |
| H7 | KyberDash | rule | `docs/rules/kyberdash-harness-collection.md`: the Factory row in the per-harness ground truth table — plugin hooks as the PRIMARY mechanism, file ingest as the token source, and the completeness predicate (D2, D8), plus the D28 split (D36). |
| H8 | KyberDash | onboarding | `docs/dash/onboarding.md`: the Factory capture plugin section (what it collects, what consent governs, how to turn it on). |
| H9 | KyberSquad | architecture | `docs/kyber-squad/architecture.md` and `docs/kyber-squad/onboarding.md`: the global Factory capture-plugin step (preserve mode) beside the existing OpenCode step, and the **marketplace tree under KyberDash's own state directory** that Squad does not own (**amended by D43** — the capture's owned subtree is no longer under `~/.factory/` at all) (D18). |
| H10 | KyberDash | reference | `docs/install.md` §Updating: the plugin step's Factory guidance line (D18). |
| H11 | KyberDash | architecture | `docs/dash/architecture.md`: the authorized native-OpenTelemetry path for Factory — `OTEL_TELEMETRY_ENDPOINT`, the `genai` attribute vocabulary, the `POST /v1/metrics` route, the plugin-first precedence rule (D23), the tokenless and lineage-free limits, and the organisation-override reporting rule (D26). |
| H12 | KyberDash | reference | `docs/dash/telemetry-inventory.md`: the Factory model-resolution chain (D24, as superseded by **D39**) and the child-session token pairing rule (D25), stated as the model and token facts the Factory rows carry — including the file source's own `unknown` placeholder (D40). |
| H13 | KyberDash | architecture | `docs/dash/architecture.md`: the Factory source-precedence rule (D28) — in a `droid` session holding both plugin rows and file rows, the plugin rows are the session's turns (count, timeline, per-turn model, tools, duration, context) and the file rows are the session's counters (the totals and the availability that gates them) and its cost; neither source is counted twice, and the rule is inert for every other harness. |
| H14 | KyberDash | reference | `docs/dash/telemetry-inventory.md`: the child-session token source (D29) — a child session's tokens come from the child's **own** companion file through the exact-id pairing, and the parent's `childInclusiveTokenUsageBySessionId` map is the parent's aggregate view and never a token source; and the plugin rows' `not_measurable` reasons name the file-source pairing (D30). H12's child-pairing half is superseded by this row. |
| H15 | KyberDash | architecture | `docs/dash/architecture.md` and `docs/dash/telemetry-inventory.md`: the native OTLP metrics destination (D31, as amended by **D38** and **D42**) — the decoded metric points are persisted in the canon store's **`metric_point`** table through the production sink's `upsertMetrics` and the `ingestMetricBatch` seam, one row per **distinct** decoded point (a point's identity is a content digest, so a re-delivered point lands on the row it already occupies); nothing projects them yet (a todo); the receiver activity records the batch as `otlp:metrics` with the points it carried; and the writer's rule is unchanged — a sink that cannot persist metrics is refused, never accepted-and-dropped. **The two documents' current "one row" shorthand is corrected by T20** (D42). |
| H16 | KyberDash | architecture | `docs/dash/architecture.md`: the status rule at the normalization seam (D32) — an error status is never downgraded, so the `droid` adapter's `error.type` reaches the stored record and the policy-denied tool call counts as an error; the rule is inert for every other harness. |
| H17 | KyberSquad | architecture | `docs/kyber-squad/architecture.md` and `docs/kyber-squad/onboarding.md`: the capture-plugin step's harness-aware shape (D33, extending H9) — one plugin call per capture-plugin harness, in a fixed order with the OpenCode call first and the Factory call following it, the OpenCode call handed its OWN resolved global root (`~/.config/opencode`) and the Factory call handed NONE (D35) — and the updater's Factory call (D34): the same verb in preserve mode with `--if-installed` and no root. **AMENDED (D65): the harvest must also correct the target document's own example**, which still shows the Factory call as `kyberdash kyber capture plugin install --harness factory --preserve --config-dir <Squad-resolved Factory root>` (`docs/kyber-squad/architecture.md:605`) — a command the Factory verb **refuses** with exit 2 (D35, D55), and which the shipped step does not run (`KyberDashCapturePluginStep.Arguments` adds `--config-dir` only for a non-null root, and the Factory call passes none: `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs:202-217`). The document's own next paragraph already states the correct rule, so the example contradicts the prose beneath it |
| H18 | KyberDash | reference | The `--config-dir` flag's scope (D35, **amended by D55**): it is the OpenCode config root; the Factory plugin-install verb refuses it (exit 2), and the Factory tree lands under **KyberDash's own marketplace root** (`~/.kyberdash/marketplaces/kyberdash/`), never under a Factory root — `$FACTORY_DIR` is honoured only by the file source, for the sessions tree. |
| H19 | KyberDash | adr | ADR 0031 amended in place (D49): the Factory writer owns a **marketplace tree** under `~/.kyberdash/marketplaces/kyberdash/` (not a plugin directory under `~/.factory/plugins/`), the install is **delegated to the harness's own commands** (`droid plugin marketplace add`, `droid plugin install … --scope user`), the **delegated-install clause** — an install the harness's own CLI performs is not KyberDash owning the harness's settings, and the owner's settings file changes by the harness's hand — and the owner's settings and hooks files are still never created, read, merged, rewritten or restored **by KyberDash** (D48). |
| H20 | KyberDash | adr | The new Factory ADR (H1) amended with the install route and the native path's fate (D43, D50): the capture plugin is installed through the harness's marketplace; the owned unit is the marketplace tree and the receipt; the native **span** claim is withdrawn with the group-vote arithmetic that made it inert, and the native **metrics** path (the `metric_point` destination) is what remains. |
| H21 | KyberDash | architecture | `docs/dash/architecture.md`: the Factory capture pipeline as it now stands — the plugin → OTLP → `droid` adapter → store → projection → `/api/kyber/*` path (unchanged) **plus** the install lifecycle: the Kyber-owned marketplace tree, the harness's own plugin commands, the state vocabulary (`marketplace-tree-written`, `marketplace-registered`, `plugin-installed`, `unknown`), the refusal vocabulary (`conflict`, `drift`, `unreadable`) and the residue rule (D43–D47). **AMENDED (D69): the state vocabulary is the INSTALL-side state the installer classifies; the status surfaces do not report it, because a status read never spawns the harness's CLI, and the row reports the tree's state only.** |
| H22 | KyberDash | runbook | `docs/dash/runbook.md`: the Factory install, enable, disable and uninstall procedure under the marketplace route; the fact that the harness's own commands write the owner's settings file; the residue rule and the exact command that clears it; and the live probe (T46) that establishes the harness-side semantics (D43, D45, D46, D47). |
| H23 | KyberDash | rule | `docs/rules/kyberdash-harness-collection.md`: the Factory row states the install route (the plugin is installed **through the harness's marketplace**, and the harness's own registry — not a directory under the Factory root — is the install evidence), the D28 split, and the fact that **the native path claims no span** (D43, D50). |
| H24 | KyberDash | reference | `docs/dash/telemetry-inventory.md`: the native export's **effective shape** — the format (`legacy` \| `genai`), the granularity (`user` \| `aggregate`), the message-content switch, the organisation's `enabled` master switch, and which of the organisation's settings pin each — and what KyberDash stores from it: the metrics in `metric_point`, **no native span**, and what the consent governs, stated as **what the consent governs and what it does not** (D51, D52, **amended by D57 and D58**: the earlier "no Factory content while the consent is off" is withdrawn, because the Factory **file source** stores the first user-message fragment whatever the consent file says). |
| H25 | KyberDash | reference | `docs/cli-command-surface.md`: the Factory plugin-install verb's route and its exit codes — the marketplace route, the refusal of `--config-dir` (exit 2, D55), the named refusal states (`conflict`, `drift`, `unreadable`) and the residue report (D37, D46, D47). |
| H26 | KyberDash | reference | `docs/dash/telemetry-inventory.md` and `docs/dash/runbook.md`: **the consent's scope, stated as what it governs and what it does not** (D57) — the plugin's prompt text is the only content the consent governs; the Factory **file source** stores the first user-message fragment (≤ 500 characters) whatever the consent file says, and the native route's content is the harness's own switch; and the **organisation-control position** (D60) — an organisation's `strictKnownMarketplaces`, `strictEnabledPlugins` or `allowManagedHooksOnly` can refuse the route or make it silently inert, KyberDash works around none of them, and an install the harness refused is reported with the harness's own stderr. |
| H27 | KyberDash | architecture | `docs/dash/architecture.md`: the Factory install lifecycle's **upgrade path** (D65) — a tree the receipt proves KyberDash wrote, whose bytes still match that receipt and whose content is not the current bundle, is an **upgrade**, not an owner edit and not `unchanged`; a tree no receipt proves is foreign, because matching an older bundle's bytes never establishes ownership; the upgrade rewrites the Kyber-owned tree and its receipt and then refreshes the harness's cached copy with `droid plugin update kyberdash-factory@kyberdash --scope user`; the refresh command is chosen from the harness's own registry, never `plugin install` against an installed plugin; and `unchanged` means **both** a current tree and an installed harness copy. The status surface's own word for such a tree is `plugin: installed (an older version than this build)`, which is distinct from `plugin: modified since install or not written by KyberDash`. |
| H28 | KyberDash | runbook | `docs/dash/runbook.md`: the routine Factory install against the upgrade — when KyberDash's tree is rewritten and the harness's cached copy refreshed (`upgraded`), when nothing happens (`unchanged`), and the exact command that refreshes the harness's copy when the harness refuses the refresh (D65). |
| H29 | KyberDash | reference | `docs/cli-command-surface.md`: the Factory plugin-install verb's **stdout tokens** — `installed`, `would-install`, `unchanged`, **`upgraded`** and **`would-upgrade`** — and the refusal reporting, which keeps a harness refusal from ever reading as a success (D65). |

## 3. The design, in detail

### 3.1 The owned plugin tree

**AMENDED (D43, D44) — the owned unit is a MARKETPLACE TREE under KyberDash's own state
directory, and the harness's plugin cache is the harness's file.**

```
~/.kyberdash/marketplaces/kyberdash/       # the registered marketplace (the directory name IS the name)
├── .factory-plugin/
│   └── marketplace.json    # the marketplace manifest: plugins[] names kyberdash-factory, source './kyberdash-factory'
└── kyberdash-factory/     # the plugin payload — the marketplace's source is a RELATIVE path
    ├── .factory-plugin/
    │   └── plugin.json     # Factory plugin manifest
    └── hooks/
        ├── hooks.json      # registers the script on the six hook events, keyed directly by event name (D53)
        └── kyberdash-factory.mjs # the bundled capture script
```

The install then runs the **harness's own commands** — `droid plugin marketplace add
~/.kyberdash/marketplaces/kyberdash`, then `droid plugin install kyberdash-factory@kyberdash
--scope user` — and the harness copies the payload into its own cache
(`~/.factory/plugins/cache/<marketplace-hash>/<plugin-hash>/<version>/`), writes its own
`installed_plugins` record, and enables the plugin by writing `enabledPlugins` into the owner's
`~/.factory/settings.json`. **KyberDash writes none of that.** The `${DROID_PLUGIN_ROOT}` the
hook command uses expands to the **cached** path, so the script is found wherever the harness put
it.

Alongside it, under `~/.kyberdash/` (KyberDash's own state directory, never the owner's Factory
tree):

| Path | Owner | Meaning |
|---|---|---|
| `~/.kyberdash/factory-plugin-receipt.json` | KyberDash | `{ "version": 1, "marketplace": "kyberdash", "plugin": "kyberdash-factory@kyberdash", "root": "<marketplace dir>", "files": [{ "path": ..., "sha256": ... }] }` |
| `~/.kyberdash/factory-plugin.json` | KyberDash | consent: `{ "version": 1, "content": false }` |
| `~/.kyberdash/capture/factory/spool.jsonl` | KyberDash | bounded file spool (500 events / 4 MB, drop-oldest) |
| `~/.kyberdash/capture/factory/diagnostics.log` | KyberDash | bounded 64 KB diagnostic tail |

The plugin payload may carry `.factory-plugin/plugin.json`, `hooks/hooks.json`, `mcp.json` and
`droids/`; KyberDash ships only the manifest and the hooks, and never writes `mcp.json` or
`droids/`. **The plugin root may no longer live under `~/.factory/plugins/`** — that directory is
not a discovery path (A1, §2.6), so the subtree leaves the owner's Factory home entirely and
cannot collide with Squad's `~/.factory/droids/` or `~/.factory/skills/` at all.

### 3.2 The hook → span mapping

| Hook event | Span | Attributes (contract `factory.contract_version = 1`) | Content |
|---|---|---|---|
| `SessionStart` | `factory.session` | `factory.session.id`, `factory.cwd` (path only) | none |
| `UserPromptSubmit` | `factory.turn` (op `llm.invoke`) | `factory.session.id`, `factory.message.id`, `factory.turn.prompt.text` (capped) | prompt text, gated |
| `PreToolUse` | `factory.tool` (start) | `factory.session.id`, `factory.tool.name`, `factory.tool.call_id`, `factory.tool.input_bytes` | none — the **size** of the tool input, never the input itself (D61) |
| `PostToolUse` | `factory.tool` (end; same span id) | plus `factory.tool.status`, `factory.tool.error_type`, `factory.tool.result_bytes` | none — the **size** of the tool result, never the result itself (D61) |
| `Stop` | `factory.turn` (end) | `factory.session.id`, `factory.message.id` | none |
| `SubagentStop` | **no span (D54)** | — the documented payload carries `task_name`, `task_result`, `task_error` and `stop_hook_active`, and **no child session id**; the common `session_id` is the **parent's** session, so a span here would name the parent as the child | none |
| `SessionEnd` | `factory.session` (end) | `factory.session.id` | none |
| `PreCompact` | not collected in v1 | — | — |

Every attribute the payload does not carry is **absent**, never zero and never empty string.

**The model (D24, as shipped — D39).** The chain has three steps, and they belong to three
different readers. (1) **The plugin** names a model only when the hook payload carries one:
the mapper emits `factory.model.id` only then, and the shipped bundle reads the hook payload and
nothing else. Factory's own hook payload documents no `model` field, so a plugin-sourced turn
generally names none — and an absent model stays absent all the way down, because the adapter
never defaults one. (2) **The file source** reads the session's `model` from the companion
per-session file `<session-id>.settings.json` beside the transcript — read-only, and never
written — and publishes it **wrapper-stripped**. (3) **The session** publishes **one** model:
the model its **turn records** name, and where no turn record names one, the **counter
record's** model. A turn's own model always wins, so a paired session publishes one model and
never two; a session whose sources named none publishes **none** — never a default, never a
guess. `~/.factory/settings.json` is never a model source: it has no top-level `model` key at
all, its model lives in nested per-role blocks, and reading one would attribute a subagent's
model to its parent.

**The `unknown` placeholder (D40).** A companion file that names no model leaves the file
source with its own long-standing placeholder, the literal `unknown` — the file source's own
string, not a measured model, and one the plugin path never produces. It is pre-existing
file-source behaviour outside this plan's scope (D21), and this plan does not change it; it is
named here so that no reader mistakes it for a measurement.

**Child sessions (D25, amended by D29 and D54).** The plugin's `SubagentStop` event carries **no child
session id** — the documented payload names `task_name`, `task_result`, `task_error` and
`stop_hook_active`, and the common `session_id` is the session the hook fires in, which for a
subagent's stop is the **parent's**; the event therefore produces no span (D54). The parent
linkage is an exact match only, and a child that cannot be linked stays an unlinked child rather
than a fabricated one. A child session's tokens are paired from the **child's own** companion file
`<child-id>.settings.json`, through the same exact-id pairing as any other Factory session: a
child session is a first-class Factory session with its own transcript and its own companion file,
so the file source discovers it as its own session and the plugin's child rows pair with it. A
child with no companion file of its own reads `not_measurable` with a stated reason, and never
borrows the parent's counters. The parent's `childInclusiveTokenUsageBySessionId` map is the
parent's aggregate view of its children, not the child's own usage: it is never a token source, and
a child's usage is never folded into the parent's session totals (A9).

### 3.3 Install, status and the Settings row

`capture status --harness factory` and `GET /api/kyber/capture` report, for Factory: the plugin
state (`on` / `partial` for an older plugin of ours / `off` / `unknown` for an edited, foreign or
unreadable tree), the receipt state, the resolved plugin path, the consent state, the install
evidence, and the receiver liveness — the same fields the OpenCode row reports, plus the resolved
plugin directory for a directory-shaped plugin. **AMENDED (D43, D47): the plugin state now has
TWO axes** — the state of the Kyber-owned marketplace tree (**AMENDED (D61): `absent` /
`unreadable` / `foreign` / `edited` / `same`** — the shipped classifier's five kinds, `install.ts:317-324`;
this plan's earlier list named `older` and `newer`, which the code has never had, and omitted
`unreadable`, which it has) and the state of the **harness-side** install
(`marketplace-tree-written` / `marketplace-registered` / `plugin-installed` / `unknown`), the
second read through the harness's own CLI. The row reports the marketplace name and the plugin
identity it installed, so a reader can see what the harness holds without looking at the owner's
settings file.

**AMENDED (D65) — the tree axis gains an UPGRADE state, and the harness's cached copy gains a
refresh.** The classifier's five kinds become six: `absent` / `unreadable` / `foreign` / `edited` /
`same` / **`upgrade`**. A tree is `upgrade` when the receipt proves KyberDash wrote it, every
file on disk still matches that receipt's digest, and the tree's content is not the current
bundle — an older bundle of ours, still ours, and safe to replace. A tree **no receipt proves**
is `foreign`, however much it resembles an older bundle of ours, because matching an older
bundle's bytes never establishes ownership; a tree whose bytes no longer match the receipt is
`edited`, and the owner's edit stays the owner's. The install rewrites the tree and the receipt
with the current bundle and then asks the harness to refresh its **cached copy**: `droid plugin
update kyberdash-factory@kyberdash --scope user` when the plugin is installed, the install path
when it is not, and the marketplace registration restored first when an installed plugin's
registration is missing. `plugin install` is never used against an installed plugin. The install
reports `upgraded` — and `would-upgrade` in a dry run — and **`unchanged` now means both a
current tree and an installed harness copy**, so a routine install runs no harness command at all
and mutates nothing (D65).

The row also states, as fixed facts and never as an echo of owner text: that native
OpenTelemetry export is configured by machine environment variables (`OTEL_TELEMETRY_ENDPOINT` and
its siblings), that an organisation-managed `telemetry` block overrides them, and that a
deployment under such an override exports nothing locally — so where no native span arrives, the
row reports the native path as **unavailable** with that named cause, never as working (D26). The
plugin path is unaffected by the native path's presence or absence. **AMENDED (D60): an
organisation's hard controls can make the whole route unavailable, and the row says so.** An
organisation's `strictKnownMarketplaces` makes its list the ceiling for marketplace sources, its
`strictEnabledPlugins` makes the `enabledPlugins` map the exhaustive plugin allowlist — so a
user-scope install is **rejected** — and its `allowManagedHooksOnly` drops user- and project-level
hooks, so a user-scope plugin can install and register as enabled and **never fire a hook**. The
first two are visible: the harness's own command fails, and KyberDash reports the refusal with the
harness's stderr quoted. The third is silent, and KyberDash cannot detect it from the harness's
own CLI; that gap is filed as a todo, and the row must not read "installed" as "capturing" (D62).

**AMENDED (D50, D51, D58) — the native report is the export's EFFECTIVE SHAPE, and it says that no
native span is claimed.** The row reports the **format** (`legacy` or `genai`, with the
organisation's `telemetry.format` winning over `OTEL_TELEMETRY_FORMAT`), the **granularity**
(`user` or `aggregate`, resolved most-restrictive-wins, and **a value that cannot be read resolves
to `aggregate`**), the **message-content switch** (`OTEL_LOG_MESSAGE_CONTENT`, or the
organisation's `telemetry.logMessageContent`, with `aggregate` overriding both), and the
organisation's **`enabled`** master switch, beside the endpoint verdict — because each of them
changes what the export sends, and the last one decides whether it sends anything at all. It then
states, in fixed words, one per consent branch: *Factory's native metrics are persisted; Factory's
native spans are not claimed; while KyberDash's consent is off, KyberDash's own Factory capture
stores no prompt text* — and, when the consent is on, *… while KyberDash's consent is on, KyberDash's
own Factory capture stores the plugin's prompt text*. **Both branches name the capture, not
"Factory content"** (D58).
The row never says the native path is "working": it says what the export is configured to send and
what KyberDash does with it.
**AMENDED (D63) — WHAT OF THAT HAS SHIPPED, AND WHAT HAS NOT.** The **format**, the **granularity**
and the **message-content switch** shipped, and the row reports them (D51). **The organisation's
`enabled` master switch and the granularity's unreadable-value fallback did not** — the shipped
reader resolves an unreadable granularity to `user` (`native-export.ts:334`), which is the
fail-open direction (D58) — **and the corrected fixed words did not**: the shipped row still reads
*"no Factory content is stored while KyberDash's consent is off"* (`factory.ts:147-151`). **T47/T48
carry those three**, and until they land the row is the false one below. The paragraph above states
the **required** row, not the shipped one.

**AMENDED (D57) — what the consent governs, stated as what it governs and what it does not.** The
fixed words above are **corrected**: the shipped row said *no Factory content is stored while
KyberDash's consent is off*, and that sentence is **false**. The consent file is read in one place
— the plugin's own hook runner — and it governs one emission: the plugin's prompt text. **Two
routes carry Factory content outside its reach, and the row names them rather than implying a
store-wide silence.** The **Factory file source** reads the first user message fragment out of the
owner's own transcript — up to 500 characters — and stores it on every file-sourced Factory record's
raw attributes, whatever the consent file says; that is the owner's own transcript read by
KyberDash's file source, and it is **not** a capture of the plugin's making. The **native route's**
content is the harness's own switch, not KyberDash's consent: it is present when the harness is
configured to send it, and the gate over that route is T42/T43's work, which has not landed. **The
plugin's tool arguments and tool results are never content at all** — the contract publishes byte
counts, so there is nothing there for the consent to govern (D61).

**The paragraph below describes the row as it ships (D41).** The native-export availability
reporting **is** part of the Factory row: the row carries the native OpenTelemetry export's
state — available, unavailable with the named cause, or not configured — on every surface the
row reaches (the printed status, the structured row and the API route), because they all read
the row's own status description (`dash/src/capture/harnesses/factory.ts:71-85`, `:100-113`,
`:121`). D37's "not shipped yet" marker was right when it was written and is removed here: it
told a reader not to infer anything from the row's silence, and the row no longer is silent.
Nothing about the native path may be inferred from the row's silence — but the row now states
the state, so there is no silence left to infer from.

### 3.4 The native-OpenTelemetry group (authorized — D12 branch A; **the span claim is withdrawn — D50**)

Factory's native export **can** be pointed at the KyberDash receiver: `export
OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"` is Factory's own documented example, and Droid
pushes OTLP/HTTP directly, so no collector is needed. **AMENDED (D50): the export is metrics only
by default**, so the receiver's metrics path is the part that earns its keep, and the span path is
withdrawn below. Two additions made the group usable:

**The receiver.** A `POST /v1/metrics` path beside `/v1/traces` and `/v1/logs`: OTLP/JSON metrics
decoded to a metrics shape and handed to a metrics sink port with the same admission, shedding and
health-window discipline as the span path (`dash/src/otel/receiver.ts:1119-1121,1370-1386`). Only
OTLP/HTTP is documented for Factory — port 4318, no gRPC, no 4317 — so no other protocol is
added. **Logs are never exported by Droid**, so `/v1/logs` is untouched by this branch.

**Where the metrics go (D31, as shipped — D38, D42).** The metrics sink port is backed by the
**canon store**: the production sink implements `upsertMetrics`, which writes the decoded metric
points to the canon store's own **`metric_point`** table — one row per **distinct** decoded
point, since a point's identity is a content digest of everything the row carries, so two
byte-identical decoded points collapse into one row and a re-delivered point lands on the row it
already occupies — carrying the metric's name, kind, temporality, the point's timestamp and
value (or its count and sum), the point's attributes, the resource attributes and the scope —
and then records the batch as receiver activity under the source `otlp:metrics`, with the points
the batch carried. The writer's rule is unchanged: a sink that cannot persist metrics does not
offer the port, and the batch is **refused**, never accepted and dropped. **Nothing projects
metrics yet** — the canon projection and the dashboard have no metric concept, and the metrics'
projection is a todo, not part of this branch. The table is a destination that keeps the values;
the projection that would read them is later work.

**The native claim — WITHDRAWN (D50).** **HISTORICAL (pre-T41):** the adapter **once** recognised the
native `genai` fingerprint by its **agent identity alone** — `gen_ai.agent.name === 'droid'` — and
that is the claim this paragraph withdraws. **The shipped adapter carries no native claim at
all**: its own header says "ONE claim path reaches this adapter: the Kyber-owned Factory plugin
envelope", and it states that "Factory's OWN native OpenTelemetry export scores 0 whatever it
carries" (`dash/src/canon/adapters/droid.ts:6-21`, `:243-252`). **The vocabulary the withdrawn
claim was written against is below, and it is Factory's own, but it is a record of what the claim
would have matched — not a description of the shipped adapter.** The measurement showed the claim
is **inert by construction**:

| Aspect | Value |
|---|---|
| Root span | `invoke_agent droid` (Internal) |
| Children | `chat <model>` (Client); `execute_tool <tool>` (Internal) — tool spans are **siblings** of the chat spans, never nested |
| Operation | `gen_ai.operation.name` ∈ `invoke_agent` \| `chat` \| `execute_tool` |
| Agent | `gen_ai.agent.name` = `droid`, **on the root only** |
| Session | `gen_ai.conversation.id` = the Droid session id; `session.id` on every data point |
| Model | `gen_ai.request.model` (chat span) |
| Provider | `gen_ai.provider.name` ∈ `anthropic` \| `openai` \| `gcp.gemini` \| `x_ai` \| `deepseek` \| `mistral_ai` |
| Tool | `gen_ai.tool.name`, `gen_ai.tool.call.id`, `gen_ai.tool.type` = `function` |
| Outcome | `error.type` = `rejected` for a policy-denied tool call; `gen_ai.response.finish_reasons` (array) |
| Server | `server.address`, `server.port` |
| Content (gated) | `gen_ai.input.messages` (root), `gen_ai.output.messages` (chat), `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result` |
| Common (every data point) | `user.id`, `user.email`, `organization.id`, `session.id`, `model.id`, `parent_session.id` |
| Resource | `service.name` = `cli`, `service.version`, `os.type`, `os.version`, `host.arch`, `terminal.type`, `telemetry.granularity` |

**Why the claim is withdrawn.** The registry votes per group: the winning adapter's evidence
divided by the group's span count, against a threshold of `0.6`
(**AMENDED (D61): the constant is at `dash/src/canon/adapters/registry.ts:34`, not the `:38` this
paragraph cited** — `DEFAULT_CONFIDENCE_THRESHOLD = 0.6`; the group's normalised score is computed
in `scoreGroup` at `:104-128`). The `gen_ai` trace is **flat** — one root plus its
children — so a turn's trace of one root and `k` children scores `1/(1+k)`. A real turn has
`k ≥ 1`, so it scores at most **0.5** and the whole group is **quarantined**; the measured probe
reported **accepted 0, quarantined 2**, with the droid adapter scoring the group **[1, 0]** — the
root carries `gen_ai.agent.name`, and the `chat` span does not. The `legacy` format is no better:
its `droid.message.*` / `droid.tool.*` spans carry no attribute fingerprint at all, so every
adapter scores them 0 and they are quarantined as `unclaimed` — and they exist only when
message-content logging is on. **The claim is therefore removed from the branch** (T40, T41),
together with the ingest's native-supersede logic and the native counter reasons it served; the
metrics path, the receiver's `/v1/metrics` route and the `metric_point` destination are untouched.

**AMENDED (D61): where a quarantined Factory span's content goes.** With the claim withdrawn, a
native Factory span's **whole decoded packet** is kept as the quarantine row's audit evidence
(`dash/src/canon/ingest.ts:401`, `:461-471`), and **that packet is where its content-bearing
attributes are**. The packet is bounded at **256 KiB each**
(`MAX_QUARANTINE_RAW_PAYLOAD_CHARS`, `dash/src/canon/ingest.ts:360-366`), which bounds **one
packet**, not the table: **no bounded retention or re-quarantine policy for the quarantine rows
themselves was identified** — the rows are cleared only by a scoped or full wipe
(`dash/src/canon/store.ts:154`, `:5008-5010`). With the native claim withdrawn, every native
Factory span the harness sends lands in that table, so its **row count** is this branch's own
consequence, and it is unbounded (D62).

**What would have to be true to restore it** (recorded as a todo, not as work): a **group-scoped**
claim — a trace whose root declares `gen_ai.agent.name = 'droid'` is Factory's own trace, so its
children are claimed with it — which means a change to the **shared** attribution contract
(`dash/src/canon/adapters/base.ts`, `registry.ts`) whose only beneficiary would be this one
harness; a model chain that lets a native per-turn model reach the dashboard (D23 excludes it
today); and a consent gate over the native content (D52, D57 — **the gate covers the native and
quarantine routes, and the file source's own first-message fragment stays outside it**). The
arithmetic, the measurement and the conditions are in D50 and A15.

**Precedence and no double counting (D23) — retained, inert (D50).** The plugin envelope is
claimed first. A native span
is claimed only when its session has no plugin rows; a native span for a session that already has
plugin rows is attributed as corroborating evidence and **excluded** from that session's turn,
tool and duration totals. A native span is distinguished from a plugin row by fingerprint, never
by name: the plugin envelope carries `factory.contract_version = 1` and a `factory.` attribute; a
native span carries `gen_ai.agent.name = 'droid'` and no `factory.` attribute. **AMENDED (D50): with
the native claim withdrawn, this rule governs nothing today; it is kept as the rule that applies
to any native claim that is ever restored, and the ingest's native-supersede logic goes with the
claim it served.**

**What the native path cannot do.** It carries **no token or cost counters** (D12), and it carries
**no subagent lineage** (D2). It therefore complements the plugin rows and never replaces them.

**Where it is silently absent (D26) — and what the row says instead (D51).** An organisation-managed `telemetry` block overrides the
machine environment variables, so a deployment whose organisation pins an endpoint exports nothing
locally. KyberDash reports that state as observed — native export unavailable — and names the
override as the cause; it never reports the native path as working when no native span arrives.
On this machine no organisation pins an endpoint (§2.5), so the caveat is real but not universal.
**AMENDED (D51):** the row now reports the export's **effective shape** — format, granularity and
the content switch beside the endpoint verdict, each resolved with Factory's own precedence rules
— and states that the metrics are persisted, that **no native span is claimed**, and — in the words
D58 fixes — that while KyberDash's consent is off, **KyberDash's own Factory capture stores no
prompt text**. **AMENDED (D57):** the earlier *no Factory content is stored* was **false**, because
the Factory **file source** stores the first user-message fragment whatever the consent file
says; the row names that route separately as one the consent does **not** govern.

### 3.5 The paired Factory session (D28, D29, D30)

One Factory session is observed by two sources. The **plugin** observes the turn boundary, the
tools, the timing and (under consent) the prompt; it observes **no counters at all**. The **file
source** observes the counters, the cost and the model; it observes **no turn boundary** and **no
reply** — **AMENDED (D57): it does observe the user's first message fragment, up to 500
characters** (`dash/src/providers/droid.ts:165-176`, `:287`), which it carries on the record's raw
attributes and the store projects as a named attribute
(`dash/src/canon/store.ts:2433`). That fragment is the owner's own transcript read by KyberDash's
file source; it is **not** governed by the capture plugin's consent file, and this plan does not
change it (D21). The two are therefore never joined and never merged into one turn: they are
**split by what each can measure**.

| The session's figure | Read from | Why |
|---|---|---|
| turn count, timeline turns, request roots, per-turn model (where the plugin turn names one), tool invocations, duration, context turns | the **plugin rows** | the plugin is the PRIMARY mechanism (D2) and alone observes the turn boundary, the tools and the timing |
| token totals (input, output, cacheRead, cacheCreation) and the availability that gates them | the **file rows** | the file source is the only source of counters (D9), and its counters are a session total split evenly across assistant calls — a session-level fact, never a turn-level one |
| cost | the turn records **and** the counter records | the file row carries the only priced cost; the two sets are disjoint, so no cost is counted twice |
| the session's published **model** | the **turn records'** model, else the **counter record's** model, else none | the model comes from the source that measured it (D39): a turn's own model always wins, the file source's model stands in when no turn record names one, and a session whose sources named none publishes none — never a default, never a guess |

The file row in a paired session is a **counter carrier**: it contributes its counters and its
cost and nothing else — it is not a turn, not a timeline entry, not a request root, not a tool
invocation, and its duration is not added to the session's duration. A plugin-only session keeps
the plugin's own declarations, so its totals read `not_measurable` with the reason D30 fixes. A
file-only session is unchanged. Every other harness is unchanged: the rule is inert outside
`droid`.

The rule is exact-match only — no fuzzy join, no time window, no counter-equality join — and it
never fabricates: a paired session's totals are the file row's counters, and a plugin-only
session's totals are absent, never zero.

A **child** session is paired the same way, from the child's own companion file (D29), and the
plugin's `not_measurable` reasons name this pairing (D30).

**The model, in a paired session (D39).** The two sources do not measure the model the same
way. The plugin's turn names one only when the hook payload carries one — and Factory's own hook
payload documents no `model` field, so it generally names none. The file source names the
session's model from its companion file and publishes it wrapper-stripped. The session therefore
publishes the turn's own model where it has one, the file source's model where no turn record
names one, and **none** where no source named one. Where the companion file names no model at
all, the file source's own `unknown` placeholder is what it publishes — a literal of the file
source's, not a measured model (D40).

## 4. Test contract (test-first)

Every unit has a RED task (tests only) and a GREEN task (implementation), authored by different
workers. RED evidence must show the contract test existed and failed for the intended missing
behaviour before the implementation landed; GREEN uses the same test, unweakened.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/capture/harnesses/factory-plugin/contract.test.ts`, `mapper.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/contract.test.ts src/capture/harnesses/factory-plugin/mapper.test.ts` | The contract's key set, span names and id derivation are stable; each hook event maps to at most one span; absent payload fields stay absent; a payload with no model carries no model attribute | The two files exist and fail against the empty module | Same tests pass, unweakened |
| T2 | same as T1 | same as T1 | Production contract and mapper exist and satisfy T1 | — | T1 green |
| T3 | `dash/src/capture/harnesses/factory-plugin/entry.test.ts`, `dash/src/capture/harnesses/transport/*.test.ts`, `dash/src/capture/consent-file.test.ts`, `dash/src/architecture/factory-leaves.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/entry.test.ts src/capture/harnesses/transport src/capture/consent-file.test.ts src/architecture/factory-leaves.test.ts` | The hook runner reads stdin, exits 0 on any input (including malformed JSON), never writes to stdout except the hook protocol, honours the consent file, drains and bounds the spool; the shared modules stay import-free leaves; the OpenCode bundle still bundles to the committed bytes | The new files exist and fail; the OpenCode drift test still passes (no regression) | All green, OpenCode drift test green |
| T4 | same as T3 | same as T3 | The shared transport/consent modules exist, the OpenCode modules are shims, the Factory entry exists | — | T3 green; `bundle-opencode-plugin.mjs --check` clean |
| T5 | `dash/src/capture/harnesses/factory-plugin/consent.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/consent.test.ts` | A consent file is on only for `{version:1,content:true}`; missing, oversized, malformed or unknown-version read as off; the env override wins | The file exists and fails | Same test passes |
| T6 | same as T5 | same as T5 | The Factory consent reader and the shared consent writer exist | — | T5 green |
| T7 | `dash/src/capture/harnesses/factory-plugin/install.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/install.test.ts` | Install writes the whole tree and the receipt; a foreign or edited tree is refused in strict mode and kept in preserve mode; an absent tree with `ifInstalled` writes nothing; uninstall removes the tree only when every digest matches; `~/.factory/settings.json` and `~/.factory/hooks.json` stay byte-identical through install, enable, disable and uninstall (**AMENDED (D45, D48): the settings file's criterion becomes "KyberDash writes nothing to it and reads nothing from it" — T36**; the hooks file's criterion stands); a dry run writes nothing | The file exists and fails | Same tests pass, unweakened |
| T8 | same as T7 | same as T7 | The Factory installer, receipt reader/writer, state and status lines exist | — | T7 green |
| T9 | `dash/src/capture/harnesses/factory.test.ts`, `dash/src/capture/status.test.ts` (Factory cases), `dash/src/capture/detect.test.ts` (Factory probes), `dash/src/capture/harnesses/factory-plugin/install-command.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory.test.ts src/capture/status.test.ts src/capture/detect.test.ts src/capture/harnesses/factory-plugin/install-command.test.ts` | The registry holds a Factory writer; `capture status` lists it with plugin, receipt and consent state; `enable` installs and `disable` removes without touching the settings file; the Settings API reports the same row; `capture plugin install --harness factory` accepts the id and rejects an unknown one with exit 2; the status row also reports the native path as **unavailable** with its named cause when the organisation override or the machine environment yields no native span (D26, D37) | The new files/cases exist and fail | Same tests pass; the OpenCode registry and status cases stay green |
| T10 | same as T9 | same as T9 | The Factory writer, its registry row, the detect probes, the status lines and the plugin-install verb exist | — | T9 green |
| T11 | `dash/src/canon/adapters/droid.test.ts`, `dash/src/canon/adapters/droid-envelope.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/adapters/droid.test.ts src/canon/adapters/droid-envelope.test.ts` | The adapter claims only the Factory plugin envelope under contract `1`; an unknown contract version scores 0 and quarantines; **AMENDED (D50): a native `gen_ai.agent.name=droid` span is NOT claimed at all** — the native branch is withdrawn, so the earlier "claimed only when the native branch is authorized" is replaced by "quarantined as `unclaimed` whatever it carries"; a plugin record without paired tokens declares `not_measurable` with a reason, never 0; the adapter stamps `harness: 'droid'` | The files exist and fail | Same tests pass; the OpenCode adapter cases stay green |
| T12 | same as T11 | same as T11 | The `droid` adapter, the envelope leaf and the `ADAPTERS` registration exist | — | T11 green |
| T13 | `dash/src/canon/sessions.droid-pairing.test.ts` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/sessions.droid-pairing.test.ts` | A plugin row and a file row with the same session id form one session and the file row's tokens are the session's; different ids stay separate and the plugin row's tokens read `not_measurable` with a reason; no token is invented; a child session whose id is in the companion file's `childInclusiveTokenUsageBySessionId` is paired, and one that is not reads `not_measurable` with a stated reason (D25) | The file exists and fails (or passes with **no production change** — see the task note) | The pairing rule holds; a needed production change is a scope-checked edit, not a weakening. **The scope decision this cell required has been made: D28, D29 and D30, and the follow-on work is T22–T26. The child clause of this row's observable behaviour is superseded by D29** — the child is paired from its **own** companion file, not from the parent's child-usage map — and the two child cases move to T26. |
| T14 | `dash/src/providers/droid.test.ts` (Factory token cases), `dash/src/refresh/refresh.integration.test.ts` (droid job) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/providers/droid.test.ts src/refresh/refresh.integration.test.ts` | `dash refresh` still yields a `droid` session whose tokens come from the companion `*.settings.json` `tokenUsage`; the refresh job is unaffected by the plugin work | The new cases exist and fail or pass unchanged | The file source is proven unchanged by the plugin work |
| T15 | `dash/src/otel/receiver.test.ts` (metrics path cases), `dash/src/canon/adapters/droid.test.ts` (native claim) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/otel/receiver.test.ts src/canon/adapters/droid.test.ts` | `POST /v1/metrics` is served and answers 200/4xx per the existing discipline; a wrong path still 404s; **AMENDED (D50): a native span carrying `gen_ai.agent.name=droid` and `gen_ai.operation.name` is NOT claimed** — it is quarantined as `unclaimed`, and the D23 precedence rule now reads as "a native span is never counted", which is strictly stronger than the old "excluded from that session's turn, tool and duration totals"; a native span carries no token counter, so no token is taken from it | The cases exist and fail | Same cases pass |
| T16 | same as T15 | same as T15 | The metrics route exists; **AMENDED (D50): the native claim does not** — the native branch is withdrawn and the metrics path is the whole of what remains of the native path | — | T15 green |
| T17 | `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs`, `tests/KyberWeave.Tests/UpdateCommandTests.cs` | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~SquadCapturePluginStepTests|FullyQualifiedName~UpdateCommandTests"` | A global Factory deploy runs `kyberdash kyber capture plugin install --harness factory --preserve` and names no root (D35); `kyber-weave update` runs `kyberdash kyber capture plugin install --harness factory --preserve --if-installed` and names no root (D34); neither changes the command's exit code; a missing KyberDash is a skip, not a failure | The new cases exist and fail | Same cases pass; the OpenCode step cases stay green |
| T18 | same as T17 | same as T17 | The step and the updater run the Factory plugin verb | — | T17 green; the step runs one plugin call per capture-plugin harness, in a fixed order, the OpenCode call with its own resolved global root and the Factory call with none (D33, D35), and the OpenCode call is byte-identical to what it was (D33) |
| T19 | `dash/e2e/harness-collection.spec.ts` (Factory replay scenario) | `scripts/proc-guard.sh run --timeout 600 -- npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/harness-collection.spec.ts` | A recorded Factory hook payload, replayed through the production plugin → receiver → `droid` adapter → projection → dashboard, renders a Factory session with model and tool attribution and honest token state | The scenario exists and fails | The scenario passes with the isolated config; the other scenarios stay green |
| T20 | governed documents | `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .` | Every H-row's target document carries the durable item, including the D28 split in the collection rule (D36) and the `--config-dir` scope in the command surface and the runbook (D35); the closeout claims the status row's native-path availability reporting, which ships (D41), and the two metrics documents state the point's content-digest identity rather than a bare "one row" (D42); the plan moves to the archive folder with its closeout mapping | The documents fail the harvest checks | Both commands report zero findings |
| T21 | **No automated test — this is the manual, owner-run live verification** that replaces one. Read-only procedure documented in `docs/dash/runbook.md`; evidence written to the gitignored `artifacts/evidence/factory-capture-live/<UTC-stamp>/` | `scripts/proc-guard.sh run -- npm --prefix dash run dev -- kyber capture status --harness factory`, then one scripted Droid turn, then the dashboard | A live Droid turn produces a dashboard-visible Factory session with model and tool attribution; tokens are paired from the file source or honestly `not_measurable`; content is absent unless the consent file says `content: true`; the check records which of A1–A5 and A8 held | n/a — manual verification | The runbook procedure is executed and its result reported; a failure is reported as a failure |
| T22 | `dash/src/canon/sessions.droid-pairing.test.ts` (amended), `dash/src/canon/droid-pairing.test.ts` (new) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/sessions.droid-pairing.test.ts src/canon/droid-pairing.test.ts` | A paired session carries the FILE row's counters **and** counts ONE Factory turn (the plugin's), and keeps the file row's cost; a file-only session's own turns, counters and cost are unchanged; the split helper returns the plugin rows as the turn records and the file rows as the counter records for a mixed `droid` session, and no split for a plugin-only, file-only or non-`droid` session (D28) | The amended cases exist and fail on the turn count and the cost; the leaf's test fails against the absent module | Same tests pass, unweakened; the six cases that already passed still pass, and the child-pairing case stays RED until T26 corrects its fixture |
| T23 | same as T22 | same as T22 | The source-precedence rule exists in the projection and is inert for every other harness | — | T22 green; `dash/src/canon/sessions.test.ts` and the whole dash unit suite stay green |
| T24 | `dash/src/canon/adapters/droid.test.ts` (the plugin-path reason case), `dash/src/canon/sessions.droid-pairing.test.ts` (the plugin-only session's published reason) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/adapters/droid.test.ts src/canon/sessions.droid-pairing.test.ts` | Every one of the plugin adapter's four counter-class reasons names the file source and the session-id pairing, while still naming the harness and the metric; the session's published reason names it too (D30) | The strengthened assertions exist and fail against the shipped wording | Same tests pass; the native path's reasons are unchanged |
| T25 | same as T24 | same as T24 | The adapter's four reasons carry D30's wording | — | T24 green |
| T26 | `dash/src/canon/sessions.droid-pairing.test.ts` (the two child cases) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/sessions.droid-pairing.test.ts` | The child case's fixture is the real Factory shape — the child session is its own session with its own companion file carrying the child's usage, and the parent's companion file keeps its child-usage map so the parent's own counters stay the parent's own — and the child session carries the child's own usage, never the parent's (D29); the unmapped child still reads `not_measurable` with a reason | The corrected fixture is committed with the evidence that it passes against the projection as D28 specifies — a test-only correction, no production change | The child cases pass; `dash/src/providers/droid.test.ts` is unchanged and green |
| T27 | `dash/src/canon/metric-ingest.test.ts` (new), `dash/src/otel/service.test.ts` (the production-wiring case), `dash/src/otel/writer.test.ts` (the metrics cases) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/metric-ingest.test.ts src/otel/service.test.ts src/otel/writer.test.ts` | The canon store persists the decoded metric points, one row per **distinct** data point (a point's identity is its content digest — D38, D42), with no value dropped and none fabricated; a `POST /v1/metrics` against the **running collector service** answers 200 and the points are in the store; a writer whose sink cannot persist metrics **refuses** the batch and queues nothing; the batch is recorded as receiver activity under `otlp:metrics`; a metrics-only commit moves the receiver's last-persisted fact; a metrics batch requests no projection (D31) | The cases exist and fail: the production-wiring case answers 500, the store has no table, the writer's refusal is unpinned | Same tests pass, unweakened |
| T28 | same as T27 | same as T27 | The metrics destination exists: the `metric_point` table (D38), the canon metric ingest, the production sink's `upsertMetrics`, and the persisted-fact rule that counts metrics | — | T27 green; the schema version does **not** change (an `IF NOT EXISTS` table needs no migration step); an existing store opens unchanged; the metric rows carry the exporter's source and no harness attribution; the writer's refusal path is unchanged |
| T29 | `dash/src/canon/ingest.test.ts` (the stored-record case) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/ingest.test.ts src/canon/adapters/droid.test.ts` | A native Factory span carrying `error.type` is stored with the status `error`, even when the exporter's own OTLP status is `unset` or `ok`; a span whose exporter status is `error` is still stored as `error`; a span with no error evidence keeps the exporter's status code exactly (D32) | The stored-record case exists and fails (the status reads `unset`/`ok`); the adapter-level case still passes | Same tests pass, unweakened; every other harness's stored status is unchanged |
| T30 | same as T29 | same as T29 | The status rule exists at the receiver-timing seam | — | T29 green; `growWithReceiverTiming`'s doc states that it gives the outcome only when the adapter did not determine one |
| T31 | `tests/KyberWeave.Tests/UpdateCommandTests.cs` (the three legacy reconciliation fixtures, and the Factory updater call's mode) | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~UpdateCommandTests"` | The three legacy reconciliation fixtures expect the two-plugin sequences and name each plugin entry's harness; the Factory updater call is pinned to the exact argument list including `--preserve`; no other assertion in the file moved (D33, D34) | The three legacy cases fail today on the stale counts — the escalation | Same tests pass, unweakened: the fixtures' substance is unchanged and now stated |
| T32 | `dash/src/capture/harnesses/factory-plugin/install-command.test.ts`, `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs` | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/install-command.test.ts` | The Factory verb **refuses** `--config-dir` with exit 2 and writes nothing; the Factory step call names no root (D35) | The cases exist and fail today | Same tests pass |
| T33 | same as T32 | same as T32 | The verb refuses the flag and the step stops passing it | — | T32 green; the OpenCode verb's `--config-dir` is unchanged, and the OpenCode step call is byte-identical (D35) |
| T34 | `dash/src/capture/harnesses/factory-plugin/marketplace.test.ts` (new), `dash/src/capture/harnesses/factory-plugin/install.test.ts` (amended) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/marketplace.test.ts src/capture/harnesses/factory-plugin/install.test.ts` | **STATUS (D63): SHIPPED.** With an injected fake `droid` runner, the install runs `droid plugin marketplace add <root>` then `droid plugin install kyberdash-factory@kyberdash --scope user`, in that order; the tree's exact layout and the marketplace manifest's shape (a relative `source`); the receipt names the marketplace, the identity, the root and a digest per owned file; the uninstall runs `droid plugin uninstall … --scope user` then `droid plugin marketplace remove kyberdash` and removes the tree and the receipt; the state vocabulary covers `marketplace-tree-written`, `marketplace-registered`, `plugin-installed` and `unknown`; a missing CLI yields `unknown` with the named cause (D43, D44, D47) | The new file exists and fails against the absent module; the amended cases fail against the shipped tree-install | Same tests pass, unweakened |
| T35 | same as T34 | same as T34 | **STATUS (D63): SHIPPED.** The marketplace route exists: the tree writer, the harness-command driver, the receipt, the state reader and the status lines | — | T34 green; the tree write is atomic per file and the receipt is written only after the tree landed; the harness's cache is never written by KyberDash |
| T36 | `dash/src/capture/harnesses/factory-plugin/marketplace.test.ts` (amended) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/marketplace.test.ts` | **STATUS (D63): SHIPPED.** The settings-file non-interference shipped first (`marketplace.test.ts:223-249`); the **name-conflict refusal and the residue report** shipped with it, on the tree root the harness's own SOURCE column is compared against. A marketplace or plugin of the same name already held by the harness is **refused** in strict mode (exit 1, nothing written) and kept with a warning in preserve mode (exit 0); a plugin of the same name in a different marketplace is not a conflict; the refusal names the entry and the command that removes it; **KyberDash writes nothing to the owner's settings file** and reads nothing from it; a residue left by the harness's uninstall is reported with the exact command and is never patched (D45, D46, D48) | The settings-file cases pass; the conflict and residue cases exist and fail against the shipped installer | Same tests pass; the OpenCode installer's cases stay green |
| T37 | same as T36 | same as T36 | **STATUS (D63): SHIPPED.** The refusals, the residue report and the settings-file non-interference all shipped. The refusals, the residue report and the settings-file non-interference exist | — | T36 green; the install and the uninstall write only under `~/.kyberdash/` |
| T38 | `dash/src/capture/harnesses/factory-plugin/install.test.ts` (the hooks-file case), `dash/src/capture/harnesses/factory-plugin/mapper.test.ts` (the `SubagentStop` case) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/install.test.ts src/capture/harnesses/factory-plugin/mapper.test.ts` | **STATUS (D63): SHIPPED.** The plugin's `hooks/hooks.json` is keyed **directly** by event name, with no top-level `hooks` wrapper (D53), and the `SubagentStop` event is **not registered** at all: it produces **no** span (D54) and a registration would cost a process per subagent stop for nothing, so the other six events' spans are unchanged. **The corrected file was verified LIVE** — a real Droid turn produced the session, turn and tool spans through the direct-keyed file, with the consent off and no prompt text on the live span — because a fixture can only assert the bytes | The `SubagentStop` cases exist and pass; the hooks-file case exists and fails against the shipped bytes | Same tests pass; the bundle's other events are unchanged |
| T39 | same as T38 | same as T38 | **STATUS (D63): SHIPPED.** The plugin's hooks file and the `SubagentStop` mapping are corrected. The committed bundle (`generated/plugin-source.ts`) carries the capture **script**, which the hooks-file change does not touch — the hooks file is generated beside it — so the bundle is unchanged and its drift guard still passes | — | T38 green; the bundle's drift check is clean; the wire contract's version stays `1` and the adapter's envelope check is unchanged |
| T40 | `dash/src/canon/adapters/droid.test.ts` (the native-claim cases), `dash/src/canon/ingest.test.ts` (the native path) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/adapters/droid.test.ts src/canon/ingest.test.ts` | **STATUS (D63): SHIPPED.** The `droid` adapter claims **no** native span: a `gen_ai` span — the root, the `chat` child and the `execute_tool` child — is quarantined as `unclaimed`; the plugin envelope is still claimed; the ingest no longer supersedes stored native rows; the native counter reasons are gone (D50) | The cases exist and fail against the shipped native claim | Same tests pass; the plugin-envelope cases and every other adapter's cases stay green |
| T41 | same as T40 | same as T40 | **STATUS (D63): SHIPPED.** The native span claim, the ingest's native supersede logic and the native reasons are withdrawn | — | T40 green; the metrics path, the receiver's `/v1/metrics` route and the `metric_point` destination are untouched |
| T42 | `dash/src/canon/ingest.test.ts` (the content-gate cases) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/canon/ingest.test.ts` | **STATUS (D63): SHIPPED.** With the Factory consent **off**, no Factory **prompt, reply, tool argument or tool result** from the **native OTLP route** reaches the store: not in a canonical record and **not in a quarantine packet** (**AMENDED (D61): the earlier "not in a metric point" clause is withdrawn — no evidence shows a Factory metric carrying message content, and the metric route is out of scope (A22)**); with the consent **on**, they are stored; another harness's `gen_ai.input.messages` is untouched (D52, **amended by D57**: the gate covers the **native and quarantine routes only** — the **file source's** own first-message fragment is outside it, and the plugin's tool arguments and results are **byte counts**, never content, so there is nothing there to gate) | The cases exist and fail against the shipped behaviour | Same tests pass; the other harnesses' records are unchanged |
| T43 | same as T42 | same as T42 | **STATUS (D63): SHIPPED.** The consent gate over the native and quarantine routes exists, and it runs at the ingest seam **before** normalization and **before** the quarantine write. **The gate's fail-closed arm is recorded in D52:** a span that belongs to **no** harness has an unknown origin, so its content is stripped too — which is what covers a Factory child that arrives before its root and crosses a batch boundary. A **claimed** span is never touched | — | T42 green; the gate is applied before normalization and before the quarantine write |
| T44 | `dash/src/capture/harnesses/factory-plugin/native-export.test.ts` (amended), `dash/src/capture/harnesses/factory.test.ts` (the status row) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/native-export.test.ts src/capture/harnesses/factory.test.ts` | **STATUS (D63): SHIPPED.** The native reader resolves the **format**, the **granularity** and the **message-content switch** with Factory's precedence rules — the organisation's value wins for the format and the endpoint, most-restrictive-wins for the granularity, `aggregate` overrides every content opt-in — and the row reports the effective shape and states that no native span is claimed (D51). **The two criteria D58 added are NOT part of what shipped** and are carried by **T48**: the organisation's **`enabled`** master switch, and **a granularity value that cannot be read resolves to `aggregate`** (the shipped reader resolves it to `user`, `native-export.ts:334`, pinned by `native-export.test.ts:117`). **The row's consent wording is also not part of what shipped**: the shipped row still reads *"no Factory content is stored while KyberDash's consent is off"* (`factory.ts:147-151`), which is false (D57); T47/T48 correct it | The cases exist and fail against the shipped endpoint-only reader | Same tests pass |
| T45 | same as T44 | same as T44 | **STATUS (D63): SHIPPED.** The native reader and the status row report the effective shape | — | T44 green; no new read of the owner's settings file is introduced |
| T46 | **No automated test — this is the manual, owner-run probe** that establishes the harness-side semantics. Evidence written to the gitignored `artifacts/evidence/factory-capture-live/<UTC-stamp>/` | `droid plugin marketplace list`, `droid plugin list --scope user`, `droid plugin install --scope user`, `droid plugin uninstall --scope user`, `droid plugin marketplace remove` | **STATUS (D63): CLOSED.** The probe was run against a real Factory Droid CLI 1.1.0 and every question is answered, with the evidence in `artifacts/evidence/factory-capture-live/20261102T094024Z/`. **The registered marketplace name** is the source directory's **basename**, not the manifest's own `name` field. **The settings-entry shapes** are `extraKnownMarketplaces: { "<name>": { "source": { "source": "local", "path": "<absolute path>" } } }` and `enabledPlugins: { "<plugin>@<marketplace>": true }` — the install **auto-enables**. **`droid plugin uninstall` DELETES** the `enabledPlugins` entry outright rather than restoring it, and leaves the marketplace registration; **`droid plugin marketplace remove` DELETES** the registration and leaves `enabledPlugins` alone; a local marketplace's source directory **survives** the removal, which is why KyberDash removes its own tree. **A re-install does NOT upgrade**: `droid plugin install` is REFUSED for an already-installed plugin ("already installed at user scope"), and only `droid plugin update` re-copies the plugin's files into the harness's cache — and `droid plugin update` itself fails without the marketplace registration, which is why the upgrade path (D65) restores the registration first. **The scope flags** accept exactly `user` and `project`. **The settings path follows NEITHER `$HOME` NOR `$FACTORY_DIR`**: the plugin commands always act on the owner's real `~/.factory`, so an automated test that spawned the real binary would write to the developer's own settings file — which is why the route reaches the harness through an injected CLI and the spawn guard refuses a real spawn (D45, D46, A12, A13, A14) | n/a — manual verification | The probe's answers are recorded in the plan's assumption table and the evidence directory; a contradiction with the plan is reported as a failure, not worked around |
| T47 | `dash/src/capture/harnesses/factory.test.ts` (the status row's consent wording), `dash/src/capture/harnesses/factory-plugin/native-export.test.ts` (amended — the `enabled` switch and the granularity fallback) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory.test.ts src/capture/harnesses/factory-plugin/native-export.test.ts` | **STATUS (D63): SHIPPED.** The row's fixed words are **corrected** in **both** consent branches — *"while KyberDash's consent is off, KyberDash's own Factory capture stores no prompt text"* and *"while KyberDash's consent is on, KyberDash's own Factory capture stores the plugin's prompt text"* — and the shipped *"no Factory content is stored while KyberDash's consent is off"* and *"Factory content is stored while KyberDash's consent is on"* are **both gone** (D57, D58); the row names the **file source**'s route as one the consent does not govern, and the **native route**'s content as the harness's own switch; the reader resolves the organisation's **`enabled`** master switch and reports it beside the endpoint verdict; and a **granularity value that cannot be read resolves to `aggregate`**, not `user` (D58) | The cases exist and fail against the shipped wording and the shipped reader | Same tests pass, unweakened |
| T48 | same as T47 | same as T47 | **STATUS (D63): SHIPPED.** The status surface says what the consent governs, and the reported shape is the export's effective one | — | T47 green; no new read of the owner's settings file is introduced; the native reader's own tests stay green |
| T49 | `dash/src/capture/harnesses/factory-plugin/install.test.ts` (amended), `dash/src/capture/harnesses/factory-plugin/marketplace.test.ts` (amended) | `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/install.test.ts src/capture/harnesses/factory-plugin/marketplace.test.ts` | **STATUS (D65): SHIPPED.** A receipt-proven tree whose bytes match that receipt and whose content is not the current bundle is an **upgrade** — not `edited` and not `same`; the install rewrites the tree and the receipt with the current bundle and refreshes the harness's cached copy with `droid plugin update kyberdash-factory@kyberdash --scope user`, **never** `plugin install` against an installed plugin; a registered-but-uninstalled plugin takes the install path, and an installed plugin whose marketplace registration is missing gets the registration restored first; a tree no receipt proves stays `foreign` and a tree whose bytes drifted stays `edited`; `--if-installed` decides from the harness's registry and refreshes even when KyberDash's tree is absent and must be written for the first time; `unchanged` requires **both** a current tree and an installed harness copy and runs no harness command; the tokens are `upgraded` and `would-upgrade`; and a harness refusal is never reported as a success (D65) | The cases exist and fail against the shipped classifier, which returns `same` for a receipt-proven older bundle, never rewrites the tree, and runs no harness command | Same tests pass, unweakened; the shipped refusal and non-interference cases stay green |
| T50 | same as T49 | same as T49 | **STATUS (D65): SHIPPED.** The upgrade path exists: the `upgrade` classification, the tree-and-receipt rewrite, the harness refresh chosen from the harness's own registry, and the `upgraded` / `would-upgrade` tokens | — | T49 green; the receipt's shape is unchanged; the OpenCode installer's cases stay green |

## 5. Dispatchable tasks

Skills are named, not owners: the conductor maps them to the live specialist inventory.

### T1 — RED: the Factory wire contract and the hook-to-span mapper

**Objective.** Write the failing contract tests that pin the wire vocabulary and the event mapping
before any production code exists.
**Files.** `dash/src/capture/harnesses/factory-plugin/contract.test.ts`,
`dash/src/capture/harnesses/factory-plugin/mapper.test.ts` (new).
**Acceptance criteria.** The tests name every attribute key, the three span names, the id
derivation (deterministic from the session id plus the anchor id, as the OpenCode contract does
at `dash/src/capture/harnesses/opencode-plugin/contract.ts:60-76`), and one case per hook event.
Each case asserts an **absent** attribute for a payload that does not carry it.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T2 — GREEN: the Factory wire contract and the hook-to-span mapper

**Objective.** Implement `contract.ts` (a leaf: `node:` imports only) and `mapper.ts`.
**Files.** `dash/src/capture/harnesses/factory-plugin/contract.ts`,
`dash/src/capture/harnesses/factory-plugin/mapper.ts` (new).
**Acceptance criteria.** T1 green, unweakened; the contract module has no non-`node:` value
import (asserted by the leaf test in T3); no attribute is fabricated.
**Depends on.** T1.
**Skills.** `typescript-dev`.

### T3 — RED: the hook runner, the shared transport and consent leaves

**Objective.** Write the failing tests for the one-shot hook runner, the shared transport
extraction, the shared consent writer and the leaf rule.
**Files.** `dash/src/capture/harnesses/factory-plugin/entry.test.ts`,
`dash/src/capture/harnesses/transport/outbox.test.ts`,
`dash/src/capture/harnesses/transport/otlp-sender.test.ts`,
`dash/src/capture/consent-file.test.ts`, `dash/src/architecture/factory-leaves.test.ts` (new).
**Acceptance criteria.** The tests cover: malformed stdin, a hook event with no payload fields,
the consent file off and on, the bounded spool (drop-oldest at the bound), the POST deadline,
the exit code 0 on every path, stdout carrying only the hook protocol, the shared modules being
import-free leaves, and **the OpenCode bundle still bundling to the committed bytes**
(`dash/src/capture/harnesses/opencode-plugin/bundle.test.ts` must stay green — it is the
regression witness for D14: the committed artefact must be CURRENT, not necessarily unchanged).
**Depends on.** T1 (the vocabulary).
**Skills.** `test-dev`, `typescript-dev`.

### T4 — GREEN: the shared transport, the shared consent writer, the hook runner

**Objective.** Extract the shared leaves, turn the OpenCode modules into re-export shims, and
implement the Factory hook runner and its file-backed spool.
**Files.** `dash/src/capture/harnesses/transport/outbox.ts`,
`dash/src/capture/harnesses/transport/otlp-sender.ts`,
`dash/src/capture/consent-file.ts`,
`dash/src/capture/harnesses/opencode-plugin/transport/outbox.ts` and
`.../transport/otlp-sender.ts` (shims),
`dash/src/capture/harnesses/opencode-plugin/consent-write.ts` (shim),
`dash/src/capture/harnesses/factory-plugin/entry.ts`,
`dash/src/capture/harnesses/factory-plugin/spool.ts` (new).
**Acceptance criteria.** T3 green; `node dash/scripts/bundle-opencode-plugin.mjs --check` exits
0 (the committed OpenCode bytes are CURRENT — a fresh bundle of the entry — not that they never changed; D14); the hook runner exits 0 on every input and never
throws.
**Depends on.** T3, T2.
**Skills.** `typescript-dev`.

### T5 — RED: the Factory consent reader

**Objective.** Write the failing tests for the consent read side.
**Files.** `dash/src/capture/harnesses/factory-plugin/consent.test.ts` (new).
**Acceptance criteria.** On/off/absent/invalid/unreadable/oversized are each pinned; the env
override (`KYBERDASH_FACTORY_CONTEXT`) is pinned as taking precedence over the file.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T6 — GREEN: the Factory consent reader

**Objective.** Implement `consent.ts` (a leaf, `node:` built-ins only, as the OpenCode consent
reader is at `dash/src/capture/harnesses/opencode-plugin/consent.ts`).
**Files.** `dash/src/capture/harnesses/factory-plugin/consent.ts` (new).
**Acceptance criteria.** T5 green.
**Depends on.** T5, T4 (the shared consent writer).
**Skills.** `typescript-dev`.

### T7 — RED: the plugin installer, the receipt and the refusal rules

**Objective.** Write the failing tests for install, upgrade, refusal, uninstall, the receipt and
the byte-identity guarantee.
**Files.** `dash/src/capture/harnesses/factory-plugin/install.test.ts` (new).
**Acceptance criteria.** Strict refuses a foreign tree and an edited tree with exit 1 and writes
nothing; preserve keeps both with a warning and exit 0; the receipt lists every installed file
with its digest; uninstall removes the tree only when every digest matches and otherwise leaves
it with a refusal; `~/.factory/settings.json` and `~/.factory/hooks.json` stay byte-identical
after install, enable, disable and uninstall; `--dry-run` writes nothing; `ifInstalled` on an
absent tree writes nothing. The byte-identity cases **create** an owner `~/.factory/hooks.json`
(with a foreign hook in it) and a project `.factory/hooks.json`, because this machine has neither
(§2.5) and the criterion is about an owner who has them. **AMENDED (D45, D48): the settings
file's byte-identity criterion is SUPERSEDED** — under the marketplace route the harness's own
commands change that file, so the criterion becomes "**KyberDash writes nothing to it and reads
nothing from it**", which T36 pins; the **hooks file's** criterion is unchanged, because the
plugin route does not touch `~/.factory/hooks.json` at all.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T8 — GREEN: the plugin installer, the receipt and the state/status surface

**Objective.** Implement `install.ts`: paths, the tree writer, the receipt, the classifier
(**AMENDED (D61): `absent` / `unreadable` / `foreign` / `edited` / `same`** — the shipped five; this
task's earlier list named `older` and `newer`, which the code has never had, and omitted
`unreadable`, which it has), strict and preserve modes, uninstall,
`state` and `describeStatus`.
**Files.** `dash/src/capture/harnesses/factory-plugin/install.ts` (new).
**Acceptance criteria.** T7 green; the receipt format is `{version, root, files[]}`; writes are
atomic per file and refuse a symlink at a target path, as the OpenCode installer does
(`install.ts:107-136`).
**Depends on.** T7, T4.
**Skills.** `typescript-dev`.

### T9 — RED: the registry row, the status surface and the plugin-install verb

**Objective.** Write the failing tests for the Factory writer as a registry row.
**Files.** `dash/src/capture/harnesses/factory.test.ts` (new),
`dash/src/capture/status.test.ts` (Factory cases),
`dash/src/capture/detect.test.ts` (Factory probes),
`dash/src/capture/harnesses/factory-plugin/install-command.test.ts` (new).
**Acceptance criteria.** The registry contains a `factory` writer with the declared shape
(D16); `capture status --harness factory` prints the plugin state, the receipt state, the
resolved plugin path and the consent state; `capture enable --harness factory` installs and
`disable` removes it, touching no settings file; `GET /api/kyber/capture` reports the same row;
`capture plugin install --harness factory` accepts the id and `--harness bogus` exits 2.
**Depends on.** T7 (the installer the row drives).
**Skills.** `test-dev`, `typescript-dev`.

### T10 — GREEN: the registry row, the status surface and the plugin-install verb

**Objective.** Implement the Factory writer, register it, add the detect probes, and generalize
the plugin-install verb to both harnesses.
**Files.** `dash/src/capture/harnesses/factory.ts` (new),
`dash/src/capture/harnesses/registry.ts` (register the row),
`dash/src/capture/detect.ts` (the `factory` probe group),
`dash/src/capture/harnesses/factory-plugin/install-command.ts` (new; the OpenCode command keeps
working),
`dash/src/cli/register.ts` (the verb's `--harness` help text and the new consent verb).
**Acceptance criteria.** T9 green; the OpenCode row's cases stay green; the registry order gains
Factory at the end (a status-only row order rule already appends Kilo last); the Factory status row
reports the native-export availability with its named cause — never as working when no native span
can arrive (D26, D37).
**Depends on.** T9, T8.
**Skills.** `typescript-dev`.

### T11 — RED: the `droid` canon adapter

**Objective.** Write the failing tests for the adapter that claims the Factory plugin envelope.
**Files.** `dash/src/canon/adapters/droid.test.ts`,
`dash/src/canon/adapters/droid-envelope.test.ts` (new).
**Acceptance criteria.** The adapter claims only spans carrying `factory.contract_version = 1`
plus a `factory.` marker; an unknown or missing version scores 0 (quarantine, as the OpenCode
envelope does at `dash/src/canon/adapters/opencode-envelope.ts:74-79`); a normalized record
carries `harness: 'droid'`, the session id, the model and the tool name; a record with no token
counter declares `token_usage` `not_measurable` with a reason naming the file-source pairing and
never 0. **AMENDED (D50): the native claim is WITHDRAWN and is NOT written here.** This task's
original text authorized it (D12 branch A) and pinned the D23 precedence rule. **The native branch
does not exist in the shipped adapter**: a span carrying `gen_ai.agent.name = 'droid'` is
**quarantined as `unclaimed` whatever it carries**, the whole decoded packet is kept as the audit
evidence, and no native span ever contributes a turn, a tool, a duration or a token. **The D23
precedence rule survives in the weaker form** — a native span is never counted at all — and the
withdrawal's measurement and the conditions for restoring it are filed as a todo (D50).
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T12 — GREEN: the `droid` canon adapter

**Objective.** Implement `droid-envelope.ts` (a leaf) and `droid.ts` (the adapter), and register
the adapter in `ADAPTERS`.
**Files.** `dash/src/canon/adapters/droid-envelope.ts`,
`dash/src/canon/adapters/droid.ts` (new),
`dash/src/canon/ingest.ts` (registration).
**Acceptance criteria.** T11 green; the OpenCode adapter's cases stay green; the adapter's
`detect` never reads the span's source name (R6.2).
**Depends on.** T11, T2.
**Skills.** `typescript-dev`.

### T13 — Session pairing (test-first, production change only if the test proves one is needed)

**Objective.** Prove the D8 pairing rule: a plugin row and a file row with the same session id
form one session whose tokens come from the file row; different ids stay separate with the
plugin row honest. Prove the D25 child rule alongside it: a child session whose id is in the
companion file's `childInclusiveTokenUsageBySessionId` is paired, and one that is not reads
`not_measurable` with a stated reason.
**Files.** `dash/src/canon/sessions.droid-pairing.test.ts` (new).
**Acceptance criteria.** The test states both rules and passes against the current projection if
the projection already groups by session id (likely); **if it does not**, the implementation
change is confined to the projection's session grouping and is reported as a scope decision
before it is made, never a weakening of the assertion.
**Depends on.** T12.
**Skills.** `test-dev`, `typescript-dev`.

**Outcome and follow-on (D28–D30).** The test exists and six of its eight cases pass. The two
that fail proved three gaps, and the scope decision this task required has been made: **D28**
(the Factory source-precedence rule), **D29** (the child session's token source) and **D30** (the
adapter's reasons name the pairing). The projection change is therefore specified, not improvised,
and it is delivered by **T22–T26**; the two child cases move to **T26**.

### T14 — The file source stays the token source

**Objective.** Prove that `dash refresh` still produces the `droid` session with tokens from the
companion `*.settings.json`, so the plugin work does not disturb it.
**Files.** `dash/src/providers/droid.test.ts` (Factory token cases),
`dash/src/refresh/refresh.integration.test.ts` (the droid job case).
**Acceptance criteria.** The cases exist and pass; any change they demand is a scope decision.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T15 — RED: the native-OTel receiver path and claim (authorized)

**Objective.** Write the failing tests for `POST /v1/metrics` and the native `droid` claim.
**Files.** `dash/src/otel/receiver.test.ts` (new cases), `dash/src/canon/adapters/droid.test.ts`
(the native cases).
**Acceptance criteria.** The route serves the path, answers 200 for a valid OTLP/JSON metrics
body, 4xx for a malformed one, keeps 404 for an unknown path, and participates in the paused-503
and the health window like the other signal paths. **AMENDED (D50): the native cases are NOT
written here, because the native claim is withdrawn.** The native span is **quarantined as
`unclaimed`**, and the D23 precedence rule now reads as "a native span adds no turn, no tool and
no duration" — strictly stronger than the old "excluded from a session that already has plugin
rows", because there is no session it can contribute to at all. **T40/T41 carry the withdrawal's
own cases**; the §3.4 vocabulary table stays in the plan as the record of what the withdrawn claim
would have matched.
**Depends on.** T11 (the adapter's vocabulary).
**Skills.** `test-dev`, `typescript-dev`.

### T16 — GREEN: the native-OTel receiver path and claim (authorized)

**Objective.** Implement the metrics route and the native claim.
**Files.** `dash/src/otel/receiver.ts`, `dash/src/otel/writer.ts` (the metrics sink port),
`dash/src/canon/adapters/droid.ts` (the native claim).
**Acceptance criteria.** T15 green; the plugin path is unaffected; no token counter is taken from
a native span that does not carry one; only OTLP/HTTP on 4318 is added — no gRPC path, no 4317,
no `OTEL_EXPORTER_OTLP_PROTOCOL` support.
**AMENDED (D50): the native claim is NOT implemented here — it is WITHDRAWN.** The metrics route
and its sink port are implemented as written; **the `droid.ts` change in this task's file list is
the removal of the native branch, not the addition of one**, and the withdrawal's own RED/GREEN
pair is **T40/T41**.
**Depends on.** T15, T12.
**Skills.** `typescript-dev`.

### T17 — RED: the Squad and update steps for the Factory plugin

**Objective.** Write the failing .NET tests for the global-install step and the updater.
**Files.** `tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs`,
`tests/KyberWeave.Tests/UpdateCommandTests.cs` (new cases).
**Acceptance criteria.** A global Factory deploy runs
`kyberdash kyber capture plugin install --harness factory --preserve` and names no root (D35); the
updater runs `kyberdash kyber capture plugin install --harness factory --preserve --if-installed`
and names no root (D34); the step's outcome is never Squad's exit code; a missing
KyberDash is a skip; the OpenCode step's cases stay green.
**Depends on.** none.
**Skills.** `test-dev`, `csharp-dev`.

### T18 — GREEN: the Squad and update steps for the Factory plugin

**Objective.** Extend `KyberDashCapturePluginStep` and `SelfUpdater`.
**Files.** `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`,
`src/KyberWeave.Cli/Update/SelfUpdater.cs`.
**Acceptance criteria.** T17 green; the step is **harness-aware** — one plugin call per
capture-plugin harness, in a fixed order with the OpenCode call first and the Factory call
following it; the OpenCode call is handed its own resolved global root and the **Factory call is
handed no root at all** (D35), so the plugin lands under `$FACTORY_DIR` else `~/.factory`; the
OpenCode invocation is byte-identical to what it was.
**Depends on.** T17, T10 (the verb must exist).
**Skills.** `csharp-dev`.

### T19 — The replay e2e through the production pipeline

**Objective.** Add a Factory scenario to the conformance spec: a recorded hook payload through
the production plugin, receiver, `droid` adapter, projection and dashboard.
**Files.** `dash/e2e/harness-collection.spec.ts`, its fixtures.
**Acceptance criteria.** The scenario renders a Factory session with model and tool attribution
and an honest token state; the spec runs only with the isolated config
(`dash/playwright.isolated.config.ts`); the existing scenarios stay green; the scenario is
labelled as replay evidence, not live capture.
**Depends on.** T12, T4.
**Skills.** `test-dev`, `typescript-dev`.

### T20 — Documentation harvest and closeout

**Objective.** Land every H-row, amend ADR 0031, create the new ADR, update the plan's closeout
mapping, and archive the plan.
**Files.** `docs/dash/architecture.md`, `docs/dash/telemetry-inventory.md`,
`docs/dash/runbook.md`, `docs/dash/onboarding.md`, `docs/cli-command-surface.md`,
`docs/rules/kyberdash-harness-collection.md`, `docs/kyber-squad/architecture.md`,
`docs/kyber-squad/onboarding.md`, `docs/install.md`, `docs/adr/0031-…md`, the new ADR,
`docs/adr/README.md` (the ADR index row), `docs/plans/README.md` (the row moves to Archived
Plans), `docs/todo/` (the sibling coverage gap), and the plan file itself (`git mv` to
`docs/archive/plans/`).
**Acceptance criteria.** `docs validate . --merge-ready` and `docs drift .` report zero findings;
the closeout mapping links every D-row to its destination; the ADR waiver / documentation waiver
rules are honoured. **Three harvests carry rules the plan set:** the harness-collection rule's
Factory row states the D28 split (D36, H7); the CLI command surface's plugin-install row and
exit-code row and the runbook's Factory plugin paragraph state the `--config-dir` scope (D35,
H18); and the closeout **claims** the status row's native-path availability reporting, because
it ships (D41, commit `e140472` — D37's "not shipped yet" marker no longer holds). **The two
metrics documents state the point's identity, not just its count** (D42): the architecture and
the telemetry inventory say **one row per distinct decoded point**, a point's identity being a
content digest, so a re-delivered point lands on the row it already occupies — their current
"one row" shorthand is corrected here rather than left to mislead. **Three todos are filed under
the todo index** (D21): the sibling harness
coverage gap (Cursor, Warp, ZCode, Devin, Command Code), the Factory file source's even
token split, recording that the companion `<session-id>.settings.json` carries `lastCallTokenUsage`
and `childInclusiveTokenUsageBySessionId`, so the split is a known lossy approximation with
better data available (§2.5), and the metric point's content-digest identity (D42) — that the
multiplicity of byte-identical decoded points is not preserved and that no shipped test pins the
collision, so a later change to the identity would be unobserved. **AMENDED (D49, D50, D56): the
harvest now carries FOUR more rules and a FOURTH todo.** (1) ADR 0031 is amended in place with the
**delegated-install clause** and the corrected Factory account — the marketplace tree, the
harness-driven install, and the fact that the owner's settings file is never read or written **by
KyberDash** (H19, D49). (2) The new Factory ADR is amended with the install route and the native
path's withdrawal (H20, D43, D50). (3) The architecture, the runbook, the collection rule, the
telemetry inventory and the command surface carry the marketplace route, the state and refusal
vocabularies, the residue rule, the effective-shape report and the "no native span is claimed"
statement (H21–H25, D45–D47, D51). (4) A **fourth todo** is filed under the todo index: the
withdrawn native span path, with its measurement, the group-vote arithmetic and the three
conditions for restoring it (D50, A15, A16) — so the evidence is kept and the claim is not
re-derived from scratch. **The closeout must not claim the marketplace route, the native
withdrawal or the consent gate before T34–T45 have landed** (D56).
**Depends on.** T10, T12, T16, T18, T19, T35, T41, T43, T45.
**Skills.** `app-docs-standard`, `kyber-docs-authoring`.

### T21 — The live check (owner-run, not a CI gate)

**Objective.** Document and run, with the owner, the live proof: install the plugin, run one
scripted Droid turn, and read the Factory session in the dashboard.
**Files.** `docs/dash/runbook.md` (the procedure), `artifacts/evidence/factory-capture-live/<UTC-stamp>/`
(gitignored).
**Acceptance criteria.** The session appears with model and tool attribution; tokens are either
paired from the file source or honestly `not_measurable`; content is absent unless consent says
`content: true`. The check also records which assumptions held and which did not: the hook's
`session_id` against the transcript's session id (A2), the hook payload's exact field names and
whether it carries a model (A3), what identity `Stop` and `SubagentStop` carry (A4), the hook
script's exit-code and stdout protocol (A5), the plugin's discovery path (A1), and the model's
resolution link (D24). **AMENDED (D43, D50, D53):** the check now also verifies the **install
route** — that the plugin reports STATUS "Enabled" after the marketplace install, that its hooks
fire in a real turn (which the first live check already proved, §2.6), and that
`droid plugin list --scope user` and `droid plugin marketplace list` report what the status row
reports; that the plugin's `hooks/hooks.json` in its **documented** shape fires (D53); and that
the native path is reported as the row states it — no native span claimed, no Factory content
stored while the consent is off (D50, D52). A failure is reported as a failure, never worked
around.
**Depends on.** T20.
**Skills.** `app-docs-standard`.

### T22 — RED: the Factory source-precedence contract (D28)

**Objective.** Pin the accounting rule of D28 in the pairing test: a paired session carries the FILE
row's counters **and** counts ONE Factory turn (the plugin's), and keeps the file row's cost; a
file-only session is unchanged.
**Files.** `dash/src/canon/sessions.droid-pairing.test.ts` (amended),
`dash/src/canon/droid-pairing.test.ts` (new).
**Acceptance criteria.** The paired-session case is **extended, never weakened**: every existing
assertion stays, and the case adds `expect(summary.turn_count).toBe(1)` and the cost assertion (the
session's cost is the file row's priced cost, not the plugin's unknown one). A new case — "counts
one Factory turn for a paired session, and the file row is not a second turn" — fails today on the
turn count (it is 2). A new case — "keeps a file-only session's own turns, counters and cost" — is
a guard that passes today. A new case — "keeps the paired session's context figures" — pins that
the file source's context figures survive the plugin's arrival. The new leaf's test pins the split
helper's contract: for a mixed `droid` session it returns the plugin rows as the turn records and
the file rows as the counter records; for a plugin-only session, a file-only session and a
non-`droid` session it returns no split at all. The two child cases are **not** touched here — they
move to T26, and the child-pairing case stays RED until T26 corrects its fixture, which is
expected and is not a failure of this task.
**Depends on.** T12, T13.
**Skills.** `test-dev`.

### T23 — GREEN: the Factory source-precedence rule (D28)

**Objective.** Implement the split and apply it in the session projection.
**Files.** `dash/src/canon/droid-pairing.ts` (new leaf),
`dash/src/canon/sessions.ts` (the session row's derivation).
**Acceptance criteria.** T22 green, unweakened. In a mixed `droid` session the turn count, the
timeline's turns, the request roots, the per-turn model, the tool invocations, the duration and the
context turns are read from the plugin rows, and the token totals and the availability that gates
them are read from the file rows; the file row contributes its counters and its cost and nothing
else; the cost is summed over the turn records and the counter records together, and the
cost-basis verdict is computed over that same union; the context analysis falls back to the counter
records when no turn record carries a measured input. A plugin-only `droid` session and a
file-only `droid` session behave exactly as they do today, and **every other harness is
unchanged** — `dash/src/canon/sessions.test.ts` and the whole dash unit suite stay green. No
counter is summed twice and no zero is fabricated.
**Depends on.** T22.
**Skills.** `typescript-dev`.

### T24 — RED: the pairing reasons name the file-source pairing (D30)

**Objective.** Strengthen the reason assertions so the requirement D8 and D9 already state is
pinned.
**Files.** `dash/src/canon/adapters/droid.test.ts` (the plugin-path reason case),
`dash/src/canon/sessions.droid-pairing.test.ts` (the plugin-only session's published reason).
**Acceptance criteria.** Each of the adapter's four counter-class reasons must name the file source
and the session-id pairing (a regex over the file source's name and the session), while the
existing assertions (the reason names the harness and the metric) keep holding. The plugin-only
session's **published** reason must name it too. The strengthened assertions fail against the
shipped wording.
**Depends on.** T12, T13.
**Skills.** `test-dev`.

### T25 — GREEN: the adapter's reason wording (D30)

**Objective.** Implement D30's wording.
**Files.** `dash/src/canon/adapters/droid.ts`.
**Acceptance criteria.** T24 green; the four reasons are exactly D30's; the native path's reasons
are unchanged; no other adapter case moves; the reason never claims the tokens exist.
**Depends on.** T24.
**Skills.** `typescript-dev`.

### T26 — The child-pairing fixture correction (D29; test-only, no production change)

**Objective.** Correct the two child cases' fixture to the real Factory shape, and record that the
child pairing needs no production change beyond D28's split.
**Files.** `dash/src/canon/sessions.droid-pairing.test.ts` (the two child cases).
**Acceptance criteria.** The fixture writes the child session as its **own** Factory session — its
own `<child-id>.jsonl` and its own `<child-id>.settings.json` carrying the child's usage — and the
parent's companion file **keeps** its `childInclusiveTokenUsageBySessionId` map, so the parent's own
counters stay the parent's own. The child case asserts that the child session carries the child's
own usage and never the parent's; the unmapped child still reads `not_measurable` with a reason.
The corrected cases pass against the projection as D28 specifies, with **no production change** —
that is the evidence this task exists to record. `dash/src/providers/droid.test.ts` is **unchanged**
and green: its characterization case ("ignores the companion file child-usage map, so the child
tokens never reach the session") is correct under D29 and stays.
**Depends on.** T23.
**Skills.** `test-dev`.

### T27 — RED: the native metrics destination and the production wiring (D31)

**Objective.** Pin where the native OTLP metrics go, and pin the production path itself — the
seam the receiver's own tests cannot reach.
**Files.** `dash/src/canon/metric-ingest.test.ts` (new),
`dash/src/otel/service.test.ts` (the production-wiring case),
`dash/src/otel/writer.test.ts` (the metrics cases).
**Acceptance criteria.**
1. **The canon store persists the metric points.** A decoded metrics batch lands in the store's
   `metric_point` table (D38), one row per **distinct** data point — a point's identity is a
   content digest of everything the row carries, so two byte-identical decoded points collapse
   into one row and a re-delivered point lands on the row it already occupies (D42) — carrying
   the metric's name, kind, unit, the
   aggregation temporality, the point's timestamp, the point's value — or, for a histogram or a
   summary, its count and its sum — the point's attributes, the resource attributes, the scope and
   the exporter's `service.name`. The rows survive a reopen of the store. **No metric value is
   dropped and none is fabricated**: a point that carries no value keeps it absent, never a zero.
2. **The production wiring serves the path.** A `POST /v1/metrics` against the **running
   collector service** — the real `toCanon` sink, not a store double — answers **200**, and the
   points are in the store. This is the case that fails today: the production sink has no metrics
   port, so the batch is refused and the receiver answers 500.
3. **The writer refuses a batch a sink cannot persist.** `writeMetrics` on a sink without the
   metrics port throws and queues nothing — never accepts-and-drops.
4. **The receiver activity records the metrics.** The batch writes one `ingest_log` row with the
   source `otlp:metrics`, sized to the points received, and the write is fenced: a failing audit
   write never fails the ingest it audits.
5. **A metrics-only commit moves the receiver's last-persisted fact.** The persisted-fact hook
   counts metrics as well as spans and logs, so a Factory deployment whose export is
   metrics-only (A6) is not left reporting a stale last-persisted time.
6. **A metrics batch requests no projection** — it changes no projected record.
**Depends on.** T16 (the route and the native claim exist).
**Skills.** `test-dev`.

### T28 — GREEN: the native metrics destination (D31)

**Objective.** Implement the destination and the production sink.
**Files.** `dash/src/canon/store.ts` (the `metric_point` DDL in `SCHEMA_SQL` — the shipped name,
D38 — and the store method
that writes the points), `dash/src/canon/metric-ingest.ts` (new — the OTLP-metric → row mapping,
mirroring `log-ingest.ts`), `dash/src/otel/service.ts` (the sink's `upsertMetrics`),
`dash/src/otel/writer.ts` (the persisted-fact hook counts metrics).
**Acceptance criteria.** T27 green, unweakened. The **schema version does not change** — an
`IF NOT EXISTS` table needs no migration step, so an existing store opens unchanged, exactly as
`model_context_window_catalog` landed. The metric rows carry the exporter's `service.name` as
their source and **no harness attribution** (R6.2): the source name is never harness evidence, and
a metric point has no span for the fingerprint vote to run on. The full corpus wipe clears the
table; a scoped per-harness wipe does not, because those rows' harness attribution is unknown.
The audit write is fenced. No projection is requested for a metrics batch. The writer's refusal
path is unchanged — a sink without the port still refuses the batch.
**Depends on.** T27.
**Skills.** `typescript-dev`.

### T29 — RED: the stored native error status (D32)

**Objective.** Pin the outcome at the seam the defect actually lives at: the **stored** record.
**Files.** `dash/src/canon/ingest.test.ts` (the stored-record case).
**Acceptance criteria.** A native Factory span carrying `error.type` is **stored** with the status
`error`, even when the exporter's own OTLP status is `unset` or `ok`; a span whose exporter status
is `error` is still stored as `error`; a span with no error evidence keeps the exporter's status
code exactly, so every other harness's stored records are unchanged. The case goes through the
production ingest path (`ingestBatch`), not the adapter alone: the adapter-level case already
passes, and the defect appears only after normalization.
**Depends on.** T16.
**Skills.** `test-dev`.

### T30 — GREEN: the status rule at the receiver-timing seam (D32)

**Objective.** Implement the rule so an error status is never downgraded.
**Files.** `dash/src/canon/ingest.ts` (`growWithReceiverTiming`).
**Acceptance criteria.** T29 green, unweakened. The function keeps the record's own `error` status
and takes the exporter's status code for every other value; its doc states that it gives the
record the timing, and the outcome **unless the adapter already determined one**. The change is
confined to that function: the adapter, the projection, the store and every other harness's stored
records are untouched.
**Depends on.** T29.
**Skills.** `typescript-dev`.

### T31 — The legacy update fixtures: the two-plugin sequences and the updater call's mode (D33, D34)

**Objective.** Modernize the three legacy reconciliation fixtures in `UpdateCommandTests.cs` to the
two-plugin sequences, and pin the Factory updater call's exact argument list. **Test-only: no
production change is required — the worker's implementation stands.**
**Files.** `tests/KyberWeave.Tests/UpdateCommandTests.cs` (the three legacy fixtures and the
Factory updater case).
**Acceptance criteria.**
1. `RunReconcilesAfterCommitInEngineTrayPluginOrder` — the expected sequence becomes
   `["engine","tray","plugin","plugin"]`, and **add**
   `Assert.Equal("opencode", HarnessOf(steps[2].Args));` and
   `Assert.Equal("factory", HarnessOf(steps[3].Args));` so the fixture states "one plugin step per
   capture-plugin harness, OpenCode before Factory" instead of a bare count. The existing
   `Assert.Equal(PluginArgs, steps[2].Args)` stays as it is — the OpenCode call is the first plugin
   call — and so do the `Assert.All(...)`, `steps[0].Args` and `steps[1].Args` assertions. The doc
   comment names the new order.
2. `RunAlreadyCurrentStillReconcilesEngineTrayAndPlugin` — the expected sequence becomes
   `["engine","tray","plugin","plugin"]`, with the same two harness assertions; the no-download
   assertion stays as it is; the doc comment names the plugin steps.
3. `RunWithNoRestartSkipsEngineAndTrayButStillUpdatesThePlugin` — the expected sequence becomes
   `["plugin","plugin"]`, with `Assert.Equal("opencode", HarnessOf(steps[0].Args));` and
   `Assert.Equal("factory", HarnessOf(steps[1].Args));`; the doc comment says the capture plugins
   still upgrade.
4. The method names, the `RunReconciling` helper, the `PluginArgs` constant and every other fixture
   in the file stay as they are. `RunWithNoKyberDashSkipsEngineTrayAndPlugin` still asserts an
   empty list — `--no-kyberdash` returns before any plugin call.
5. `RunInstallsTheFactoryCapturePluginWithTheIfInstalledFlag` — add a `FactoryPluginArgs` constant
   beside `PluginArgs`, holding
   `["kyber","capture","plugin","install","--harness","factory","--preserve","--if-installed"]`, and
   assert `Assert.Equal(FactoryPluginArgs, factory.Args);`. The case's present partial assertions
   (the verb's first four arguments, `--if-installed` present, no `--config-dir`) are subsumed by
   the exact list and may be kept; the mode is pinned either way.
**Test command.** `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~UpdateCommandTests|FullyQualifiedName~SquadCapturePluginStepTests"`
**Depends on.** T18.
**Skills.** `test-dev`.

### T32 — RED: the Factory verb refuses `--config-dir`, and the step hands the Factory call no root (D35)

**Objective.** Pin the flag's scope on both sides: the Factory plugin-install verb refuses a root
flag it cannot honour, and the Squad step stops passing one.
**Files.** `dash/src/capture/harnesses/factory-plugin/install-command.test.ts` (new case),
`tests/KyberWeave.Tests/SquadCapturePluginStepTests.cs` (the amended Factory cases).
**Acceptance criteria.**
1. `runFactoryPluginInstall` with a `configDir` answers **exit 2**, a stderr message naming the
   flag and saying where the Factory plugin goes, and **writes nothing** — no plugin tree, no
   receipt, no consent file.
2. Without the flag the behaviour is unchanged: the tree lands under `$FACTORY_DIR` else
   `~/.factory` (the existing installer cases stand).
3. The Squad step's Factory call is `kyberdash kyber capture plugin install --harness factory
   --preserve` — **no `--config-dir`** — and the OpenCode call keeps `--config-dir <opencodeRoot>`
   and its place in the order.
4. The case that pinned "the verb receives the resolved Factory global root" is **replaced** by
   one that pins "the Factory call names no root, so the verb resolves the harness's own root" —
   the same substance, stated against the rule D35 sets.
**Test command.** `scripts/proc-guard.sh run -- npm --prefix dash exec -- vitest run src/capture/harnesses/factory-plugin/install-command.test.ts` and
`scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests --filter "FullyQualifiedName~SquadCapturePluginStepTests"`.
**Depends on.** T18.
**Skills.** `test-dev`, `csharp-dev`.

### T33 — GREEN: the verb refuses the flag, and the step stops passing it (D35)

**Objective.** Implement the refusal and the corrected invocation.
**Files.** `dash/src/capture/harnesses/factory-plugin/install-command.ts`,
`dash/src/cli/register.ts` (the flag reaches the Factory branch so the refusal is reachable, and
the plugin-install command's `--config-dir` help names its scope),
`src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs`.
**Acceptance criteria.** T32 green, unweakened. The OpenCode verb's `--config-dir` behaviour is
unchanged, the OpenCode step call is byte-identical, the Factory call is handed no root, and the
verb's exit-code contract names the new usage error. **This supersedes T18's Factory-call
shape**: the earlier T18 implementation handed the Factory call the resolved `~/.factory` root,
which D35 rejects. The documents change with it: the CLI command
surface's plugin-install row and its exit-code row, and the runbook's Factory plugin paragraph
(H18).
**Depends on.** T32.
**Skills.** `typescript-dev`, `csharp-dev`.

### T34 — RED: the marketplace-route install contract (D43, D44, D47)

**Objective.** Pin the install route Factory actually loads, with the harness's commands as the
only interface to the harness's state.
**Files.** `dash/src/capture/harnesses/factory-plugin/marketplace.test.ts` (new),
`dash/src/capture/harnesses/factory-plugin/install.test.ts` (amended).
**Acceptance criteria.**
1. The install runs **`droid plugin marketplace add <marketplace root>`** and then
   **`droid plugin install kyberdash-factory@kyberdash --scope user`**, in that order, through an
   injected runner (so the test asserts the argv, not the effect). The runner is a seam, so no
   test needs a real `droid` on the machine.
2. The tree's exact layout is pinned: `~/.kyberdash/marketplaces/kyberdash/.factory-plugin/
   marketplace.json` and `.../kyberdash-factory/{.factory-plugin/plugin.json, hooks/hooks.json,
   hooks/kyberdash-factory.mjs}`; the marketplace manifest carries a `plugins[]` entry whose
   `source` is the **relative** `./kyberdash-factory`; and nothing is written under the Factory
   root.
3. The receipt names the marketplace (`kyberdash`), the identity
   (`kyberdash-factory@kyberdash`), the tree root and a SHA-256 per owned file.
4. The uninstall runs **`droid plugin uninstall kyberdash-factory@kyberdash --scope user`** and
   then **`droid plugin marketplace remove kyberdash`**, and afterwards removes the tree and the
   receipt.
5. The state vocabulary is pinned: `marketplace-tree-written`, `marketplace-registered`,
   `plugin-installed`, `unknown`; a missing `droid` CLI yields `unknown` with the named cause, and
   never "installed".
6. The harness-side state is read **only** through `droid plugin marketplace list` and
   `droid plugin list --scope user` — the test asserts that no other command is run and that the
   owner's settings file is never opened.
**Depends on.** T39 (the payload corrections change the tree's bytes, so the tree's digest cases
must see the corrected payload).
**Skills.** `test-dev`, `typescript-dev`.

### T35 — GREEN: the marketplace route (D43, D44, D47)

**Objective.** Implement the route: the marketplace tree, the harness-command driver, the
receipt, the state reader and the status lines.
**Files.** `dash/src/capture/harnesses/factory-plugin/marketplace.ts` (new — the tree writer, the
command driver and the state reader), `dash/src/capture/harnesses/factory-plugin/install.ts`
(amended — the tree becomes the marketplace tree, the receipt grows the marketplace and identity
fields, and the shipped tree-install path is removed), the receipt writer.
**Acceptance criteria.** T34 green, unweakened. The tree write is atomic per file (staged and
renamed) and the receipt is written **only after** the tree landed; a failed write leaves no
partial tree and no receipt. The harness's cache, its `installed_plugins` record and the owner's
`~/.factory/settings.json` are **never written** by KyberDash. The command driver has a bounded
timeout and a bounded output capture, and a missing or non-zero `droid` is reported as `unknown`
with the harness's own stderr quoted in the error — never as a success. The `droid` executable is
resolved from `PATH`, and the install reports which executable it used.
**Depends on.** T34.
**Skills.** `typescript-dev`.

### T36 — RED: the refusals, the collisions, the residue and the settings-file non-interference (D45, D46, D48)

**STATUS (D63): criterion 4 is SHIPPED; criteria 1, 2, 3 and 5 are NOT shipped.**

**Objective.** Pin what happens when the harness already holds a name KyberDash wants, and pin
that KyberDash never touches the owner's settings file.
**Files.** `dash/src/capture/harnesses/factory-plugin/marketplace.test.ts` (amended).
**Acceptance criteria.**
1. A marketplace named `kyberdash` already registered, and **not** the one KyberDash registered,
   is **refused** in strict mode (exit 1, nothing written) and kept with a warning in preserve
   mode (exit 0). The same holds for the plugin identity `kyberdash-factory@kyberdash` already
   installed by something else.
2. A plugin named `kyberdash-factory` in a **different** marketplace is **not** a conflict.
3. The refusal names the conflicting entry and prints the command that removes it.
4. **KyberDash writes nothing to `~/.factory/settings.json` and reads nothing from it**: the test
   writes a settings file with a foreign marketplace and a foreign plugin in it, runs the whole
   install, enable, disable and uninstall cycle with a fake `droid`, and asserts the file's bytes
   are unchanged and that no read of it occurred. **SHIPPED** (`marketplace.test.ts:223-249`).
5. A residue the harness's own uninstall leaves is **reported** — which registry, which entry, and
   the exact `droid plugin …` command that removes it — and is **never patched**: the test asserts
   the file's bytes are still unchanged after the uninstall reported the residue.
   **AMENDED (D59): the report is grounded in the harness's own answers, NOT in a captured prior
   state.** The shipped receipt holds `{version, marketplace, plugin, root, files[]}` and **no
   pre-install harness state**, so KyberDash cannot say "I removed an entry you had before" — it
   can say "the harness's own teardown removed the entry `x`, and here is the command that
   restores it **where such a command exists**". **Where no restoring command exists, the report
   says so plainly** rather than implying one. The test therefore asserts the report's shape —
   the named entry, the command where one exists, and the explicit absence where none does — and
   not a captured before/after diff.
**Depends on.** T34.
**Skills.** `test-dev`, `typescript-dev`.

### T37 — GREEN: the refusals, the residue report and the non-interference (D45, D46, D48)

**Objective.** Implement the refusal rules, the residue report and the guarantee.
**Files.** `dash/src/capture/harnesses/factory-plugin/marketplace.ts` (amended).
**Acceptance criteria.** T36 green, unweakened. The refusal reasons reuse the shipped vocabulary
(`conflict`, `drift`, `unreadable`), so the exit codes and the status lines stay one shape across
the harnesses. The residue report is a **status line and a structured field**, never a write.
**AMENDED (D59): the residue report carries the named entry and the restoring command where one
exists, and states plainly where none does** — it does not claim a captured prior state, because
the receipt holds none. **NOT SHIPPED** (D63): the refusal rules and the residue report are the
pending half of this task; the settings-file non-interference (T36's criterion 4) has shipped.
**Depends on.** T36.
**Skills.** `typescript-dev`.

### T38 — RED: the plugin payload corrections (D53, D54)

**STATUS (D63): HALF SHIPPED — criterion 2 shipped; criterion 1 is NOT shipped.**
**Objective.** Pin the two contract-level corrections the live evidence forced.
**Files.** `dash/src/capture/harnesses/factory-plugin/install.test.ts` (the hooks-file case),
`dash/src/capture/harnesses/factory-plugin/mapper.test.ts` (the `SubagentStop` case).
**Acceptance criteria.**
1. The plugin's `hooks/hooks.json` is keyed **directly by event name** — `SessionStart`,
   `UserPromptSubmit`, `PreToolUse`, `PostToolUse`, `Stop`, `SessionEnd` — with **no** top-level
   `hooks` wrapper, and each event's value is the array of matcher groups carrying a `hooks` array
   of command objects (D53). **NOT SHIPPED: the shipped hooks file still carries the `hooks:`
   wrapper and still registers the `SubagentStop` event.**
2. The `SubagentStop` event produces **no** span, and the mapper does not read a child session id
   from it (D54). **SHIPPED: the mapper and the contract declare no child-session span.**
3. The other seven events' spans are **unchanged** — same names, same attributes, same ids — so
   the correction cannot be made by accident.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T39 — GREEN: the plugin payload corrections (D53, D54)

**Objective.** Rewrite the hooks file in the documented shape, withdraw the `SubagentStop` span,
and regenerate the committed bundle.
**Files.** `dash/src/capture/harnesses/factory-plugin/install.ts` (the hooks file's bytes),
`dash/src/capture/harnesses/factory-plugin/mapper.ts` (the `SubagentStop` case), the contract leaf
(the emitted span kinds), the committed bundle
(`dash/src/capture/harnesses/factory-plugin/generated/plugin-source.ts`).
**Acceptance criteria.** T38 green, unweakened; the bundle's drift check is clean. The wire
contract's **version stays `1`** — the correction removes a span kind from the emitted set and
changes no span name or attribute key, so no consumer's claim changes. The receipt's digest
follows the new bytes automatically.
**STATUS (D63): PARTLY SHIPPED — the `SubagentStop` withdrawal shipped; the hooks file's bytes and
the bundle regeneration did not.** **The remaining work is a correction to a shipped artefact**: the
marketplace tree was installed with the wrapper-bearing hooks file, so the receipt's digests
change when this lands, and the committed bundle must be regenerated with them.
**Depends on.** T38.
**Skills.** `typescript-dev`.

### T40 — RED: the native span claim's withdrawal (D50)

**Objective.** Pin that the `droid` adapter claims **no** native span, and that the ingest no
longer treats native rows specially.
**Files.** `dash/src/canon/adapters/droid.test.ts` (the native-claim cases),
`dash/src/canon/ingest.test.ts` (the native path).
**Acceptance criteria.**
1. A `gen_ai` span is **quarantined as `unclaimed`** — the root (`gen_ai.agent.name=droid`), the
   `chat` child and the `execute_tool` child alike — while the plugin envelope is still claimed
   with score 1.
2. The ingest no longer supersedes stored native rows when a plugin row arrives for the same
   session (D23's rule is retained as a rule, but nothing exercises it).
3. The native counter reasons are gone: a native span's counter classes are declared by the
   **plugin** path's reasons or by the common path, and no native-specific reason string remains
   in the adapter.
4. The metrics path is untouched: `POST /v1/metrics` still answers 200 and the points still land
   in `metric_point`.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T41 — GREEN: the native span claim's withdrawal (D50)

**Objective.** Remove the native claim, the ingest's native supersede logic and the native
reasons.
**Files.** `dash/src/canon/adapters/droid.ts`, `dash/src/canon/ingest.ts`.
**Acceptance criteria.** T40 green, unweakened. The adapter's `detect` claims the plugin envelope
and nothing else; `isDroidNativeSpan` and the native branch of `normalize` are gone, together
with the ingest's `droidCoveredSessions` / `supersedeStoredNativeRows` logic and the
`DROID_NATIVE_SUPERSEDED_REASON` constant. Every other adapter's cases stay green, and the
plugin-envelope cases are unchanged.
**Depends on.** T40.
**Skills.** `typescript-dev`.

### T42 — RED: the consent gate over the native and quarantine routes (D52, amended by D57)

**Objective.** Pin that no Factory content from the **native OTLP route** reaches the store while
KyberDash's consent is off — **in a record, or in a quarantine packet**.
**AMENDED (D57): the scope is the native and quarantine routes, and the objective says so.** The
**file source's** own first-message fragment is **outside** this gate — it is the owner's
transcript read by KyberDash's file source, it predates this branch, and the plan does not change
it (D21) — and the **plugin's** tool arguments and results are **byte counts**, never content, so
there is nothing there to gate (D61).
**AMENDED (D61): the earlier "or in a metric point" clause is WITHDRAWN.** A metric point is a
different surface from a span: Factory's documented native metrics are `droid.*` under `legacy`
and `gen_ai.*` under `genai`, and **no evidence in this session shows either carrying message
content**, while the metric point's own attribute column is a generic string map that any
collector could populate. Claiming a metric-point gate would therefore be a claim about a
surface this plan has not measured, and would risk a gate that looks comprehensive and is not.
**Metric-point content is out of scope for T42/T43**, and if a Factory metric carrying content is
ever observed, it is separate work with its own evidence — the assumption is recorded as **A22**.
**Files.** `dash/src/canon/ingest.test.ts` (the content-gate cases).
**Acceptance criteria.**
1. With the consent file off (or absent, or malformed), a Factory span carrying
   `gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.tool.call.arguments`,
   `gen_ai.tool.call.result` or a `droid.message.*` `message.content` is stored with **none** of
   that text — **and the quarantine packet for an unclaimed Factory span carries none of it
   either**.
2. With the consent on, the text is stored as it is today.
3. **Another harness's** `gen_ai.input.messages` (the Copilot and pi shapes) is untouched — the
   gate is scoped to Factory's own spans.
4. The gate is applied **before** normalization and **before** the quarantine write, so no
   intermediate holds the text.
5. **The gate does not touch the file source's records**: a file-sourced Factory record keeps its
   `userMessage` fragment whatever the consent file says, and the case is pinned so the gate's
   scope is a tested boundary rather than an accident (D57).
**Depends on.** T41 (the native spans are quarantined by then, so the quarantine case is
reachable).
**Skills.** `test-dev`, `typescript-dev`.

### T43 — GREEN: the consent gate over the native and quarantine routes (D52, amended by D57)

**Objective.** Implement the gate.
**Files.** `dash/src/canon/ingest.ts` (the seam), a small consent-reading leaf
(`dash/src/canon/droid-content.ts` — a leaf, `node:` built-ins only, as the capture-side consent
reader is).
**Acceptance criteria.** T42 green, unweakened. The gate reads KyberDash's Factory consent file
(`~/.kyberdash/factory-plugin.json`) and the `KYBERDASH_FACTORY_CONTEXT` override, and treats
anything other than an explicit on as **off**. It strips the content-bearing attributes of
**Factory's own spans** — the `droid.*` span names, and the `gen_ai.*` spans of a trace whose root
declares `gen_ai.agent.name = 'droid'` — and touches nothing else. The stripped attributes are
never written anywhere: not to a record, not to a quarantine packet.
**AMENDED (D61): the earlier "not to a metric point" clause is WITHDRAWN**, for the reason T42
gives: no evidence shows a Factory metric carrying message content, so the gate does not claim a
surface it has not measured (A22). **AMENDED (D57): the gate's reach is the native and quarantine
routes only.** It does **not** read,
rewrite or suppress the **file source's** records, whose `userMessage` fragment is the owner's own
transcript and is outside this branch's scope (D21), and it has nothing to do with the **plugin's**
tool attributes, which are byte counts (D61).
**Depends on.** T42.
**Skills.** `typescript-dev`.

### T44 — RED: the native export's effective shape on the status surface (D51)

**Objective.** Pin that the row reports the export's shape, not only its endpoint.
**Files.** `dash/src/capture/harnesses/factory-plugin/native-export.test.ts` (amended),
`dash/src/capture/harnesses/factory.test.ts` (the status row).
**Acceptance criteria.**
1. The reader resolves the **format** (`legacy` \| `genai`), the **granularity** (`user` \|
   `aggregate`) and the **message-content switch**, with Factory's own precedence: the
   organisation's `telemetry.format` wins over `OTEL_TELEMETRY_FORMAT` and an unrecognised value
   falls back to `legacy`; the granularity is **most-restrictive-wins**; `aggregate` overrides
   every content opt-in; the organisation's `logMessageContent` wins over
   `OTEL_LOG_MESSAGE_CONTENT` when it is set at all.
2. The status row names the effective shape and states, in fixed words, that the metrics are
   persisted, that **no native span is claimed**, and that **no Factory content is stored while
   the consent is off**.
3. The reader reads the organisation's managed-settings cache it already reads, and **no new read
   of the owner's `~/.factory/settings.json`** is introduced.

**AMENDED (D51, D57, D58) — this task's criterion 2 is withdrawn and replaced, and two criteria
are added.** Criterion 2's fixed words were **false** and are replaced by *"while KyberDash's
consent is off, KyberDash's own Factory capture stores no prompt text"* (D57); the row names the
file source's route as one the consent does not govern. The two added criteria are the
organisation's **`enabled`** master switch, which the reader now resolves and the row reports
beside the endpoint verdict, and the granularity's **unreadable-value fallback**, which resolves
to `aggregate` and not to `user` (D58). **Criterion 1 stands as written.**
**AMENDED (D63) — WHAT ACTUALLY SHIPPED, AND WHAT THIS TASK STILL OWES.** The **shape** shipped:
the reader resolves the format, the granularity and the content switch (`native-export.ts:288-378`)
and the row reports them. **The three D57/D58 criteria did not ship**: the row still reads
*"no Factory content is stored while KyberDash's consent is off"* (`factory.ts:147-151`), the
reader does not resolve the organisation's `enabled` master switch, and the reader resolves an
unreadable granularity to `user` (`native-export.ts:334`) rather than to `aggregate`. **Those
three are carried by T47/T48, not by this task** — this task's record is that the shape landed
and that the two criteria D58 added were never part of the shipped change.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T45 — GREEN: the native export's effective shape on the status surface (D51)

**Objective.** Extend the native reader and the status row.
**Files.** `dash/src/capture/harnesses/factory-plugin/native-export.ts`,
`dash/src/capture/harnesses/factory.ts`.
**Acceptance criteria.** T44 green, unweakened. The reader's verdict is a **shape**, not a
boolean: the endpoint verdict, the format, the granularity and the content switch, each with the
source it came from. The status lines state the consequence in fixed words and never echo
owner-written text. The shipped endpoint verdict's wording is unchanged, so a reader of the old
status sees the same line.
**AMENDED (D63): SHIPPED.** The reader resolves the shape and the row reports it. **What did not
ship with it** is the organisation's `enabled` master switch and the granularity's unreadable-value
fallback — both are T48's, and the row's consent wording is T47's.
**Depends on.** T44.
**Skills.** `typescript-dev`.

### T46 — The harness-side probe (owner-run, not a CI gate)

**Objective.** Establish — by measurement, not by assumption — the harness-side facts the
install contract depends on, and record them in this plan's assumption table.
**Files.** `docs/dash/runbook.md` (the procedure), `artifacts/evidence/factory-capture-live/<UTC-stamp>/`
(gitignored).
**Acceptance criteria.** The probe records: the **registered marketplace name** for a local path
(A12); the shape of the `extraKnownMarketplaces` entry and of the `enabledPlugins` entry (A14);
**what `droid plugin uninstall` and `droid plugin marketplace remove` actually clean up** — the
settings entries, the cache, the `installed_plugins` record (A13); whether a re-install **upgrades**
an already-installed plugin, and which command does it; which **scope flags** those commands
accept (`uninstall` and `update` are documented with `--scope project` examples, and the user
scope is what the install uses); and whether the settings path follows `HOME` — which is what
would make the whole flow automatically testable against the real CLI. Each answer is written into
§12's assumption table with its evidence. **A contradiction with the plan is reported as a
failure**, never worked around: if the harness's uninstall leaves a residue, D45's residue rule
stands and the plan says so; if the settings path does not follow `HOME`, the plan says that the
flow cannot be automatically tested against the real CLI and keeps the fake-runner tests.
**Depends on.** T35.
**Skills.** `app-docs-standard`.

### T47 — RED: the status surface's corrected consent wording and the export's effective shape (D57, D58)

**Objective.** Pin that the row says **what the consent governs** and **what it does not**, and
that the reported shape is the export's effective one.
**Files.** `dash/src/capture/harnesses/factory.test.ts` (the status row's fixed words),
`dash/src/capture/harnesses/factory-plugin/native-export.test.ts` (amended — the `enabled` switch
and the granularity fallback).
**Acceptance criteria.**
1. The row's consequence line reads, in fixed words: *"Factory's native metrics are persisted;
   Factory's native spans are not claimed; while KyberDash's consent is off, KyberDash's own
   Factory capture stores no prompt text."* — and the shipped *"no Factory content is stored while
   KyberDash's consent is off"* is **gone** (D57). **The ON branch is corrected in the same shape**:
   the shipped *"Factory content is stored while KyberDash's consent is on"* is equally false — the
   file source stores its fragment whatever the consent says — and the ON wording becomes *"while
   KyberDash's consent is on, KyberDash's own Factory capture stores the plugin's prompt text"*.
   **Both branches name the capture, not "Factory content"**, because the two words are the whole
   difference between an accurate statement and a false one.
2. The row names the **file source**'s route as one the consent does **not** govern, and the
   **native route**'s content as the harness's own switch — never as KyberDash's consent (D57).
3. The reader resolves the organisation's **`enabled`** master switch: the organisation's
   `telemetry.enabled` wins over `OTEL_CUSTOMER_ENABLED` when it is set at all, and the row reports
   the resolved value and its source beside the endpoint verdict (D58).
4. A **granularity value that cannot be read resolves to `aggregate`**, not `user` — the shipped
   reader resolves it to `user`, which is the fail-open direction on a privacy control (D58).
5. The reader reads the organisation's managed-settings cache it already reads, and **no new read
   of the owner's `~/.factory/settings.json`** is introduced.
**Depends on.** none.
**Skills.** `test-dev`, `typescript-dev`.

### T48 — GREEN: the status surface's corrected consent wording and the export's effective shape (D57, D58)

**Objective.** Correct the status row's wording and complete the native reader's shape.
**Files.** `dash/src/capture/harnesses/factory.ts`,
`dash/src/capture/harnesses/factory-plugin/native-export.ts`.
**Acceptance criteria.** T47 green, unweakened. The consequence line carries the corrected fixed
words — **both** branches: *"… while KyberDash's consent is off, KyberDash's own Factory capture
stores no prompt text"* and *"… while KyberDash's consent is on, KyberDash's own Factory capture
stores the plugin's prompt text"* — and the row names the routes the consent does not govern. The reader's verdict is a
**shape**, not a boolean: the endpoint verdict, the format, the granularity, the content switch and
the `enabled` switch, each with the source it came from, and the granularity's unreadable value
resolving to `aggregate`. The status lines state the consequence in fixed words and never echo
owner-written text. The shipped endpoint verdict's wording is unchanged, so a reader of the old
status sees the same line.
**Depends on.** T47.
**Skills.** `typescript-dev`.

### T49 — RED: the upgrade path (D65)

**Objective.** Pin that a superseded bundle of ours is **upgraded** — not refused as an owner edit
and not reported as `unchanged` — and that the harness's cached copy is refreshed through the
harness's own command.
**Files.** `dash/src/capture/harnesses/factory-plugin/install.test.ts` (amended),
`dash/src/capture/harnesses/factory-plugin/marketplace.test.ts` (amended).
**Acceptance criteria.**
1. **The classification.** A tree the receipt proves — the receipt names this root and records a
   digest for every owned file — whose every file still matches its recorded digest, and whose
   content is not the current bundle, is an **upgrade**: not `edited`, and not `same`. The fixture
   is a real older bundle: the tree's script differs from the current `FACTORY_MARKETPLACE_CONTENT`
   and the receipt records that tree's own digests.
2. **The rewrite.** The install rewrites the tree with the current bundle and rewrites the
   receipt, and the receipt's recorded digests are the current bundle's.
3. **The harness refresh.** With the plugin installed at the harness, the install runs `droid
   plugin update kyberdash-factory@kyberdash --scope user` and **no** `droid plugin install` — the
   argv the fake runner recorded is the assertion.
4. **The install path.** With the marketplace registered and the plugin **not** installed, the
   install runs `droid plugin install kyberdash-factory@kyberdash --scope user` and **no** `droid
   plugin update`.
5. **The restored registration.** With the plugin installed and the marketplace registration
   missing, the install runs `droid plugin marketplace add <the marketplace root>` **before** `droid
   plugin update kyberdash-factory@kyberdash --scope user`, in that order.
6. **The ownership rule.** A tree with **no** receipt, an unreadable receipt, or a receipt naming
   another root is `foreign` — refused in strict mode (exit 1, nothing written) and kept in
   preserve mode (exit 0) — **even when its bytes match an older bundle of ours**; and a tree whose
   bytes no longer match the receipt stays `edited`, with the same refusal and keep.
7. **`--if-installed`.** With the harness's registry reporting the plugin installed and KyberDash's
   own tree absent, the tree is written **and** the harness's copy is refreshed with `droid plugin
   update …`; with the registry reporting it not installed, nothing is written and no harness
   command runs.
8. **`unchanged`.** With the current bundle on disk and the plugin installed, the outcome is
   `unchanged` and **no harness command runs at all**; the same tree with the plugin **not**
   installed takes the install path.
9. **The tokens.** An upgrade prints `factory plugin: upgraded <the marketplace tree>`; a dry run
   prints `factory plugin: would-upgrade <the marketplace tree>` and writes nothing; and **no
   upgrade prints `installed`**.
10. **The refusal.** A harness that refuses the update is refused in strict mode (exit 1) and kept
    with a warning in preserve mode (exit 0), and **neither prints `upgraded`**.
**Depends on.** none — the marketplace route (T34/T35) has shipped.
**Skills.** `test-dev`.

### T50 — GREEN: the upgrade path (D65)

**Objective.** Make the shipped installer upgrade a superseded bundle and refresh the harness's
cached copy.
**Files.** `dash/src/capture/harnesses/factory-plugin/install.ts`,
`dash/src/capture/harnesses/factory-plugin/marketplace.ts`,
`dash/src/capture/harnesses/factory-plugin/install-command.ts` (the verb's documented stdout
contract).
**Acceptance criteria.** T49 green, unweakened. The classifier gains the `upgrade` kind beside
the five it has, and the ownership rules are unchanged: `edited` for a tree whose bytes drifted,
`foreign` for a tree no receipt proves. The install writes the tree **and** the receipt with the
current bundle when the tree is `absent` **or** `upgrade`, and the receipt is written only after
the tree landed. The harness-side command is chosen from the harness's own registry: the update
path for an installed plugin, the install path for a plugin that is not installed, and the
marketplace registration restored **before** the update when an installed plugin's registration is
missing. `plugin install` is never run against an installed plugin. `--if-installed` decides from
the harness's registry, and the refresh runs even when the tree had to be written for the first
time. The `unchanged` outcome requires **both** a current tree and an installed harness copy, and
no harness command runs in that case. The dispositions and the stdout tokens are `installed`,
`would-install`, `unchanged`, `upgraded` and `would-upgrade`; a harness refusal never prints a
success token, and the refusal quotes the harness's stderr and names the exact command that
completes the refresh. The receipt's shape is unchanged — `{version, marketplace, plugin, root,
files[]}` — and the receipt forms this plan already requires the installer to read stay readable.
The verb's documented stdout contract in `install-command.ts` names the two new tokens and the
refusal reporting.
**Depends on.** T49.
**Skills.** `typescript-dev`.

## 6. Dependency graph

```
T1 ─► T2 ─┐
           ├─► T4 ─► T6 ─┐
T3 ────────┘             ├─► T8 ─► T10 ─► T18 ─┐
T5 ─► T6 ─┘             │                     │
T7 ─► T8 ─┘             │                     │
           T9 ─► T10 ───┘                     │
T11 ─► T12 ─► T13 ───────────────────────────┤
T15 ─► T16 (needs T11, T12) ──────────────────┤
T14 (independent)                              │
T17 ─► T18 ─────────────────────────────────────┤
T19 (needs T4, T12) ────────────────────────────┤
T13 ─► T22 ─► T23 ─► T26 ──────────────────────┤
T12 ─► T24 ─► T25 ─────────────────────────────┤
T16 ─► T27 ─► T28 ─────────────────────────────┤
T16 ─► T29 ─► T30 ─────────────────────────────┤
T18 ─► T31 ────────────────────────────────────┤
T18 ─► T32 ─► T33 ────────────────────────────┤
T38 ─► T39 ─► T34 ─► T35 ─► T46 ──────────────┤
           T34 ─► T36 ─► T37 ──────────────────┤
T40 ─► T41 ─► T42 ─► T43 ─────────────────────┤
T44 ─► T45 ───────────────────────────────────┤
T47 ─► T48 ───────────────────────────────────┤
T49 ─► T50 ───────────────────────────────────┤
                                              ▼
                                             T20 ─► T21
```

T14 is independent and may run at any time. T15/T16 are authorized (D12 branch A) and hang off
T11/T12 for the vocabulary; they do not block the plugin path.
No dependency is declared for file-scope overlap alone: T1/T5/T7/T11/T14/T17 have disjoint
scopes and may run concurrently.

**The D43–D56 amendment's tasks (D56).** T38–T39 correct the plugin payload and were declared the
**precondition** for T34, because the tree's digest cases must see the corrected payload. **AMENDED
(D63): that precondition is now historical — T34/T35 shipped before T39's hooks-file half landed**,
so the tree was installed with the wrapper-bearing hooks file and the `SubagentStop` registration.
The pending T39 half is therefore a **correction to a shipped artefact**, not a precondition for
an unshipped one, and T39 must regenerate the committed bundle when it lands. T36–T37 add the
refusals, the residue report and the settings-file non-interference. T40–T41 withdraw the native
span claim, T42–T43 add the consent gate over the native and quarantine routes, and T44–T45 make
the status surface report the export's effective shape. T46 is the owner-run probe that
establishes the harness-side semantics; it hangs off T35 and feeds the closeout. T20's harvest
carries the ADR 0031 amendment (H19) and the corrected install route (H20–H25).

**The D57–D64 amendment's tasks (D63, D64).** The task accounting is re-based on the working tree
as it stands, because the branch has moved on since the paragraph above was written. **Shipped:**
T34/T35 (the marketplace route), T38's **SubagentStop** half (the mapper and the contract declare
no child-session span), T40/T41 (the native span claim's withdrawal) and T44/T45 (the export's
effective shape on the status surface). **Partly shipped:** T36/T37 (**the settings-file
non-interference shipped; the name-conflict refusal and the residue report did not**), T38 (**the
`SubagentStop` half shipped; the hooks-file half did not** — the shipped hooks file still carries
the `hooks:` wrapper and still registers the `SubagentStop` event), T44/T45's **D58 criteria did
not ship** (the organisation's `enabled` switch is unresolved and an unreadable granularity still
resolves to `user`), and T46 (**some of the harness's answers are recorded; the probe is not
closed**). **Still pending:** T42/T43 (the consent gate over the native and quarantine
routes). **New in this amendment: T47 and T48**, which correct
the status surface's consent wording and the native reader's `enabled` switch and granularity
fallback (D57, D58) — both hang off nothing, because both are corrections to shipped code and to
the plan's own text, and T20's harvest carries the corrected consent scope and the
organisation-control position (H26).

**The D65 amendment's tasks (D65).** T49/T50 add the **upgrade path**: a receipt-proven tree
whose content is not the current bundle is **upgraded** rather than refused as an owner edit or
reported as `unchanged`; the tree and the receipt are rewritten with the current bundle; and the
harness's cached copy is refreshed through `droid plugin update kyberdash-factory@kyberdash
--scope user`, the command chosen from the harness's own registry. They hang off nothing, because
the marketplace route they correct (T34/T35) has shipped, and T20's harvest carries the upgrade
path into the architecture, the runbook and the command surface (H27–H29).

## 7. Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Factory's plugin discovery path or `${DROID_PLUGIN_ROOT}` expansion differs from the expectation | **disproven as a risk — it happened, and it is now settled** | The discovery path was **not** `~/.factory/plugins/<name>/`: the shipped install produced "No plugins installed" and no plugin spans (§2.6, A1). The route is now the harness's own marketplace and cache (D43), and `${DROID_PLUGIN_ROOT}` was **measured** to expand to the cached path, not the source path — so the plugin's hook command stays portable. What remains is the ordinary risk that the harness's plugin lifecycle changes, which the "install route is wrong again" row below carries |
| The hook payload's field names differ from the expectation (A3) | medium | The documented payload carries `session_id`, `tool_name`, `tool_input`, `tool_response`, `session_duration_ms` and `message_count`; it does **not** document a model field, which is why the model comes from the source that measured it (D39) — the plugin names one only when the payload carries it, and the file source names it from the companion file. The mapper tolerates every absent field; no attribute is fabricated; the contract is versioned, so a wrong guess is a version bump, not a data corruption |
| The hook's `session_id` differs from the jsonl `session_start` id (A2) | medium | The exact-match pairing degrades to `not_measurable`, never to a wrong number; the live check reports which case held |
| A Factory hook event blocks the host while the script posts | low | The POST has a ≤ 2 s abort deadline; every error is swallowed; the script exits 0; the spool absorbs a failed delivery |
| The shared-transport extraction drifts the OpenCode bundle | low | The committed bundle's drift test is the witness and is in T3's scope; `bundle-opencode-plugin.mjs --check` must exit 0 |
| Two adapters named `droid` | low | D7 pins **one** adapter, which claims the plugin envelope and nothing else (the native claim is withdrawn, D50), checked under the registry's duplicate-name guard |
| Native spans and plugin rows double-count in one `droid` session | medium | D23 pins the precedence rule (plugin envelope first; a native span for a session that already has plugin rows is evidence, excluded from turns, tools and duration), T15 pins it in a test, and the rule is stated in the ADR (H11) before the branch ships |
| Native export is silently absent where an organisation pins an endpoint | medium | D26 makes the row report the native path as unavailable with that named cause, never as working; on this machine no organisation pins an endpoint (§2.5). The plugin path is unaffected either way |
| Plugin rows and file rows double-count in one `droid` session | **high — it is the observed defect** | D28 pins the source-precedence rule (the plugin defines the session's turns, the file defines its counters, neither is counted twice); T22 pins it in the pairing test and T23 implements it; the rule is inert for every other harness, and the paired session's totals are the file row's counters rather than a sum of two sources |
| The source-precedence rule changes a published figure for a Factory session that has no plugin rows | low | the rule applies only to a **mixed** session; a file-only and a plugin-only `droid` session keep today's figures, and `dash/src/canon/sessions.test.ts` stays green (T23) |
| A child session's tokens are unavailable because the child has no companion file of its own | medium | D29 pairs a child from its **own** companion file through the ordinary exact-id pairing, and the parent's child-usage map is never a token source (A9); a child with no companion file of its own reads `not_measurable` with a stated reason and never the parent's counters |
| The native metrics path answers 500 in production | **high — it is every metrics request the running service receives** | D31 gives the metrics a destination (the canon store's `metric_point` table — D38) and the production sink the port that persists them; T27 pins the production wiring and T28 implements it. The writer's refusal rule is unchanged, so a sink that still cannot persist refuses the batch rather than dropping it |
| The metrics are persisted but nothing projects them | medium | D31 keeps the destination and makes the projection a todo (T20); the telemetry inventory states that the metrics are stored and not yet projected, so no reader is told a metric was measured and shown as one (the honest-unobservability rule) |
| The stored record's status loses the adapter's error evidence | **high — a policy-denied tool call reads as an ordinary one** | D32's rule never downgrades an error status at the receiver-timing seam, and it is inert for every other harness (the `droid` adapter is the only adapter that assigns a status); T29 pins the stored record and T30 implements it |
| The legacy update fixtures are rewritten instead of modernized | medium | D33 records that the plugin count was incidental to what the three fixtures proved, that their substance is unchanged, and that the modernization states it (each plugin entry names its harness); T31 updates them in place, keeps their names, their helper and their other assertions, and the diff shows that no other assertion moved |
| The updater's Factory call drifts into strict mode | medium | D34 pins the mode in the RED test as the exact argument list, including `--preserve`; D11 already mandates preserve mode for `kyber-weave update`, so a strict-mode Factory call would contradict a decision the plan had already made — and it would turn every owner-edited Factory plugin into a failed step |
| The Factory plugin is installed under a root Factory never reads | **disproven — it no longer happens** | D35 keeps the Factory root with the installer (`$FACTORY_DIR` else `~/.factory`, the resolution the file source uses), hands the Factory call **no root**, and refuses the flag. **AMENDED (D43, D55): the installer no longer writes under the Factory root at all** — the owned unit is the Kyber-owned marketplace tree under `~/.kyberdash/`, so the plugin and the file source no longer share a root and the D28 pairing cannot drift apart |
| A reader takes the file source's `unknown` placeholder for a measured model | low | D40 names the placeholder as the file source's own string, in §3.2, in §3.5 and in the telemetry inventory (H12); the plugin path never produces it; changing it is a file-source change outside this plan's scope (D21) and is recorded as a follow-up, not done |
| Two byte-identical decoded metric points collapse into one row | low | D42 keeps the content-digest identity and corrects the plan's phrasing to "one row per **distinct** decoded point"; the duplicate case is a re-delivery of the same point, which the digest makes idempotent; the multiplicity is not preserved, no shipped test pins the collision, and the gap is a todo for T20 |
| The native path's availability is claimed before the row reports it | low | D41 replaces the marker with the shipped fact: the row carries the native-export state and its named cause on every status surface (commit `e140472`), so the closeout claims what ships and nothing more |
| The command surface and the runbook keep describing the flag as inert for Factory | medium | D35 names the documents that must change — the CLI command surface's plugin-install row and exit-code row, and the runbook's Factory plugin paragraph (H18) — and T33 carries the edits with the code |
| The install route is wrong again because Factory's plugin lifecycle changes | medium | The route is the harness's **own** documented lifecycle (a marketplace, `droid plugin install`, the `enabledPlugins` setting), not a private convention, so a Factory change breaks it loudly rather than silently: the plugin stops being loaded, the status row's harness-side state changes, and the unit stays Kyber-owned and removable. T46 records the harness's answers against the real CLI, and the live check (T21) remains the detector |
| The harness's uninstall leaves a residue in the owner's settings file | medium | D45's rule is **report, never patch**: the residue is named (which registry, which entry) with the exact `droid plugin …` command that removes it, and KyberDash writes nothing either way — so the residue is visible and reversible by the owner. **AMENDED (D59): the report says where a restoring command exists and says plainly where none does** — the shipped receipt holds no pre-install harness state, so KyberDash reports what the harness's own teardown did rather than claiming a captured before/after. T46 establishes what the harness actually cleans (A13) instead of assuming it |
| The harness's CLI is absent, or its output is not what the installer expects | medium | The installer's state is `unknown` with the named cause, never a success; the command driver has a bounded timeout and bounded output capture; and the harness-side state is read through the harness's own CLI rather than by parsing a directory KyberDash does not own |
| The owner already holds a marketplace or a plugin named `kyberdash` | medium | D46 refuses the install with a named conflict and prints the command that removes the conflict; it never takes a name it does not own. **AMENDED (D59): the refusal and the residue report cover KyberDash's own actions only** — "the owner's registry is exactly as it was found" is **not** a promise KyberDash can make about the harness's own commands, and the plan no longer makes it |
| The native path is reported as working while it claims nothing | **high — it is the observed defect** | D50 withdraws the native span claim and D51 makes the status surface report the export's **effective shape** — format, granularity, content switch, and (D58) the organisation's `enabled` switch — and state in fixed words that the metrics are persisted, that no native span is claimed, and that **while KyberDash's consent is off, KyberDash's own Factory capture stores no prompt text** (D57). T44/T45 pin the wording; the shipped endpoint-only reader is the pre-fix state |
| Factory content reaches the store through the native export while the consent is off | **high — the export carries the user's prompt** | D52's gate covers the **native and quarantine routes** into the store — the record and the **quarantine packet** — and it is applied before normalization and before the quarantine write, so the text is never written at all. **AMENDED (D61): the metric point is not in the gate's scope** — no evidence shows a Factory metric carrying message content, and the metric route is out of scope (A22). T42/T43 pin it, including the case of another harness's `gen_ai.input.messages`, which the gate must not touch. **AMENDED (D57): the gate's scope is the native and quarantine routes only** — the file source's own first-message fragment is outside it (D21), and the plugin's tool attributes are byte counts, never content (D61) |
| **The status surface overstates its own privacy behaviour** | **high — it is the observed defect** | The shipped row says *"no Factory content is stored while KyberDash's consent is off"*, and that is **false**: the Factory file source stores the first user-message fragment whatever the consent file says (D57). D57 narrows the claim to what the consent governs; D58 fixes the fixed words; **T47/T48** correct the shipped wording and the reader's shape. Until they land, the branch carries a status line that overstates its own privacy behaviour, and the closeout must not claim the corrected wording |
| The file source's prompt fragment is read as the consent's failure | medium | D57 names the route explicitly: the fragment is the **owner's own transcript**, read by KyberDash's file source, pre-existing and outside this branch's scope (D21) — not the capture plugin recording something it was told not to. The status surface names it (T47/T48) and the telemetry inventory states it (H26), so no reader has to infer it |
| The organisation's `allowManagedHooksOnly` makes the capture silently inert | medium | D60: the install succeeds, the plugin registers as enabled, and **no hook fires**; the harness's own CLI does not report which hooks a scope loads, so KyberDash **cannot** detect it. The position is the honest one — the surfaces must not read "installed" as "capturing" — and the gap is filed as a todo (D62) with the candidate remedies the next decision needs |
| The organisation's `strictKnownMarketplaces` or `strictEnabledPlugins` refuses the route | medium | D60: the refusal is **visible** — the harness's own command fails and the installer reports it with the harness's stderr quoted (`install.ts:517-518`). KyberDash works around **none** of them: the only way to add a marketplace or an enablement without the harness's commands is to write the owner's settings file, which D48 forbids and ADR 0026 exists to prevent |
| The quarantine table grows without bound | medium | D61/D62: the **packet** is bounded at 256 KiB each, but **no bounded retention or re-quarantine policy for the rows themselves was identified** — the rows are cleared only by a wipe. With the native claim withdrawn (D50), every native Factory span the harness sends lands there, so the row count is this branch's own consequence. The concern rides on the existing `kyberdash-quarantine-harness-index` todo, whose neighbouring question names payload retention; **filing a dedicated quarantine-retention todo is T20's work** (D62) |
| Consent is read as a purge or a retention control | medium | D62: turning the consent off does not remove what is already stored, `records.raw` is never purged, and a backfill re-derives content from it — so an ingest-time strip is **not retroactive**. The consent is a capture switch, and the documents say so (H26) |
| The plugin's hooks file relies on undocumented tolerance | medium | D53 rewrites it in the documented shape (keyed directly by event name); the shipped wrapper was the `settings.json` shape and worked only by tolerance, which a Factory upgrade may remove. T38/T39 pin the shape and the fact that the other seven events are unchanged. **AMENDED (D63): T38's `SubagentStop` half has shipped; the hooks-file half has not** — the shipped file still carries the `hooks:` wrapper and still registers the `SubagentStop` event |
| The plugin's `SubagentStop` span names the parent session as the child | medium — it was the shipped behaviour | D54 withdraws the span, because the documented payload carries no child identity; a child session's own rows are unaffected, because the child's own events fire in the child's session. T38/T39 pin the withdrawal, and the live check confirms the payload's fields (A3, A4) |
| Two tool calls in one session collapse into one row when the payload carries no per-call id | medium | The documented tool payload names `tool_name`, `tool_input` and `tool_response` but no call id, and the mapper anchors a tool span on the call id when it has one and on the session id when it does not — so the two hooks of one call would still share a span id, but a second call in the same session could land on the first one's row. A3 records it as open, the live check settles it, and the contract's anchor is a named change if the payload carries no per-call identity |
| The harness refuses the refresh command, or does not accept `--scope user` for it | medium | D65: the refresh command is chosen from the harness's **own registry**, never guessed, and a refusal is the harness's answer — reported with its stderr quoted, refused in strict mode (exit 1) and kept with a warning in preserve mode (exit 0). **No refusal is reported as a success.** The documented form of the command is `droid plugin update [plugin@marketplace] --scope <scope>` and the documented example uses `--scope project`, while the plugin is installed at **user** scope; which scope values the command accepts is open (A23) and is settled by T46's probe. The fallback to `plugin install` is **not** available, because Factory 1.1.0 refuses an install of an already-installed plugin |
| A refused refresh leaves the harness's cached copy stale, and no later install retries it | medium | D65: the tree is rewritten before the harness is asked, so a refused refresh leaves KyberDash's own tree current and the harness's copy stale. Because `unchanged` requires only a current tree and an installed harness copy, a later install does **not** retry the refresh — so the refusal is reported with the exact command that completes it (`droid plugin update kyberdash-factory@kyberdash --scope user`), which is D59's residue rule: report, never patch. Whether the harness's own plugin list reports an installed version, which would let a later amendment detect a stale cached copy, is open (A23) |

## 8. Out of scope

The sibling harness coverage gap (Cursor, Warp, ZCode, Devin, Command Code) is filed as a todo
under the todo index, not implemented here (D21). So is the Factory file source's even token
split, with the evidence that `lastCallTokenUsage` exists and would remove the approximation (the
companion file's `childInclusiveTokenUsageBySessionId` is the parent's aggregate of its children,
not a per-call breakdown — A9, D29). A rewrite of the Factory file import is not in scope.
No change to `dash/web` or `dash/tray`. No change to the shared projection's derivation rules
beyond what T13 proves is missing **and D28 specifies** — the one droid-scoped change, the Factory
source-precedence rule, and nothing else. The canon projection's **consumption** of the native
OTLP metrics is out of scope too: the metrics are persisted (D31), not projected, and their
projection is a todo for T20 to file. **No read and no write by KyberDash to any Factory settings
or hooks file, ever (D48)** — the harness's own commands may change the harness's own settings
file across a marketplace install, and that is the harness's act, reported and never patched
(D45). **The native span claim is withdrawn rather than repaired (D50)**: restoring it — a
group-scoped claim in the **shared** attribution contract, a model chain that lets a native
per-turn model reach the dashboard, and a native-content consent gate — is a todo for T20 to file,
not work in this branch. So is the per-turn model from the transcript's `message.modelId` (D39
notes no shipped reader reads it): the file source's even token split and its model handling stay
as they are (D21).

**AMENDED (D57, D62).** Four further boundaries are now named, and none of them is work in this
branch. **The Factory file source's first-message fragment** is the owner's own transcript, read
by KyberDash's file source, and it stays as it is: changing it is a file-source change, which D21
excludes, and the consent file does not govern it — the plan says so rather than implying the
consent does (D57). **The quarantine table's retention** is not this branch's to set: the packet
is bounded at 256 KiB, the table's growth is not bounded, and no retention or re-quarantine policy
was identified (D61, D62). **Consent is not a purge or a retention control** — it governs
capture, not what the store keeps, and `records.raw` is never purged, so a backfill re-derives
content from it and an ingest-time strip is not retroactive (D62). **An organisation's
`allowManagedHooksOnly`** can make the capture silently inert, and the harness's own CLI does not
report which hooks a scope loads, so the surfaces cannot detect it; the gap and its candidate
remedies are a todo (D60, D62).

**AMENDED (D65).** Two further boundaries are named, and neither is work in this branch. **The
Factory tree carries no content-version marker** — its plugin manifest's `version` is the static
`1.0.0` — so a tree written by a **newer** KyberDash cannot be told apart from an older one:
both are "not the current bundle", and the running build replaces the tree with its own bundle.
**A refused refresh is not retried automatically**: the tree is rewritten before the harness is
asked, so a refusal leaves the harness's cached copy stale, and the remedy is the exact command
the refusal names (D59's rule: report, never patch).

## 9. Verification gates

| Gate | Command | Requirement |
|---|---|---|
| Dash types | `scripts/proc-guard.sh run -- npm --prefix dash run typecheck` | zero errors |
| Dash lint | `scripts/proc-guard.sh run -- npm --prefix dash run lint` | zero findings |
| Dash unit | `scripts/proc-guard.sh run -- npm --prefix dash run test` | zero failures |
| Dash reachability | `scripts/proc-guard.sh run -- npm --prefix dash run check:reachable` | clean |
| .NET | `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests` | zero failures |
| E2E conformance | `scripts/proc-guard.sh run --timeout 600 -- npx --prefix dash playwright test --config dash/playwright.isolated.config.ts dash/e2e/harness-collection.spec.ts` | green, isolated config only |
| Docs | `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- docs drift .` | zero findings each |
| Task gate | `task-gate` on every changed file, per the repository's declared gate set | green |
| Live | T21's owner-run procedure | reported, never claimed by CI |

## 10. Review

The whole change is reviewed as one diff against `origin/main`, with the council gates the
repository declares (`.kyber-weave/kyber-weave.yml`). The reviewer checks, in particular: that
no Factory settings or hooks file is **read or written** anywhere in the diff — the harness's own
commands may change the harness's own settings file across a marketplace install, and the diff
must contain no merge into it (D48, D49); that the install drives the harness's own `droid plugin`
commands and nothing else (D43); that the native path's status line says plainly that no native
span is claimed (D51); that the consent gate covers the quarantine path as well as the record path
(D52) **and that it touches nothing outside the native and quarantine routes** (D57) — **except the one
deliberate exception D52 and D66 record: a span that belongs to NO harness has an unknown
origin, so its content is stripped too, and a claimed span's content is never touched**; that the
consent path never
turns content on by itself; that **the status surface claims nothing the consent does not
deliver** — the row must not say "no Factory content is stored" while the file source stores the
first user-message fragment, and it must name the routes the consent does not govern (D57, D58);
that **no organisation hard control is worked around** — the diff must contain no write to the
owner's settings file aimed at widening a marketplace, a plugin allowlist or a hook policy, and no
attempt to defeat `strictKnownMarketplaces`, `strictEnabledPlugins` or `allowManagedHooksOnly`
(D60); that the receipt records the tree and its digests and **no harness-side prior state**
(D59); that the install's harness-side refresh is armed for a tree it has **just written** as well
as for a superseded one, so the copy the harness actually runs is never left stale; that the
plugin's ownership is proved by the receipt that names **this** tree, **this** marketplace and
**this** identity, so a plugin KyberDash itself installed is never a conflict; that the
disposition and the printed line name what happened to the **tree** — `installed` for a tree
written fresh, `upgraded` for a superseded one; that the status surface's own word for a
superseded tree is `plugin: installed (an older version than this build)` and never the owner-edit
word `plugin: modified since install or not written by KyberDash` (D70); that the **uninstall**
applies the **same** receipt proof as the install — the receipt it already reads proves the plugin
identity KyberDash's own under the install's own conditions, so the harness's own
`plugin uninstall` runs instead of a conflict that removes the proof and leaves the plugin
installed and capturing (D71); that the organisation's cached settings are read from the
**harness's own directory** and never from the directory `FACTORY_DIR` moves, so the reported
organisation pin is the pin the harness wrote (D71); that **one** consent file has **one** bound
in **UTF-8 bytes** for every reader — the Factory plugin's reader, the OpenCode plugin's reader,
the canon gate and the engine-side state reader — and that the committed OpenCode bundle matches
the reader it inlines (D71); that the ownership rule is an **OR** — the tree or the receipt — and
that the documents say so (D71); that the `unchanged` verdict reads the tree and the harness's own
registry and never the harness's cached copy (D71); that the capture-plugin guidance relay lives
in **one** place, with a test driving the update and the Squad deploy step over one verb output
and asserting they relay the same lines (D71); that no token value is
fabricated; that the OpenCode **wire** is unchanged and the
committed OpenCode bundle is **current** — `node dash/scripts/bundle-opencode-plugin.mjs --check`
exits 0, and `node dash/scripts/bundle-factory-plugin.mjs --check` likewise, which is what D14
records (the shared-transport extraction regenerated the committed OpenCode artefact, so
byte-stability is not the criterion and currency is); that the native path implements D23's
precedence rule exactly, so a native span and a plugin row for one session are never summed;
and that the native path stays OTLP/HTTP on 4318 only.

## 11. Closeout

`docs-dev` harvests every H-row into its target document, amends ADR 0031 in place, creates the
new ADR, updates the plan's closeout mapping with one destination per durable decision, moves the
plan file to `docs/archive/plans/`, and moves the `## Active Plans` row to `## Archived Plans` in
`docs/plans/README.md` — in the same change, so `docs validate --merge-ready` passes with no
plan left in the active folder (`KW-DOC-LIFECYCLE-003`).

**AMENDED (D49, D56).** The harvest now also carries the **delegated-install clause** into ADR
0031 (H19) and the new ADR's corrected account of the install route and of the native path's
withdrawal (H20), plus the runbook's procedure and residue rule (H22), the collection rule's
Factory row (H23), the telemetry inventory's effective-shape account (H24) and the command
surface's route and exit codes (H25). The closeout mapping gains a row per new decision, D43–D56.
**AMENDED (D63): the "must not claim" list is re-based on what shipped.** The marketplace route
(T34/T35), the `SubagentStop` withdrawal (T38's shipped half), the native span claim's withdrawal
(T40/T41) and the native export's effective shape (T44/T45) **have landed**, so the closeout
**may** describe the marketplace route and the native withdrawal as shipped. It **must not** claim
the **name-conflict refusal and the residue report** (T36/T37), the **hooks file's shape** (T38's
pending half), or the **consent gate over the native and quarantine routes** (T42/T43), because
those have not landed.

**AMENDED (D66).** The list is re-based again, on what the branch carries now: the **hooks file's
shape** (T38) **has landed** — keyed directly by event name, and the `SubagentStop` event is not
registered at all — and it was verified **live**, because a fixture can only assert the bytes: a
real Droid turn produced the session, turn and tool spans through the corrected file, with the
consent off and no prompt text on the live span. The **consent gate over the native and quarantine
routes** (T42/T43) **has landed** and runs at the ingest seam before normalization and before the
quarantine write, with the fail-closed arm D52 records. The **name-conflict refusal** (T36/T37)
**has landed** in the harness-state reader, which parses the registry and decides the refusal;
the installer's own wiring of that refusal and the residue report are **pending**. The closeout
**may** describe the hooks file, the consent gate and the corrected wording as shipped, must not
claim the installer-side refusal or the residue, and it must name the gate's fail-closed arm and
the harness's **wrapped** list table, because both are properties a reader of the closed plan
cannot infer from the code.

**AMENDED (D64).** The closeout mapping gains a row per decision of this amendment, D57–D64, and
the harvest carries **H26** — the consent's scope stated as what it governs and what it does not,
and the organisation-control position (D57, D60). **The closeout must not claim the corrected
consent wording before T48 lands**: the branch carries a status surface that says *"no Factory
content is stored while KyberDash's consent is off"*, and that sentence is **false** — the Factory
file source stores the first user-message fragment whatever the consent file says (D57). T47/T48
correct it, and until they land the closeout describes the row as it stands, including the
sentence that is wrong, rather than describing the row that has not shipped. **AMENDED (D66): T47
and T48 HAVE LANDED**, so the closeout **may** describe the corrected row: both consent branches
name what KyberDash's own capture stores, the file source's route is named as one the consent
does **not** govern, the native route's content is attributed to the harness's own switch, and
the reported shape carries the organisation's `enabled` master switch and the granularity's
unreadable-value fallback. **The closeout also
must not claim the quarantine is bounded**: the 256 KiB cap bounds one packet, not the table, and
no retention or re-quarantine policy for the rows themselves was identified (D61, D62).

**AMENDED (D67).** The closeout mapping gains a row for **D67** — the receipt that is repaired when the
tree is current but unproven, the command sequence derived from the harness's registry so that "installed
but not registered" is expressible, and the teardown that removes KyberDash's own tree on a conflict
while leaving the foreign entry alone. The closeout **may** claim the upgrade path now: T49/T50 landed
and the upgrade was verified LIVE against a real harness, where the tree was rewritten with the current
bundle and the harness's cached copy was refreshed a second later.

**AMENDED (D68, D69).** The closeout mapping gains a row for each of **D68** and **D69**.
**D68** is the consent reader's duplication: the two production copies are kept — merging them
in either direction breaks a leaf rule — and a parity test holds them to one behaviour.
**D69** is the harness-side lifecycle vocabulary's absence from the status surfaces: the
vocabulary is install-side, a status read never spawns the harness's CLI, and the
honest-unobservability gap stays open as a todo. The closeout **may** claim both as decided.
It **must not** claim that the status surface reports the harness-side state: the shipped
`factory harness: <state>` line is printed only when a caller injects a harness CLI, and no
production caller does.

**AMENDED (D70).** The closeout mapping gains a row for **D70**. **D70** is the code council's
finding that the shipped install path did not do what **D65** and **D67** decide, and the three
corrections that made it do so: the harness's **cached copy** is refreshed for a freshly written
tree as well as for a superseded one, the **receipt** proves the plugin's identity KyberDash's
own (so an absent tree no longer turns KyberDash's own plugin into a conflict), and the
superseded tree's status word is `partial` with its own fixed line, `plugin: installed (an older
version than this build)`. The closeout **may** claim the shipped install path as D65 and D67
decide it. The corrections are covered by tests that failed before the fix
(`dash/src/capture/harnesses/factory-plugin/install.test.ts`, `253e2aa`), and the implementation
that ships them is `9775b9b`.

**AMENDED (D65).** The closeout mapping gains a row for **D65**, and the harvest carries **H27**
(the architecture's upgrade path), **H28** (the runbook's routine install against the upgrade, and
the exact command that refreshes the harness's copy) and **H29** (the command surface's stdout
tokens — `installed`, `would-install`, `unchanged`, `upgraded`, `would-upgrade` — and the refusal
reporting). **The closeout must not claim the upgrade path before T50 lands**: the shipped
installer still reports a receipt-proven older bundle as `unchanged`, never rewrites the
Kyber-owned tree, and never refreshes the harness's cached copy. The ADR harvest records the
harness update route in **ADR 0053**'s install-route account (and in ADR 0031's delegated-install
clause, if its text names the refresh), and the ADR index rows change only if their summaries
change.

**AMENDED (D71).** The closeout mapping gains a row for **D71** — the second code council's six
findings on the branch, and the six corrections that closed them. The **uninstall** now proves the
plugin with the receipt it already reads, under the install's own conditions, so the harness's own
`plugin uninstall` runs instead of a conflict that deleted the proof and left the plugin installed
and capturing. The organisation's cached settings are read from the **harness's own directory**,
never from the directory `FACTORY_DIR` moves. **One** consent file has **one** bound in **UTF-8
bytes** for every reader, and the committed OpenCode bundle was regenerated to match the reader it
inlines. The ownership rule is an **OR** — the tree or the receipt — and the canonical
architecture document says so. The `unchanged` verdict reads the tree and the harness's own
registry and never the harness's cached copy. The capture-plugin guidance relay lives in **one**
place in the .NET CLI, held by a test that drives the update and the Squad deploy step over one
verb output. The closeout **may** claim all six. The corrections are covered by tests that failed
before the fix (`3d05a5e`, `a33931a` and the guard test in `ecb64d3`), and the implementations
that ship them are `df7ee07`, `199406d` and `ecb64d3`.

## 12. Assumptions and open questions

Factory-side facts, each labelled by its evidence source: **documented** (Factory's own
documentation, via the research digest), **observed** (this machine's real Factory state, via the
local-environment digest), or **open** (still to be confirmed by the live check).

| ID | Assumption or question | Evidence so far | Resolution point | Effect if wrong |
|---|---|---|---|---|
| A1 | **DISPROVEN by measurement — Factory does NOT discover a plugin at `~/.factory/plugins/<name>/`.** The real discovery path is the harness's plugin **cache**, filled only by `droid plugin install` from a **registered marketplace**. | **MEASURED (2026-11-01, real Factory 1.1.0):** the shipped installer wrote `~/.factory/plugins/kyberdash-factory/` and Factory did not load it — `droid plugin list` reported "No plugins installed", and a real Droid turn produced **no plugin spans at all** (§2.6). **DOCUMENTED:** plugins are installed from a marketplace, Droid copies them into its cache unchanged, the identity is `pluginName@marketplaceName`, and there is no `droid plugin enable` or `disable` command — enablement is the `enabledPlugins` setting | **RESOLVED, and resolved AGAINST the assumption.** T46 re-confirms the harness's route against the real CLI; the live check (T21) remains the detector | The install route changes — which is exactly what D43 does. The unit stays Kyber-owned and removable, and the owned tree leaves the Factory root entirely (D44) |
| A2 | The hook payload's `session_id` equals the `session_start` entry's id in `~/.factory/sessions/*.jsonl` | **MEASURED, and it held:** the real plugin spans that arrived at the receiver carried **a live session id** (§2.6), and the captured span was accepted with the correct session id on the stored record. The remaining question is whether it equals the **transcript's** `session_start` id, which the live check reads alongside | The live check in T21, which reads both | Sessions split in two; tokens read `not_measurable` (honest, not wrong) |
| A3 | The hook payload's field names | **DOCUMENTED, in full (Factory's own hooks documentation):** every hook receives `session_id`, `transcript_path`, `cwd`, `permission_mode`, `hook_event_name` and an optional `message_id`; tool hooks add `tool_name`, `tool_input` and `tool_response`; `UserPromptSubmit` adds `prompt` and `has_images`; `Stop` adds `stop_hook_active`, `tool_execution_count` and `elapsed_time`; `SubagentStop` adds `task_name`, `task_result`, `task_error` and `stop_hook_active`; `SessionEnd` adds `reason`, `session_duration_ms` and `message_count`; `SessionStart` adds `source`. **No model field is documented** (the model is resolved by D39's chain), and **no per-tool call id is documented** — the shipped mapper anchors a tool span on `tool_call_id` when the payload carries one and on the session id when it does not, so two tool calls in one session could land on one row. Whether Factory's payload carries a call id under another name is **OPEN** | The live check in T21 records the real payload for each event | The mapper drops attributes it cannot find; the contract is versioned and the fix is a version bump. **The tool span's anchor is the one place where an absent id is not harmless**, and it is a named change if the payload carries no per-call identity |
| A4 | What identity `Stop` and `SubagentStop` carry for turn close and child linkage | **DOCUMENTED, and it corrects the plan:** `Stop` carries `stop_hook_active`, `tool_execution_count` and `elapsed_time`; `SubagentStop` carries `task_name`, `task_result`, `task_error` and `stop_hook_active` — **no child session id**. The common `session_id` is the session the hook fires in, which for a subagent's stop is the **parent's** session. **The plugin's `SubagentStop` span is therefore withdrawn (D54)**; a child session's own rows are unaffected, because the child's own events fire in the child's session. What identity the events carry **beyond** the documented fields is **OPEN** | The live check in T21 records the real payload for each event | A turn closes on the next event instead; a child session is linked by the file source's transcript (`parentId`, `callingSessionId`) and by nothing else (reported as not captured by the plugin, never guessed) |
| A5 | The hook script's execution protocol | **Partly documented, and partly MEASURED:** hooks are shell commands that receive JSON on stdin; a non-zero exit other than 2 is a non-blocking error, and the command may carry a `timeout`. **Measured:** the plugin's hooks fired in a real Droid session and its spans arrived at the receiver, so the shipped runner's stdin-and-exit-0 protocol works (§2.6). The stdout expectations for the plugin's own protocol are still open | The live check in T21 | The script is renamed or invoked differently; the hook registration is the only change |
| A6 | Whether the native export needs `OTEL_LOG_MESSAGE_CONTENT` for spans | **RESOLVED, and the issue's phrasing is only half right.** Default is metrics only, delta temporality, 60-second flush. Under the **`legacy`** format, spans exist only as the content spans `droid.message.user`, `droid.message.assistant`, `droid.tool.call`, `droid.tool.result`, and only when `OTEL_LOG_MESSAGE_CONTENT=true`; `aggregate` + `legacy` is metrics only with zero spans. Under the **`genai`** format, the trace structure is emitted whenever a customer endpoint is configured and the content flag gates only the four content attributes; `aggregate` + `genai` keeps the full trace with the content attributes stripped. **Tokens and cost are never exported**, in either format. **AMENDED (D51):** the organisation's `telemetry` block pins the **format** as well as the endpoint, and where it pins the format the machine's `OTEL_TELEMETRY_FORMAT` is not read at all; the **granularity** resolves most-restrictive-wins, and `aggregate` overrides every content opt-in; the organisation's `logMessageContent` is authoritative; and the two formats **replace each other and do not stack**. **MEASURED:** the `legacy` spans scored 0 at every adapter and were quarantined as `unclaimed`, and the `genai` trace's group scored 0.500 against a 0.6 threshold — **accepted 0, quarantined 2** (§2.6). **AMENDED (D50): the native span path is withdrawn on that evidence; the metrics path is unaffected** | The research digest, Factory's own published documentation, and the live check's probe; no further check needed | Nothing for the metrics path — the plan states both branches and never asserts the `legacy`-only rule as general. **For the span path, the consequence is D50's withdrawal**, not a degraded claim |
| A7 | Whether Factory's native OTLP endpoint is user-configurable | **RESOLVED: YES.** `OTEL_TELEMETRY_ENDPOINT` (base URL; metrics → `{endpoint}/v1/metrics`, spans → `{endpoint}/v1/traces`; falls back to `OTEL_EXPORTER_OTLP_ENDPOINT`) and its sibling variables are machine environment variables; there is no personal `settings.json` telemetry key; Factory's own documented example is `export OTEL_TELEMETRY_ENDPOINT="http://localhost:4318"`. **Caveat (D26)**: an organisation-managed `telemetry` block overrides the machine variables, so a pinned organisation gets no local export. On this machine the org cache holds no `telemetry` block | The research digest; the live check exercises the local case | None for the plugin path; D26 governs how the native path is reported |
| A8 | The model's resolution when the hook payload carries none | **Observed, and now settled by the shipped code (D39)**: `~/.factory/settings.json` has no top-level `model` key (its model lives in nested per-role blocks) and is never read for a model; the companion `<session-id>.settings.json` carries the session's `model`, and the **file source** is the reader that publishes it, wrapper-stripped; each transcript assistant record carries `message.modelId`, and **no shipped reader reads it** — it is not a link in the chain. A plugin turn names a model only when the hook payload carries one, and Factory's own payload documents no model field, so it generally names none | D39's chain; the live check records which source supplied the model | The model is absent on the row — never guessed; and where the file source's own `unknown` placeholder stands in, it is the file source's literal, not a measurement (D40) |
| A9 | A Factory child (subagent) session is a **first-class session on disk**: it has its own transcript and its own companion settings file, and the file source discovers it as its own session | **Observed** (read-only inspection of this machine, 2026-11-01): the parent session `5f50fd96-…`'s companion file names the child `bfde2f1a-…`; the child's own `<child-id>.settings.json` carries exactly the counters the parent names for it; the child's own transcript's `session_start` names the child's id, and its `callingSessionId` names the parent | D29 pairs the child from its **own** companion file; T26's corrected fixture is the evidence | A child session with no companion file of its own reads `not_measurable` with a stated reason — honest, never the parent's counters |
| A10 | The parent's `childInclusiveTokenUsageBySessionId` map holds the **same numbers** the child's own companion file holds, and the parent's `inclusiveTokenUsage` is the parent's own `tokenUsage` plus every child's entry | **Observed** (read-only inspection of this machine, 2026-11-01): the parent `5f50fd96-…` reports `tokenUsage` input 1253 / output 5816 / cacheRead 320826 and `inclusiveTokenUsage` input 369300 / output 47551 / cacheRead 1938106 — the difference is exactly its child's entry (368047 / 41735 / 1617280) | D29's rule that the map is never a token source, and that a child's usage is never folded into the parent's totals | Folding a map entry into the parent would double count the child's tokens; the rule prevents it, and the characterization test that pins the map as ignored stays correct |
| A11 | What an OTLP exporter does with a metrics batch the receiver refuses | **Documented (OTLP, not Factory-specific):** an exporter retries a failed export on its own schedule, and a 5xx is a retryable failure. The receiver's own failure window records each refusal, so the refusals are observable without knowing the exporter's retry policy | The live check in T21, if the native endpoint is configured | A deployment whose exporter retries sees repeated refusals and a filled failure window — which is the production consequence D31 repairs. The plan does not depend on the retry: with a destination, the path succeeds |
| A12 | The **registered** marketplace name for a local marketplace path | **MEASURED:** for a local path, the registered marketplace name is derived from the **path's directory basename**, NOT from the `name` field inside `marketplace.json` (§2.6). **DOCUMENTED:** "The registered name is derived from the source or set by the `extraKnownMarketplaces` key. Run `droid plugin marketplace list` to read the registered name back." So the tree's directory name is load-bearing, and the manifest's `name` is metadata only | T46 reads the registered name back with `droid plugin marketplace list` | The identity changes (`kyberdash-factory@<name>`); the plan's identity, the receipt and the uninstall commands all name it, so a wrong guess is a wrong string in three places, not a broken install |
| A13 | What `droid plugin uninstall` and `droid plugin marketplace remove` actually clean up | **MEASURED since the last amendment, and recorded in ADR 0031's 2026-11-02 amendment:** `droid plugin uninstall` **deletes the `enabledPlugins` entry outright** rather than setting it to `false`, and it leaves the marketplace registration in place; `droid plugin marketplace remove` then deletes the registration and leaves `enabledPlugins` alone. **The consequence the plan must carry:** an owner who had that plugin listed as **disabled** before the install has that key **removed**, not restored to `false`, and the receipt records no harness-side prior state, so the teardown cannot restore it (D59). The remaining questions — the cache copy and the `installed_plugins` record, and the order — remain **OPEN** | T46 establishes the rest by measurement, on a scratch install; the measured facts above are already recorded in ADR 0031 and in a filed todo | D59's rule holds: the residue is **reported** with the exact command that clears it where such a command exists, and KyberDash never patches the owner's file. The **teardown does not restore an owner entry it never captured**, and the mechanism that would is a filed todo, not this branch's work (D62) |
| A14 | The shape of the `extraKnownMarketplaces` and `enabledPlugins` entries for a **local** marketplace | **PARTLY DOCUMENTED, and the documented half is now precise:** the settings-declared form is a name → source map (`extraKnownMarketplaces`) and an identity → boolean map (`enabledPlugins`); the **local-path** source object's exact shape is `{"source": "local", "path": "<absolute path>"}` (*Enterprise Controls & Managed Settings*, the `extraKnownMarketplaces` block), and the `strictKnownMarketplaces` block takes each source object **directly** rather than wrapped. What the harness actually **writes** for a local path is measured on Factory 1.1.0 to be exactly the documented shape (ADR 0031's 2026-11-02 amendment). KyberDash does not need it — it reads the harness's state through the harness's CLI, never from the file (D48) | T46 records the file's shape after a scratch install | None for the design as decided: the settings file is never read or written by KyberDash. The consequence would be confined to the plan's documentation of what the harness does |
| A15 | The registry's group-vote arithmetic for Factory's native trace | **MEASURED and CONFIRMED IN CODE:** the registry normalises a group's score by the group's **span count** (`dash/src/canon/adapters/registry.ts:104-128` — **AMENDED (D61): the range this row cited, `:94-127`, was off; `scoreGroup` is at `:104-128`**) against a threshold of **0.6** (**`:34`, not the `:38` this row cited** — `DEFAULT_CONFIDENCE_THRESHOLD`); the `droid` adapter recognised a native span **only** by `gen_ai.agent.name === 'droid'` (`dash/src/canon/adapters/droid.ts:176`, **a line that no longer holds that check** since T41 removed the native claim). Factory's data reference puts the attribute on the **root only**. A `genai` trace of one root and `k` children therefore scores `1/(1+k)`: `0.5` for a two-span trace, lower for a real one. The probe measured **[1, 0]** and **accepted 0, quarantined 2** (§2.6) | **RESOLVED.** It is the arithmetic behind D50's withdrawal | The native span path stays withdrawn; the arithmetic is the reason, and it is recorded so a future change does not have to re-derive it |
| A16 | The conditions under which the native span claim could be restored | **OPEN, by design.** Restoring it needs three things D50 names: a **group-scoped** claim in the shared attribution contract, a model chain that lets a native per-turn model reach the dashboard (D23 excludes it today), and a consent gate over the native content (D52, which lands with T42/T43 regardless). The measurement and the arithmetic are recorded; the engineering is not scheduled | A todo under the todo index (T20) carries it; the branch does not | Nothing — the path is withdrawn, and the todo keeps the evidence |
| A17 | Whether `FACTORY_DIR` redirects the harness's settings file | **MEASURED: it does NOT.** With `FACTORY_DIR` pointing elsewhere, `droid plugin marketplace add` wrote the **real** `~/.factory/settings.json` (§2.6). `FACTORY_DIR` is honoured by the **file source** for the **sessions** tree; it is not honoured for the settings file | **RESOLVED.** D55 records the per-path resolution | The install's root independence is what keeps this irrelevant — KyberDash neither reads nor writes that file, and writes nothing under the Factory root at all |
| A18 | What does KyberDash's Factory consent actually govern? | **MEASURED in the code (D57):** the consent file is read in **one** place — the plugin's hook runner (`dash/src/capture/harnesses/factory-plugin/entry.ts:132-136`) — and it reaches **one** emission, the plugin's `factory.turn.prompt.text` attribute (`mapper.ts:202-207`). The **file source** reads the owner's first user message fragment independently, up to 500 characters (`dash/src/providers/droid.ts:165-176`, `:287`), and the store projects it as a named attribute (`dash/src/canon/store.ts:2433`), so that text is in the store whatever the consent file says. The **plugin's** tool arguments and results are **byte counts** (`contract.ts:32-33`), never content | **RESOLVED.** D57 narrows the claim and D58 fixes the fixed words; T47/T48 land the correction | The plan's earlier "no Factory content is stored" claim was false; a status surface asserting it is worse than one asserting nothing. The correction is a wording change and a test, not a store-wide gate |
| A19 | Can an organisation's settings make the marketplace route fail? | **DOCUMENTED (Factory's *Enterprise Controls & Managed Settings*):** `strictKnownMarketplaces` makes the higher-level list "the ceiling" for marketplace sources; `strictEnabledPlugins` makes the `enabledPlugins` maps "the exhaustive plugin allowlist", so "enables and manual installs at lower levels are rejected"; `allowManagedHooksOnly` means "only org-managed hooks and hooks from org-enabled plugins are loaded", so a user-scope plugin's hooks are dropped (D60). **Whether the harness's own CLI reports which hooks a scope loads is OPEN** — neither `droid plugin list --scope user` nor `droid plugin marketplace list` is documented to | T46's probe asks the real CLI; the gap is filed as a todo in the meantime | The first two are visible — the harness's command fails and KyberDash reports the refusal with its stderr quoted. The third is **silent**, and KyberDash cannot detect it: the surfaces must not read "installed" as "capturing", and the gap is recorded rather than papered over |
| A20 | What is the quarantine table's retention? | **OPEN, and not identified (D61).** The **packet** is bounded: `MAX_QUARANTINE_RAW_PAYLOAD_CHARS` cuts one stored payload at 256 KiB (`dash/src/canon/ingest.ts:360-366`). The **rows** are not: no bounded retention, no re-quarantine policy and no pruning pass for the quarantine table itself was found — the rows are cleared only by a scoped or full wipe (`dash/src/canon/store.ts:154`, `:5008-5010`) | A todo under the todo index carries it (D62); the branch does not set a retention policy | With the native span claim withdrawn (D50), every native Factory span the harness sends lands in that table, so its row count is this branch's own consequence — and it is unbounded. The plan states the 256 KiB bound as a **per-packet** bound and never as a bound on the table |
| A21 | Is consent a purge or a retention control? | **MEASURED (D62):** turning the consent off does not remove what is already stored; `records.raw` is never purged; and a backfill re-derives content from it, so a row that holds content stays a content store and an ingest-time strip is **not retroactive** | A todo under the todo index carries it; the documents state the distinction (H26) | A reader who takes the consent for a purge control will believe the store is clean when it is not. The documents must say what the consent governs and what it does not |
| A22 | Can a Factory native **metric point** carry message content? | **NOT MEASURED, and deliberately out of scope (D61).** Factory's documented native metrics are `droid.*` under `legacy` and `gen_ai.*` under `genai`; the amendment session found **no evidence** that either carries a message, an argument or a result. The metric point's own attribute column is a generic string map, so a content-carrying attribute is structurally possible — but no Factory evidence populates it, and the plan does not claim a gate it has not measured | T42/T43 gate the **span** routes only (the record and the quarantine packet); the metric route is left alone, and the plan says so in those tasks' text rather than implying a coverage it does not have | A gate that claims a surface it has not measured looks comprehensive and is not — which is the failure mode D57 corrected in the status wording |
| A23 | Does `droid plugin update` refresh a plugin installed from a **local** marketplace, and which `--scope` values does it accept? | **DOCUMENTED (Factory's *Plugins* page):** the CLI's command set names `droid plugin update [plugin@marketplace] --scope project` beside `droid plugin install <plugin@marketplace> --scope user` and `droid plugin marketplace update [name]`; the page states that Droid "copies them into its cache unchanged", and that an explicit `droid plugin marketplace update` requests an immediate update. **The documented example uses `--scope project`, while KyberDash installs at `--scope user`** — whether the update accepts the user scope is **OPEN**. **Not measured:** whether the update re-copies the plugin from a local marketplace path, and what it prints on success | T46's probe asks the real CLI: the update's accepted scope flags, its output, and whether the cached copy's bytes change after the marketplace tree is rewritten | The refresh runs against a scope the command refuses — a **visible** refusal, reported with the harness's stderr quoted and never as a success (D65). The Kyber-owned tree stays current either way, so the capture keeps running the previous bundle until a refresh succeeds |
| A24 | Does `droid plugin install` refuse a plugin that is already installed? | **MEASURED on Factory 1.1.0 (the D65 amendment session, on the owner's machine): the install of an already-installed plugin is refused.** Not re-measured in the session that wrote this row | T46's probe confirms the refusal's exit code and message on a scratch install | The upgrade's fallback to the install path fails visibly. **D65 forbids that fallback**: an installed plugin is refreshed with `droid plugin update`, never with `plugin install` |

## 13. Investigation record

Twelve evidence sources, each labelled by who produced it and what it can prove:

1. **Repository evidence (architect session).** `codegraph_explore`, `docs_explore`,
   `docs_for_symbol` and `docs_glossary` are not loadable in the architect session (§2.1), so the
   findings in §2.2 come from direct reads and targeted greps of the named repository files.
2. **Documentation checks (conductor).** `docs validate .` and `docs drift .` were run by the
   conductor against this saved plan and index, over a CodeGraph-indexed checkout: both **pass,
   0 errors, 0 warnings**, 150 documents. **AMENDED (D64): that result was measured against the plan
   as it stood before the D43–D56 and D57–D64 amendments.** Both amendments changed the plan and
   the index row, so the checks must be re-run against the amended text; the two amendment
   sessions could not run them, for the reasons in §2.1a and §2.1b. **A pass recorded against an
   earlier revision is not a pass against this one**, and this plan's §2.1 names the re-run as the
   conductor's gate.
3. **External and local evidence (research digest and local-environment digest, both relayed by
   the conductor).** Factory's own documentation (Part 1 of the reconciliation) and a read-only
   inspection of this machine's real Factory state (Part 2). These are the only sources for
   Factory-side behaviour, and every statement drawn from them is labelled as such in §2.5 and
   in the `## Decisions` table. Where the two disagree with the issue text — the shape of
   `~/.factory/settings.json` and the "spans need `OTEL_LOG_MESSAGE_CONTENT`" claim — the
   corrected form is recorded (D24, A6) and the issue's form is not carried forward.
4. **The D28–D30 amendment (architect session, 2026-11-01).** The same tooling limits apply: the
   CodeGraph MCP tool and the Kyber-Weave MCP documentation tools are not loadable in this
   session, and this session has no shell, so the `codegraph explore` CLI fallback is unavailable
   too — the discovery below is direct reads and targeted greps. **Discovery: codegraph 0, docs 0,
   direct reads 15 files and 8 targeted greps** — `dash/src/canon/measurability.ts`,
   `dash/src/canon/sessions.ts`, `dash/src/canon/sessions.droid-pairing.test.ts`,
   `dash/src/canon/twin-dedupe.ts`, `dash/src/canon/adapters/droid.ts`,
   `dash/src/canon/ingest.ts`, `dash/src/canon/store.ts`, `dash/src/canon/types.ts`,
   `dash/src/canon/runs.ts`, `dash/src/providers/droid.ts`, `dash/src/providers/droid.test.ts`,
   `dash/src/synth/synth.ts`, `dash/src/synth/readers/` (a directory listing),
   `dash/src/analysis/signals.ts`, and this plan; the greps covered `measurability`,
   `childInclusive`, `synthesizeCall(`, `.measurability`, `turnsOf` and `droidAdapter`. The reads
   were necessary because the semantic index could not be loaded at all, and each one answered a
   specific question the three findings raised: the file-source declaration table and the merge
   rule (Finding 1), the twin-join rule and its all-zero exclusion (Findings 1 and 3), the
   session projection's derivation of the turn set, the totals, the availability, the cost, the
   timeline and the duration (Findings 1 and 3), the adapter's reason wording (the small
   finding), and the file source's companion-file reader (Finding 2). **Local evidence (read-only,
   outside the repository):** three files under this machine's real `~/.factory/sessions/` — the
   parent session `5f50fd96-…`'s companion settings file, the child session `bfde2f1a-…`'s own
   companion settings file, and the child's own transcript — which is the evidence for A9 and A10
   and therefore for D29. No file outside the plan was written. **The two documentation checks
   could not be re-run in this session** for the reason in §2.1 and must be re-run by the
   conductor against this amended plan.
5. **The D31–D32 amendment (architect session, 2026-11-01).** The same tooling limits apply: the
   CodeGraph MCP tool and the Kyber-Weave MCP documentation tools are not loadable in this
   session, and this session has no shell, so the `codegraph explore` CLI fallback is unavailable
   too — the discovery below is direct reads and targeted greps. **Discovery: codegraph 0, docs 0,
   direct reads 11 files and 9 targeted greps** — `dash/src/otel/service.ts`,
   `dash/src/otel/writer.ts`, `dash/src/otel/receiver.ts`, `dash/src/otel/receiver.test.ts`,
   `dash/src/canon/ingest.ts`, `dash/src/canon/ingest.test.ts`, `dash/src/canon/store.ts`,
   `dash/src/canon/log-ingest.ts`, `dash/src/canon/adapters/droid.ts`,
   `dash/src/canon/adapters/droid.test.ts`, `dash/src/canon/adapters/copilot.ts`, and this plan;
   the greps covered `writeMetrics`, `OTLP_PERSISTENCE_FAILED`, `growWithReceiverTiming`,
   `baseRecord`, `record.status`, `CREATE TABLE`, `lastAcceptedAt`, `metrics`, and the writer's
   and service's construction sites. The reads were necessary because the semantic index could
   not be loaded at all, and each one answered a specific question the two seams raised: the
   production sink's shape and the writer's metrics port and refusal rule (Seam 1); the receiver's
   metrics route, its error mapping and its health window (Seam 1); the canon schema's
   `IF NOT EXISTS` discipline and the store's wipe scope (Seam 1); the log-ingest module the
   metrics ingest mirrors (Seam 1); the normalization seam's status write and the adapters that
   assign a status (Seam 2); and the consumers of a stored record's status — the session's tool
   error counts, the run row and the outcome derivation (Seam 2). Four governed documents were read
   directly because the Kyber-Weave MCP documentation tools are unavailable: `docs/rules/
   honest-unobservability.md`, and the receiver-activity passages of `docs/dash/architecture.md`,
   `docs/dash/runbook.md` and `docs/dash/telemetry-inventory.md`. No file outside the plan was
   written. **The two documentation checks could not be re-run in this session** for the reason in
   §2.1 and must be re-run by the conductor against this amended plan.
6. **The D33–D34 mediation (architect session, 2026-11-01).** The same tooling limits apply: the
   CodeGraph MCP tool and the Kyber-Weave MCP documentation tools are not loadable in this
   session, and this session has no shell, so the `codegraph explore` CLI fallback is unavailable
   too. **Discovery: codegraph 0, docs 0, direct reads 7 files and 12 targeted greps** —
   `KyberDashCapturePluginStep.cs`, `SelfUpdater.cs`, `UpdateCommandTests.cs`,
   `SquadCapturePluginStepTests.cs`, `SquadGlobalRoots.cs`, `SquadTarget.cs`, and this plan. The
   reads answered the escalation's questions one by one: the legacy helper's step naming, the
   legacy fixtures' assertions and doc comments, the new RED fixtures, the worker's implementation
   on both sides, the global-root resolution, and the plan's D11/D18/T17/T18 wording. One read of
   the capture-plugin step returned a stale copy of the file; it was re-read before any conclusion
   was drawn from it. No file outside the plan was written. **The two documentation checks could not
   be re-run in this session** for the reason in §2.1.
7. **The D35–D37 amendment and the D14 correction (architect session, 2026-11-01).** The same
   tooling limits apply: the CodeGraph MCP tool and the Kyber-Weave MCP documentation tools are
   not loadable in this session, and this session has no shell, so the `codegraph explore` CLI
   fallback is unavailable too. **Discovery: codegraph 0, docs 0, direct reads 10 files (nine
   source files plus this plan) and 9 targeted greps** — the two installers, the shared transport
   sender, the Factory capture writer,
   `cli/register.ts` and this plan. Three governed documents were read directly because the
   documentation tools are unavailable: `docs/cli-command-surface.md`, `docs/dash/runbook.md` and
   `docs/rules/kyberdash-harness-collection.md`. No file outside the plan was written. **The two
   documentation checks could not be re-run in this session**.
8. **The D38–D42 amendment (architect session, 2026-11-01).** The same tooling limits apply: the
   CodeGraph MCP tool and the Kyber-Weave MCP documentation tools are not loadable in this
   session, and this session has no shell, so the `codegraph explore` CLI fallback is unavailable
   too. **Discovery: codegraph 0, docs 0, direct reads 18 source files and 2 governed documents,
   plus 20 targeted greps** — `dash/src/canon/store.ts`, `dash/src/canon/metric-ingest.ts`,
   `dash/src/canon/metric-ingest.test.ts`, `dash/src/canon/sessions.ts`,
   `dash/src/canon/droid-pairing.ts`, `dash/src/canon/adapters/droid.ts`,
   `dash/src/canon/sessions.droid-pairing.test.ts`, `dash/src/providers/droid.ts`,
   `dash/src/providers/droid.test.ts`, `dash/src/synth/synth.ts`, `dash/src/otel/service.ts`,
   `dash/src/otel/writer.ts`, `dash/src/otel/receiver.ts`,
   `dash/src/capture/harnesses/factory.ts`,
   `dash/src/capture/harnesses/factory-plugin/mapper.ts`, the committed plugin bundle
   (`dash/src/capture/harnesses/factory-plugin/generated/plugin-source.ts`), the two documentation
   validators (`src/KyberWeave.Core/Docs/Validation/DocDriftLinter.cs`,
   `src/KyberWeave.Core/Docs/Validation/DocSpecValidator.cs` — read to learn what the drift gate
   resolves, since this session could not run it) and this plan; plus
   `docs/dash/architecture.md` and `docs/dash/telemetry-inventory.md`, read directly because the
   documentation tools are unavailable. The reads were necessary because the semantic index could
   not be loaded at all,
   and each one answered a specific question: the metric table's DDL, its content-digest key and
   its upsert (**D38**, **D42**); the ingest seam's count and the production sink's port (**D38**,
   **D42**); the receiver's hand-over of the decoded metrics (**D42**); the file source's
   companion-file read and its `unknown` placeholder (**D39**, **D40**); the plugin mapper's
   payload-only model read and the committed bundle's file reads — **none** (**D39**, D4); the
   adapter's model bridge and the session projection's model chain (**D39**); the status row's
   native-export line (**D41**). The harvest's own report was checked against the code rather
   than copied: two of its statements needed narrowing (the plugin reads the hook payload only,
   so it is the **file source** that resolves the model; and the plan's `otel_metric` name was
   this plan's own, never a shipped name). No file outside the plan was written. **The two
   documentation checks could not be re-run in this session** for the reason in §2.1 and must be
   re-run by the conductor against this amended plan.
9. **The D43–D56 amendment session (2026-11-01).** The same tooling limits apply, and one more:
   **the session had no process-execution tool**, so neither the `codegraph explore` CLI fallback
   nor the two documentation gates nor `git` was available (§2.1a). **Discovery: codegraph 0,
   docs 0, direct reads 12 files and 8 targeted greps, plus 5 external documentation fetches** —
   the direct reads were `dash/src/capture/harnesses/factory-plugin/install.ts` (the shipped
   installer: the tree it writes, the receipt, the classifier, the uninstall, the state and status
   surface), `dash/src/capture/harnesses/factory-plugin/mapper.ts` (the hook-to-span mapping, and
   the `SubagentStop` case that names the parent's session as the child's), the same directory's
   listing and `native-export.ts` (the shipped native reader: the endpoint verdict it resolves and
   the managed-settings cache it reads), `dash/src/canon/adapters/droid.ts` (the native
   fingerprint, the plugin envelope, the counter reasons), `dash/src/canon/adapters/base.ts` (the
   `RawSpan` shape and the adapter contract), `dash/src/canon/adapters/registry.ts` (the group
   vote, the **0.6** threshold and the source-inheritance pass), `dash/src/canon/adapters/copilot.ts`
   (`baseRecord`'s verbatim `raw` and `canonicalParts`' reading of `gen_ai.input.messages`),
   `dash/src/canon/ingest.ts` (the quarantine packet and the native-supersede logic),
   `dash/src/otel/service.ts` (the production sink and its `upsertMetrics`),
   `docs/adr/0026-kyber-utilities-owned-files-not-settings.md` and the Factory amendment of
   `docs/adr/0031-…md`, and this plan. The greps covered `traceGroup`, `confidence`,
   `export type RawSpan`, the content-message keys, and four anchors inside the plan. **Every
   read was necessary because the semantic index could not be loaded at all**, and each one
   answered a specific question: the shipped install route (D43), the owned unit (D44), the
   harness-side fingerprint and the group arithmetic (D50, A15), the verbatim `raw` and the
   quarantine packet (D52), the shipped native reader's coverage (D51), the production sink's
   metrics port (D31's correction), and the ADRs' letters (D48, D49). **External evidence
   (Factory's own published documentation, fetched over HTTP):** the plugin documentation (the
   marketplace layout, the `extraKnownMarketplaces` and `enabledPlugins` shapes, the identity
   rule, the plugin cache, the `${DROID_PLUGIN_ROOT}` expansion, the absence of an enable or
   disable command), the hooks documentation (the scope table, the **direct-keyed** `hooks.json`
   shape versus the `settings.json` wrapper, the payload's fields per event), the telemetry index
   (the machine variables, the org block's keys and precedence, the metrics-only default) and the
   telemetry data-reference and privacy pages (the two export formats, the GenAI span vocabulary,
   the granularity and content rules, the org's authority). **No file outside the plan was
   written**, and **the two documentation checks could not be re-run in this session** — the
   conductor must run them against this amended plan, with `mergeReady` false, for the reason in
   §2.1a.
10. **The D57–D64 amendment session (2026-11-02).** The same tooling limits apply, and the same
   missing process-execution tool (§2.1b). **Discovery: codegraph 0, docs 0, direct reads 21 source
   files and 13 governed documents, plus 24 targeted greps and 3 external documentation fetches** —
   the source reads were
   `dash/src/capture/harnesses/factory-plugin/entry.ts` (the consent read and the mapper call),
   `.../mapper.ts` (the prompt-text gate and the byte-count tool attributes),
   `.../consent.ts` and `.../spool.ts` (the consent reader and the **spool file's name**),
   `.../install.ts` (the shipped receipt, the classifier's five kinds, the path resolution),
   `.../marketplace.ts` (the marketplace route, the harness state vocabulary, the spawn site's
   bound) and `.../marketplace.test.ts`, `.../native-export.ts` and `.../native-export.test.ts` (the
   shipped shape reader and its granularity fallback), `.../contract.ts` (the wire contract's keys),
   `dash/src/capture/harnesses/factory.ts` (the status row's shipped consequence line),
   `dash/src/providers/droid.ts` (the file source's first-message fragment), `dash/src/synth/synth.ts`
   (the synthesized raw attributes), `dash/src/canon/store.ts` (the projected attribute keys, the
   record write, the quarantine table and its wipe paths), `dash/src/canon/ingest.ts` (the
   quarantine packet, its 256 KiB cap), `dash/src/canon/metric-ingest.ts`,
   `dash/src/canon/adapters/registry.ts` (the threshold's real line and the group vote),
   `dash/src/canon/adapters/droid.ts` (the withdrawn native claim), `dash/src/otel/receiver.ts`
   (the identity stripping), `dash/src/process/spawn-guard.ts` and
   `dash/tests/spawn-sites.allowlist.ts` (the spawn allow-list); and the governed documents were
   `docs/adr/0031-…md` and
   `docs/adr/0053-…md`, `docs/todo/README.md` and the eight filed Factory todos, `docs/plans/README.md`
   (the index row), `dash/AGENTS.md` (the spawn rules), and this plan. The greps covered
   `userMessage`, `consent`, `granularity`, `aggregate`, `quarantine`, `spawnSync`, `no Factory
   content`, `outbox.jsonl`, `registry.ts:38` and the plan's own anchors. **Every read was
   necessary because the semantic index could not be loaded at all**, and each one answered a
   specific question: **where the consent is read and what it reaches** (D57, A18); the file
   source's first-message fragment and the store's projection of it (D57); the plugin's tool
   attributes as byte counts (D61); the quarantine packet's cap and the absence of a row-retention
   policy (D61, A20); the registry threshold's real line and the classifier's real vocabulary
   (D61); the shipped receipt's shape and the absence of any harness-side prior state (D59); the
   harness's own refusal and state vocabulary (D60); the spawn site's bound and its allow-list
   entry — **and that check came out clean**: the site's `spawnSync` calls carry
   `timeout: DEFAULT_COMMAND_TIMEOUT_MS (30000 ms)` and `maxBuffer: DEFAULT_MAX_OUTPUT_BYTES
   (262144 bytes)`, use a fixed argv and no shell, and the allow-list entry states the bound and
   notes that the site "is not a self-spawn, so it does not go through `guardedSpawn`" — which is
   the rule `dash/AGENTS.md` states ("Every **self-spawn** goes through `guardedSpawn`") applied to
   an external CLI, and the entry's presence is what `dash/tests/spawn-sites.test.ts` requires;
   and **which of the plan's tasks have actually shipped** (D63) — the marketplace route,
   the `SubagentStop` withdrawal, the native claim's withdrawal and the effective-shape work have
   landed, while the hooks-file correction, the consent gate, the residue report and the probe have
   not. **External evidence (Factory's own published documentation, fetched over HTTP):** the
   *Enterprise Controls & Managed Settings* page (the `telemetry` block's `enabled` master switch,
   the `strictKnownMarketplaces`, `strictEnabledPlugins` and `allowManagedHooksOnly` hard controls,
   the local marketplace source object's shape), the *Plugins* page (the marketplace layout, the
   command set, the identity rule) and the *Telemetry Privacy Controls* page (the granularity's
   most-restrictive-wins rule and **its unreadable-value fallback to `aggregate`**, the
   message-content rules). **No file outside the plan was written**, and **the two documentation
   checks could not be re-run in this session** — the conductor must run them against this amended
   plan, with `mergeReady` false, for the reason in §2.1b.
11. **The D57–D64 reconciliation pass (2026-11-02, the session that closed the amendment).** The
    same tooling limits apply (§2.1b): no semantic index, no documentation MCP server, no
    process-execution tool. **Discovery: codegraph 0, docs 0, direct reads 9 source files and 6
    governed documents, plus 32 targeted greps** — the source reads were
    `dash/src/canon/adapters/droid.ts` (the withdrawn native claim and the adapter's own header,
    which is what let §3.4's historical sentence be labelled as historical),
    `dash/src/capture/harnesses/factory-plugin/native-export.ts` (the shape resolver and the
    granularity's unreadable-value fallback at `:334`), `.../marketplace.test.ts` (the shipped
    settings-file case at `:223-249`, and the absence of any conflict or residue case),
    `.../marketplace.ts` (the absence of any refusal or residue mechanism),
    `dash/src/capture/harnesses/factory.ts` (the shipped consequence line at `:147-151`, including
    the ON branch), `dash/src/canon/adapters/registry.ts` and `dash/src/canon/metric-ingest.ts` (the
    threshold's line and the metric point's shape), `dash/src/capture/harnesses/factory-plugin/`
    (a directory listing), and `dash/tests/spawn-sites.allowlist.ts` (the `droid` CLI spawn site's
    bound); and the governed documents were `docs/todo/README.md` and the four named follow-up
    todos, `docs/todo/kyberdash-quarantine-harness-index.md`, `docs/todo/factory-uninstall-drops-disabled-plugin-entry.md`,
    `docs/plans/README.md` (the index row), `dash/AGENTS.md` and this plan. The greps covered
    `granularity`, `format`, `logMessageContent`, `aggregate`, `enabled`, `metric_point`,
    `conflict`, `residue`, `settings file`, `gen_ai.agent.name`, `no Factory content is stored`,
    `no Factory prompt`, `conflict`, the todo index's own rows, and the plan's own stale anchors
    (`outbox.jsonl`, `registry.ts:38`, `older / newer`, `four external documentation`,
    `Seven evidence sources`, `recorded in the receipt as the state the install found`), and the
    §-anchors whose text the corrections changed.
    **Every read was necessary because the semantic index could not be loaded at all**, and each one
    answered a specific question: **whether the native claim is really gone** (§3.4's historical
    label), **whether the shipped reader resolves the `enabled` switch and the aggregate fallback**
    (T44/T45's status, T47/T48's scope), **whether the shipped row's ON branch is as false as its
    OFF branch** (D58's fixed words), **whether the conflict refusal and the residue report exist**
    (T36/T37's status), **whether the metric point can carry content** (A22), **which todos
    actually carry D62's four follow-ups** (D62's precision), and **whether the `droid` spawn site
    meets the repository's process policy** (the allow-list entry's bound and its "not a
    self-spawn" note). **No file outside the plan was written**, and **the two documentation checks
    could not be re-run in this session** — the conductor must run them against this amended plan,
    with `mergeReady` false, for the reason in §2.1b.
12. **The D65 amendment session (2026-11-02).** The same tooling limits apply, and the same
    missing process-execution tool (§2.1c): no semantic index, no documentation MCP server, no
    shell, and therefore neither the `codegraph explore` CLI fallback nor `docs validate` /
    `docs drift` nor `git`. **Discovery: codegraph 0, docs 0, direct reads 7 source files and 4 governed documents
    (plus this plan), 19 targeted greps, and 2 external fetches** — the source
    reads were
    `dash/src/capture/harnesses/factory-plugin/install.ts` (the classifier's five kinds and the
    `same` branch's digest comparison, the `unchanged`/noop path, the tree-write trigger, the
    refusal vocabulary and the receipt's shape),
    `.../install-command.ts` (the verb's documented stdout contract, and how `--preserve` and
    `--if-installed` reach the installer),
    `.../marketplace.ts` (the harness state vocabulary, the install and uninstall command sets,
    and the plugin-list parsing, which reads the identity and the status and **no version**),
    `.../generated/plugin-source.ts` (the bundle's ownership marker
    `// kyberdash-factory-plugin v1`, and the plugin manifest's static `version: 1.0.0`),
    `dash/src/capture/harnesses/opencode-plugin/install.ts` (the `older`/`newer` classification
    with its `proven` flag, the `upgraded` / `would-upgrade` tokens, and the unproven-older keep)
    and `.../opencode-plugin/generated/plugin-source.ts` (the OpenCode marker's version scheme);
    `src/KyberWeave.Cli/Commands/Squad/Infrastructure/KyberDashCapturePluginStep.cs` (the shipped
    argument list, which hands the Factory call no `--config-dir`);
    the governed documents were `docs/dash/architecture.md` (the Factory pipeline section and the
    install and teardown route), `docs/cli-command-surface.md` (the Factory plugin-install row and
    its flags), `docs/kyber-squad/architecture.md` (the global capture-plugin step's example, which
    is stale) and `docs/plans/README.md` (the index row this amendment updates); the greps
    covered the plan's own anchors (the decisions, the harvest rows, the section headings, the
    task ledger, the risk table and the closeout), the harness module's exported surface, the
    OpenCode installer's vocabulary, the shipped step's argument list, and the two documents'
    Factory routes. **Every read was
    necessary because the semantic index could not be loaded at all**, and each one answered a
    specific question: **whether a superseded bundle can be upgraded today** (the classifier
    returns `same` for a receipt-proven older bundle, the tree is written only when it is
    `absent`, and the install then reports `unchanged` — so no upgrade exists) — the defect
    D65 fixes; **what the precedent already does** (the OpenCode installer's `older`/`newer`
    classification, its `upgraded`/`would-upgrade` tokens and its unproven-older keep); **which
    harness command can refresh the harness's cached copy** (the harness state vocabulary and
    Factory's own documented command set); and **what the tree can and cannot say about its own
    age** (the ownership marker is a constant `v1` and the manifest's version is static, so no
    content-version marker distinguishes an older bundle from a newer one); and **whether the
    shipped Squad step hands the Factory call a root** (it does not — `--config-dir` is added only
    for a non-null root — so `docs/kyber-squad/architecture.md:605`'s example is stale, and the
    correction is named in H17). **External evidence
    (Factory's own published documentation, fetched over HTTP):** the *Plugins* page — the CLI's
    command set (`droid plugin marketplace add|list|update|remove`, `droid plugin install|list|
    update|uninstall`), the identity rule, the cache copy ("Droid copies them into its cache
    unchanged"), and the `droid plugin update [plugin@marketplace] --scope <scope>` form whose
    documented example uses `--scope project`. **No file outside the plan and the plan's index row
    was written**, and **the two documentation checks could not be re-run in this session** — the
    conductor must run them against this amended plan and this amended index row, with
    `mergeReady` false, for the reason in §2.1c.
## Closeout mapping

**Closeout mapping, completed 2026-11-02.** Every durable decision in `## Decisions` (D0–D71)
and every harvest item in `## Design harvest` (H1–H29) has a named destination in live canonical
documentation. The `## Decisions` section is preserved intact through the archive move, and the
harvest was verified against the shipped code and the live evidence before this mapping was
written — not trusted from the plan's own table. **Two corrections were made to the mapping
after the archive, on 2026-11-02:** the install lifecycle's state vocabulary was described as a
status-surface report, which no shipped surface is (D47, H21, and now D69), and the consent
reader's duplication and the vocabulary's absence from the status surface were added as D68 and
D69. **A third correction was made on 2026-11-02, after the code council's findings:** the shipped
install path did not do what D65 and D67 decide — the refresh was armed for the upgrade only, the
ownership fell back to the tree's presence, and the superseded tree was reported as `unknown` — and
D70 records the three corrections that made it do so. **A fourth correction was made on
2026-11-02, after the second code council's findings:** the receipt proof reached the **install**
site only, the organisation's cached settings were read from the directory `FACTORY_DIR` moves,
one consent file carried two bounds in two units, the ownership rule was documented as a single
proof, the `unchanged` verdict was documented as a statement about the harness's cached copy, and
the capture-plugin guidance relay was written twice in the .NET CLI — and **D71** records the six
corrections that closed them.

**ADR disposition.** Two architectural records carry this plan's decisions:
[ADR 0053](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md), new, records the
hooks-plugin pipeline, the marketplace route, the consent contract and the native path's
withdrawal; and
[ADR 0031](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md), amended in
place for the Factory writer, carries the delegated-install clause, the semantic restore
criterion, the name-conflict refusal and the harness's wrapped list table. Every other decision
below is **documentation-only** — no ADR — ADR waiver: it refines the pattern ADR 0053 or ADR
0031 already establishes, and it changes no boundary, schema, contract, or persistent format
beyond what those two records state. The one decision that touches a persistent format (D42, the
metric point's identity) is waived separately, and its row says why: the additive `metric_point`
table lives inside the canon store ADR 0008 already owns, and the identity rule refines that
store's existing idempotent-upsert pattern rather than opening a new boundary.

**Documentation waivers.** Seven rows carry one, and each is execution or lifecycle provenance
with no surviving architectural, operational, or behavioural invariant: D0 and D1 (the packet's
development mode and autonomy values), D19 (the declared scale), D22 (the approval gate), and
D56, D63 and D64 (the amendments' lifecycle statements and task accounting, all superseded by
this archive). No ADR is waived with a documentation waiver: "no ADR" is not a documentation
waiver, and every decision that survives is carried by a live governed document.

| ID | Destination | Documentation-impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| D0 | Documentation waiver — packet-supplied development mode (`test-first`): execution metadata with no surviving invariant of its own, and the mode this plan ran under is recorded in this plan's frontmatter | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D1 | Documentation waiver — packet-supplied autonomy (`autonomous`), matching the repository root `AGENTS.md` Config Reg `<autonomy>`: execution metadata with no surviving invariant of its own | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D2 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) — the hooks-plugin pipeline is the capture path; a status-only row, native OpenTelemetry as the primary path, a merge into `~/.factory/hooks.json` and file-only ingest are each rejected with their reason | extends-component | KyberDash | 2026-11-02 | archived |
| D3 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) and [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the owned unit is a Kyber-owned marketplace tree under KyberDash's own state directory, installed by the harness's own commands, and the harness's cache copy is the harness's file | extends-component | KyberDash | 2026-11-02 | archived |
| D4 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#decision) and [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) — KyberDash never creates, reads, merges, rewrites or restores the owner's Factory settings or hooks files, and the harness's own commands change that file during an install KyberDash requested | extends-component | KyberDash | 2026-11-02 | archived |
| D5 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) and [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — contract version `1` in the `factory.*` namespace with its own span names; the OpenCode contract-3 keys are not reused, because the OpenCode adapter claims any span carrying them | extends-component | KyberDash | 2026-11-02 | archived |
| D6 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#factory-droid-capture-the-plugin-path-and-the-file-path) — one canonical harness id, `droid`, shared with the file source, so the dashboard shows Factory once | extends-component | KyberDash | 2026-11-02 | archived |
| D7 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) and [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — one adapter named `droid`, claiming the plugin envelope and nothing else, so a span is claimed by one rule only | extends-component | KyberDash | 2026-11-02 | archived |
| D8 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-honest-token-position) and [dash/architecture.md](../../dash/architecture.md#the-factory-source-precedence-rule) — the pairing is exact session-id equality: no fuzzy join, no time window, no counter-equality join, and a wrong id degrades to `not_measurable` rather than to a wrong number | extends-component | KyberDash | 2026-11-02 | archived |
| D9 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-honest-token-position) — the file source is the only source of Factory's counters, the plugin contributes none, and a missing counter stays missing rather than reading zero | extends-component | KyberDash | 2026-11-02 | archived |
| D10 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) and [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) — the consent file is created with `content: false` only when absent, and the Settings switch that installs the capture deliberately never turns content recording on | extends-component | KyberDash | 2026-11-02 | archived |
| D11 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) and [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — strict refuses a foreign or owner-edited tree with exit 1 and writes nothing, preserve keeps it with a warning and exits 0, and `--if-installed` decides from the harness's own registry | extends-component | KyberDash | 2026-11-02 | archived |
| D12 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) and [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) — native OpenTelemetry is an authorized complementary path and never the primary one; the endpoint is set by machine variables or by an organisation-managed block that overrides them | extends-component | KyberDash | 2026-11-02 | archived |
| D13 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — one self-contained ESM file, bundled by esbuild and committed as generated text, with the ownership marker `// kyberdash-factory-plugin v1` and a drift guard that fails when the committed text differs from a fresh bundle | extends-component | KyberDash | 2026-11-02 | archived |
| D14 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the generic transport and consent writers are shared leaves and the OpenCode modules are thin re-exports of them, so the OpenCode entry's imports and its wire are unchanged; what the extraction did not keep is the committed OpenCode bundle byte-stable, and the drift guard proves the committed bytes are current rather than that they never changed | extends-component | KyberDash | 2026-11-02 | archived |
| D15 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — a bounded synchronous POST plus a bounded file-backed spool (`spool.jsonl`, 500 events and 4 MB, drop-oldest) drained on every run; every error is swallowed and the hook exits 0, so the capture never fails the host | extends-component | KyberDash | 2026-11-02 | archived |
| D16 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#decision) — the registry row declares no config key, owns the marketplace tree, and takes its install evidence from the harness's own registry rather than from a file under the Factory root | extends-component | KyberDash | 2026-11-02 | archived |
| D17 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the Settings drawer renders the registry's rows generically, so the Factory row appears without a web change | extends-component | KyberDash | 2026-11-02 | archived |
| D18 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md#global-capture-plugin-step), [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md#2-global-scope---global) and [install.md](../../install.md#updating) — Squad runs one plugin call per capture-plugin harness in a fixed order and hands the Factory call no root, and the updater runs the same verb in preserve mode with `--if-installed` and no root | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D19 | Documentation waiver — the declared scale (`extends-component`, components `KyberDash` and `KyberSquad`) is authoring metadata carried in this plan's frontmatter and in the plan index row, and the components each row touches are named per row below | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D20 | [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) — the live check is an owner-run check and never part of the test suite, because a live Droid turn calls a model; the recorded replay proves what the dashboard renders from the replay and nothing more | extends-component | KyberDash | 2026-11-02 | archived |
| D21 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#what-the-file-source-measures) and [todo/README.md](../../todo/README.md) — the file source's even split of the session's token total stays as it is, is named a known lossy approximation, and the follow-up that would remove it is carried as a todo; the sibling-harness coverage gap is carried as a todo too | extends-component | KyberDash | 2026-11-02 | archived |
| D22 | Documentation waiver — execution-approval provenance only: the D22 row in `## Decisions` and the plan index row record the gate, and no architectural, operational, or behavioural invariant survives it | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D23 | [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) and [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) — the plugin envelope is the only Factory span claimed, so the plugin-first precedence rule is retained for any native claim that is ever restored and is inert while no native span is claimed | extends-component | KyberDash | 2026-11-02 | archived |
| D24 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-model-a-factory-session-publishes) — the model chain as shipped: the plugin names a model only when the hook payload carries one, the file source publishes the companion file's wrapper-stripped model, and the session publishes its turn records' model, else its counter record's, else none | extends-component | KyberDash | 2026-11-02 | archived |
| D25 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#a-child-sessions-tokens) — a child session's tokens are paired from its own companion file through the same exact-id pairing, and a child with no companion file of its own reads `not_measurable` rather than borrowing | extends-component | KyberDash | 2026-11-02 | archived |
| D26 | [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) — an organisation-managed `telemetry` block overrides the machine variables, and the row reports the export's effective shape with the level that pinned each value instead of reporting the path as working | extends-component | KyberDash | 2026-11-02 | archived |
| D27 | [plans/README.md](../../plans/README.md) — the plan file, its frontmatter, its body status and the index row are reconciled in the same change, and the body status is authoritative when the two disagree | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D28 | [dash/architecture.md](../../dash/architecture.md#the-factory-source-precedence-rule) and [rules/kyberdash-harness-collection.md](../../rules/kyberdash-harness-collection.md#per-harness-ground-truth) — the source-precedence rule: the plugin rows are the session's turns, the file rows are its counters and its cost, neither source is counted twice, and the rule is inert for every other harness | extends-component | KyberDash | 2026-11-02 | archived |
| D29 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#a-child-sessions-tokens) — a child session's counters come from its own companion file; the parent's `childInclusiveTokenUsageBySessionId` map is the parent's aggregate view of its children and is never a token source | extends-component | KyberDash | 2026-11-02 | archived |
| D30 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-honest-token-position) — all four counter-class reasons name the file source and the session-id pairing as where the number would come from, and they name the rule rather than the outcome, because the adapter cannot know whether a file row will pair | extends-component | KyberDash | 2026-11-02 | archived |
| D31 | [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) — the decoded metric points are persisted in the canon store's own `metric_point` table through the production sink's `upsertMetrics` and the `ingestMetricBatch` seam, nothing projects them yet, and the writer's refuse-rather-than-drop rule is unchanged | extends-component | KyberDash | 2026-11-02 | archived |
| D32 | [dash/architecture.md](../../dash/architecture.md#the-status-rule-at-the-normalization-seam) — an error status is never downgraded at the receiver-timing seam, so the `droid` adapter's `error.type` reaches the stored record, and the rule is inert for every other harness | extends-component | KyberDash | 2026-11-02 | archived |
| D33 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md#global-capture-plugin-step) and [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md#2-global-scope---global) — the capture-plugin step is harness-aware: one plugin call per capture-plugin harness, in a fixed order, with the OpenCode call first and the Factory call following it | extends-component | KyberSquad | 2026-11-02 | archived |
| D34 | [install.md](../../install.md#updating) and [kyber-squad/architecture.md](../../kyber-squad/architecture.md#global-capture-plugin-step) — the updater's Factory call carries `--preserve` and `--if-installed`, and hands the verb no root, because the plugin is installed where the harness already holds it | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D35 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md#global-capture-plugin-step) and [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) — `--config-dir` is the OpenCode config root; the Factory verb refuses it with a usage error and the Factory call is handed no root at all | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D36 | [rules/kyberdash-harness-collection.md](../../rules/kyberdash-harness-collection.md#per-harness-ground-truth) — the Factory row states the split, not only the join: the plugin rows are the session's turns, the file rows are its counters and its cost, and neither source is counted twice | extends-component | KyberDash | 2026-11-02 | archived |
| D37 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) and [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) — the row reports the export's endpoint verdict and its effective shape, and never reports the native path as working | extends-component | KyberDash | 2026-11-02 | archived |
| D38 | [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) — the table is `metric_point`, created by the store's own schema and written through the production sink; the name this plan first carried was never in any code | extends-component | KyberDash | 2026-11-02 | archived |
| D39 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-model-a-factory-session-publishes) — the shipped chain in three steps, and `~/.factory/settings.json` is never a model source: it has no top-level `model` key, and reading a nested per-role block would attribute a subagent's model to its parent | extends-component | KyberDash | 2026-11-02 | archived |
| D40 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-model-a-factory-session-publishes) — the literal `unknown` is the file source's own placeholder for a companion file that names no model, and it is not a measured model; the plugin path never produces one | extends-component | KyberDash | 2026-11-02 | archived |
| D41 | [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) — the native-export availability reporting shipped, and the marker that called it unshipped is gone | extends-component | KyberDash | 2026-11-02 | archived |
| D42 | [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) — a metric point's identity is a content digest of everything the row carries, so the table holds one row per distinct decoded point and a re-delivered point lands on the row it already occupies. No ADR — ADR waiver: the additive table lives inside the canon store ADR 0008 already owns, and the identity rule refines that store's existing idempotent-upsert pattern rather than opening a new boundary | extends-component | KyberDash | 2026-11-02 | archived |
| D43 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision), [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#decision) and [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) — the install is the harness's own marketplace and plugin commands, and KyberDash learns the harness's state only through the harness's own CLI | extends-component | KyberDash | 2026-11-02 | archived |
| D44 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision), [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) — KyberDash owns exactly three things: the marketplace tree, the receipt and the consent file; the uninstaller removes the harness-side state first, then the tree and the receipt | extends-component | KyberDash | 2026-11-02 | archived |
| D45 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#decision) and [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) — the restore criterion is semantic rather than byte-identity: no entry the install added remains, no entry the owner had is lost, every other key is unchanged in content, and any residue the harness's uninstall leaves is reported with the command that removes it and never patched by hand | extends-component | KyberDash | 2026-11-02 | archived |
| D46 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#decision), [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) — a marketplace name or a plugin identity the harness holds that KyberDash does not own is refused in strict mode and kept with a warning in preserve mode, and the report names the colliding entry, its registry and the command that removes it | extends-component | KyberDash | 2026-11-02 | archived |
| D47 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — every partial state is a named state (`marketplace-tree-written`, `marketplace-registered`, `plugin-installed`, `conflict`, `unknown`), every one of them is resumable by re-running the install, and a missing `droid` CLI reads as `unknown` with the named cause rather than as an install that succeeded. The vocabulary is the **install-side** state the installer classifies; the status surfaces do not report it, and the row reports the tree's state only (D69) | extends-component | KyberDash | 2026-11-02 | archived |
| D48 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#decision), [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) and [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the letter of the ownership rule stands, the byte-identity criterion is withdrawn, and the delegated write is safe because the writer owns a whole separate file and never a shared settings file | extends-component | KyberDash | 2026-11-02 | archived |
| D49 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#status) and [adr/README.md](../../adr/README.md) — ADR 0031's Factory amendment gains the delegated-install clause and the marketplace route, the index row carries the amendment pointer, and ADR 0026 needs no amendment because its rule is unchanged and unsatisfied | extends-component | KyberDash | 2026-11-02 | archived |
| D50 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision), [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) — the native span claim is withdrawn with the arithmetic that made it inert: the group vote divides the winner's evidence by the group's span count, so a real trace scores at most 0.5 against the registry's 0.6 threshold, and only the native metrics path remains | extends-component | KyberDash | 2026-11-02 | archived |
| D51 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) and [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) — the row reports the format, the granularity, the message-content switch and the organisation's `enabled` master switch, each with the level that pinned it, and says plainly that KyberDash claims no native span | extends-component | KyberDash | 2026-11-02 | archived |
| D52 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) — the consent governs the routes KyberDash's own capture controls, the native gate runs at the ingest seam before the normalization and before the quarantine write, and its fail-closed arm strips the content of a span that belongs to no harness while a claimed span's content is never touched | extends-component | KyberDash | 2026-11-02 | archived |
| D53 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) and [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the plugin's `hooks/hooks.json` is keyed directly by event name, the shape Factory's own documentation shows for a plugin's hooks file, and the earlier wrapper worked only through undocumented tolerance | extends-component | KyberDash | 2026-11-02 | archived |
| D54 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision), [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#what-the-plugin-measures) — the `SubagentStop` event produces no span and is not registered, because the documented payload carries no child session id; a child session's own turns still arrive as its own plugin spans | extends-component | KyberDash | 2026-11-02 | archived |
| D55 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory), [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#what-the-file-source-measures) and [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) — `FACTORY_DIR` is honoured only by the file source, for the sessions tree; the install writes nothing under the Factory root, and the Factory tree lands under KyberDash's own marketplace root | extends-component | KyberDash | 2026-11-02 | archived |
| D56 | Documentation waiver — the lifecycle statement this amendment recorded is superseded by this archive: the plan's body status and the plan index row carry the lifecycle state, and no architectural, operational, or behavioural invariant survives it | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D57 | [rules/kyberdash-harness-collection.md](../../rules/kyberdash-harness-collection.md#per-harness-ground-truth), [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) and [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the consent governs the plugin's prompt text on the wire and the native route's content, and it does not govern the Factory file source, which stores the owner's first message fragment whatever the consent file says | extends-component | KyberDash | 2026-11-02 | archived |
| D58 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) — the reported shape gains the organisation's `enabled` master switch, and a granularity that exists and cannot be read resolves to `aggregate`, the restrictive one, while a granularity nothing pinned stays `user` | extends-component | KyberDash | 2026-11-02 | archived |
| D59 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#decision), [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) and [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the receipt records the owned tree and its digests and nothing about the harness-side state the install found, and the teardown promises only what KyberDash's own actions can promise | extends-component | KyberDash | 2026-11-02 | archived |
| D60 | [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify), [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) and [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — an organisation's `strictKnownMarketplaces`, `strictEnabledPlugins` and `allowManagedHooksOnly` can refuse the route or make it silently inert, KyberDash works around none of them, and a refusal is reported with the harness's own stderr rather than as a success | extends-component | KyberDash | 2026-11-02 | archived |
| D61 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#what-the-plugin-measures) — the corrections the shipped code forced are carried in the documents: the spool's name is `spool.jsonl`, the tree classifier's kinds are `absent`, `unreadable`, `foreign`, `edited`, `upgrade` and `same`, the tool attributes carry byte sizes rather than content, and the child-session sentence is gone | extends-component | KyberDash | 2026-11-02 | archived |
| D62 | [todo/README.md](../../todo/README.md) — the follow-ups this amendment filed are carried as todos under the todo index, each named exactly: the quarantine table's unbounded rows, consent as a capture switch rather than a purge, the harness's teardown dropping a pre-existing owner entry, and an organisation's `allowManagedHooksOnly` making the capture silently inert | extends-component | KyberDash | 2026-11-02 | archived |
| D63 | Documentation waiver — the task accounting this amendment recorded is execution bookkeeping, superseded by this archive and by the plan index row; the durable content of each shipped task is carried by the harvest rows below | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D64 | Documentation waiver — the lifecycle statement this amendment recorded is superseded by this archive: the plan's body status and the plan index row carry the lifecycle state, and no architectural, operational, or behavioural invariant survives it | extends-component | KyberDash, KyberSquad | 2026-11-02 | archived |
| D65 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision), [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) — the upgrade path: a receipt-proven tree whose bytes still match its receipt and whose content is not the current bundle is an upgrade, the tree and receipt are rewritten, and the harness's cached copy is refreshed with `droid plugin update kyberdash-factory@kyberdash --scope user`, never with `plugin install` against an installed plugin | extends-component | KyberDash | 2026-11-02 | archived |
| D66 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#decision), [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) — the harness's registry is read as the harness reports it, with the marketplace list's source and the plugin list's status, and the list table read as the wrapped table it is; the consent gate's fail-closed arm is named, and a refusal is reported with the harness's own stderr | extends-component | KyberDash | 2026-11-02 | archived |
| D67 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#decision) — a tree no receipt proves whose content is the current bundle is not foreign: the install writes the receipt that proves it, so the next upgrade is provable; the harness-side command sequence is derived from the harness's own registry, so "installed but not registered" is expressible; and the teardown removes KyberDash's own tree on a conflict while leaving the foreign entry alone | extends-component | KyberDash | 2026-11-02 | archived |
| D68 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the consent file's read side exists twice, the canon seam's gate and the copy the plugin bundle inlines; the duplication is kept because each copy is a leaf and merging them in either direction breaks a leaf rule, and a parity test runs both readers over one corpus (valid, malformed, oversized, missing, unknown-version) and round trips the shared writer into both | extends-component | KyberDash | 2026-11-02 | archived |
| D69 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [todo/factory-org-managed-plugin-not-reported.md](../../todo/factory-org-managed-plugin-not-reported.md#an-organisation-controlled-factory-plugin-that-will-never-load-is-not-reported) — the harness-side lifecycle vocabulary is the install-side state the installer classifies and is not surfaced by the status surfaces, because a status read never spawns the harness's CLI; the install path is the only place it is reported, and the organisation-policy gap stays open as a todo | extends-component | KyberDash | 2026-11-02 | archived |
| D70 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the shipped install path does what D65 and D67 decide: the harness's cached copy is refreshed for a freshly written tree as well as for a superseded one, the disposition and the printed line name what happened to the tree (`installed` for a tree written fresh, `upgraded` for a superseded one), the receipt proves the plugin identity KyberDash's own when it names this tree, this marketplace and this identity, and the status surface's own word for a superseded tree is `plugin: installed (an older version than this build)` | extends-component | KyberDash | 2026-11-02 | archived |
| D71 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) and [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) — the second council's six corrections: the **uninstall** proves the plugin with the receipt it already reads, under the install's own conditions, so the harness's own `plugin uninstall` runs instead of a conflict that deleted the proof and left the plugin capturing; the organisation's cached settings are read from the **harness's own directory**, never from the directory `FACTORY_DIR` moves; one consent file has **one bound in UTF-8 bytes** for every reader; the ownership rule is an **OR** — the tree or the receipt — and the document says so; the `unchanged` verdict reads the **tree** and the harness's own registry, never the harness's cached copy; and the capture-plugin guidance relay lives in **one** place in the .NET CLI, with the update and the Squad deploy step held to the same lines by one test (the statements of what the step relays stay in [install.md](../../install.md#updating) and [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md#2-global-scope---global)) | extends-component | KyberDash | 2026-11-02 | archived |
| H1 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md) — the new ADR: Factory (Droid) capture is a hooks-plugin pipeline, with the marketplace tree, the receipt, the consent file, the `droid` wire contract and the rejection of native OpenTelemetry as the primary path (D2, D3, D5, D12) | extends-component | KyberDash | 2026-11-02 | archived |
| H2 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#status) — amended in place: the Factory writer owns a plugin tree and a consent file, declares no config key, and never writes `~/.factory/settings.json` or `~/.factory/hooks.json` (D4, D10, D16) | extends-component | KyberDash | 2026-11-02 | archived |
| H3 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the Factory capture pipeline with its Mermaid flow, the marketplace tree's layout, the file-backed spool, the consent contract with its two read-side copies and the parity that holds them together (D68), the install modes and the shared `droid` harness id (D3, D6, D8, D11, D15) | extends-component | KyberDash | 2026-11-02 | archived |
| H4 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#factory-droid-capture-the-plugin-path-and-the-file-path) — the Factory rows: what the plugin measures, what the file source measures, and the honest token position with its exact-id pairing (D8, D9) | extends-component | KyberDash | 2026-11-02 | archived |
| H5 | [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) — enable and verify Factory capture, the consent verb and its exit codes, the live check, and the uninstall procedure | extends-component | KyberDash | 2026-11-02 | archived |
| H6 | [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) — `kyber capture enable`, `disable` and `status` for the `factory` harness, `kyber capture plugin install --harness factory`, and the new `kyber capture plugin consent` verb (D10) | extends-component | KyberDash | 2026-11-02 | archived |
| H7 | [rules/kyberdash-harness-collection.md](../../rules/kyberdash-harness-collection.md#per-harness-ground-truth) — the Factory row of the per-harness ground truth table: the plugin hooks as the primary mechanism, the file source as the token source, the completeness predicate, and the D28 split (D2, D8, D28, D36) | extends-component | KyberDash | 2026-11-02 | archived |
| H8 | [dash/onboarding.md](../../dash/onboarding.md#factory-capture-plugin) — the Factory capture plugin section: what it collects, what consent governs, how to turn it on, and where to read the runbook | extends-component | KyberDash | 2026-11-02 | archived |
| H9 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md#global-capture-plugin-step), [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md#2-global-scope---global) and [kyber-squad/architecture.md](../../kyber-squad/architecture.md#the-factory-plugin-subtree-squad-does-not-own) — the global capture-plugin step in preserve mode beside the OpenCode step, and the marketplace tree under KyberDash's own state directory that Squad does not own (D18) | extends-component | KyberSquad | 2026-11-02 | archived |
| H10 | [install.md](../../install.md#updating) — the plugin step's Factory guidance line, its preserve mode and `--if-installed`, and the relay of the consent reminder (D18) | extends-component | KyberDash | 2026-11-02 | archived |
| H11 | [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) — the authorized native path: `OTEL_TELEMETRY_ENDPOINT`, the `genai` attribute vocabulary, the `POST /v1/metrics` route, the plugin-first precedence rule, the tokenless and lineage-free limits, and the organisation-override reporting rule (D23, D26) | extends-component | KyberDash | 2026-11-02 | archived |
| H12 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-model-a-factory-session-publishes) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#a-child-sessions-tokens) — the model-resolution chain as shipped (D24, D39), the child-session token pairing rule (D25, D29), and the file source's own `unknown` placeholder (D40) | extends-component | KyberDash | 2026-11-02 | archived |
| H13 | [dash/architecture.md](../../dash/architecture.md#the-factory-source-precedence-rule) — the Factory source-precedence rule (D28): the plugin rows are the session's turns, the file rows are its counters and its cost, neither source is counted twice, and the rule is inert for every other harness | extends-component | KyberDash | 2026-11-02 | archived |
| H14 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#a-child-sessions-tokens) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-honest-token-position) — the child-session token source (D29) and the plugin rows' `not_measurable` reasons naming the file-source pairing (D30) | extends-component | KyberDash | 2026-11-02 | archived |
| H15 | [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) — the native OTLP metrics destination (D31, D38, D42): the `metric_point` table, one row per distinct decoded point, nothing projecting them yet, the `otlp:metrics` receiver activity, and the writer's unchanged refusal rule | extends-component | KyberDash | 2026-11-02 | archived |
| H16 | [dash/architecture.md](../../dash/architecture.md#the-status-rule-at-the-normalization-seam) — the status rule at the normalization seam (D32): an error status is never downgraded, so the `droid` adapter's `error.type` reaches the stored record, and the rule is inert for every other harness | extends-component | KyberDash | 2026-11-02 | archived |
| H17 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md#global-capture-plugin-step) and [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md#2-global-scope---global) — the capture-plugin step's harness-aware shape (D33), the OpenCode call's own root and the Factory call's none (D35), the updater's Factory call with `--preserve` and `--if-installed` and no root (D34), and the corrected example that no longer shows a Factory `--config-dir` | extends-component | KyberSquad | 2026-11-02 | archived |
| H18 | [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) — the `--config-dir` flag's scope (D35, D55): it is the OpenCode config root, the Factory plugin-install verb refuses it with exit 2, and the Factory tree lands under KyberDash's own marketplace root while `FACTORY_DIR` is honoured only by the file source, for the sessions tree | extends-component | KyberDash | 2026-11-02 | archived |
| H19 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#decision) — amended in place (D49): the Factory writer owns a marketplace tree under `~/.kyberdash/marketplaces/kyberdash/`, the install is delegated to the harness's own commands, the delegated-install clause, and the owner's settings and hooks files still never created, read, merged, rewritten or restored by KyberDash (D48) | extends-component | KyberDash | 2026-11-02 | archived |
| H20 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#status) — amended with the install route and the native path's fate (D43, D50): the plugin is installed through the harness's marketplace, the owned unit is the marketplace tree and the receipt, and the native span claim is withdrawn with the group-vote arithmetic that made it inert | extends-component | KyberDash | 2026-11-02 | archived |
| H21 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the capture pipeline as it now stands plus the install lifecycle: the marketplace tree, the harness's own plugin commands, the state vocabulary, the refusal vocabulary and the residue rule (D43–D47); the state vocabulary is the install-side one, and the status surfaces report the tree's state only (D69) | extends-component | KyberDash | 2026-11-02 | archived |
| H22 | [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) — the install, enable, disable and uninstall procedure under the marketplace route, the fact that the harness's own commands write the owner's settings file, the residue rule with the exact command that clears it, and the live check that establishes the harness-side semantics (D43, D45, D46, D47) | extends-component | KyberDash | 2026-11-02 | archived |
| H23 | [rules/kyberdash-harness-collection.md](../../rules/kyberdash-harness-collection.md#per-harness-ground-truth) — the Factory row states the install route, with the harness's own registry as the install evidence and no directory under the Factory root, the D28 split, and the fact that the native path claims no span (D43, D50) | extends-component | KyberDash | 2026-11-02 | archived |
| H24 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) — the native export's effective shape (the format, the granularity, the message-content switch and the organisation's `enabled` master switch, with which level pinned each), what KyberDash stores from it (the metrics in `metric_point`, no native span), and what the consent governs and what it does not (D51, D52, D57, D58) | extends-component | KyberDash | 2026-11-02 | archived |
| H25 | [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) — the Factory plugin-install verb's route and its exit codes: the marketplace route, the refusal of `--config-dir` with exit 2, the named refusal states `conflict`, `drift` and `unreadable`, and the residue report (D37, D46, D47, D55) | extends-component | KyberDash | 2026-11-02 | archived |
| H26 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) and [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) — the consent's scope stated as what it governs and what it does not (D57), and the organisation-control position (D60): the hard controls can refuse the route or make it silently inert, KyberDash works around none of them, and an install the harness refused is reported with the harness's own stderr | extends-component | KyberDash | 2026-11-02 | archived |
| H27 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) — the install lifecycle's upgrade path (D65): a receipt-proven tree whose bytes still match and whose content is not the current bundle is an upgrade, the tree and its receipt are rewritten, the harness's cached copy is refreshed with `droid plugin update`, and `unchanged` means both a current tree and an installed harness copy; the status surface's own word for such a tree is `plugin: installed (an older version than this build)` | extends-component | KyberDash | 2026-11-02 | archived |
| H28 | [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) — the routine Factory install against the upgrade: when the tree is rewritten and the harness's cached copy refreshed (`upgraded`), when nothing happens (`unchanged`), and the exact command that refreshes the harness's copy when the harness refuses the refresh (D65) | extends-component | KyberDash | 2026-11-02 | archived |
| H29 | [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) — the Factory plugin-install verb's stdout tokens `installed`, `would-install`, `unchanged`, `upgraded` and `would-upgrade`, and the refusal reporting that keeps a harness refusal from ever reading as a success (D65) | extends-component | KyberDash | 2026-11-02 | archived |

### Harvest verification (H1–H29)

Each harvest target was read after the implementation landed and checked against the plan's row,
not trusted from the plan. Nine rows were not satisfied as written — H17, H18, H21, H23, H24,
H25, H26, H27 and H28 — and the document was corrected in each case; the table below records the
correction, and names the three rows whose own items were already present but whose sibling rows
needed the same sentence added to the same place (H3, H7, H9). **A later correction pass, on
2026-11-02, corrected three more claims in the same document**: the install lifecycle's state
vocabulary was described as a status-surface report, which no shipped surface is (H21, and D47,
D69), the status surface's own word for a tree holding the previous bundle was missing (H27), and
the consent contract's two read-side copies were not stated (H3, D68).

| ID | Target document | Verified claim | Result |
|---|---|---|---|
| H1 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md) | The record states the pipeline, the marketplace tree, the receipt, the consent file, the contract-1 wire contract, and the native path as authorized and complementary with no native span claimed | present |
| H2 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#status) | The Factory amendment states the owned tree, the receipt, the consent file, the declared absence of a config key, and the delegated install | present |
| H3 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) | The pipeline, the Mermaid flow, the tree layout, the spool, the consent contract, the install modes and the shared `droid` id are all stated | present; the bundle's delivery (D13) and the shared transport leaves (D14) were added to the same section; the consent contract's two read-side copies and the parity that holds them together were added in the 2026-11-02 correction pass (D68) |
| H4 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#factory-droid-capture-the-plugin-path-and-the-file-path) | The plugin row, the file-source row and the honest token position are stated | present |
| H5 | [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) | The enable-and-verify procedure, the consent verb, the live check and the uninstall procedure are stated | present |
| H6 | [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) | The enable, disable, status, plugin install and consent rows carry the Factory harness | present |
| H7 | [rules/kyberdash-harness-collection.md](../../rules/kyberdash-harness-collection.md#per-harness-ground-truth) | The Factory row states the primary mechanism, the token source, the completeness predicate and the split | present; the install route that H23 also requires was added to the same row |
| H8 | [dash/onboarding.md](../../dash/onboarding.md#factory-capture-plugin) | The section states what is collected, what consent governs, and how to turn it on | present |
| H9 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md#global-capture-plugin-step) and [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md#2-global-scope---global) | The global step, the preserve mode, and the subtree Squad does not own are stated | present; the updater's own call that H17 also requires was added to the same pages |
| H10 | [install.md](../../install.md#updating) | The Factory guidance line, the preserve mode with `--if-installed`, and the consent reminder are stated | present |
| H11 | [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) | The endpoint, the vocabulary, the metrics route, the precedence rule, the limits and the organisation override are stated | present |
| H12 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-model-a-factory-session-publishes) | The model chain, the child pairing and the file source's `unknown` placeholder are stated | present |
| H13 | [dash/architecture.md](../../dash/architecture.md#the-factory-source-precedence-rule) | The split, the per-figure table, and the rule's inertness outside `droid` are stated | present |
| H14 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#a-child-sessions-tokens) | The child's own companion file, the parent's map, and the reasons naming the pairing are stated | present |
| H15 | [dash/architecture.md](../../dash/architecture.md#factorys-native-opentelemetry-path-authorized-complementary) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) | The `metric_point` table, the content-digest identity, the `otlp:metrics` activity and the writer's refusal rule are stated | present |
| H16 | [dash/architecture.md](../../dash/architecture.md#the-status-rule-at-the-normalization-seam) | The never-downgrade rule, the `error.type` consequence and the rule's inertness are stated | present |
| H17 | [kyber-squad/architecture.md](../../kyber-squad/architecture.md#global-capture-plugin-step) and [kyber-squad/onboarding.md](../../kyber-squad/onboarding.md#2-global-scope---global) | The harness-aware shape, the roots, and the updater's Factory call are stated | corrected this pass — the updater's call was missing, and the document's own example had shown a Factory `--config-dir` the verb refuses |
| H18 | [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) | The flag's scope, the exit-2 refusal and the root resolution are stated | corrected this pass — the exit code and the root resolution were missing |
| H19 | [adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md#decision) | The delegated install, the owned tree, and the ownership letter are stated | present |
| H20 | [adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md](../../adr/0053-factory-capture-is-a-hooks-plugin-pipeline.md#status) | The install route and the native path's withdrawal, with the arithmetic, are stated | present |
| H21 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) | The tree, the harness commands, the state vocabulary, the refusal vocabulary and the residue rule are stated | corrected this pass — the state and refusal vocabularies and the residue rule were missing, and the `--if-installed` clause described a rule the shipped code no longer follows; the 2026-11-02 correction pass added that the state vocabulary is install-side and that no status surface reports it (D69) |
| H22 | [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) | The procedure, the delegated settings write, the residue rule and the live check are stated | present |
| H23 | [rules/kyberdash-harness-collection.md](../../rules/kyberdash-harness-collection.md#per-harness-ground-truth) | The install route, the registry as the install evidence, the split and the withdrawn native claim are stated | corrected this pass — the install route was missing |
| H24 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) | The effective shape, the stored metrics, and the consent's reach are stated | corrected this pass — the consent's scope was stale: it named the plugin's prompt text alone and did not carry the native route's gate that has since shipped |
| H25 | [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) | The route, the exit codes, the refusal states and the residue report are stated | corrected this pass — the exit-2 refusal of `--config-dir` and the residue report were missing |
| H26 | [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md#the-native-export) and [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) | The consent's scope, and the organisation-control position, are stated in both | corrected this pass — the organisation-control position was missing from both, and the runbook did not state what the consent does not govern |
| H27 | [dash/architecture.md](../../dash/architecture.md#factory-droid-capture-plugin-pipeline-kyberdash-factory) | The upgrade path, the refresh command, the registry-derived sequence and the meaning of `unchanged` are stated | corrected this pass — the sentence that every unproven tree is foreign was superseded by D67 and was corrected; the 2026-11-02 correction pass added the status surface's own word for an upgrade tree |
| H28 | [dash/runbook.md](../../dash/runbook.md#1c-factory-droid-capture-enable-and-verify) | The `upgraded` and `unchanged` verdicts, and the command that completes a refused refresh, are stated | present; the refused-refresh command was added this pass |
| H29 | [cli-command-surface.md](../../cli-command-surface.md#canonical-telemetry-commands-kyber) | The five verdict tokens and the refusal reporting are stated | present |

### Closeout evidence

- **Live evidence.** The upgrade path, the harness's list-table wrapping, the marketplace route
  and the hooks file's shape were verified against a real Factory installation on 2026-11-02;
  the records are under `artifacts/evidence/factory-capture-live/20261102T094024Z/` (gitignored,
  on disk). The upgrade rewrote the Kyber-owned tree and the harness's cached copy a second
  later, and a real Droid turn produced the session, turn and tool spans through the corrected
  hooks file with the consent off and no prompt text on the live span.
- **Documentation gates.** `docs validate . --merge-ready` and `docs drift .` are run after the
  archive move; their results are recorded in the closeout report, not in this artifact.
- **Not claimed.** The quarantine table's rows are not bounded: the 256 KiB cap bounds one
  packet, not the table, and no retention policy for the rows themselves was identified. The
  organisation's `allowManagedHooksOnly` case is silent, and the status surface does not report
  it. Both are carried as todos under the todo index (D62).
