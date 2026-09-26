---
id: archive/plans/2026-09-25-mcp-docs-walk-containment
title: Contain documentation-walk enumeration to configured roots and known harness folders
doc-type: plan
status: archived
development-mode: test-first
component: DocGraph
owner: dpalfery
created: 2026-09-25
last-reviewed: 2026-09-26
approved: 2026-09-25
---

# Contain documentation-walk enumeration to configured roots and known harness folders

## Goal

Stop `DocumentLoader.Load()` and `DocumentIndexHost.ComputeDocsStamp()` from descending into a
symbolic link encountered while walking a documentation root — the confirmed code weakness
behind GitHub issue [dpalfery/kyber-weave#124](https://github.com/dpalfery/kyber-weave/issues/124)
— and, per user decision D3, fix the one other component (KyberDash's Codex provider) already
known to probe outside its allowed folders automatically instead of on explicit request. This
plan does not add a new escape-detection diagnostic: an encountered symlink is skipped
silently (Q2), and the walk is designed so it never has to resolve or stat anything outside the
directory it is currently listing to make that decision (see "Containment design", below).

## Context

**Finding source**: GitHub issue #124, reported 2026-09-25, "kyber-weave is asking for access
to folders it does not need access to" (0.1.7-rc13, macOS 27 beta 2).

### What was actually observed (evidence, not inference)

The reporter's screenshot shows a macOS Photos-access dialog naming `kyber-weave-mcp`. System
Settings → Privacy & Security's own TCC log read-out (a second screenshot, described verbatim
by the user) lists rows for `/Users/dave/.local/bin/kyber-weave-mcp`:

| TCC service | Recorded state |
|---|---|
| Photos | none ("No Access" in Privacy & Security; the access dialog itself was shown, naming this binary) |
| DocumentsFolder | none |
| DownloadsFolder | full |
| SystemPolicyAllFiles | none |
| SystemPolicyAppDataDetailed | none — 7 rows, each another app's `~/Library/Containers` data that the process tried to reach and was blocked |
| FileProviderDomain | none ×2, full ×1 |

A TCC row exists only after macOS has recorded an actual access *attempt* against that service
for that binary — it is not a capability manifest and not a prediction. Every row above is
therefore evidence of an attempt, whatever the outcome. The 21-day TCC log retention window has
already elapsed; the specific run and the specific path that triggered each attempt cannot be
recovered from logs. Nothing in this plan claims to know which enumeration call produced which
row — the footprint below is a *closure* argument (the fix removes every enumeration path wide
enough to reach these locations), not a reconstruction of the incident.

### What is CONFIRMED (by reading the current code, byte-identical between `main` and rc.13)

1. `DocumentLoader.Load()` (`src/KyberWeave.Core/Docs/Parsing/DocumentLoader.cs:54-56`) walks
   every configured docs root with:
   ```csharp
   foreach (string file in Directory
                .EnumerateFiles(absoluteRoot, "*.md", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal))
   ```
   No `EnumerationOptions` are supplied, and the result is never checked for containment.
2. `DocumentIndexHost.ComputeDocsStamp()` (`src/KyberWeave.Core/Docs/Search/DocumentIndexHost.cs:139`)
   walks the same roots the same way, and runs on **every** `DocumentIndexHost.Current()` call —
   i.e. on every `docs_explore`, `docs_for_symbol`, `docs_analysis_candidates`, and
   `docs_glossary` MCP call (`DocsTools`, `src/KyberWeave.Mcp/`).
3. Both also read the catalog file directly (`DocumentLoader.cs:66-72`,
   `DocumentIndexHost.cs:149-159`) without any per-file containment check.
4. Verified against Microsoft Learn (below): a directory symbolic link encountered during a
   recursive `Directory.EnumerateFiles(..., SearchOption.AllDirectories)` walk is **always**
   followed on every OS .NET supports; there is no shipped option to opt out
   ([dotnet/runtime#52666](https://github.com/dotnet/runtime/issues/52666), open,
   unresolved). If a docs root ever contained a symlink whose target left the repository —
   into a cloud-synced folder, a protected system location, or another app's data — every one
   of the four call sites above would walk into it.

### What is SUSPECTED, not confirmed

*The specific escape route this incident's process took.* Two candidate mechanisms fit the
confirmed weakness and neither can be confirmed or ruled out without the lost log: (a) an
ordinary-looking symlink *inside* an accepted docs root whose target left it, or (b) the
project/docs root itself sitting inside a folder macOS already treats as protected or
cloud-synced (e.g. under `~/Library/CloudStorage/`), so every ordinary file inside it is
already "outside" in TCC's terms even though nothing in the walk itself misbehaved. Either
would explain the observed footprint; this plan closes (a) unconditionally and treats (b) as
"open by design" per the route-coverage table below, because D3 permits project folders
wherever they live.

**This is a code weakness independent of any one incident.** It is confirmed present in the
current code regardless of which of the two routes above actually fired.

### Correction to the prior draft (Q1)

The prior draft claimed the KyberDash Codex nested-launcher-home timing bug (D1) shared "the
same root cause" as this incident. **That is wrong and is removed.** The Codex bug is
TypeScript, in a different product (`dash/`), reachable only when KyberDash's own process
runs — it cannot be what produced TCC rows against `kyber-weave-mcp`. It is included in this
plan (Q1 = INCLUDE) only because it is the one other confirmed instance of the general
principle D3 states (a component probing outside its allowed folders other than on explicit
request), and the user chose to close both in one release. Task T7/T8 below is unrelated to
T1-T6; there is no shared file, symbol, or fix between them.

## Containment design (verified against Microsoft Learn; not the prior draft's design)

The prior draft's design cannot work, and cited APIs that do not exist or do not do what it
assumed. Verified facts, each against a live Microsoft Learn page:

| Claim in the prior draft | Verified fact | Source |
|---|---|---|
| "No risk: `Path.GetFullPath` resolves symlinks" | `Path.GetFullPath` is lexical only. Its own Remarks describe it as using "the current directory and current volume information to fully qualify `path`"; nothing in the API resolves a reparse point. A symlink's own path string lexically normalizes to wherever the *link* sits (inside the root), not to its target — so a containment check built on `GetFullPath` would never detect an escaping symlink at all. This risk claim is removed outright, not softened. | [Path.GetFullPath](https://learn.microsoft.com/en-us/dotnet/api/system.io.path.getfullpath?view=net-9.0) |
| `EnumerationOptions.FollowSymbolicLinks` (T3 acceptance criteria) | **This property does not exist.** `EnumerationOptions` has no symlink-following switch of any kind; the .NET team has an open, unimplemented proposal for one ([dotnet/runtime#52666](https://github.com/dotnet/runtime/issues/52666)). | [EnumerationOptions Class](https://learn.microsoft.com/en-us/dotnet/api/system.io.enumerationoptions?view=net-9.0) |
| Setting `EnumerationOptions.AttributesToSkip = FileAttributes.ReparsePoint` would stop recursion into a symlinked directory | Default `AttributesToSkip` is `Hidden \| System` (no `ReparsePoint`). Setting `ReparsePoint` filters what is **returned**, but per the runtime team's own comment on #52666, recursive enumeration still **descends into** a symlinked subdirectory regardless of `AttributesToSkip` — "these symlinks are currently invariably followed." There is no combination of `EnumerationOptions` that stops recursion into a linked directory. | [EnumerationOptions.AttributesToSkip](https://learn.microsoft.com/en-us/dotnet/api/system.io.enumerationoptions.attributestoskip?view=net-9.0), [dotnet/runtime#52666](https://github.com/dotnet/runtime/issues/52666) |
| `FileSystemInfo.LinkTarget` / `ResolveLinkTarget` | `LinkTarget` (property): "the target path of the link located in `FullName`, or `null` if the `FileSystemInfo` instance doesn't represent a link" — reading it identifies that an entry **is itself** a link without following it. `ResolveLinkTarget(returnFinalTarget)` (method) instead **follows** the chain — up to 40 hops on Unix / 63 on Windows — and returns a `FileSystemInfo` for the target, which the docs are explicit exists "independently if the target exists or not." Following the chain means `lstat`-ing every intermediate hop, including hops outside any configured root — exactly the class of touch that can trip a sandboxing/TCC prompt. This plan therefore never calls `ResolveLinkTarget` or any realpath-equivalent; it only reads `LinkTarget`/`Attributes` on the entry itself. | [FileSystemInfo.ResolveLinkTarget](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.resolvelinktarget?view=net-9.0), [FileSystemInfo.LinkTarget](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.linktarget?view=net-9.0) |
| (new fact used by the design) `(entry.Attributes & FileAttributes.ReparsePoint) != 0` / `entry.LinkTarget is not null` detect a link from data already returned by the directory listing itself, without opening or accessing the target | Both are documented, verified ways to test whether an enumerated `FileSystemInfo` **is** a link; neither requires resolving it. | [dotnet/runtime#24655](https://github.com/dotnet/runtime/issues/24655) (community-confirmed pattern); [FileSystemInfo.LinkTarget](https://learn.microsoft.com/en-us/dotnet/api/system.io.filesysteminfo.linktarget?view=net-9.0) |

**Design decision (mine to make; not reopened per user)**: build a **manual** recursive walk —
`new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false })`
one level at a time — instead of `SearchOption.AllDirectories`. For each entry:

- if `entry.LinkTarget is not null` (the entry is itself a symlink, file or directory): **skip
  it silently** (Q2) — do not descend into it if it is a directory, do not add it if it is a
  file. This is a single check that answers defect 3's question about file-level symlinks too:
  yes, same handling, same check.
- otherwise, recurse into directories and collect matching files exactly as today.

This is **stricter** than "resolve the target and check containment": it skips a symlink even
when its target happens to sit inside the same root, because deciding that would require
resolving it first. Given defect 10's repository scan (below) found zero existing symlinks
under any configured docs root, this costs nothing today, and it is the only design that never
performs a single filesystem operation on a path outside the directory currently being listed —
which is what D3 requires and what Q2's "no diagnostic" instruction presumes (there is nothing
to resolve, so there is nothing to report). This directly answers defect 3's design question:
a walk that never descends into links satisfies both D3 and Q2; "resolve, then containment-check"
does not, because resolving is itself the kind of outside-the-root touch D3 forbids.

The catalog file (read outside the per-root walk in both `DocumentLoader.Load()` and
`DocumentIndexHost.ComputeDocsStamp()`) gets the identical file-level check before it is parsed
or stamped — this is the one place Q2's rule still matters after Q3 is dropped (see Defect 7
below): the catalog string is already contained, but nothing today stops the catalog *path*
from resolving to a symlink.

## Route coverage

One row per TCC service actually observed, naming the task that closes its enumeration route
and why. "Closed" means: no enumeration call this plan touches can reach that location once T2,
T4, and T6 land, because the walk never descends into or resolves a link.

| TCC service | Route this plan closes | Task | Open by design? |
|---|---|---|---|
| Photos | A symlink under a docs root (or the catalog path) resolving into `~/Pictures`/Photos Library storage would be walked/read today (confirmed weakness #1/#3 above); it never will be after the fix, because the walk never descends into or opens a link. | T2 (helper), T4 (`DocumentLoader`), T6 (`DocumentIndexHost`) | No — this route is closed, not declared acceptable. |
| DocumentsFolder | Same mechanism — a link into `~/Documents` (iCloud Desktop & Documents sync lives here on many Macs) would be walked today; closed the same way. | T2, T4, T6 | No |
| DownloadsFolder | Same mechanism — a link into `~/Downloads` would be walked today; closed the same way. | T2, T4, T6 | No |
| SystemPolicyAllFiles | This is macOS's broadest "Full Disk Access" bucket; an unbounded recursive walk that can follow an arbitrary link is exactly the shape of behavior this bucket exists to gate. Once the walk cannot leave the directory it is listing, nothing it does implicates this bucket. | T2, T4, T6 | No |
| SystemPolicyAppDataDetailed (7 rows, other apps' `~/Library/Containers/*`) | Container paths are reachable only through a link (nothing in a normal docs tree points there natively); same mechanism, same fix. | T2, T4, T6 | No |
| FileProviderDomain (×2 none, ×1 full — OneDrive/iCloud file-provider domains) | Same mechanism when the link's target is a cloud-provider-backed path. **Exception, stated explicitly**: if the *project root itself* (not a link inside it) is a folder the user deliberately keeps under a cloud-storage location such as `~/Library/CloudStorage/OneDrive/...`, ordinary enumeration of that project's own files legitimately touches the FileProviderDomain service — D3 permits project folders wherever they live, and that is not a link-following bug. This plan does not gate where a project root may live; it only stops a *link encountered while walking one* from reaching further out. | T2, T4, T6 (link route); project-root case is **open by design** — legitimate per D3 | Partially — see exception |

No service in the observed footprint is left "open by design" without the explicit reasoning
above; the only open-by-design case is the project-root-in-a-cloud-folder exception D3 already
permits, not a route this plan declines to close.

## Approved decisions (verbatim; no further approval needed)

- **D1**: "launcher-home detection never runs automatically at startup or provider load. It
  runs only on explicit request (doctor / explicit refresh)."
- **D2**: "keep KyberDash's Codex nested-launcher-home check (functionally needed); fix only
  its timing."
- **D3**: "every component, kyber-weave-mcp included, may touch only a finite, enumerated list
  of known harness folders (KyberDash's ~40-provider roster) plus project folders. Nothing may
  scan or walk the home directory or the wider filesystem."
- **Q1 = INCLUDE**: the KyberDash D1 timing fix rides in this plan (T7/T8), corrected per the
  note above to state it is unrelated to the Photos incident.
- **Q2 = SKIP SILENTLY**: an escaping (or, per the design above, any) symlink encountered
  during a walk is skipped with no diagnostic and no new `KW-*` rule id.
- **Q4 = A (explicit request)**: approved 2026-09-25. Once nest-detection lives inside
  `discoverSessions()`/`probeRoots()`, a scheduled or explicit refresh calling them is ordinary
  permitted provider work, and no follow-on item is opened for option B.
- **Development-mode**: `test-first` (user input). Plan chosen over spec (user input).

## Dropped: Q3 (catalog path containment)

The prior draft treated this as open. **It is not.** `OntologyConfigLoader.NormalizeCatalogPath`
(`src/KyberWeave.Core/Configuration/OntologyConfigLoader.cs:125-144`) already calls
`DocsRootPath.Normalize` on the configured `ontology.catalog-path` string — the exact function
that rejects an absolute path or a `..` segment for a docs root (`DocsRootPath.cs:40-68`).
Verified by reading both files: the catalog string cannot be absolute and cannot escape via
`..` today. There is no separate catalog task in this plan. The one thing string-level
normalization cannot catch — the catalog path resolving through a *symlink* — is exactly what
the file-level `LinkTarget` check added to `DocumentLoader.Load()` (T4) and
`DocumentIndexHost.ComputeDocsStamp()` (T6) now covers, so no separate catalog task is needed
for that either.

## Open question carried into this plan (Q4 — does not block T7/T8)

**Q4 — does KyberDash's scheduled/background refresh count as "explicit request" under D1?**

Investigation (`dash/src/refresh/orchestrator.ts:63-95`, `dash/src/providers/index.ts:196-262`,
`dash/tray/src-tauri/src/scheduler.rs:151-268`) found: the tray's `Scheduler` runs a refresh
"one at start, then one per cadence" (its own comment, citing Requirement 10.1) — i.e.
automatically, with no user action in the moment — by spawning the `kyberdash` CLI's refresh
command. That command's `refreshHarnessSources` calls `getAllProviders()`
(`dash/src/synth/provider.ts:57`), which builds the full provider list including `codex`, and
then runs one job per `HARNESS_DESCRIPTORS` entry (`dash/src/refresh/registry.ts`) — including
Codex's — which calls the Codex provider's own `discoverSessions()`/`probeRoots()`. Today,
launcher-home detection runs at `createCodexProvider()` **construction** time
(`dash/src/providers/codex.ts:1359-1361`, inside the function body, before the object is
returned), which is what makes it run merely because the module loaded — the literal,
confirmed bug D1 names. T7/T8 (below) move that detection out of construction and into the
provider's own `probeRoots()`/`discoverSessions()` methods, memoized per instance. That
directly fixes the named bug and satisfies D2 ("keep the check, fix only its timing"),
regardless of how Q4 resolves.

What T7/T8 do **not** resolve: after the fix, a scheduled/background refresh still calls
`discoverSessions()`/`probeRoots()` on the Codex provider as ordinary refresh work — the same
call an explicit `doctor` or a user-clicked "Refresh now" makes. Whether D1's "only on explicit
request" should be read to additionally suppress that call specifically when the trigger was
the cadence timer (not a user action) is a genuine, unresolved policy question:

- **Option A (recommended; what T7/T8 implement)**: "explicit request" in D1 targets the
  literal, reported failure mode — detection firing merely because a module was imported or an
  object constructed, with no discovery ever requested. Once detection lives inside
  `discoverSessions()`/`probeRoots()` — the same place the provider does its normal,
  D3-permitted scan of its own known harness folder — invoking those methods during any refresh
  (scheduled or explicit) is ordinary provider operation, not the automatic home-probing D1
  targeted. Smallest correct fix; matches D2's "fix only the timing" instruction; testable with
  a clean RED/GREEN pair (T7/T8).
- **Option B**: scheduled/cadence-triggered refresh must not run Codex nest-detection at all;
  only `doctor` or a user-clicked refresh may. This requires threading a trigger-provenance flag
  from the Rust `Scheduler` (`tick` vs `run_now`, `dash/tray/src-tauri/src/scheduler.rs:246-268`)
  through `refreshHarnessSources`, `HARNESS_DESCRIPTORS` job execution, and the Codex provider's
  methods — a materially larger, cross-language change (Rust IPC/state, TypeScript pipeline,
  new tests on both sides) — and would leave scheduled Codex ingestion incomplete until the user
  takes an explicit action, a real behavior regression to weigh against the privacy gain.

This does not block T7/T8, which implement Option A now; it is recorded so the conductor can
put it to the user rather than have it silently decided. If the user wants Option B, it is a
follow-on plan or todo, not an amendment to T7/T8's scope.

## Repository impact check (defect 10)

Searched the full working tree for existing symbolic links: `find . -type l` (excluding
`.git/`). Every hit is an npm-managed `node_modules/.bin/*` shim (`dash/node_modules/.bin/*`,
`dash/web/node_modules/.bin/*`, `.ds-sync/node_modules/.bin/*`) — none is under any configured
docs root (`docs/`), none is discovered by `DocumentLoader`/`DocumentIndexHost` (which only
enumerate `*.md` under configured roots, never `node_modules`, which is already excluded by
`OntologyConfig.ExcludedPathSegments`). **The new silent-skip rule changes the result of
`docs validate`/`docs drift` on this repository today for exactly zero files.**

## Tasks

Every implementation task has a preceding RED task in its own row of the table below; no GREEN
task is scheduled to run while its own RED task is still open. T1/T3/T5/T7 (all RED) have no
dependency on each other and may run together; T2 depends only on T1; T8 depends only on T7 and
is a fully separate codebase from T1–T6.

### T1 — RED: `DocsRootPath` containment-safe enumeration (test-dev)

**Scope**: `tests/KyberWeave.Tests/DocsRootPathSymlinkContainmentTests.cs` (new file).
**Depends on**: none.

Fixtures use two sibling `TempDirectory` instances (the fixture helper already used throughout
this suite, e.g. `tests/KyberWeave.Tests/DocsScaffolderTests.cs`, `KiloRendererContractTests.cs`)
— one stands in for the walked root, one for "somewhere outside it." **Neither test creates,
reads, or depends on anything under the real `$HOME` or any other path outside its own two
temp directories** — this replaces the prior draft's `~/.research` fixture outright.

Acceptance — a not-yet-existing `DocsRootPath.EnumerateContainedFiles(string root, string
searchPattern)`:
1. A directory entry under `root` that is itself a symlink (`Directory.CreateSymbolicLink`,
   pointing at the sibling temp directory) is not descended into; a file inside the sibling
   directory never appears in the result.
2. A file entry under `root` that is itself a symlink (`File.CreateSymbolicLink`, pointing at a
   file in the sibling directory) does not appear in the result.
3. A symlink whose target happens to sit *inside* `root` itself is still skipped (uniform
   policy — the design never resolves a link to find out where it points).
4. An ordinary file several directories deep, with no symlink anywhere in its ancestry, is
   still returned (guard against over-broad exclusion).

**Windows CI note**: `Directory.CreateSymbolicLink`/`File.CreateSymbolicLink` need either an
elevated process or Developer Mode enabled on Windows; a non-elevated, non-Developer-Mode
process gets `IOException`/`UnauthorizedAccessException`. This repository's only CI job that
runs the full, unfiltered `KyberWeave.Tests.csproj` suite (`build-test`,
`.github/workflows/ci.yml:36-38`) runs on `ubuntu-latest` only. `windows-latest` appears
elsewhere in the matrix (`squad-filesystem-contract`, `.github/workflows/ci.yml:116-140`) but
that job filters to `FullyQualifiedName~SquadDeploymentStateTests` and will never select these
new tests. No CI job needs symlink creation to succeed on Windows for this plan. A developer
running the full suite locally on Windows without Developer Mode will see these tests fail with
that specific exception; that is a known, documented local-environment gap, not a CI risk, and
is out of scope to fix here.

**Test contract**
- Runner: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~DocsRootPathSymlinkContainmentTests"`
- RED evidence: `DocsRootPath` has no `EnumerateContainedFiles` member yet — the file fails to
  compile against the not-yet-existing method (a build failure is valid RED evidence for a
  not-yet-existing member; test-dev records the compiler error as the pre-GREEN observable).

### T2 — GREEN: implement `DocsRootPath.EnumerateContainedFiles` (csharp-dev)

**Scope**: `src/KyberWeave.Core/Configuration/DocsRootPath.cs`.
**Depends on**: T1.

Add `internal static IEnumerable<string> EnumerateContainedFiles(string root, string searchPattern)`:
manual one-level-at-a-time recursion via `new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = false })`;
for each entry, skip silently when `entry.LinkTarget is not null` (covers both a symlinked
directory — do not recurse — and a symlinked file — do not yield it); otherwise recurse into
directories and yield files matching `searchPattern`. No signature change to any existing
`DocsRootPath` member.

**Test contract**
- Runner: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~DocsRootPathSymlinkContainmentTests"`
- GREEN acceptance: all four T1 assertions pass; no existing `DocsRootPath` test regresses.

### T3 — RED: `DocumentLoader.Load()` containment (test-dev)

**Scope**: `tests/KyberWeave.Tests/DocumentLoaderSymlinkContainmentTests.cs` (new file).
**Depends on**: none (independent of T1/T2 — different files; may run in the same wave).

Same two-sibling-`TempDirectory` fixture discipline as T1 — no `$HOME`, no path outside the
test's own temp directories. Acceptance:
1. A docs root containing a directory symlink to the sibling temp directory: `Load().Documents`
   does not contain the sibling's `.md` file, and `Load()` does not throw.
2. The configured catalog path itself replaced by a file-level symlink to a file in the sibling
   directory: the catalog document is excluded from `Load().Documents`, `Load()` does not
   throw, and `Components`/`Owners` are empty rather than reflecting the sibling's content.
3. An ordinary catalog and ordinary nested `.md` files, with no symlinks involved, are still
   loaded exactly as today (guard, protects existing behavior).

**Test contract**
- Runner: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentLoaderSymlinkContainmentTests"`
- RED evidence: today's `DocumentLoader.Load()` calls raw `Directory.EnumerateFiles(absoluteRoot, "*.md", SearchOption.AllDirectories)` (confirmed above), which *does* follow the directory
  symlink, and reads `_catalogPath` directly with no link check — assertions 1 and 2 FAIL today
  (the escaped content IS present).

### T4 — GREEN: wire `DocumentLoader.Load()` through the shared helper (csharp-dev)

**Scope**: `src/KyberWeave.Core/Docs/Parsing/DocumentLoader.cs` (the `Load()` method, lines
44-82, and its catalog read at lines 66-72).
**Depends on**: T2 (needs the helper), T3 (needs its own RED).

Replace the raw `Directory.EnumerateFiles(...)` call with
`DocsRootPath.EnumerateContainedFiles(absoluteRoot, "*.md")`. Before parsing the catalog
(`if (!visited.Contains(_catalogPath) && File.Exists(_catalogPath))`), add a check that the
catalog path itself is not a symlink (`new FileInfo(_catalogPath).LinkTarget is null`) before
including it. No signature change to `Load()`; no change to its exception contract.

**Test contract**
- Runner: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentLoaderSymlinkContainmentTests"`
- GREEN acceptance: all three T3 assertions pass; every pre-existing `DocumentLoader`-touching
  test (`DocsScaffolderTests`, `MultipleDocsRootTests`, `OntologyConfigTests`,
  `DocumentCorpusTests`, `McpAnalysisToolsTests`, `McpRepositoryRootTests`,
  `MotorcycleRagHostProfileTests`) remains green.

### T5 — RED: `DocumentIndexHost.ComputeDocsStamp()` containment (test-dev)

**Scope**: `tests/KyberWeave.Tests/DocumentIndexHostSymlinkContainmentTests.cs` (new file).
**Depends on**: none (independent file; may run in the same wave as T1/T3/T7).

Same sibling-`TempDirectory` fixture discipline. Acceptance:
1. A docs root with a directory symlink to the sibling directory: adding/removing a file
   *inside the sibling directory* does not change the value `ComputeDocsStamp()` returns.
2. The configured catalog replaced by a file-level symlink to the sibling: same — stamp is
   unaffected by edits to the sibling file.
3. An ordinary file add/remove *inside the root* (no symlink involved) still changes the stamp
   (guard — the fix must not make the stamp inert).

**Test contract**
- Runner: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentIndexHostSymlinkContainmentTests"`
- RED evidence: today's `ComputeDocsStamp()` (`DocumentIndexHost.cs:139`) walks with the same
  unguarded `Directory.EnumerateFiles(..., SearchOption.AllDirectories)` and reads the catalog
  directly (`DocumentIndexHost.cs:151-159`) — assertions 1 and 2 FAIL today (the stamp DOES
  change when the sibling's content changes).

### T6 — GREEN: wire `DocumentIndexHost.ComputeDocsStamp()` through the shared helper (csharp-dev)

**Scope**: `src/KyberWeave.Core/Docs/Search/DocumentIndexHost.cs` (`ComputeDocsStamp()`, lines
125-166).
**Depends on**: T2, T5.

Same substitution as T4: `DocsRootPath.EnumerateContainedFiles(docsRoot, "*.md")` for the root
walk; a `LinkTarget is null` guard before stamping the catalog file. No signature change to
`ComputeDocsStamp()` or `Current()`.

**Test contract**
- Runner: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~DocumentIndexHostSymlinkContainmentTests"`
- GREEN acceptance: all three T5 assertions pass; existing `DocumentCorpusTests` and
  `McpAnalysisToolsTests` remain green.

### T7 — RED: Codex provider construction does not probe the launcher home (test-dev)

**Scope**: `dash/src/providers/codex-lazy-launcher-home.test.ts` (new file, co-located next to
`codex.ts` following the existing `codex-pricing-1075.test.ts` / `codex-resume.test.ts`
convention).
**Depends on**: none — separate codebase from T1–T6; may run in the same wave.

Acceptance:
1. Spy on `realpathSync` (from `node:fs`, as `dash/src/ingest/launcher-homes.ts:1` imports it)
   or on `isNestedLauncherCodexHome`/`sameCodexHome` directly (whichever the implementation
   makes easier to isolate). Calling `createCodexProvider()` with no arguments — the exact form
   used for the module-level singleton at `codex.ts:1409` — must not call it.
2. Calling the returned provider's `probeRoots()` **or** `discoverSessions()` for the first time
   must call it exactly once; a second call to either method must not call it again (memoized).
3. Existing Codex provider tests (`dash/src/providers/codex-resume.test.ts`,
   `dash/src/providers/codex-pricing-1075.test.ts`, `dash/src/ingest/launcher-homes.test.ts`)
   are unaffected.

**Test contract**
- Runner: `npm --prefix dash run test -- codex-lazy-launcher-home`
- Also run for this task: `npm --prefix dash run typecheck`, `npm --prefix dash run lint`
- RED evidence: `createCodexProvider()`'s body (`codex.ts:1357-1361`) computes `duplicateHome`
  and `nestHome` synchronously — `sameCodexHome`/`isNestedLauncherCodexHome` (hence
  `realpathSync`) run before the function returns, regardless of whether any method is ever
  called on the result — assertion 1 FAILS today.

### T8 — GREEN: defer Codex nested-launcher-home detection to first use (skill: the `TypeScript worker` role that owned equivalent non-React `dash/src/*.ts` GREEN work in [`docs/2026-09-23-kyberdash-ingestion-report-integrity.md`](../2026-09-23-kyberdash-ingestion-report-integrity.md), rows T5-T9 — `react-dev` is scoped to `.tsx`/`.jsx` only and does not cover this file)

**Scope**: `dash/src/providers/codex.ts` (`createCodexProvider`, lines 1347-1407).
**Depends on**: T7.

Move the `duplicateHome`/`nestHome`/`scanBoth` computation out of `createCodexProvider()`'s
synchronous body and into a memoized closure invoked from `probeRoots()` and
`discoverSessions()` on first call (cached on the closure thereafter, so a session-discovery
call after the first one does not recompute it). `createCodexProvider()` itself becomes cheap —
building the object shape only — regardless of when or why the module loads. **D2 is
unaffected**: the check itself, `isNestedLauncherCodexHome`, is untouched in
`dash/src/ingest/launcher-homes.ts`; only when it runs changes. No change to `Provider`'s public
shape or to `dash/src/ingest/launcher-homes.ts`.

**Why this belongs where it already lives**: per `docs/dash/architecture.md`'s repository
layout table, `dash/src/**` is "the CLI engine: provider session parsers, ..."; `codex.ts` and
`launcher-homes.ts` are exactly that. [ADR 0020](../../adr/0020-kyberdash-one-time-fork.md) retired
the merge-zone rule this file previously sat under ("no further upstream change is merged ...
any file under `dash/` is edited on its merits, under the repository's gates") — there is no
merge-zone constraint left to apply, and no relocation is implied by this fix.

**Test contract**
- Runner: `npm --prefix dash run test -- codex-lazy-launcher-home`
- Also run for this task: `npm --prefix dash run typecheck`, `npm --prefix dash run lint`
- GREEN acceptance: all three T7 assertions pass.

### T9 — docs-dev closeout (docs-dev)

**Scope**: `docs/configuration.md`, `docs/standards/csharp/README.md`, `docs/plans/README.md`.
**Depends on**: T4, T6, T8, and a green run of the Verification gates section below.

Not a code-verification task — that already happened in T4/T6/T8's own Test contracts and the
gates below; this task only harvests durable facts and closes the plan:
1. `docs/configuration.md`: one short note under the catalog section stating the catalog path
   is also checked at read time for a symlinked target, skipped silently if so (mirrors the
   existing string-containment note already there).
2. `docs/standards/csharp/README.md`: record the "never resolve a link, only ask whether an
   enumerated entry is itself one" pattern as the house idiom for any future recursive
   filesystem walk in this repository, citing `DocsRootPath.EnumerateContainedFiles`, so a
   later contributor does not reintroduce unguarded `SearchOption.AllDirectories`.
3. Move this plan's row from `docs/plans/README.md`'s Active Plans table to the Archived Plans
   table, dated, linking the two doc updates above; no ADR is warranted (this is a bug fix
   against an already-decided containment principle, D3, not a new architectural decision).

**Test contract**: N/A (documentation-only) — verification is `dotnet run --project
src/KyberWeave.Cli -c Release -- docs validate .` and `... docs drift .`, both zero findings,
which this task's own archival edit must not break.

## File-scope matrix (no file is in two tasks' scope)

| Task | File scope |
|---|---|
| T1 | `tests/KyberWeave.Tests/DocsRootPathSymlinkContainmentTests.cs` (new) |
| T2 | `src/KyberWeave.Core/Configuration/DocsRootPath.cs` |
| T3 | `tests/KyberWeave.Tests/DocumentLoaderSymlinkContainmentTests.cs` (new) |
| T4 | `src/KyberWeave.Core/Docs/Parsing/DocumentLoader.cs` |
| T5 | `tests/KyberWeave.Tests/DocumentIndexHostSymlinkContainmentTests.cs` (new) |
| T6 | `src/KyberWeave.Core/Docs/Search/DocumentIndexHost.cs` |
| T7 | `dash/src/providers/codex-lazy-launcher-home.test.ts` (new) |
| T8 | `dash/src/providers/codex.ts` |
| T9 | `docs/configuration.md`, `docs/standards/csharp/README.md`, `docs/plans/README.md` |

## Dependency graph and concurrency

```
T1 ──> T2 ──┬──> T4 (needs T2 and T3)
T3 ─────────┤
T5 ─────────┴──> T6 (needs T2 and T5)
T7 ──> T8

T4, T6, T8 ──> T9
```

- Wave 1 (parallel, independent files): **T1, T3, T5, T7** — all RED, all different files, two
  different codebases (T7 is TypeScript). `test-dev` runs T1/T3/T5/T7.
- T2 (`csharp-dev`) starts as soon as T1 finishes; it does not need T3/T5/T7 to finish first
  (disjoint file scopes). T8 (`TypeScript worker`) starts as soon as T7 finishes, fully
  independent of the .NET side.
- Wave 2 (parallel once T2 is done): **T4** (needs T2 + T3) and **T6** (needs T2 + T5) — disjoint
  files (`DocumentLoader.cs` vs `DocumentIndexHost.cs`).
- T9 last, after T4, T6, T8, and the Verification gates below.

**MAX_CONCURRENCY = 4** (Wave 1's width). No GREEN task is ever scheduled in the same window as
its own RED task; T2 cannot start until T1's RED evidence is recorded, T4 cannot start until T3's
RED evidence is recorded (and T2's helper exists), T6 cannot start until T5's RED evidence is
recorded (and T2's helper exists), T8 cannot start until T7's RED evidence is recorded.

## Risks

1. **Uniform skip of internal (non-escaping) symlinks is a behavior narrowing**, not just a
   security fix: an author who deliberately symlinked a shared file *within* a docs root would
   see it silently stop being indexed. Mitigation: defect 10's scan found no such case in this
   repository today; if one is ever wanted, resolving-and-containment-checking is the
   alternative design, and it would need its own decision given it re-opens the "must resolve
   outside the currently-listed directory" question this plan avoids.
2. **Performance of per-entry `LinkTarget` reads is not benchmarked.** `EnumerateFileSystemInfos`
   already returns `FileAttributes` from the underlying directory listing; whether `LinkTarget`
   specifically requires an additional per-entry stat beyond that is not confirmed by the
   Microsoft Learn pages consulted. Not a blocker — the corpus this repository walks is small —
   but this plan does not claim it is free.
3. **Q4 (above) is unresolved.** T7/T8 implement the fix that is correct under either answer to
   Q4; nothing in this plan depends on Q4 resolving one way or the other.

## Out of scope

1. **MCP capability/root declarations** in `src/KyberWeave.Mcp/Program.cs` — unchanged by this
   plan; `RepositoryRootResolver` still validates only that `.kyber-weave` exists at the given
   root, which is a separate hardening question from "does the walk itself escape."
2. **The configured docs-root path itself being a symlink** (as opposed to a symlink
   *encountered while walking beneath* a root) is not addressed — the operator names the root
   explicitly in `kyber-weave.yml`, which is the same kind of explicit, deliberate choice D3
   already permits for a project folder; this plan's containment check applies to what the walk
   *discovers*, not to the root the operator configured.
3. **Q4's Option B** (suppressing Codex nest-detection specifically during a scheduled/cadence
   refresh trigger) — flagged above, not implemented; would need its own plan or todo if chosen.
4. **Cloud-synced or protected project roots** — per the route-coverage table, a project
   deliberately kept under a cloud-storage folder remains accessible, by design (D3 permits
   project folders); this plan does not restrict where a project may live.

## Verification gates

After T1-T8 land:

```bash
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~SymlinkContainment" -v normal
```

(a single `~` contains-filter is sufficient and valid vstest syntax because all three new .NET
test-class names deliberately share the substring `SymlinkContainment` — see defect 9 below for
why the prior draft's `--filter "FullyQualifiedName~(A|B|C)"` form does not work at all: `~` is
a literal substring "contains" test with no regex/alternation support; `(A|B|C)` is only valid
when each alternative is its own `FullyQualifiedName~X` clause joined by `|`, e.g.
`"FullyQualifiedName~A|FullyQualifiedName~B"`, not parenthesized alternation inside one clause.)

```bash
npm --prefix dash run test -- codex-lazy-launcher-home
npm --prefix dash run typecheck
npm --prefix dash run lint
```

Then the full local gate suite, both required by this repository's `AGENTS.md`:

```bash
dotnet run --project src/KyberWeave.Cli -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli -c Release -- docs drift .
```

Both must show zero findings before T9 closes the plan.

## Discovery ledger

- Code read (verbatim, current on disk, confirmed byte-identical between `main` and rc.13 for
  the files in scope): `DocumentLoader.cs`, `DocumentIndexHost.cs`, `DocsRootPath.cs`,
  `OntologyConfigLoader.cs`, `codex.ts`, `launcher-homes.ts`, `providers/index.ts`,
  `synth/provider.ts`, `refresh/orchestrator.ts`, `dash/tray/src-tauri/src/scheduler.rs`.
- `.NET API behavior verified against live Microsoft Learn pages (`Path.GetFullPath`,
  `EnumerationOptions`, `EnumerationOptions.AttributesToSkip`, `FileSystemInfo.LinkTarget`,
  `FileSystemInfo.ResolveLinkTarget`) and the open `dotnet/runtime#52666`/`#24655` issues, all
  fetched 2026-09-25; URLs cited inline above.
- `dotnet test --filter` grammar verified against `microsoft/vstest`'s own `docs/filter.md`.
- CI OS matrix read from `.github/workflows/ci.yml`: the unfiltered `KyberWeave.Tests.csproj`
  run (`build-test`) is `ubuntu-latest`-only; the Windows-inclusive matrix job
  (`squad-filesystem-contract`) filters to an unrelated test class.
- Repository-wide `find . -type l` (excluding `.git/`) run 2026-09-25: zero symlinks under any
  docs root; all hits are `node_modules/.bin/*` shims.
- `docs/archive/plans/2026-09-23-kyberdash-ingestion-report-integrity.md` read for TypeScript
  ownership precedent on non-React `dash/src/*.ts` GREEN work (rows T5-T9, specialist
  `TypeScript worker`).
- [ADR 0020](../../adr/0020-kyberdash-one-time-fork.md) read to confirm the merge-zone rule cited
  by the task instructions is retired, not merely inapplicable.
- Issue #124 read via `gh issue view 124` for the exact reported text and screenshots described.

## Plan lifecycle and closure

**Status**: Archived. **Complete**: All implementation tasks T1–T8 passed under test-first development mode with full code review and verification. Council decision: APPROVE with 15/15 gates green. Test evidence: .NET 2126 tests passed / 0 failed; KyberDash 257 files, 3632 tests passed. Issue #124 fixed by commits 2d1d5535 (fix: stop docs walks following symlinks out of the repo) and merge 1d65a4a0 (Merge remote-tracking branch 'origin/main' into main). Canonical documentation harvested: symlink containment rule at [docs/standards/csharp/README.md](../../standards/csharp/README.md), catalog-path symlink-check note at [docs/configuration.md](../../configuration.md). No ADR required — this is a bug fix against the already-decided D3 containment principle, not a new architectural decision. Archived 2026-09-26.
