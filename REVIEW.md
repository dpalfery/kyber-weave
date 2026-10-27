# Review guidance

Standing guidance for every reviewer of a Kyber-Weave change: the
`code-reviewer` council and its lenses, and automated PR reviewers. It is distilled from the
findings Kilo and CodeRabbit raised across about 100 pull requests from 30 Sep to 14 Oct 2026, and from the 125 Kilo inline comments on PRs #407-#439 (mid to late October 2026), with the fixes that were accepted. It adds to — never replaces — the governing standards: [`AGENTS.md`](AGENTS.md), [`docs/standards/`](docs/standards/README.md) and [`docs/rules/`](docs/rules/README.md).

## How reviewers use this file

- **`code-reviewer` council** — each lens takes the sections that match it
  ([lens map](#lens-map)). The adjudicator discards any finding that does not meet the
  *Ground rules*.
- Each review-lens seat returns `guidance: [<section ids>]` on every finding, for example
  `guidance: [R8, §1]`.
- Rules tagged with an id (`R2`–`R16`) are cited by that id. Untagged bullets are cited by
  section number (`§N`). Never invent an id for an untagged bullet.
- A `NO FINDINGS` reply names the sections it checked.
- A seat given guidance that returns no attestation is recorded as a council-coverage gap; its
  findings are not dropped.
- *(low evidence)* marks a rule that rests on one PR or a thin body of evidence.
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
6. **Style and wording nits are minor** and never block. Comment typos, capitalisation, and
   alignment belong in a single batched note, not one comment each.
7. **Severity follows consequence**: data loss, wrong figures shown to the user, silent failure,
   or a crash path → critical when it ships silently or corrupts data, otherwise major. Wrong only
   in an edge case with a visible failure → minor. Cosmetic → minor, and never blocks.

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

- **R2** `typeof x === 'number'` accepts `NaN` and `Infinity`; require `Number.isFinite`. Also
  guard numeric DB columns (`NaN` from a non-numeric value) and divisions. Read every number from
  untrusted input through one guard: `Number.isFinite`; `>= 0` for counts; `Number.isSafeInteger`
  for indexes. Never compare an unvalidated value with `>`. (#439, #435, #433, #422, #414)
- **R3** Wire and transcript data (`JSON.parse`, API casts, transcript fields such as `input`,
  `arguments`) is `unknown` until validated. A TypeScript cast is not validation. Reject `42`,
  `[]`, `true`, and `null` where an object is expected. (#433, #425)
- **R3** Validate every level of an untrusted payload: nested objects, field types, script inputs,
  and API responses, including error responses with no data. (#439, #425)
- **R3** Parsers reject invalid input. Decode strictly (`fatal: true`). Forward only allowlisted
  keys. Never invent identity (for example, a fabricated `sessionId`). (#433, #425)
- A flat fallback must not override a more specific field that is present (precedence order
  stated and tested).
- **R4** In .NET, a deserialised value (`as T`, `JsonSerializer` result) must be null-checked and
  shape-checked before use; `"maybe"` is not a valid enum-like string. Don't use `!` on a value
  that can be null at runtime (merged config, deserialised data, receipts, `TryGetValue` results);
  guard it or reject it with a validation error. (#431)

### 3. Failure handling that hides the failure

- **R5** **No empty or blanket `catch`.** Catch the specific errors you can act on and let the
  rest propagate to a visible diagnostic. The accepted split for filesystem probes: `ENOENT` /
  `ENOTDIR` mean *absent*; `EPERM` / `EACCES` / `EBUSY` / `IOException` / `UnauthorizedAccess`
  mean *present but unusable* and must surface, not be treated as empty. Examples to reject: a
  Python `except OSError:` that publishes or skips, and coercing a wrong shape to empty
  (`components = []`), which hides the failure. (#432, #434, #431, #433, #414)
- **R6** Check a "never throws" or "fails closed" contract against every call inside it. Catch
  filters must cover the documented failure set (IO, `UnauthorizedAccess`, Win32, timeout), not
  only the one error seen. Every probe (`lstat`, or `existsSync` then a read) runs inside the
  `try`. (#435, #434, #414, #422)
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
  comment, and sibling code paths follow the same order or explain why not. A comment documenting
  an ordering or crash-window rationale on one path needs its sibling paths to carry it or
  reference it (see also [§11](#11-docs-plans-and-agent-files)).

### 4. Unbounded work and unbounded growth

- Before flagging any unbounded-work finding, name the growth dimension: what grows, and with
  what input (rows per run, sessions per day, requests per second, and so on).
- Any query without `LIMIT`/paging that is materialised and JSON-parsed in full, where the named
  dimension grows without bound.
- Any per-row query inside a loop over all runs/sessions.
- Any structure rebuilt per request, where the named dimension grows with the request count or
  the dataset size.
- UI work per render or per keystroke: `JSON.stringify` of large payloads, full-tree scans,
  spreading thousands of items into a constructor (`new X(...ranges)`), O(n²) membership
  checks. Large values stringify lazily (on click) and behind a size cap.
- Tables that are only ever appended to (`ingest_log`, caches, quarantine, problem rows) need
  retention, an index for their read path, or a stated reason they do not.
- Dedup or lookup keys must not embed the payload (megabytes of prompt in a `Map` key); hash
  or reference it.
- Time-window clustering must say whether it is chaining (single-linkage) or bounded; chaining
  silently merges distinct events across a long sequence.
- **R7** *(low evidence)* Cap the serialized size of host-supplied payloads and every parser
  token. Page through an external list until `hasNextPage` is false, or fail loudly at the page
  limit. (#435, #433, #439)

### 5. Identity, dedup, and merge correctness

Most of the Major CodeRabbit findings were here.

- Merge only on positive identity. If both sides carry a native ID and they differ, they are
  different records. Counter-only or time-proximity fusion applies only when IDs are absent.
- Positional matching must not override explicit identity; do two-pass lookup (identity
  first, position as fallback) and share the lookup between callers (drawer and detail view).
- Dedup passes are restricted to the sources that can actually produce twins; `isFileSource`
  is not evidence of a twin collector.
- When merging counters, compare *every* dimension (including `reasoning`), not a subset.
- **R8** A normalisation function exists once. A second copy of a predicate (`normalizeHarnessName`,
  empty-vs-measured, token-total) is a finding; map keys built with it must be looked up with it
  everywhere. Apply one sanitizer, validator, or normaliser per value on every sibling entry path,
  and derive the same field the same way in every module. (#439, #435, #434, #431)
- **R9** *(low evidence)* Substring or keyword matching is not identity. `codex-desktop` matching
  `desktop` → explicit equality or an explicit set. Likewise `Contains("documentation waiver")`
  and `.Contains(retired)` are not identity checks. (#434)
- 0-based vs 1-based endpoints, and pagination `page` derived from the *applied* offset and
  limit, not the requested ones.
- *(low evidence)* Compare against the same root's receipt, not another root's, when checking
  drift (see the *compare like with like* rule in [§9](#9-net--c-renderers-and-tool-catalogs)).
  (#435)

### 6. Persistence and filesystem

- SQLite copy/open fallbacks: a failed cache copy must not drop committed WAL rows; check the
  fingerprint/checkpoint before opening a source immutable.
- Migrations are idempotent and guarded: check the column exists before `ALTER TABLE`, and
  note what a re-run does.
- `~` is expanded before `path.join`; env-configured directories are normalised.
- Zero-record checkpoints are not reused as proof that records were committed.
- Case folding can change string length (`İ`); never reuse offsets from a lower-cased copy on
  the original string.
- **R10** Contain every path that comes from a receipt, manifest, archive member, or caller:
  resolve the canonical full path, check it against the root prefix, and reject rooted paths
  and `..`. (#435, #434, #432, #431)
- **R10** Refuse symlinks before writing or deleting (`lstat`; no-follow delete). A check-then-write
  that follows a symlink is a finding. (#435, #431)
- **R10** Write atomically through an unpredictable temp file (`wx`, `fsync`, rename), not a
  predictable `<pid>.tmp`. (#435)
- **R10** Tighten permissions on existing files, not only new ones. (#414)

### 7. Cross-platform shell, scripts, CI, and line endings

The repo runs on macOS (BSD userland), Linux (GNU), and Windows.

- `mktemp -t` / `sed -i` / `date` / `stat` flags differ between BSD and GNU. Scripts use
  portable forms or branch explicitly, and clean up temp files on every exit path. **R11** On
  failure, restore state (ownership, stale outputs) and log each retry. (#420, #432)
- **R11** Workflow values that depend on the triggering event (for example `base_ref`, empty on
  `push`) branch on the event and reach the shell through `env`, not interpolation. (#434)
- **R11** A list used in two places lives in one place and is read from there. (#420)
- **R11** *(low evidence)* Frontmatter and line slicing take the delimiter length from the matched
  text (`\r\n` or `\n`), never a fixed offset. (#431)
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

Governing standards: [`docs/standards/csharp`](docs/standards/csharp/README.md) and the
`code-review` skill's C# reference.

- Escaping and interpolation: see [§12](#12-untrusted-values-reaching-an-interpreter-or-a-generated-document) (R13).
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
- **R14** Structural tests assert structure: scope checks to the job or `needs:` block; compare
  sets, not strings or `IndexOf` order; assert a definition, not a mention. Derive expected counts
  from the source. Helpers guard JSON `ValueKind` and `Assert.Fail` with the path. (#432, #422,
  #415, #408)
- **R15** *(low evidence)* On fix PRs, every new guard or parser ships a malformed-input test
  (wrong type, non-finite, negative, CRLF, empty) that fails with the fix reverted. (#439, #414,
  #435)

### 11. Docs, plans, and agent files

- Documents agree with code *and with each other*: a status of `complete` with open
  sections, or an archived spec still linked from a live README row, is a finding. Check
  that every relative link resolves after a move or archive
  ([relocation over deletion](docs/rules/relocation-over-deletion.md)).
- Doc comments describe what the code does, not what the author hoped: "keys **or** paths"
  when only paths are returned is wrong.
- Ordering comments and sibling paths: see [§3](#3-failure-handling-that-hides-the-failure)
  (crash-recovery ordering).
- Agent and prompt files: guidance must not contradict itself (a "cap" bullet next to
  "no cap"), must name which tool/flag it means, and must not instruct a non-interactive
  agent to run an interactive command or confirm an unspecified thing.
- **R16** Agent and skill prompts define every outcome, verdict, key, and rubric they refer to,
  and change together with their sibling contracts. (#424, #434, #429)
- **R16** Counts, status lines, claimed measurements, and links are updated everywhere they
  appear. (#410, #419, #432)
- **R16** *(low evidence)* Code examples in skill references obey the surrounding guidance. (#415)
- Archived or completed plans are not edited except to correct them; open gates (e.g. a
  live-store acceptance owner gate) are recorded as open, not omitted.

### 12. Untrusted values reaching an interpreter or a generated document

- **R13** Renderers must escape or quote values that can carry YAML/Markdown/JSON metacharacters
  (model ids with spaces or suffixes, `[reasoningEffort=…]`, wildcards `server/*`) and be tested
  for the *negative* ("coarse wildcard is **not** emitted in the granular render").
- **R13** Pass values as parameters (GraphQL variables, argv arrays, quoted YAML scalars), never
  by string interpolation. (#431, #439)
- **R13** Use an allowlist, not a denylist, for shell metacharacters. A denylist misses `&`, `|`,
  `<`, `>`. (#431)
- **R13** Match hosts and addresses by parsing, not by spelling. Forms such as `127.0.0.2`,
  `[::1]`, and `2130706433` must all be recognised as loopback. (#435)

### 13. TypeScript for Node (dash engine, capture plugins, MCP, statusline)

- **R12** Keep module state per session or instance, and make every shutdown path clear it. Global
  state such as an active TUI or a live-TUI list leaks across sessions. (#422)
- **R12** Copy an object received from another component before mutating it. Do not `delete` a
  field on a cached object. (#433)
- **R12** For child processes, await only on live state, and attach a stdin error listener so
  `EPIPE` is handled. (#433)
- **R12** Derive a decision from one filesystem read, not two. A second read is a time-of-check
  race. (#414)
- **R12** *(low evidence)* Do not type host handlers as `never[]` or `any[]`. (#422)
- Shared registrations across instances are covered in [§8](#8-react-and-typescript-ui-kyberdash).

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

Each seat gets its lens row, the technology rows that match the diff, and the *Ground rules*.
A refuter gets the same sections as the seat it refutes. The three triage seats
(duplicate-implementation, static-analysis-triage, dependency-supply-chain) get *Ground rules*
and *What not to flag* only.

### By lens

| Lens | Take these sections |
|---|---|
| intent-alignment | Scope discipline, 11 |
| correctness | 1, 2, 3, 5, 6, 7, 13 |
| security | 2, 4, 5, 6, 12 |
| authz-tenancy | 6, 12 |
| di-composition | Ground rules only |
| model-placement | Ground rules only |
| prior-art | 5 |
| duplicate-implementation | Ground rules and *What not to flag* only |
| test-adequacy | 10 |
| static-analysis-triage | Ground rules and *What not to flag* only |
| performance | 4, 13 |
| blast-radius-revertibility | 3, 6, 11 |
| supportability | 3 |
| dependency-supply-chain | Ground rules and *What not to flag* only |
| infra-workflow | 7, 12 |

## Technology map

### By technology

The sections named here are sections of this file. Only the technologies with a file under `references/` also have a standalone checklist; `shell` and `agent-prompts` use these sections alone.

| Technology | Files | Take these sections |
|---|---|---|
| azure | Azure resource definitions | 12 |
| csharp | `*.cs`, `*.csproj` | 2, 3, 5, 6, 7, 9, 10, 12 |
| github-actions | `.github/workflows/*.yml`, `templates/github-actions/*.yml` | 7, 12 |
| pulumi | Pulumi programs | 12 |
| python | `*.py` and embedded Python | 2, 3, 4, 5, 12 |
| react | `*.tsx`, `*.jsx`, React UI `.ts` | 1, 2, 8 |
| sql | `*.sql`, migrations | 4, 6 |
| typescript | Node `*.ts`, `*.mts`, `*.cts`, `*.mjs` outside a React UI | 1, 2, 3, 4, 5, 6, 10, 12, 13 |
| rust | `*.rs`, `Cargo.toml`, `build.rs` outside Tauri | 3, 6 (no Kilo evidence) |
| tauri | `src-tauri/**`, IPC, capabilities | 6, 12 (no Kilo evidence; also load the rust and react rows) |
| go | `*.go`, `go.mod` | 3, 6 (no Kilo evidence) |
| shell | `*.sh`, `*.bash`, `scripts/**` | 3, 6, 7, 12 |
| agent-prompts | `products/**/agents/**/*.md`, `**/SKILL.md`, skill `references/*.md`, `docs/plans/**`, `docs/archive/**` | 11, Scope discipline |

Single tasks are not reviewed by an agent: the developer's deterministic `task-gate` covers the
mechanical checks, and everything judgemental is council-owned.

## Finding format

```text
SEVERITY  critical | major | minor
WHERE     <file>:<line> — quoted excerpt
CLASS     <section number and name>
FAILS     <input or state> → <wrong behaviour>
FIX       <exact change>; test: <what proves it>
GUIDANCE  <section ids>
```

## Maintaining this file

Add a rule here when the same class of finding appears in three or more reviews across at least
two PRs, or when it extends an existing rule. A class below that threshold becomes an example
bullet under the nearest section. Delete one that stops recurring. Link the rule it derives from
instead of restating it.
