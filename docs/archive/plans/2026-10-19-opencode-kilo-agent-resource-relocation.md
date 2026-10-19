---
id: plans/2026-10-19-opencode-kilo-agent-resource-relocation
title: "OpenCode and Kilo: agent reference files deploy as phantom agents"
doc-type: plan
status: complete
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-10-19
development-mode: test-first
---

# OpenCode and Kilo: agent reference files deploy as phantom agents

**Status: Ready 2026-10-19 — approved and execution authorized.** The human approved
execution ("Approve and execute", 2026-10-19, via conductor); D1–D4 are answered in the
ledger below with approval provenance recorded (D2 departs from the recommendation:
strict scope, **no todo seed**), and both required documentation checks have passed (see
the validation record below). Revision history: revision 1 proposed ZCode-style
skills-tree relocation; the human's constraint (§1.4) drove re-investigation against both
harnesses' source and the approved co-located extension-guard design. Frontmatter
`status` uses the ontology's closed vocabulary (`current`); the Ready lifecycle state is
carried here and in the plan index.

**Development mode:** `test-first` (the default; no opt-out supplied). Every behavior
change lands behind a failing contract test first; the existing tests that pin the
misrouting are the RED vehicle once rewritten.

**Validation record.** The authoring session has no process-running tool, so both
documentation checks were run by the conductor after the revision-3 save:
`docs validate .` → **0 findings (PASSED)** and `docs drift .` → **0 findings (PASSED)**,
reported 2026-10-19. The finalize edits that followed (approval provenance and the Ready
transition) are metadata-only; the binding re-checks remain the T5/T6 docs gates and the
finishing PR's `docs validate . --merge-ready` per §5.3 and KW-DOC-LIFECYCLE-003.

---

## 1. Problem and goal

### 1.1 User report (verbatim)

> "in opencode and kilo the reference files are being displaed / deployed as agents and not
> reference files"

### 1.2 Reproduction (investigator, confirmed)

A scratch console app referencing `KyberWeave.Core` invoked the production
`OpenCodeRenderer`/`KiloRenderer`/`ZCodeRenderer.RenderAsync` over `products/kyber-squad`:
ten agent-reference `.md` files were emitted under `.opencode/agents/{architect,conductor,task-reviewer}/references/`
and the same ten under `.kilo/agents/…`, while ZCode correctly relocates them under
`.zcode/skills/<owner>/references/`. Both OpenCode and Kilo register those nested `.md`
files as agents (source-verified, §2.6).

### 1.3 Goal

On the `opencode` and `kilo` targets, an agent's declared resource closure must never be
**registered** as agents by the harness, while the files stay **co-located beside and
wholly owned by their agent principal** and the authored links still resolve. The
deviation from verbatim projection must be recorded as a degradation and governed by an
ADR, closing the third recurrence of this symptom class (ZCode, Copilot-global, now
OpenCode/Kilo) with a rule future targets inherit.

### 1.4 Human design constraint (2026-10-19, verbatim; spelling as written)

> "i would really prefer to keep the reference files wholy owned by the agents they belong
> too. putting them in a skills folder weather visible or not makes them hard to find, no.
> is there no mechanisumm for agents to read other files?"

Constraint honored below: co-location with the owning principal; no skills-tree
relocation. The closing question is answered by evidence in §2.6: agents **can** read
sibling files — the defect is purely discovery/registration, not readability.

## 2. Investigation findings

### 2.1 Root cause (confirmed, now source-verified)

`SquadResourceProjection.Append` (`src/KyberWeave.Core/Squad/Rendering/SquadRendererRegistry.cs:500-524`)
projects each resource at its artifact-relative path under the principal's directory. For
OpenCode and Kilo the agent principal is a flat `.md` inside the harness's agent-discovery
directory (`.opencode/agents/`, `.kilo/agents/`), so `architect`'s closure lands at
`.opencode/agents/architect/references/*.md`. Both harnesses scan those directories with a
recursive `**/*.md` glob and derive an agent's name from the matched file's relative path
(§2.6), so every reference file registers as a phantom agent. Unlike ZCode (ADR 0021) and
Copilot (global scope), these two targets have no exclusion mechanism in the renderer.

### 2.2 Code evidence

- `OpenCodeRenderer.cs:56-57` — `AgentsDirectory = ".opencode/agents"`,
  `SkillsDirectory = ".opencode/skills"`; `:173-185` agent loop calls
  `SquadResourceProjection.Append(files, principal, agent.Resources)`; `:235-239` flat
  agent principal `{agentsDir}/{agent.Name}.md`; `:117-132` global scope strips only the
  `.opencode/` wrapper.
- `KiloRenderer.cs:34-35` — same shape for `.kilo/agents` / `.kilo/skills`; `:99-116`
  agent loop with verbatim `Append`; `:160-164` flat principal; `:40-55` global-scope
  prefix stripping.
- `ZCodeRenderer.cs:154-172` (class remarks) and `:544-605` (`RewriteResourceLinks`) —
  the prior pattern (relocation to `.zcode/skills/<owner>/…` + link rewrite): still the
  template for *degradation-record* and *closure-driven-rewrite* mechanics, no longer the
  template for destination layout.
- `SquadResourceLinkRewriter.cs:24-99` — shared closure-driven rewriter (ADR 0021),
  already used by ZCode and Copilot-global; preserves `#fragment`/`?query`, titles,
  angle-form destinations; leaves fenced/inline code untouched. The target-specific part
  is only the `mapPath` function — an extension swap slots in directly.
- `SquadSourceLoader.cs:702-718,1441,1465` — resource closure resolution (unchanged; the
  in-closure alias validation keeps operating on authored paths).
- `products/kyber-squad/agents/architect.md` — authored links of the form
  `architect/references/<file>.md` (relative to the principal's directory); same for
  `conductor` and `task-reviewer`.
- Tests asserting the misrouting today: `SquadRenderingContractTests.cs:355-384` (shared
  `AssertNativeProjectionAsync`, expects agent resources at
  `{agentsDirectory}/{AgentName}/assets|references/…` with `.md` extensions),
  `OpenCodeRendererContractTests.cs:726-734`, `KiloRendererContractTests.cs:343-351`. No
  test fails today — the suite pins the defect.

### 2.3 Harness documentation (self-gathered, fetched 2026-10-19)

The nested-subagent delegate was unavailable in this session (depth limit), so external
verification was self-gathered:

- **OpenCode agents** — <https://opencode.ai/docs/agents/>: "Place them in: Global:
  `~/.config/opencode/agents/`, Per-project: `.opencode/agents/` … The markdown file name
  becomes the agent name." Documents a `disable: true` option ("Set to `true` to disable
  the agent"). (Page last updated 2026-10-19.)
- **OpenCode skills** — <https://opencode.ai/docs/skills/>: discovery loads only
  `skills/<name>/SKILL.md`, one level deep; a directory without a `SKILL.md` matches
  nothing.
- **Kilo agents** — <https://docs.kilocode.ai/docs/customize/custom-subagents>: "Place
  them in: Global: `~/.config/kilo/agents/`, Project-specific: `.kilo/agents/`. The
  **filename** (without `.md`) becomes the agent name."
- **Kilo skills** — <https://docs.kilocode.ai/docs/customize/skills>: a skill is "a folder
  containing a `SKILL.md`"; the format explicitly documents optional bundled resources
  (`scripts/`, `references/`, `assets/`) inside a skill folder; `SKILL.md` must sit
  "directly inside the skill directory … not nested further".

### 2.4 Harness source verification (fetched 2026-10-19) — the decisive facts

**OpenCode v2** — [`anomalyco/opencode` `dev` branch](https://github.com/anomalyco/opencode),
`packages/core/src/config/plugin/agent.ts`:

```ts
const legacySources = [
  { pattern: "{agent,agents}/**/*.md", primary: false },
  { pattern: "{mode,modes}/*.md", primary: true },
] as const
```

- `discover()` scans each config root with `fs.scan(source.pattern, { dot: true, symlink: true })`
  — **recursive into subdirectories, hidden files included, `.md` files only**.
- `decode()` derives the agent name from the file's path relative to the config root:
  `path.relative(...).replace(/^(agent|agents|mode|modes)\//, "").replace(/\.md$/, "")` —
  a nested `architect/references/intake-assessment.md` becomes an agent named
  `architect/references/intake-assessment` (`Agent.Name` in
  `packages/schema/src/agent.ts` is an unrestricted `Schema.String`, so the slashes are
  legal — this is the phantom the human sees).
- `packages/schema/src/config/agent.ts` defines a native `disabled: Schema.Boolean`, and
  the plugin's registry loop hard-removes such entries: `if (item.disabled) {
  editor.remove(agentID); continue }`.

**Kilo (and the OpenCode v1 line)** — [`Kilo-Org/kilocode` `main`](https://github.com/Kilo-Org/kilocode)
carries the OpenCode v1 engine at `packages/opencode` (root `.opencode-version` pins
**v1.18.26**). `packages/opencode/src/config/agent.ts`:

```ts
for (const item of await Glob.scan("{agent,agents}/**/*.md", {
  cwd: dir, absolute: true, dot: true, symlink: true,
}))
```

- The glob line carries no `kilocode_change` marker: it is inherited verbatim from
  OpenCode v1.18.26, so **both the v1 line and Kilo recurse exactly like v2** — the
  recursion is not a v2-only behavior.
- The entry name is derived from the relative path (`configEntryNameFromPath`), and
  `packages/opencode/src/agent/agent.ts` hard-removes disabled entries from the registry:
  `if (value.disable) { delete agents[key]; continue }` (v1 key `disable`).

**Consequences for the design space** (answers to the conductor's five questions):

1. **Recursion: yes** on OpenCode v1 line, v2, and Kilo — source-verified above. The
   premise of the bug holds; nested references are NOT safe as plain `.md`.
2. **Frontmatter opt-out: exists** — `disable: true` (v1/Kilo) / `disabled: true` (v2)
   removes the entry from the agent registry. Viable, but it requires injecting
   target- and version-specific frontmatter into every deployed reference file
   (deployed-content mutation, key drift across harness lines; an unknown key can trip
   schema warnings on the other line).
3. **Filename convention: yes — the extension.** Both globs match **only `*.md`**;
   dot-prefixing does not escape (`dot: true`). A `.markdown` extension is invisible to
   discovery on both harnesses with zero content mutation.
4. **Readability (the human's closing question):** unaffected. Agents exercise the `read`
   tool at predictable relative paths beside their principal; nothing in either runtime
   restricts reads inside the agents tree (Kilo's runtime even auto-whitelists its config
   directories for `external_directory`). The defect is misregistration, not
   reachability — there is no need for a separate "agents read other files" mechanism.

### 2.5 Governed documentation state

- `docs/kyber-squad/architecture.md:428-457` — the "Resource projection" bullet documents
  verbatim projection with exactly two exceptions (ZCode, Copilot-global) and states the
  rewriter "only ZCode and Copilot (global) use today". Both statements become false with
  this fix and must be updated in the same change.
- `docs/kyber-squad/architecture.md:400-401` — projection table rows for `opencode` and
  `kilo` (paths unchanged by this fix).
- `docs/adr/0021-zcode-command-lowering-and-resource-relocation.md` — the precedent this
  fix follows in *mechanism* (closure-driven rewrite + recorded degradation) while
  departing from in *destination* (co-location retained). Highest existing ADR number is
  0036; the new record is 0037.

### 2.6 Discovery report

- **CodeGraph: 4** (first session). Registry/projection + renderer shapes; rewriter body;
  blast radius (Append has 12 renderer call sites; OpenCode/Kilo renderers covered by
  three test files each).
- **Docs MCP: 1, unusable.** `docs_status` reported `root=/Users/dave/git/orchestrator/kyber-weave`
  — a different checkout than this working tree — so per root `AGENTS.md` the
  `kyber-weave` docs tools were abandoned and the documentation index was read directly
  (stated fallback).
- **Direct reads of indexed code: 6** (first session; targeted ranges). Reason: two
  CodeGraph queries for the named renderer/test files returned over-matching siblings;
  the reads are the narrow fallback.
- **Self-gathered external lookups:** revision-1 — 6 successful doc fetches (§2.3);
  revision-2 — ~14 fetches across `anomalyco/opencode@dev` and `Kilo-Org/kilocode@main`
  (repo/package trees via the GitHub APIs, plus the five loader/schema files quoted in
  §2.4); 1 grep.app rate-limit, 2 web-search failures (retried once, then replaced by
  direct source fetches).

## 3. Decisions

Permanent decision ledger. Unapproved rows carry `RECOMMENDED`; no recommendation becomes
the approved answer until the conductor returns an explicit decision.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Destination layout for agent-owned reference files on `opencode` and `kilo` — **revised after human pushback (§1.4)** to: how do we keep resources co-located beside their owning agent without phantom registration? | **Approved — co-located extension guarding:** agent-owned resources stay exactly where `SquadResourceProjection` puts them today (beside the principal, under `{agents}/<owner>/…`); any agent-owned `.md` resource deploys with a `.markdown` extension; authored links rewritten `.md` → `.markdown` via `SquadResourceLinkRewriter`; non-`.md` resources project verbatim (invisible to both scanners) | Satisfies the human's co-location/ownership constraint verbatim. Source-verified on both harnesses (§2.4): both agent scanners match only `{agent,agents}/**/*.md` with `dot: true`, so `.markdown` is invisible to discovery while the file stays beside its agent and readable by the `read` tool. No content mutation, no registry participation, no version-key semantics. Alternatives rejected on record: (b) frontmatter `disable`/`disabled: true` injection — exists on both (v1 `disable`, v2 `disabled`, both hard-remove from the registry) but mutates every deployed reference with target/version-specific frontmatter and risks schema-warning noise on the wrong line; (c) ZCode-style skills-tree relocation — the revision-1 recommendation, rejected by the human 2026-10-19 ("putting them in a skills folder … makes them hard to find"); (d) status quo — the reported defect | ANSWERED — human decision 2026-10-19 via conductor; matches the recommendation; (b) and (c) rejected on record; **approved for execution ("Approve and execute", 2026-10-19)** |
| D2 | Scope: only `opencode`+`kilo`, or also the other flat-file agent targets (Claude, Cursor, Copilot, Codex, Pi, Factory, Devin, Antigravity)? | **Approved — `opencode`+`kilo` only, and no `docs/todo` seed** (the human chose the strict-scope option, departing from recommendation (a) of fix-now-plus-todo) | Considered trade-off: per-target misdiscovery is unverified and the right mechanism is source-dependent (extension guard, frontmatter opt-out, relocation — §2.4 inventory), which argued for a deferred-work seed. The human's strict choice means **no deferred-work record is created**; ADR 0037 carries the property-based rule and the three verified mechanisms so any future target can apply it without a seed. Nothing in this plan creates or references a todo artifact | ANSWERED — human decision 2026-10-19 via conductor; departs from recommendation (a); recorded as the governing scope; **approved for execution ("Approve and execute", 2026-10-19)** |
| D3 | ADR requirements | **Approved — new ADR 0037**, following the ADR 0021 shape: *a target whose agent discovery registers nested markdown under the agents directory must deploy agent-owned markdown resources in a form that scanner ignores, keeping them co-located with their principal; the deviation is recorded as a `resource-links-rewritten` degradation*. Covers opencode+kilo, quoting the verified scanner globs (`"{agent,agents}/**/*.md"` with `dot: true`, on both the v1/Kilo line and opencode v2, §2.4) and the rejected alternatives (frontmatter `disable`/`disabled` injection; skills-tree relocation); states the rule for future targets without creating a deferred-work seed (D2) | Precedent cuts both ways: the 2026-10-10 Copilot-global change took an ADR waiver as a "narrow, target-specific refinement" of a two-exception rule. This change is not narrow — it adds a property-based rule binding every future renderer, revises the "authored links resolve verbatim" rule for a third and fourth target, and is the class's third recurrence (escalation tripwire). Amending ADR 0021 in place was rejected: its Context is a ZCode-source verification record and its decision is specifically about relocation; a waiver under-states a rule change of this reach | ANSWERED — human decision 2026-10-19 via conductor; matches the recommendation; **approved for execution ("Approve and execute", 2026-10-19)** |
| D4 | Link rewriting and degradation, including the impossible cases | **Approved — closure-driven `.md` → `.markdown` rewrite**, reusing ZCode semantics with a different mapping: rewrite only a Markdown link target that exactly equals a declared resource's relative path (closure-driven; `#fragment`/`?query`, titles, angle-form preserved; fenced/inline code untouched), mapping an in-closure `.md` path to the same path with a `.markdown` extension and passing any non-`.md` in-closure path (e.g. `.txt` assets) through unchanged; record one `resource-links-rewritten` degradation per resource-owning agent (Target `opencode`/`kilo`, identities = agent name, digest = `BodyDigest`, details quoting the harness glob and the extension guard, with the "no authored link required rewriting" variant). **Fail closed** with `SquadRenderValidationException` in the one newly-impossible case: an agent whose closure contains both `x.md` and `x.markdown` (the extension map would alias them). Skill-owned resources keep verbatim beside-principal projection (both skill scanners are `*/SKILL.md`, one level) | Forced by the shared rewriter contract and ADR 0021 precedent (recorded, closure-driven deviation). The revision-1 fail-closed case (canonical-skill directory collision) disappears with relocation. The `.md`+`.markdown` alias case is new and is the only way the extension map can make two authored paths collide; repo precedent is fail-closed because a merge would overwrite one principal's file. Links outside the declared closure are untouched by design; an authored `.markdown` link is already correct and its file projects verbatim | ANSWERED — human decision 2026-10-19 via conductor; matches the recommendation; **approved for execution ("Approve and execute", 2026-10-19)** |
| D5 | Development mode | `test-first` | Default; no opt-out supplied. The rewritten contract tests are the RED evidence | Set by conductor packet 2026-10-19; **approved for execution ("Approve and execute", 2026-10-19)** |

## 4. Proposed design

### 4.1 Shared extension-guard helper (new)

New internal static class `SquadAgentResourceDiscoveryGuard`
(`src/KyberWeave.Core/Squad/Rendering/SquadAgentResourceDiscoveryGuard.cs`, mirroring
`SquadResourceLinkRewriter`'s file shape), consumed by `OpenCodeRenderer` and
`KiloRenderer`:

- `MapResourcePath(relativePath)` — ordinal `.md` suffix → `.markdown`; anything else
  verbatim. Used both for the projected file path and, indirectly, for link rewriting.
- `RewriteBody(agent)` — no-op when `agent.Resources.Count == 0`; otherwise
  `SquadResourceLinkRewriter.Rewrite(agent.InstructionBody, closure, pathPart =>
  MapResourcePath(pathPart), out _)`. In-closure non-`.md` paths pass through unchanged,
  so asset links are untouched.
- `Append(files, principal, resources, degradations, targetToken, scannerQuote)` —
  fails closed when the closure contains both `x.md` and `x.markdown` for any path
  (message mirroring ZCodeRenderer.cs:569-575 in style); appends each resource at
  `{principalDirectory}/{MapResourcePath(resource.RelativePath)}` (same directory as
  today — only the extension changes); appends the `resource-links-rewritten` degradation
  whose details quote the harness scanner pattern (e.g. `"{agent,agents}/**/*.md"`).
- ZCode and Copilot keep their existing private implementations (different geometry:
  relocation / absolute-path rewrite). Converging them is explicitly out of scope (§8).

### 4.2 Renderer integration (both renderers, identical shape)

In `RenderAsync`'s agent loop: pass the guard's rewritten body into `RenderAgent`
(Copilot's `RewriteResourceLinks`-inside-`RenderAgent` shape, CopilotRenderer.cs:297-299)
so the composed agent file carries `.markdown` links while its frontmatter is unchanged;
replace the agent-loop `SquadResourceProjection.Append` with the guard's `Append`. The
skill loop's `SquadResourceProjection.Append` stays verbatim (skill scanners are one
level deep in both harnesses and Kilo's skill format explicitly blesses bundled
resources). Degradation record order is deterministic and pinned by the rewritten tests.
Both scopes behave identically: the guard touches only the resource's own filename, and
`ResolvePrefixedDirectory` affects only the root prefix.

### 4.3 Documentation changes

- New `docs/adr/0037-flat-agent-dir-discovery-guarded-resources.md` (title working) —
  Status / Context (the human's constraint and quote; the source-verified scanner facts
  from §2.4 with repo paths, branches and fetch dates; the symptom-class history) /
  Decision (co-location retained; `.md` → `.markdown` extension guard on targets whose
  agent discovery registers nested markdown; closure-driven link rewrite; recorded
  degradation; fail-closed on the `x.md`+`x.markdown` alias; skill resources verbatim;
  the rejected alternatives: frontmatter `disable`/`disabled` injection and skills-tree
  relocation) / Consequences (deployed reference files carry a `.markdown` extension on
  these two targets; agents directories now register only principals; a harness that
  widens its glob beyond `*.md` would resurface the phantom — re-check the quoted globs;
  the other flat-file targets are out of scope by human decision (D2), and the rule is
  stated for any future target whose scanner is verified to match nested markdown)
  / Related (ADR 0021, architecture §8, this plan).
- `docs/adr/README.md` — index row for 0037.
- `docs/kyber-squad/architecture.md:428-457` — rewrite the exceptions list into the
  property-based rule (ZCode relocation per ADR 0021; OpenCode and Kilo co-located
  extension guard per ADR 0037; Copilot global keeps its absolute-path form) and correct
  "only ZCode and Copilot (global) use today" to name the rewriter's four users plus the
  new guard helper.
- Renderer class remarks (`OpenCodeRenderer.cs`, `KiloRenderer.cs`) — add a paragraph in
  the ZCodeRenderer.cs:154-172 style, citing the §2.4 source locations verbatim (repo,
  path, glob) and dating the verification 2026-10-19.
- **No todo is seeded** (D2 human decision): the other flat-file targets (Claude, Cursor,
  Copilot project scope, Codex, Pi, Factory, Devin, Antigravity) are out of scope with no
  deferred-work record; ADR 0037's rule and the §2.4 mechanism inventory (extension
  guard, frontmatter opt-out, relocation) carry the knowledge for any future target.

## 5. Test contract (test-first)

1. **RED** — rewrite and extend the contract tests before any production change:
   - `SquadRenderingContractTests`: add `AssertDiscoveryGuardedProjectionAsync(renderer,
     target, agentPrincipalPath, agentsDirectory, skillsDirectory)` beside the existing
     helpers: agent principal path and content shape unchanged; agent `.md` resource at
     `{agentsDirectory}/{AgentName}/references/runbook.markdown` with **byte-identical
     content to the authored file** (no frontmatter injected); agent non-`.md` resource
     verbatim at `{agentsDirectory}/{AgentName}/assets/agent-payload.txt`; agent body's
     authored link rewritten to the `.markdown` path (byte-asserted); skill resources
     unchanged beside the skill principal; **no** emitted path under `agentsDirectory`
     other than the principals ends in `.md`; exactly one `resource-links-rewritten`
     degradation for the agent with the right Target, identities, and `BodyDigest`;
     render-twice determinism. Existing `AssertNativeProjectionAsync` stays for the
     targets that keep verbatim projection.
   - `OpenCodeRendererContractTests` / `KiloRendererContractTests`: switch
     `RenderAsync_*_ProjectsLinkedAgentAndSkillResourcesDeterministically` to the new
     helper; add `RenderAsync_*_ExtensionGuardFailsClosedOnMdAndMarkdownAlias` (fixture:
     an agent owning both `references/x.md` and `references/x.markdown`) asserting
     `SquadRenderValidationException`; add a case asserting an agent whose only resources
     are non-`.md` projects byte-identically to today (guard is a no-op for it).
   - Gate: `dotnet test tests/KyberWeave.Tests -c Release --filter
     "FullyQualifiedName~OpenCodeRendererContractTests|FullyQualifiedName~KiloRendererContractTests|FullyQualifiedName~SquadRenderingContract"`
     — the new/changed tests fail against the current renderers; unchanged tests in the
     filter stay green. Failure output recorded as RED evidence.
2. **GREEN** — after T2/T3 the same filter passes; T4 runs the full suite
   (`dotnet test tests/KyberWeave.Tests -c Release`) plus
   `dotnet format KyberWeave.sln whitespace --verify-no-changes` and the style check, with
   `TreatWarningsAsErrors` + `AnalysisMode=all` clean (any warning fails the build).
3. **Docs gates** — after T5: `docs validate .` zero findings (not `--merge-ready` while
   this plan is active; the finishing PR archives it and runs `--merge-ready` per
   KW-DOC-LIFECYCLE-003) and `docs drift .` zero findings against this checkout's
   CodeGraph index.
4. **Live owner gate (post-merge)** — `squad install`/`update` for `opencode` and `kilo`
   in a consumer repository; confirm the agent pickers list no reference files, the files
   sit beside their agents with the `.markdown` extension, and the rewritten links
   resolve when read. Renderer tests do not establish picker runtime behavior.

## 6. Tasks

| Task | Objective | Files / symbols | Acceptance criteria | Depends on | Skills |
|---|---|---|---|---|---|
| T1 | Author the RED contract tests | `tests/KyberWeave.Tests/SquadRenderingContractTests.cs` (new helper + alias-collision fixture support), `OpenCodeRendererContractTests.cs`, `KiloRendererContractTests.cs` | §5.1 RED gate met and evidence recorded; no production file touched | — | `test-dev` |
| T2 | Extension-guard helper + OpenCode integration | new `src/KyberWeave.Core/Squad/Rendering/SquadAgentResourceDiscoveryGuard.cs`; `OpenCodeRenderer.cs` (loop, `RenderAgent`, remarks) | OpenCode tests in the §5.1 filter green; helper unit-clean under analyzers; remarks cite §2.4 sources with fetch date | T1 | `csharp-dev` |
| T3 | Kilo integration | `KiloRenderer.cs` (loop, `RenderAgent`, remarks) | Kilo tests in the §5.1 filter green; whole filter green | T2 | `csharp-dev` |
| T4 | Sweep remaining pinned assertions + full gates | any test pinning `.opencode/agents/<name>/…` or `.kilo/agents/<name>/…` resource literals (bounded search: `grep -rn "agents/.*/references/" tests/KyberWeave.Tests`, plus `SquadRootWrapperAgentTests`, `PullRequestSkillConsolidationTests`, squad filesystem/update contract tests) | Full suite green; whitespace + style format checks clean; install/update receipt contract still green (extension change alters owned-path lists; receipt recovery must replace old `.md` paths) | T3 | `test-dev` |
| T5 | Governed documentation | `docs/adr/0037-*.md`, `docs/adr/README.md`, `docs/kyber-squad/architecture.md` | §5.3 docs gates zero findings; ADR follows 0021 shape and quotes the §2.4 globs; architecture bullet states the generalized rule accurately; **no todo is created** (D2) | T4 | `docs-dev` |
| T6 | Review + closeout | whole change | `code-review` council over the accumulated change with findings fixed; completion summary with closeout mapping (D1/D3/D4 → ADR 0037 + architecture.md; D2 → todo; this plan archived by the finishing PR) | T5 | `code-review`, `docs-dev` |

## 7. Dependency graph and concurrency

```text
T1 → T2 → T3 → T4 → T5 → T6
```

Strictly sequential: T2/T3 share the new helper file and the renderers' shared test
filter; T4 needs both renderers final; T5 documents the shipped behavior; T6 reviews the
whole change. **MAX_CONCURRENCY: 1.**

## 8. Risks, boundaries, out of scope

- **Upstream glob drift**: the extension guard depends on both harnesses' scanners
  matching only `*.md` (source-verified 2026-10-19, §2.4). An upstream change to
  `*.markdown`-inclusive globs would resurface the phantom. Mitigations: the ADR names
  the exact globs and files to re-check (ADR 0021 precedent), the degradation record
  surfaces the guard in every receipt, and the live owner gate (§5.4) catches it.
- **Update path on installed squads**: extension-mapped paths change the receipt's
  owned-file list; `squad update`'s transactional receipt recovery must replace the old
  `.md` files. Covered by T4's filesystem/update contract tests; live verification is
  the §5.4 owner gate.
- **Extension surprise**: deployed reference files carry `.markdown` on these two
  targets while canonical source keeps `.md`. Editors, GitHub, and the `read` tool all
  handle `.markdown`; the degradation record and ADR document the intent. If the human
  later prefers frontmatter `disable`/`disabled` injection (content mutation, versioned
  keys), D1's option (b) remains implementable behind the same tests.
- **Global scope**: unchanged behavior from today (relative links from global agents
  were already out of scope of this defect; the Copilot-global plan is the precedent for
  any future absolute-rewrite need). The extension guard itself is scope-invariant.
- **Degradation ordering drift** between renderers — pinned byte-exactly by the
  rewritten determinism tests.
- **Out of scope**: the other eight flat-file targets — out by human decision (D2), with
  **no todo seed**; ADR 0037 records the rule and the mechanism inventory for future
  targets; converging ZCode/Copilot onto the new helper; skill-owned resource projection (correct as-is);
  any change to `SquadResourceProjection`, `SquadSourceLoader`, or the `.apm` skills
  product path; no new dependencies (Markdig/YamlDotNet unchanged).
- **Non-negotiables honored**: warnings are errors; `docs validate`/`docs drift` return to
  zero findings after every docs edit; no ontology widening; rule ids untouched.

## 9. Verification gates summary

| Gate | Command / check | When |
|---|---|---|
| RED | §5.1 filtered test run failing as specified | after T1 |
| GREEN (focused) | same filter passing | after T3 |
| Full suite + format | `dotnet test` Release, `dotnet format whitespace` + `style` `--verify-no-changes` | T4 |
| Docs | `docs validate .` and `docs drift .` zero findings | T5, re-run at T6 |
| Review | council verdict, findings fixed | T6 |
| Owner (post-merge) | live picker + link-resolution check on installed opencode/kilo | after merge |

## 10. Closeout (complete)

The change shipped and this plan is archived by its finishing PR per KW-DOC-LIFECYCLE-003.

Closeout mapping:

- **D1/D3/D4 → [ADR 0037](../adr/0037-flat-agent-dir-discovery-guarded-resources.md)** (new ADR
  stating the property-based co-location rule with the source-verified scanner globs) and the
  **resource-projection bullet** in `docs/kyber-squad/architecture.md` (exceptions list
  generalized to the property-based rule; rewriter-user count corrected).
- **D2 → this plan's `## Decisions` ledger** (human chose strict `opencode`+`kilo` scope and
  declined a deferred-work seed — no harvest target exists by design).
- **D5 → this plan's `§5 Test contract`** (test-first; RED established in T1, GREEN at T3/T4).

Completion summary: all tasks T1–T6 executed. RED contract tests authored (T1), the
`SquadAgentResourceDiscoveryGuard` helper and OpenCode/Kilo renderer integration landed (T2/T3),
stale assertions swept and the full suite passed 2681/2681 (T4), ADR 0037 + architecture.md
updated with `docs validate` and `docs drift` zero findings (T5), and the code-review council ran
over the change (T6). The council's two minor in-scope findings (degradation wording over-claim
for text-only closures; a dead `out` overload) were fixed and pinned with tests before this PR.
Review verdict on the finished change was `NEEDS_HUMAN` solely for tree-level reasons outside this
change (a concurrent intake change on a reserved path and uninstalled dash tooling), not for any
finding on the opencode/kilo work itself. The live owner gate (§5.4) remains a post-merge check:
`squad install`/`update` for opencode and kilo in a consumer repo, confirming the agent pickers
list no reference files.
