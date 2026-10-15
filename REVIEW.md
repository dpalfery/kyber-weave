# Review guidance

Standing guidance for every reviewer of a Kyber-Weave change: `task-reviewer`, the
`code-reviewer` council and its lenses, and automated PR reviewers. It is distilled from the
findings Kilo and CodeRabbit raised across about 100 pull requests in two weeks (30 Sep – 14 Oct
2026) and from the fixes that were accepted. It adds to — never replaces — the governing
standards: [`AGENTS.md`](AGENTS.md), [`docs/standards/`](docs/standards/README.md) and
[`docs/rules/`](docs/rules/README.md).

## How reviewers use this file

- **`task-reviewer`** — read *Ground rules* and the sections for the paths the task touched.
  Report only what you can prove from the diff and tree. Out-of-task concerns are
  `ESCALATION: end-of-run`, not fixes.
- **`code-reviewer` council** — each lens takes the sections that match it
  ([lens map](#lens-map)). The adjudicator discards any finding that does not meet the
  *Ground rules*.
- **Never** cite `scripts/fast-loop.sh` as evidence; only the declared gate suite counts.

## Ground rules

1. **Every finding names a concrete failure**: the input or state, the code path, the wrong
   result. "Could be a problem" is not a finding.
2. **Quote the code and verify it exists at that line** before reporting.
3. **Review the diff, not the neighbourhood.** Pre-existing issues are reported only when the
   change makes them worse or relies on them.
4. **Don't restate what a gate already enforces** (format, analyzers, lint, typecheck). A
   mechanical finding means the author's gate did not run — say that once.
5. **Don't ask for scope creep.** Unrelated refactors, renames, and new features are
   out of bounds for a review. Equally, flag unrelated edits *in* the diff (see
   [Scope discipline](#scope-discipline)).
6. **Style and wording nits are Trivial** and never block. Comment typos, capitalisation, and
   alignment belong in a single batched note, not one comment each.
7. **Severity follows consequence**: data loss, wrong figures shown to the user, silent failure,
   or a crash path → Major. Wrong only in an edge case with a visible failure → Minor.
   Cosmetic → Trivial.

## Recurring defect classes

These are the findings that recurred. Check for each one whenever the diff touches the pattern.

### 1. Absent is not zero (the single most frequent class)

Governing rule: [honest unobservability](docs/rules/honest-unobservability.md).

- A missing, unmeasured, or unparseable value must stay distinguishable from a measured `0`.
  Look for `?? 0`, `|| 0`, `Number(x)`, `Math.max` over empty sets, and ternaries that collapse
  `null`/`undefined`/`0` into one branch.
- An empty filter or empty array that means "match nothing" in one place and "match everything"
  in another is a defect. State the meaning once and test both.
- Aggregates over partial data (one priced session among unpriced ones) must say they are
  partial, not present a clean total.
- A harness or provider with no telemetry must never rank as efficient by absence of evidence.

### 2. Untrusted numbers and shapes

- `typeof x === 'number'` accepts `NaN` and `Infinity`; require `Number.isFinite`. Also
  guard numeric DB columns (`NaN` from a non-numeric value) and divisions.
- Wire and transcript data (`JSON.parse`, API casts, transcript fields such as `input`,
  `arguments`) is `unknown` until validated. A TypeScript cast is not validation. Reject
  `42`, `[]`, `true`, and `null` where an object is expected.
- A flat fallback must not override a more specific field that is present (precedence order
  stated and tested).
- In .NET, a deserialised value (`as T`, `JsonSerializer` result) must be null-checked and
  shape-checked before use; `"maybe"` is not a valid enum-like string.

### 3. Failure handling that hides the failure

- **No empty or blanket `catch`.** Catch the specific errors you can act on and let the rest
  propagate to a visible diagnostic. The accepted split for filesystem probes: `ENOENT` /
  `ENOTDIR` mean *absent*; `EPERM` / `EACCES` / `EBUSY` / `IOException` / `UnauthorizedAccess`
  mean *present but unusable* and must surface, not be treated as empty.
- Promises are awaited or have a `.catch`. `void promise` on a clipboard, close, or flush call
  produces unhandled rejections. Fire-and-forget needs an explicit error sink.
- A failed precondition must not report success: an invalid request returning `noop`, exit
  code 2 reported as a clean run, or an error surfaced to the user as "no data".
- Error messages carry the underlying cause. Check `Buffer` vs string stderr, doubled
  messages, and advice that cannot fix the actual error (e.g. "check your settings" for a
  malformed body).
- Reads that can fail on a locked or missing store (`getMetadata`, `prepare().get()`) are
  inside the `try`, including setup calls such as `PRAGMA`.
- Ordering that matters for crash recovery (receipt then ledger, marker last) is stated in a
  comment, and sibling code paths follow the same order or explain why not.

### 4. Unbounded work and unbounded growth

- Any query without `LIMIT`/paging that is materialised and JSON-parsed in full; any per-row
  query inside a loop over all runs/sessions; any structure rebuilt per request.
- UI work per render or per keystroke: `JSON.stringify` of large payloads, full-tree scans,
  spreading thousands of items into a constructor (`new X(...ranges)`), O(n²) membership
  checks. Large values stringify lazily (on click) and behind a size cap.
- Tables that are only ever appended to (`ingest_log`, caches, quarantine, problem rows) need
  retention, an index for their read path, or a stated reason they do not.
- Dedup or lookup keys must not embed the payload (megabytes of prompt in a `Map` key); hash
  or reference it.
- Time-window clustering must say whether it is chaining (single-linkage) or bounded; chaining
  silently merges distinct events across a long sequence.

### 5. Identity, dedup, and merge correctness

Most of the Major CodeRabbit findings were here.

- Merge only on positive identity. If both sides carry a native ID and they differ, they are
  different records. Counter-only or time-proximity fusion applies only when IDs are absent.
- Positional matching must not override explicit identity; do two-pass lookup (identity
  first, position as fallback) and share the lookup between callers (drawer and detail view).
- Dedup passes are restricted to the sources that can actually produce twins; `isFileSource`
  is not evidence of a twin collector.
- When merging counters, compare *every* dimension (including `reasoning`), not a subset.
- Substring matching on identifiers (`codex-desktop` matching `desktop`) → explicit
  equality or an explicit set.
- 0-based vs 1-based endpoints, and pagination `page` derived from the *applied* offset and
  limit, not the requested ones.
- A normalisation function exists once. A second copy of a predicate (`normalizeHarnessName`,
  empty-vs-measured, token-total) is a finding; map keys built with it must be looked up with
  it everywhere.

### 6. Persistence and filesystem

- SQLite copy/open fallbacks: a failed cache copy must not drop committed WAL rows; check the
  fingerprint/checkpoint before opening a source immutable.
- Migrations are idempotent and guarded: check the column exists before `ALTER TABLE`, and
  note what a re-run does.
- `~` is expanded before `path.join`; env-configured directories are normalised.
- Zero-record checkpoints are not reused as proof that records were committed.
- Case folding can change string length (`İ`); never reuse offsets from a lower-cased copy on
  the original string.

### 7. Cross-platform shell and paths

The repo runs on macOS (BSD userland), Linux (GNU), and Windows.

- `mktemp -t` / `sed -i` / `date` / `stat` flags differ between BSD and GNU. Scripts use
  portable forms or branch explicitly, and clean up temp files on every exit path.
- Tests relying on POSIX permissions, symlinks, or path separators are skipped or isolated on
  Windows and the CI job must say so; a CI job that runs a subset must match the comment
  that describes it.
- Temp-root helpers: PIDs get recycled, so a stale directory can be reused dirty. Create
  unique directories and fail per test, not at module initialisation.
- No host-specific absolute paths (`/Users/<name>/…`) in committed plans, docs, or scripts.

### 8. React and TypeScript UI (KyberDash)

Governing rules: [display layer](docs/rules/kyberdash-display-layer.md),
[composite efficiency ban](docs/rules/composite-efficiency-ban.md),
[secondary cost display](docs/rules/secondary-cost-display.md).

- Effect and memo dependency arrays include every value read, including `text`/content
  identities; React Query `queryKey` includes every parameter that changes the payload
  (`full`, offset, limit) or a stale truncated result is served.
- Keys are stable, unique ids, never server-controlled strings or `problem-${index}`
  synthesised ids that the server cannot resolve.
- Several instances of one component must not share global registrations (highlight names,
  `id`s, listeners). Cleanup of one must not clear another.
- Hidden content (collapsed `<details>`) is excluded from counters and scroll targets, or
  opened first.
- Live results (match counters, loading, errors) are announced (`aria-live`); errors are
  separated from "not found" (404 → not found; anything else → load error).
- An error boundary or fallback exists for payload-driven rendering; a malformed row must
  not take the view down.
- Re-sorting or re-keying UI that reorders under the user's hands on every incoming event.

### 9. .NET / C# renderers and tool catalogs

Governing standards: [`docs/standards/csharp`](docs/standards/csharp) and the
`code-review` skill's C# reference.

- Renderers must escape or quote values that can carry YAML/Markdown/JSON metacharacters
  (model ids with spaces or suffixes, `[reasoningEffort=…]`, wildcards `server/*`) and be
  tested for the *negative* ("coarse wildcard is **not** emitted in the granular render").
- The tool catalog is an ordered, pinned list: changing it requires updating the exact-order
  test deliberately, and each tool's required capability (`filesystem.read`,
  `process.execute`) must be consistent with sibling tools doing the same thing.
- Link/Markdown rewriting handles titles and balanced parentheses (CommonMark), not just
  bare `[x](path)`.
- Block-header appenders put a blank line before untrusted prompt text.
- Digest/hash comparisons compare like with like (SHA-256 hex of the body vs the body).
- Test-only static flags flipped in a `ModuleInitializer` leave the production branch
  untested; there must be at least one test that exercises the real branch.

### 10. Tests must prove what they claim

- The assertion matches the test name and the PR description. A test that cannot fail for
  the defect it names is a finding.
- Regression fixes ship a test that fails without the fix.
- Test helpers named for a shape (`McpWildcardTrio`) match their contents.
- Don't delete a guard assertion while the behaviour it guarded is still live.
- Inputs used by tests should be derived from the contract, not copied from the
  implementation's constants.

### 11. Docs, plans, and agent files

- Documents agree with code *and with each other*: a status of `complete` with open
  sections, or an archived spec still linked from a live README row, is a finding. Check
  that every relative link resolves after a move or archive
  ([relocation over deletion](docs/rules/relocation-over-deletion.md)).
- Doc comments describe what the code does, not what the author hoped: "keys **or** paths"
  when only paths are returned is wrong.
- A comment documenting an ordering or crash-window rationale on one path needs its sibling
  paths to carry it or reference it.
- Agent and prompt files: guidance must not contradict itself (a "cap" bullet next to
  "no cap"), must name which tool/flag it means, and must not instruct a non-interactive
  agent to run an interactive command or confirm an unspecified thing.
- Archived or completed plans are not edited except to correct them; open gates (e.g. a
  live-store acceptance owner gate) are recorded as open, not omitted.

### Scope discipline

- A change that touches `AGENTS.md`, `settings.json`, hooks, CI workflows, or generated
  agent files must be the point of the PR. Incidental rewrites (re-wrapping bullets, moving a
  block outside its markers, dropping a flag from a command the PR does not own) are findings.
- Mixed concerns in one diff → `intent-alignment` finding; ask for a split.

## What not to flag

- Anything `dotnet format`, ReSharper, `tsc`, ESLint, `clippy`, or `docs validate` already
  enforces.
- Joke-grade or stylistic prose in comments, naming taste, or "I would have written this
  differently" with no failure scenario.
- Suggestions to add features, abstractions, or configurability the task did not ask for.
- Pre-existing issues the change does not touch.
- Re-raising a finding the author already answered with a justified reason, unless the reason
  is demonstrably wrong.

## Lens map

| Lens | Take these sections |
|---|---|
| correctness | 1, 2, 3, 5, 6 |
| performance / scalability | 4 |
| security, authz-tenancy | 2, 3 (error disclosure), 6 (paths) |
| test-adequacy | 10 |
| intent-alignment | Scope discipline, 11 |
| docs / agent-instruction | 11 |
| react / UI | 8 |
| csharp / renderers | 9 |
| ci / scripts | 7 |

`task-reviewer` applies sections 1–3 and 10 to the task's own files and the section matching
its paths; everything else is council-owned.

## Finding format

```text
SEVERITY  Major | Minor | Trivial
WHERE     <file>:<line> — quoted excerpt
CLASS     <section number and name>
FAILS     <input or state> → <wrong behaviour>
FIX       <exact change>; test: <what proves it>
```

## Maintaining this file

Add a rule here when the same class of finding appears in three or more reviews; delete one
that stops recurring. Link the rule it derives from instead of restating it.
