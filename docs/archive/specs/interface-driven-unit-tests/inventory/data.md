---
id: specs/interface-driven-unit-tests/inventory/data
title: Inventory of concrete dependencies, Data
doc-type: reference
status: draft
owner: dpalfery
last-reviewed: 2026-11-17
component: CI Pipelines
---

# Inventory of concrete dependencies: Data

Produced 2026-11-17 by a read-only pass over every `.cs` file in scope, at commit `a5af901f` of the
role-based worker agents branch. Evidence for this specification, not living guidance: it goes stale
as the tasks land.


Read-only analysis of the whole project: **137 `.cs` files, 27,187 lines**, at
`/Users/dave/git/opencode-worktrees/kyber-weave/inventory/src/KyberWeave.Data`.

## Bottom line

The data layer is **already wired interface-first at its composition roots** — `ServiceCollectionExtensions.cs`
and `Squad/SquadServiceCollectionExtensions.cs` register 113 classes under `I*` contracts, and `Contracts/Repositories`
holds ~110 repository interfaces. That surface is in good shape.

What is **not** interface-driven is the implementation half:

1. **1,347 sites call an outside resource directly** (file system, processes, environment, clock, randomness, network,
   console, mutexes) across 111 of the 137 files. About 145 of those sit in the deliberate physical adapters
   (`PhysicalFileSystem`, `PhysicalRepositoryFileStore`, …) and are acceptable. The other **~1,200 are in classes
   that also hold logic**: path semantics, deployment planning, state stores, migrations, loaders, packers, doctor
   checks, review stores. Those cannot be unit-tested; 73 test files use `Path.GetTempPath`, and 63 test files
   construct `SquadDeploymentPlanner` / `SquadTransaction` / `SquadStateStore` / `SquadSourceLoader` directly.
2. **32 of the 137 classes are `static`** (`SquadDeploymentPlanner`, `SquadSourceLoader`, `SquadPacker`,
   `SquadInstanceLinkSync`, `SquadSkillShadowing`, `FactoryHooksShadowing`, `DevinImportOverlap`,
   `ZCodeMcpConfiguration`, `SquadHookJsonFile`, `DocsRootPath`, `AnalysisCacheSafety`, …). A static class cannot
   receive an injected seam at all, so no interface in `Contracts` can reach the logic inside it. This is the single
   largest structural blocker: 716 of the 1,347 resource sites (53%) are in `Squad/`, and nearly all of those are
   behind static entry points.
3. **12 concrete-service dependencies (B)** and **13 `new`-inside-a-service sites (C)** remain. They cluster in
   Squad deployment: `SquadTransaction(SquadStateStore)`, `SquadStateMigration(SquadStateStore)`,
   `SquadTransactionFactory(SquadStateStore)`, plus six copies of the static field
   `private static readonly SquadFileSystemPathSemantics Semantics = SquadFileSystemPathSemantics.Instance;`.
4. The existing seam is **too thin for what the code needs**. `IFileSystem` covers exists/text/enumerate;
   `IWritableFileSystem` adds write/newfile/delete/link-target. Nothing covers bytes, streams, file length, last-write
   time, attributes, final-target link resolution, directory trees, atomic rename, exclusive-create race semantics,
   delete-tree-no-follow, or temp-directory creation — so every Squad class that needs one of these reaches past the
   interface into `System.IO`. Fixing the interface is more valuable than fixing individual classes.

The exception the owner allowed is honoured: `Mutex`, `ProcessStartInfo`, `HttpClient`, `ZipArchive`, `FileStream`,
`JsonDocument`, `SHA256` are sealed framework types with no interface. They still deserve thin wrappers where tests
need determinism (time, temp names, retry delays), which is noted per row.

## How findings were counted

* **A (outside resource used directly)** — a call to `File.*`, `Directory.*`, `FileInfo`/`DirectoryInfo`,
  `Path.GetFullPath`/`Combine`/`GetTempPath`/`GetRelativePath`/… on real paths, `Process`/`ProcessStartInfo`,
  `Environment.*`, `AppContext.BaseDirectory`, `DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`, `Stopwatch`,
  `Random`, `Guid.NewGuid`, `HttpClient` created or used inline, `Console.*`, `Thread.Sleep`, `Task.Delay`,
  `Mutex`, `OperatingSystem.Is*`, plus static mutable state.
* **B (concrete service dependency)** — a constructor parameter, field, or local typed as a concrete service class
  that has an interface in `Contracts`, instead of that interface. Records, DTOs, value objects, options and enums are
  data, not dependencies, and are excluded by design.
* **C (`new` of a service inside a service)** — constructing a Data service class anywhere other than a composition
  root (`ServiceCollectionExtensions`, `SquadServiceCollectionExtensions`).
* **D (acceptable)** — (a) pure data: records, DTOs, enums, options; (b) a **physical adapter** whose entire purpose is
  one outside resource and which is swapped wholesale at the composition root (it implements an interface, holds no
  policy beyond the primitive, and is the thing tests replace); (c) a sealed framework type with no interface.
  Where an adapter smuggles policy (a skip-list, a pruning interval, a retry) into itself, that is called out.
* Counts are mechanical (regex over non-comment lines) and were then read for meaning; treat them as lower bounds.
  `size` is S ≤ 120 lines, M ≤ 400, L > 400.

Interface names in the "should depend on" column that do not exist yet are marked **(new)**; see
[New interfaces this project needs](#new-interfaces-this-project-needs).

---

## `Squad/Deployment` — the priority folder (22 files, 379 resource sites)

This is where the owner's suspicion is correct. The folder is a mix of static algorithms that read and write real
files and thin `File*` adapters that call them. The adapters are `ISquad*`-shaped and look testable; the work they
delegate to is not.

### `SquadTransaction.cs` (3,372 lines — largest class in the project, 117 A sites, 1 B, 2 static-state)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `SquadTransaction.cs:43-44` | `SquadTransaction` | ctor takes concrete `SquadStateStore` | **B** | `ISquadStateStore` (exists) | L |
| `SquadTransaction.cs:35` | `SquadTransaction` | `static ConcurrentDictionary ActiveLeases` — process-wide mutable lease table shared by every instance and every test | **A** | instance `ITransactionLeaseRegistry` (new) | L |
| `SquadTransaction.cs:52-63` | `SquadTransaction` | `static bool _defaultSyncToDisk` + `AsyncLocal` override; tests flip a global to disable `Flush(true)` | **A** | injected `TransactionOptions { SyncToDisk }` | L |
| `…:119,120,124,146,153,165,166` (`Execute`) | do. | `Path.Combine`, `Directory.Exists` ×3, `Directory.CreateDirectory` ×3, `File.Exists` on the journal/work tree | A | `ISquadFileAccess` (extended) | L |
| `…:358,367,368` (`Recover`) | do. | `Directory.Exists`, `Path.Combine`, `File.Exists` — recovery reads the live tree to find the journal | A | `ISquadFileAccess` | L |
| `…:436,438,473,503` (`CaptureIntent`) | do. | `Path.GetDirectoryName`/`GetRelativePath`, `File.ReadAllBytes` for the original digest | A | `ISquadFileAccess` + `IFileSystemPathSemantics` | L |
| `…:536` (`ValidatePrecondition`) | do. | `File.ReadAllBytes` to compare a precondition digest | A | `ISquadFileAccess` | L |
| `…:745` (`ApplyFiles`) | do. | `Directory.CreateDirectory` per target | A | `ISquadFileAccess` | L |
| `…:960,961` (`ApplyStateFileNoOverwrite`) | do. | `Path.GetDirectoryName`/`GetFileName` to build an atomic write | A | `IAtomicFileWriter` (new) | L |
| `…:991-1001` (`ClaimStateNoOverwrite`) | do. | five `Path.*` calls to place a claim file | A | `IAtomicFileWriter` | L |
| `…:1048,1049,1071,1073,1074,1111` (`ClaimExistingNoOverwrite`) | do. | `Path.IsPathRooted`, `new DirectoryInfo(...).ResolveLinkTarget(true)`, `Directory.CreateSymbolicLink`, `File.ReadAllBytes` | A | `ISquadFileAccess` + `IFileSystemPathProbe` (new) | L |
| `…:1195` (`VerifyAfterImage`) | do. | `File.ReadAllBytes` to verify a written image | A | `ISquadFileAccess` | L |
| `…:1321,1327,1352` (`ResolveActiveClaims`) | do. | `Path.GetFileName` ×2, `Directory.CreateDirectory` | A | `ISquadFileAccess` | L |
| `…:1392,1397,1402` (`EntryMatchesOriginal`) | do. | `File.ReadAllBytes`, `new FileInfo(...).LinkTarget`, `new DirectoryInfo(...).LinkTarget` | A | `IFileSystemPathProbe` | L |
| `…:1424` (`EntryMatchesAfter`) | do. | `File.ReadAllBytes` | A | `ISquadFileAccess` | L |
| `…:1518,1543` (`CaptureVerifiedRestorePayload`) | do. | `File.ReadAllBytes` ×2 | A | `ISquadFileAccess` | L |
| `…:1578,1605,1608,1611` (`RestoreEntry`) | do. | `Directory.CreateDirectory`, `new FileStream(FileMode.CreateNew)` + `FileOptions.WriteThrough`, `File.CreateSymbolicLink`, `Directory.CreateSymbolicLink` | A | `ISquadFileAccess` | L |
| `…:1772,1773,1784,1796` (`CaptureNodeArtifact`) | do. | `new FileInfo().Length`, `File.ReadAllBytes`, link-target reads for both kinds | A | `IFileStatReader` (exists) + `ISquadFileAccess` | L |
| `…:1821,1823` (`MoveEntryNoOverwrite`) | do. | `Directory.Move` / `File.Move` | A | `ISquadFileAccess.MoveNoOverwrite` | L |
| `…:1828,1834,1836` (`RemoveEmptyArtifactParents`) | do. | `Path.GetDirectoryName` walk + `Directory.Exists` + `Directory.Delete` | A | `ISquadFileAccess` | L |
| `…:1889,1890,1917,1936` (`BackupEntry`) | do. | link-target reads, `File.ReadAllBytes` ×2 | A | `ISquadFileAccess` | L |
| `…:1966,1970,1973` (`CopyEntry`) | do. | `Directory.CreateDirectory`, `File.Copy`, `Directory.CreateDirectory` for directories | A | `ISquadFileAccess` | L |
| `…:1993,1998` (`GetEntryKind`) | do. | `OperatingSystem.IsWindows()` and `Directory.Exists` decide file-vs-directory | A | `IFileSystemPathProbe` | L |
| `…:2015,2016,2025` (`CaptureMissingDirectories`) | do. | `Path.GetFullPath`/`TrimEndingDirectorySeparator`/`GetDirectoryName` over the real root | A | `IFileSystemPathSemantics` | L |
| `…:2078,2090` (`PhysicalLeaf`, `RelativeAuthorityPath`) | do. | `Path.Combine`/`GetDirectoryName`/`GetRelativePath` | A | `IFileSystemPathSemantics` | L |
| `…:2123,2126,2137,2140` (`DeleteEmptyDirectory`, `DeleteEntry`) | do. | `Directory.EnumerateFileSystemEntries(...).Any()`, `Directory.Delete`, `File.Delete` | A | `ISquadFileAccess` | L |
| `…:2154,2159,2161,2171` (`DeleteDirectoryTree`) | do. | `new DirectoryInfo(...).EnumerateFileSystemInfos()` + deletes, hand-rolled no-follow tree delete | A | `ISquadFileAccess.DeleteTreeNoFollow` | L |
| `…:2184,2188` (`AcquireLease`) | do. | `new Mutex(false, $"kyber-weave-squad-{rootKey}")` — a named cross-process mutex inside business logic | A | `ITransactionLease`/`ILockFile` (new) | L |
| `…:2228,2231,2234,2238,2240` (`CleanupOwnedArtifacts`) | do. | `Directory.Exists` ×2, `new DirectoryInfo().EnumerateFileSystemInfos()`, deletes | A | `ISquadFileAccess` | L |
| `…:2297,2298,2314,2318,2319` (`PublishFileAtomic`) | do. | stage-and-rename with `FileOptions.WriteThrough`, cleanup on failure | A | `IAtomicFileWriter` | L |
| `…:2335` (`ReadIntent`) | do. | `File.ReadAllText(intentPath, UTF8)` | A | `ISquadFileAccess` | L |
| `…:2712` (`AddExpectedStateArtifacts`) | do. | `File.ReadAllBytes` for a digest | A | `ISquadFileAccess` | L |
| `…:2862-2872` (`ArtifactFingerprintMatches`) | do. | `FileInfo.Length`, `File.ReadAllBytes`, both link-target reads | A | `IFileSystemPathProbe` | L |
| `…:3023,3027` (`VerifyExactTransactionTreeArea`) | do. | `Path.GetDirectoryName` walk on transaction paths | A | `IFileSystemPathSemantics` | L |
| `…:3043,3045` (`EnumerateTransactionTree`) | do. | `new DirectoryInfo().EnumerateFileSystemInfos()`, `Path.GetRelativePath` | A | `ISquadFileAccess.EnumerateTree` | L |
| `…:3068-3076` (`ResolveArtifactNode`) | do. | `Path.GetFullPath`/`Combine`/`GetDirectoryName` | A | `IFileSystemPathSemantics` | L |
| `…:3084-3086` (`ContainsTemporaryArtifact`) | do. | `Directory.Exists` + `Directory.EnumerateFileSystemEntries(AllDirectories)` | A | `ISquadFileAccess` | L |
| `…:3098,3103,3108,3116` (state path helpers) | do. | four `Path.Combine` path builders | A | `IFileSystemPathSemantics` | L |

Also in this file: `SyncToDiskScope` (:3353) is an `IDisposable` static-scope helper — D, but it exists only
because of the static `SyncToDisk` flag.

### `SquadStateStore.cs` (1,785 lines, 48 A)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `SquadStateStore.cs:94` | `SquadStateStore` | ctor takes `ISquadUserPaths` — **good**, the one injected seam this folder gets right | D | — | L |
| `…:15` | do. | `static SquadFileSystemPathSemantics Semantics` concrete field | **B** | `IFileSystemPathSemantics` (exists) | L |
| `…:164,794,980` (`ResolveHomeOwnedFilesRoot`, `ResolveHomeCreationBoundary`, `RegistryPath`) | do. | `Path.GetDirectoryName`/`Combine` on real roots | A | `IFileSystemPathSemantics` | L |
| `…:221,225,226,232` (`ListProjects`) | do. | `Path.GetFileName`, `Path.Combine`, `File.Exists`, `File.ReadAllText` — the registry rebuild reads every project receipt | A | `ISquadFileAccess` | L |
| `…:654,655,663,664` (`ReadReceipt`, `ReadLock`) | do. | `File.Exists` + `File.ReadAllText` twice | A | `ISquadFileAccess` | L |
| `…:784,808-811` (`ResolveHomeStateRoot`, `ResolveGlobalStateDirectory`) | do. | `Path.GetFullPath`, `File.Exists` to decide global vs project layout | A | `IFileSystemPathProbe` | L |
| `…:823,835-847` (`ResolveInstanceStateDirectory`) | do. | 8 `Path.*` + `GetFullPath` calls computing instance-state identity | A | `IFileSystemPathSemantics` | L |
| `…:869,877` (`ResolveInstanceAuthorityRoot`, `ResolveInstanceLeaseIdentity`) | do. | `Directory.Exists` decides canonicalisation, `Directory.Exists` again for the lease key | A | `IFileSystemPathProbe` | L |
| `…:886,923,926,947,953` (`ResolveDirectoryUnder`, `EnumerateProjectStateDirectories`, `DescribeEntry`) | do. | `Path.GetDirectoryName`, `Directory.Exists`, `File.Exists` | A | `ISquadFileAccess` | L |
| `…:991,996` (`ReadRegistry`) | do. | `File.Exists` + `File.ReadAllText` + `JsonNode.Parse` | A | `ISquadFileAccess` | L |
| `…:1053,1073,1083,1087,1088` (`WriteRegistry`) | do. | `Directory.CreateDirectory`, temp name from `Guid.NewGuid()`, `File.Move`, delete-on-failure | A | `IAtomicFileWriter` | L |

### `SquadDeploymentPlanner.cs` (1,341 lines, 79 A, static class)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `SquadDeploymentPlanner.cs:13` | `SquadDeploymentPlanner` | `static` class with `static SquadFileSystemPathSemantics Semantics` — no injection point at all | **B** (+static) | instance class + `IFileSystemPathSemantics` | L |
| `…:42,43,45,48,56,58,61-67,72-85,100` (`CreateInstall`) | do. | 21 sites: `File.Exists`, `File.ReadAllBytes`, `Directory.Exists`, `Path.GetFullPath` per rendered file — the install preflight reads the whole target tree | A | `ISquadFileAccess` + `IFileStatReader` | L |
| `…:153-187,195-281,343` (`CreateUpdate`) | do. | 23 sites: same read pattern plus block drift detection reading existing hook files | A | `ISquadFileAccess` | L |
| `…:403,409,412,466` (`CreateUninstall`) | do. | `File.Exists`/`Directory.Exists`/`Path.GetFullPath` for each owned file | A | `ISquadFileAccess` | L |
| `…:534,537,543,545,553` (`CollectUnmanagedCollisions`) | do. | walks the target tree `Directory.Exists` per path | A | `ISquadFileAccess` | L |
| `…:564,566,567` (`ToCollision`) | do. | `Path.GetRelativePath`/`Combine` | A | `IFileSystemPathSemantics` | L |
| `…:735,754,759,771,814,844-849,881-902` (`PlanBlockInstalls`) | do. | 11 sites reading and splicing existing hook JSON files | A | `ISquadFileAccess` | L |
| `…:1001,1004,1007` (`RemoveOwnedBlockEntries`) | do. | reads the hook file to remove its entries | A | `ISquadFileAccess` | L |
| `…:20-32` (`CreateInstall` signature) | do. | takes `TimeProvider` — the **correct** pattern already present, and the model for the clock seam | D | — | L |

### `SquadStateMigration.cs` (618 lines, 49 A, 1 B)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `SquadStateMigration.cs:60` | `SquadStateMigration` | ctor takes concrete `SquadStateStore` | **B** | `ISquadStateStore` | M |
| `…:23` | do. | static concrete `SquadFileSystemPathSemantics` | **B** | `IFileSystemPathSemantics` | M |
| `…:453,564` | do. | calls `SquadTransaction.AcquireLease` and reads `SquadTransaction.SyncToDisk` — a second class reaching into another's static state | **A** | `ILeaseRegistry`, injected options | M |
| `…:92` (`MigrateGlobal`) | do. | `File.Exists` on the home receipt | A | `ISquadFileAccess` | M |
| `…:195-207` (`ImportProject`) | do. | 7 sites: `Directory.Exists`, `File.Exists`, `Path.Combine`, `File.ReadAllText` | A | `ISquadFileAccess` | M |
| `…:238-284` (`ImportInRepoState`) | do. | 7 sites: same over the in-repo `.kyber-weave` state | A | `ISquadFileAccess` | M |
| `…:315-377` (`PlanGlobalCopy`) | do. | 9 sites: `Directory.Exists`/`File.Exists`/`File.ReadAllBytes`/`Path.*` while planning a copy | A | `ISquadFileAccess` | M |
| `…:429,432,433` (`ApplyGlobalCopy`) | do. | `Path.Combine` + `Directory.CreateDirectory` | A | `IAtomicFileWriter` | M |
| `…:463` (`WriteUnderLease`) | do. | `Path.Combine` | A | `IFileSystemPathSemantics` | M |
| `…:479` (`HomeLockBlocksCopy`) | do. | `File.ReadAllBytes` | A | `ISquadFileAccess` | M |
| `…:500,503,506` (`FindNonEmptyLegacyRootDirectories`) | do. | `Directory.Exists` + `Directory.EnumerateDirectories` | A | `IFileSystem` (exists) | M |
| `…:554-572` (`WriteAtomically`) | do. | hand-rolled stage+rename with `Guid.NewGuid()` temp name and `Flush(SquadTransaction.SyncToDisk)` | A | `IAtomicFileWriter` | M |
| `…:595,609` (`LedgerRecordsGlobalMigration`, `LedgerPath`) | do. | `Path.Combine` + `SquadMigrationLedger.Read` (reads the file) | A | `ISquadFileAccess` | M |

### Other `Squad/Deployment` files

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `SquadFileSystemPathSemantics.cs:21-39,70-101,103-122,132-158,160-184` | `SquadFileSystemPathSemantics` | **This is the seam class and it does 24 raw IO sites itself**: `Path.GetFullPath`, `Directory.Exists`, `new DirectoryInfo().EnumerateFileSystemInfos()`, `File.GetAttributes`, `ResolveLinkTarget(true)`. It is registered as `IFileSystemPathSemantics` but cannot be faked, so every consumer that needs it fakes nothing | **A** | `IFileSystemPathProbe` (new) — keep the algorithm, move the probes | M |
| `SquadFileSystemPathSemantics.cs:13` | do. | `static Instance` singleton — acceptable as a default but it is what forces the six static fields above | D | — | M |
| `SquadDeploymentAdapters.cs:74-77` | `SquadTransactionFactory` | ctor takes concrete `SquadStateStore`; `Create` does `new SquadTransaction(stateStore, …)` | **B + C** | `ISquadStateStore` + `ISquadTransaction` factory delegate | S |
| `SquadDeploymentAdapters.cs:88` | `FileSquadResponseGuidanceStore` | `new SquadResponseGuidanceDeployer(homeStateRoot)` inside the adapter | **C** | `ISquadResponseGuidanceDeployer` factory | S |
| `SquadDeploymentAdapters.cs:111-162` | `PhysicalSquadFileAccess` | adapter, but `NewScratchDirectoryPath` uses `Path.GetTempPath()`+`Guid.NewGuid()` and `FindCheckoutSquadSourceRoot` walks from `AppContext.BaseDirectory` — both are host facts, not file access | **A** (inside a D adapter) | `IProcessEnvironment.TempDirectory`, injected start directory | M |
| `SquadDeploymentAdapters.cs:13` | `FileSquadSourceLoader` | pure delegation to static `SquadSourceLoader.Load` — testable only as far as the static is | D (thin) | — | S |
| `SquadDeploymentAdapters.cs:20-69` | `FileSquadDeploymentPlanner` | pure delegation to the static planner | D (thin) | — | M |
| `SquadDeploymentAdapters.cs:98-104` | `FileFactoryHooksShadowing` | pure delegation to static `FactoryHooksShadowing` | D (thin) | — | S |
| `SquadDeploymentAdapters.cs:166-171` | `FileSquadModelPresetValidator` | pure delegation | D | — | S |
| `SquadDeploymentAdapters.cs:177-229` | `CheckoutOrSnapshotCanonicalProfileResolver` | **model example**: takes three interfaces (`ISquadFileAccess`, `ISquadSourceLoader`, `ISquadCanonicalProfileStore`) and no raw path | D | — | M |
| `SquadDeploymentAdapters.cs:232-236` | `FileSquadTargetResolver` | pure delegation to static resolver | D (thin) | — | S |
| `TransactionLease.cs:4,15-16,21-28` | `TransactionLease` | holds a `Mutex` (sealed framework type) and opens a `FileStream` journal | A / D | `Mutex` is acceptable (D); `HoldJournal` should be `ISquadFileAccess.OpenJournal` | S |
| `SquadWorkerBaselineFile.cs:16,32-73,75-88` | `SquadWorkerBaselineFile` | static, `static ConcurrentDictionary` cache keyed on real path, `FileInfo.LastWriteTimeUtc`, `File.ReadAllText`; cache is cleared by a public static method tests must call | **A** + static mutable state | `IFileStatReader` (exists) + `IFileSystem` + an injected cache | S |
| `SquadMigrationLedger.cs:60-79` | `SquadMigrationLedger` | a *record* whose static `Read(path)` does `File.Exists`/`File.ReadAllText` — IO on a data type | A | move read to a store taking `IFileSystem` | S |
| `SquadResponseGuidanceDeployer.cs:82-87,137,164-167,212-252` | `SquadResponseGuidanceDeployer` | ctor validates a real path; writes via `Guid.NewGuid()` temp + `File.Move`; reads back with `File.ReadAllBytes` | A (13) | `IAtomicFileWriter` + `ISquadFileAccess` + `IFileSystemPathSemantics` | M |
| `SquadResponseGuidanceDeployer.cs:53` | do. | static concrete `SquadFileSystemPathSemantics` | **B** | `IFileSystemPathSemantics` | M |
| `SquadResponseGuidanceReader.cs:29,36,44` | `SquadResponseGuidanceReader` | static; `Path.Combine` + `File.ReadAllText` (already injects `readDefault`, a good partial seam) | A | `IFileSystem` | S |
| `SquadHookJsonFile.cs:27,41-47,59-77` | `SquadHookJsonFile` | static; `File.Exists`/`ReadAllText`/`WriteAllText` on the hook file | A | `ISquadFileAccess` | S |
| `FileSquadHookJsonFile.cs:10-20` | `FileSquadHookJsonFile` | pure delegation | D | — | S |
| `SquadSkillShadowing.cs:40-58,69-74` | `SquadSkillShadowing` | static; `Path.GetFullPath`, `File.Exists`, `File.ReadAllText` per candidate skill | A | `IFileSystem` | S |
| `FileSquadSkillShadowing.cs:9-13` | `FileSquadSkillShadowing` | pure delegation | D | — | S |
| `FactoryHooksShadowing.cs:66-79` | `FactoryHooksShadowing` | static; `File.Exists` ×2, `File.ReadAllText` on `.factory/settings.json` | A | `IFileSystem` | S |
| `DevinImportOverlap.cs:66-91,116-146` | `DevinImportOverlap` | static; **half-seamed**: takes `Func<string,string?> readFileText` for content but reads directories/existence directly (`Directory.Exists`, `EnumerateDirectories`, `EnumerateFiles`, `File.Exists`) | A | `IFileSystem` (would make the existing delegate redundant) | M |
| `FileDevinImportOverlapInspector.cs:9-13` | `FileDevinImportOverlapInspector` | pure delegation | D | — | S |
| `ZCodeMcpConfiguration.cs:82-109` | `ZCodeMcpConfiguration` | static; `Path.Combine`, `Path.GetFullPath`, `Path.Exists` walking up to the worktree root; content via delegate | A | `IFileSystem` | S |
| `FileZCodeMcpInspector.cs:9-14` | `FileZCodeMcpInspector` | pure delegation | D | — | S |
| `SquadTargetResolver.cs:106,113,138-144` | `SquadTargetResolver` | static; `Path.GetFullPath` + `Directory.Exists`/`File.Exists` per marker (`StrongMarker.ExistsUnder`) | A | `IFileSystem` | S |
| `SquadWorktreeMcp.cs:35-43,128-137,151-161,213-218` | `SquadWorktreeMcp` | static; `Path.GetFullPath`/`IsPathRooted`, link-chain walk with `Directory.Exists`, `FileSystemInfo`, `.codegraph` existence | A | `IFileSystemPathProbe` + `IFileSystemPathSemantics` | M |

---

## `Squad/Instances` (7 files, 110 sites)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `SquadInstanceLinkSync.cs:74` | `SquadInstanceLinkSync` | `new Mutex(false, mutexName)` — named mutex inside the sync algorithm | A | `ILockFile` (new) | M |
| `…:129-140,166-233` | do. | **bypasses its own `ISquadLinkFileSystem` parameter**: `Directory.Exists`, `File.Exists`, `File.Delete` ×2 alongside `fileSystem.IsLink` | A (bypass) | extend `ISquadLinkFileSystem` with `FileExists`/`DeleteFile`/`CreateDirectory`, or take `IWritableFileSystem` | M |
| `…:256,257,272,492-495` | do. | `Path.GetFullPath`, `OperatingSystem.IsWindows()` for comparison, `Directory.CreateDirectory` | A | `IFileSystemPathSemantics` | M |
| `…:280-324` | do. | reads/writes `links.json` with `File.Exists`, `File.ReadAllText`, `Guid.NewGuid()` temp, `File.Move`, delete-on-failure | A | `IAtomicFileWriter` + `ISquadFileAccess` | M |
| `…:338-397,411-440` | do. | main-home and instance-root enumeration via `new DirectoryInfo(...)` + `Path.GetRelativePath` | A | `IFileSystem.Enumerate*` | M |
| `…:466-467` | do. | `File.OpenRead` ×2 to compare bytes for split detection | A | `ISquadFileAccess` (bytes) | M |
| `…:24` | do. | static concrete `SquadFileSystemPathSemantics` | **B** | `IFileSystemPathSemantics` | M |
| `FileSquadInstanceLinkSync.cs:9-16` | `FileSquadInstanceLinkSync` | pure delegation, correctly passes the injected `ISquadLinkFileSystem` | D | — | S |
| `SquadInstanceLinkPlanner.cs:1-231` | `SquadInstanceLinkPlanner` | **the one genuinely pure planner in the folder** — documented "zero file I/O", takes listings as data | D | — | M |
| `UnixSquadLinkFileSystem.cs:14-219` | `UnixSquadLinkFileSystem` | physical adapter for links; 21 sites (`File.CreateSymbolicLink`, `FileInfo.LinkTarget`, attributes) | D (adapter) | keep; it is the fake target | M |
| `WindowsSquadLinkFileSystem.cs:31-406` | `WindowsSquadLinkFileSystem` | physical adapter; 29 sites including P/Invoke `GetFileInformationByHandle` | D (adapter) | keep | M |
| `SquadHarnessLauncher.cs:44-165` | `SquadHarnessLauncher` | static, but **correctly parameterised**: env accessor, PATH finder and `isWindows` are all passed in; builds a `ProcessStartInfo` (sealed, acceptable) | D | — | S |
| `ProcessSquadHarnessLauncher.cs:18-24` | `ProcessSquadHarnessLauncher` | delegates to the static builder (good) but `RunInteractive` calls `ProcessRunner.RunInteractive` statically | A (minor) | `IProcessExecutor` via `IBoundedProcessExecutor` (exists) | S |

---

## `Squad/Parsing`, `Packaging`, `Presets`, `Release`, `Rendering` (19 files, 164 sites)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `Parsing/SquadSourceLoader.cs:67,68,703-710,923-937,985-1107,1133-1183,1631-1664,1718-1777` | `SquadSourceLoader` | **static, 46 IO sites, 1,904 lines**: reads every manifest, agent, skill, profile and schema file from the real tree (`Directory.Exists`, `Directory.EnumerateFiles`, `File.ReadAllBytes`, link-chain resolution). Every Squad test therefore needs a real source tree on disk | **A** (+static) | `IFileSystem` + `IFileSystemPathProbe`; split the pure YAML/markdown parsing (Markdig/YamlDotNet) from the reading | L |
| `Parsing/SquadSourceLoader.cs:47` | do. | `public static readonly HashSet<string> ModelProfileFields` — mutable collection exposed publicly | A (static mutable data) | `IReadOnlySet` already exists for the sibling field | L |
| `Packaging/SquadPacker.cs:34-92,103-105,159-171,221-263` | `SquadPacker` | static; `Directory.CreateDirectory`, `Directory.GetFiles`, `File.ReadAllBytes`/`WriteAllText`, `Guid.NewGuid()` temp + `File.Move` | A | `IFileSystem` + `IAtomicFileWriter` | M |
| `Packaging/SquadCompanionSkills.cs:27-186` | `SquadCompanionSkills` | static; 21 sites: `DirectoryInfo.LinkTarget` probes, enumerations, `File.Exists` | A | `IFileSystem` + `IFileSystemPathProbe` | M |
| `Packaging/SquadPackSourceLocator.cs:24-37` | `SquadPackSourceLocator` | static; `Directory.Exists`, `File.Exists` ×2, `Path.GetFullPath` | A | `IFileSystem` | S |
| `Packaging/FileSquadPackager.cs:15-19` | `FileSquadPackager` | validates by calling the static loader | D (thin) | — | S |
| `Packaging/FileSquadPackager.cs:26-66` | do. | `Path.GetTempPath()` + `Guid.NewGuid()` scratch dir, `Directory.CreateDirectory`, recursive delete in `finally` | A | `IProcessEnvironment.TempDirectory` + `ISquadFileAccess` | S |
| `Packaging/FileSquadSourceLocator.cs:10` | `FileSquadSourceLocator` | pure delegation | D | — | S |
| `Presets/SquadCanonicalProfileStore.cs:52,80-82,120,411-428,481-530` | `SquadCanonicalProfileStore` | takes `ISquadUserPaths` (good) but does 16 raw IO sites: `File.Exists`, `File.ReadAllBytes`, `File.GetLastWriteTimeUtc`, `Guid.NewGuid()` temp + `File.Move` | A | `IFileSystem` + `IFileStatReader` + `IAtomicFileWriter` | L |
| `Presets/SquadModelPresetSelectionStore.cs:43,56-61,150-236` | `SquadModelPresetSelectionStore` | 17 sites incl. `File.Delete`, atomic write, `FileOptions.WriteThrough` | A | same | M |
| `Presets/SquadModelPresetStore.cs:23,32-76` | `SquadModelPresetStore` | 6 sites: enumerates and reads preset YAML | A | `IFileSystem` | S |
| `Presets/SquadModelPresetValidator.cs:28-30` | `SquadModelPresetValidator` | static; `File.Exists` + `File.ReadAllLines` to locate the offending line | A | `IFileSystem` | S |
| `Release/GitHubSquadReleaseSource.cs:49,51,76,78` | `GitHubSquadReleaseSource` | `new HttpClient(handler)` over an injected handler (good), `Environment.GetEnvironmentVariable` in the public ctor (edge, acceptable) | D | — | L |
| `…:114,505-752` | do. | 37 sites: staging dir from `Guid.NewGuid()`, `ZipArchive` extraction, `FileStream` writes, link/attribute checks, `Directory.Move` publish, cleanup | A | `ISquadFileAccess` + `IAtomicFileWriter` | L |
| `Release/LocalCheckoutSquadReleaseSource.cs:32-69` | `LocalCheckoutSquadReleaseSource` | `Directory.CreateDirectory`, `EnumerateFiles`, `File.Copy` | A | `ISquadFileAccess` | S |
| `Rendering/FileSquadRenderInputReader.cs:26,98-108` | `FileSquadRenderInputReader` | adapter that calls three statics (`SquadSourceLoader.Load`, `CopilotMcpServerProbe.HasServer`, `SquadOperatorChannelOverlayFile.ReadText`); one `Directory.Exists` | A (thin) | the statics' interfaces | S |
| `Rendering/CopilotMcpServerProbe.cs:36-80` | `CopilotMcpServerProbe` | static; `FileInfo.LinkTarget`, `ResolveLinkTarget(true)`, `FileStream`, `JsonDocument` | A | `IFileSystemPathProbe` + `IFileSystem` stream read | S |
| `Rendering/SquadOperatorChannelOverlayFile.cs:17-25` | `SquadOperatorChannelOverlayFile` | static; `Path.Combine` + `File.ReadAllText` | A | `IFileSystem` | S |

---

## `Hooks` (11 files, 103 sites)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `PhysicalFileSystem.cs:8-75` | `PhysicalFileSystem` | **the canonical physical adapter** (14 sites). Note it also holds policy: the `SkippedDirectories` set decides what a recursive enumerate hides, so an `IFileSystem` fake must agree with it | D (adapter, policy note) | — | S |
| `KernelProcessRunner.cs:31,36-51,78` | `KernelProcessRunner` | physical process adapter over the static Kernel `ProcessRunner`; 3 sites | D (adapter) | — | S |
| `YamlHooksConfigSource.cs:9-45` | `YamlHooksConfigSource` | **model example**: takes `IFileSystem` + `IProcessEnvironment`, zero raw IO | D | — | M |
| `ConventionTestClassResolver.cs:8-79` | `ConventionTestClassResolver` | takes `IFileSystem`; only `Path.GetFileName`/`GetDirectoryName` remain (pure string work) | D | — | S |
| `Arbiter/FileBackgroundDispatchStore.cs:30-99` | `FileBackgroundDispatchStore` | 21 sites: `Directory.CreateDirectory`, `Guid.NewGuid()` temp + `File.Move`, `Directory.EnumerateFiles`, `DateTime.UtcNow` for a prune interval | A | `IFileSystem` + `IAtomicFileWriter` + `TimeProvider` | M |
| `Arbiter/FileDispatchClaimStore.cs:38-130` | `FileDispatchClaimStore` | 22 sites: exclusive-create claim via temp+`File.Move(overwrite:false)`, busy-wait loop with `File.Exists`, `DateTime.UtcNow` stale cutoff | A | `IAtomicFileWriter` + `TimeProvider` | M |
| `Arbiter/FileHookSnapshotStore.cs:14-100` | `FileHookSnapshotStore` | 19 sites: `File.Open(FileShare.None)` as a lock, `Guid.NewGuid()` temp, `Directory.GetFiles` cleanup | A | `IFileSystem` + `ILockFile` + `IAtomicFileWriter` | M |
| `Arbiter/FileWorkerBudgetStore.cs:13-59` | `FileWorkerBudgetStore` | 11 sites: `File.ReadAllLines`, `Directory.EnumerateFiles`, `File.GetLastWriteTimeUtc` retention, `File.Delete` | A | `IFileSystem` + `IFileStatReader` | S |
| `Arbiter/FileFactoryTranscriptSource.cs:12` | `FileFactoryTranscriptSource` | takes `IFileStatReader` for length but reads lines with static `File.ReadLines` — bypasses its own seam | A (bypass) | `IFileSystem.ReadLines` (new member) | S |
| `Arbiter/FileHookFileWindowReader.cs:12-24` | `FileHookFileWindowReader` | `File.Exists`, `new FileInfo().Length`, `File.ReadLines` | A | `IFileSystem` + `IFileStatReader` | S |
| `Arbiter/FileLinkProbe.cs:15` | `FileLinkProbe` | `new DirectoryInfo/FileInfo` link-target probe | A | `IFileSystem.GetLinkTarget` (exists) | S |

---

## `Arbiter` (24 files, 141 sites)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `Facts/GitFacts.cs:12-13,307,450,459-505` | `GitFacts` | implements `IGitRepositoryFacts` but shells out via the **static** `ProcessRunner.Run` (554 lines of git-output parsing behind a real process). No `IProcessRunner`/`IProgramRunner` in sight | A (+static) | `IProgramRunner` (exists) | L |
| `Facts/GateReportFacts.cs:68-77,165-207,262,283-298` | `GateReportFacts` | 27 sites: `Path.Combine`, `File.Exists`, `File.GetLastWriteTimeUtc`, `Directory.EnumerateFiles`, `OperatingSystem.IsWindows` for path comparison | A | `IFileSystem` + `IFileStatReader` + `IFileSystemPathSemantics` | L |
| `Facts/FileTextFacts.cs:83,147-150,177-183` | `FileTextFacts` | `File.OpenRead`, `Path.GetFullPath`, `new FileInfo().Length` | A | `IFileSystem` stream read + `IFileStatReader` | M |
| `ClaudeHookCoverage.cs:116-180,198-241` | `ClaudeHookCoverage` | 18 sites: `Directory.Exists`, `Directory.EnumerateFiles`, `File.Exists`, `Path.Combine` over `.claude` | A | `IFileSystem` | L |
| `Plans/PlanFacts.cs:83,92,168-180` | `PlanFacts` | takes `IPlanFileResolver` (interface, good) but reads plans itself: `File.Exists`, `File.ReadAllText`, `Directory.EnumerateFiles`, `File.Exists` in a LINQ filter | A | `IFileSystem` | M |
| `Plans/PlanPathResolver.cs:66-69,94-147` | `PlanPathResolver` | `Path.GetFullPath`/`IsPathRooted`/`Combine`, link-chain walk with `FileSystemInfo` | A | `IFileSystemPathProbe` | M |
| `Plans/PlanRootResolver.cs:48,79,87,119-120,148-150` | `PlanRootResolver` | **good constructor** (`IGitRepositoryFacts`, `IPlanFileResolver`) but 13 raw sites: `Directory.Exists`, `File.GetLastWriteTimeUtc`, `File.Exists`, `File.ReadAllText`, `OperatingSystem.Is*` | A | `IFileSystem` + `IFileStatReader` | S |
| `Stores/ArbiterRunStore.cs:74-94,164-372` | `ArbiterRunStore` | **good clock seam** (`Func<DateTimeOffset>`); 11 raw IO sites for store paths, tail reads, `Directory.Exists`, `Path.GetFullPath` | A | `IFileSystem` + `IFileStatReader` | M |
| `Ledger/InFlightLedger.cs:22-100,265-318` | `InFlightLedger` + `ArbiterLogFiles` | ctor takes a directory string; static helper opens `Directory.CreateDirectory`, `File.Open(FileShare.None)` lock, append `FileStream` | A | `ISquadFileAccess`-style ledger store + `ILockFile` | M |
| `Ledger/DecisionLog.cs:27,37-40` | `DecisionLog` | ctor takes a directory string and derives `FilePath`/`LockFilePath` with `Path.Combine` | A | same ledger store | S |
| `FileArbiterStateFactory.cs:11,14,18` | `FileArbiterStateFactory` | **C ×3**: `new InFlightLedger`, `new DecisionLog`, `new ArbiterRunStore` inside the service | **C** | inject factories, or an `IArbiterStateFactory` returning already-built instances | S |
| `Providers/SystemOneClient.cs:56,116,155,368` | `SystemOneClient` | handler injected (good) but `DateTimeOffset.UtcNow` drives the retry deadline and `Retry-After` — retry logic is untestable without waiting | A | `TimeProvider` (already used elsewhere in the repo) | M |
| `SystemOneProviderFactory.cs:28` | `SystemOneProviderFactory` | `new HttpClientHandler()` + static `SystemOneClient.CreateForProduct` | **C** | `IArbiterHttpHandlers` (exists) | S |
| `ConfiguredArbiterProviderFactory.cs:72,83-84` | `ConfiguredArbiterProviderFactory` | passes `Environment.GetEnvironmentVariable` as the key resolver and `new HttpClientHandler()` as a fallback handler | A (edge) | `IProcessEnvironment.GetVariable` + `IArbiterHttpHandlers` | S |
| `Credentials/OperatingSystemCredentialStore.cs:12-16` | `OperatingSystemCredentialStore` | static factory doing `OperatingSystem.Is*` and `new` of four concrete stores + `new ProcessRunnerCredentialProcessRunner()` twice | **C** ×4 | build from DI; or inject `ICredentialStore` set | S |
| `Credentials/MacKeychainCredentialStore.cs:37-134` | do. | builds `ProcessStartInfo` and runs through injected `ICredentialProcessRunner` — **right pattern** for a sealed framework type | D | — | S |
| `Credentials/SecretServiceCredentialStore.cs:35-148` | do. | same pattern | D | — | S |
| `Credentials/WindowsCredentialStore.cs:27-118` | `WindowsCredentialStore` | P/Invoke `advapi32` with `OperatingSystem.IsWindows()` gates | D (native adapter) | — | S |
| `Credentials/ProcessRunnerCredentialProcessRunner.cs:1-17` | `ProcessRunnerCredentialProcessRunner` | physical adapter over `ICredentialProcessRunner` | D | — | S |
| `FileStatReader.cs:9,12` | `FileStatReader` | **is** the `IFileStatReader` implementation (`new FileInfo().Length`, `File.GetLastWriteTimeUtc`) — correctly isolated | D | — | S |
| `FileArbiterModelTargetsReader.cs:9-19` | `FileArbiterModelTargetsReader` | takes `IFileSystem`; only `Path.Combine` remains | D | — | S |
| `FileArbiterUserSettings.cs:10` | `FileArbiterUserSettings` | takes `IFileSystem` | D | — | S |
| `FileArbiterUserOverrideWriter.cs:8-15` | `FileArbiterUserOverrideWriter` | takes `IWritableFileSystem`; one `Path.GetDirectoryName` | D | — | S |
| `SocketArbiterHttpHandlers.cs:13` | `SocketArbiterHttpHandlers` | is the `HttpMessageHandler` factory | D | — | S |
| `ArbiterDataServiceCollectionExtensions.cs:1-47` | `ArbiterDataServiceCollectionExtensions` | composition root | D (C acceptable here) | — | S |

---

## `Review` (16 files, 107 sites)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `FileReviewCycleStore.cs:15-58,80` | `FileReviewCycleStore` | 25 sites: `Directory.EnumerateDirectories`, `File.Exists`, `Directory.CreateDirectory`, `File.WriteAllText`, `Path.GetFullPath` | A | `IFileSystem` + `IAtomicFileWriter` | S |
| `FileCoverageReportSource.cs:20-62` | `FileCoverageReportSource` | 11 sites: enumerates `artifacts/coverage` and `TestResults` for `*.xml`, `new FileInfo` | A | `IFileSystem` + `IFileStatReader` | S |
| `FileTestClassSources.cs:15-22,42,65,75-114` | `FileTestClassSources` | 14 sites, including `Directory.GetCurrentDirectory()` at :65 — **bypasses `IProcessEnvironment.CurrentDirectory`** | A (bypass) | `IFileSystem` + `IProcessEnvironment` | M |
| `FileNotSeatedStore.cs:21-49` | `FileNotSeatedStore` | `Directory.CreateDirectory`, `File.WriteAllText`, `File.ReadAllText`, `Directory.EnumerateDirectories` | A | `IFileSystem` | S |
| `FileRepositoryTreeProbe.cs:15-58` | `FileRepositoryTreeProbe` | 11 sites: existence probes, link-chain walk, `Path.GetFullPath` — this *is* the tree-probe adapter, so it is the fake target | D (adapter) | keep | S |
| `FileProjectFileLocator.cs:16-31` | `FileProjectFileLocator` | walks parent directories with `Directory.Exists`/`EnumerateFiles` | A | `IFileSystem` + `IProcessEnvironment` | S |
| `FileRepositoryFileLister.cs:19-107` | `FileRepositoryFileLister` | takes `IProgramRunner` for git (good) but enumerates the tree itself (`Directory.Exists`, `new FileInfo`) | A | `IFileSystem` | M |
| `FileReviewDecisionCacheStore.cs:16-36` | `FileReviewDecisionCacheStore` | `File.Exists`/`ReadAllText`/`CreateDirectory`/`WriteAllText` | A | `IFileSystem` + `IAtomicFileWriter` | S |
| `FileSeverityOverrideSource.cs:20-33` | `FileSeverityOverrideSource` | `Path.GetFullPath`, `File.Exists`, `File.ReadAllText` | A | `IFileSystem` | S |
| `FileCitedSourceFiles.cs:12-23` | `FileCitedSourceFiles` | `Path.GetFullPath`, containment check, `new FileInfo().Length`, `File.ReadLines` | A | `IFileSystem` + `IFileStatReader` | S |
| `FileReviewEvidenceStore.cs:9-16` | `FileReviewEvidenceStore` | `Directory.CreateDirectory`, stage+`File.Move` | A | `IAtomicFileWriter` | S |
| `FileBoundedFileReader.cs:15` | `FileBoundedFileReader` | `File.Exists` + delegated read | A (minor) | `IFileSystem` | S |
| `FileSeatFolder.cs:9-16` | `FileSeatFolder` | `Directory.Exists` + `new DirectoryInfo().GetFileSystemInfos()` | A | `IFileSystem` | S |
| `FileContainedFileReader.cs:8` | `FileContainedFileReader` | pure containment logic over `DocsRootPath` | D | — | S |
| `GitChangeReader.cs:9` | `GitChangeReader` | takes `IProgramRunner`; no raw IO | D | — | M |
| `ProcessProgramRunner.cs:8-35` | `ProcessProgramRunner` | is the physical `IProgramRunner` adapter over the static Kernel `ProcessRunner` | D | — | S |

---

## `Docs` (17 files, 101 sites)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `Analysis/Persistence/SqliteAnalysisPersistence.cs:52-67,344-406,605-629` | `SqliteAnalysisPersistence` | ctor takes a **string** repository root; creates the database dir, shells out to the `sqlite3` CLI via static `ProcessRunner.Run`, probes file/dir with `new FileInfo`/`DirectoryInfo` | **A** (+creates its own store) | `ISqliteCommand` (new) + `IProcessRunner` (exists) + `IFileSystem` | L |
| `Analysis/Persistence/AnalysisCacheSafety.cs:26-49,102-119` | `AnalysisCacheSafety` | static; reads `.gitignore` with `File.ReadLines`, runs `git` via static `ProcessRunner.Run` | A | `IFileSystem` + `IProgramRunner` | S |
| `Analysis/Embeddings/OpenAiCompatibleEmbeddingGenerator.cs:26-27,44,300-320` | `OpenAiCompatibleEmbeddingGenerator` | internal ctor injects handler, DNS resolver and env reader (**good**); public ctor wires real ones; loopback socket connect is inline | A (edge) / D | `TimeProvider` for timeout logic | M |
| `Parsing/DocumentLoader.cs:36-39,66-72,138-193,412` | `DocumentLoader` | ctor takes a **string** repo root; `Directory.Exists`, `Directory.EnumerateFiles`, `File.ReadAllText`, `File.ReadLines` | A | `IFileSystem` (would also make `DocumentLoaderFactory` honest) | L |
| `Parsing/DocumentLoaderFactory.cs:10` | `DocumentLoaderFactory` | `new DocumentLoader(repoRoot, ontology)` inside the factory | **C** | inject `Func<string, OntologyConfig, IDocumentSource>` | S |
| `Mcp/GitCheckoutProbe.cs:28-179` | `GitCheckoutProbe` | 20 sites: `Path.Combine`, `File.Exists`/`ReadAllText` over `.git`, `File.GetLastWriteTimeUtc`, and static `ProcessRunner.Run("git")` | A | `IFileSystem` + `IProgramRunner` | M |
| `Mcp/GitWorktreeIdentity.cs:18-49` | `GitWorktreeIdentity` | runs `git` through static `ProcessRunner.Run`, then `Path.GetFullPath` on its output | A | `IProgramRunner` | S |
| `Mcp/LaunchCommandResolver.cs:48-87,141-203` | `LaunchCommandResolver` | 11 sites: `Directory.GetCurrentDirectory()`, `Environment.GetEnvironmentVariable("PATH")`, `"PATHEXT"`, `File.Exists` while resolving a command | A | `IProcessEnvironment` + `IFileSystem` | M |
| `Mcp/PhysicalMcpConfigFiles.cs:29-72` | `PhysicalMcpConfigFiles` | physical adapter (`Directory.Exists`, `File.GetAttributes`, `File.ReadAllBytes`, delete) | D (adapter) | — | S |
| `FileSystem/PhysicalRepositoryFileStore.cs:12` | `PhysicalRepositoryFileStore` | field initialised with `new PhysicalFileSystem()` — **C**, and it means two adapters must be kept in sync | **C** | inject `IWritableFileSystem` | S |
| `FileSystem/PhysicalRepositoryFileStore.cs:46-101` | do. | physical adapter with 11 sites, including `File.OpenRead`, `File.Open(Append)`, `Directory.GetFiles`, `EnumerateFileSystemInfos`, `File.GetAttributes` — **these are exactly the members `IFileSystem` lacks** | D (adapter) | — | S |
| `FileSystem/KernelBoundedProcessExecutor.cs:11-12` | `KernelBoundedProcessExecutor` | physical adapter over the static Kernel runner | D | — | S |
| `Closeout/ExclusiveFileWriter.cs:11,53` | `ExclusiveFileWriter` | is the `IExclusiveFileWriter` primitive (the atomic-write seam that already exists and is under-used) | D | — | S |
| `Closeout/BoundedFile.cs:9-21` | `BoundedFile` | static bounded-read helper over `File.ReadAllText` | D (primitive) | — | S |
| `Closeout/PresentFile.cs:16-29` | `PresentFile` | static `ReadIfPresent` over `File.ReadAllText` | D (primitive) | — | S |
| `Export/DocGraphExporter.cs:19,39-59` | `DocGraphExporter` | takes `ICodeGraphResolver` (good) but writes with `Directory.CreateDirectory` + `File.WriteAllLines` ×2 | A | `IFileSystem.WriteAllLines` | S |
| `GraphClaimCandidateSource.cs:1-135` | `GraphClaimCandidateSource` | pure scoring over resolver results | D | — | M |
| `SparseLexicalCandidateSource.cs:1-119` | `SparseLexicalCandidateSource` | pure scoring | D | — | M |

---

## `Configuration`, `Doctor`, `FileSystem`, `Host`, `RunStart`, `Update`, `Utilities`, `Skills`, root (22 files, 154 sites)

| file:line | class | finding | cat | should depend on | size |
|---|---|---|---|---|---|
| `Configuration/FileKyberWeaveConfigRepository.cs:15-90` | `FileKyberWeaveConfigRepository` | takes `IRepositoryTreeProbe` (good) but reads the YAML itself: 30 sites of `Path.GetFullPath`, `File.Exists`, `File.ReadAllText` | A | `IFileSystem` | M |
| `Configuration/FileUserHostConfigStore.cs:18-86` | `FileUserHostConfigStore` | 13 sites: `Directory.CreateDirectory`, `File.Exists`/`ReadAllText`/`WriteAllText` | A | `IFileSystem` + `IAtomicFileWriter` | M |
| `Doctor/FileCodeGraphIndexWorkspace.cs:18-88` | `FileCodeGraphIndexWorkspace` | `DateTime.UtcNow` deadline loop waiting on a lock file, `Guid.NewGuid()` probe file, `Process.GetProcessById` to test whether a PID holds the lock | A | `TimeProvider` + `IFileSystem` + `IProcessProbe` (new) | S |
| `Doctor/FileGitHookFiles.cs:15-35` | `FileGitHookFiles` | adapter including `File.GetUnixFileMode`/`SetUnixFileMode` and `OperatingSystem.IsWindows` | D (adapter) | — | S |
| `Doctor/PhysicalDoctorFileSystem.cs:9-34` | `PhysicalDoctorFileSystem` | adapter (`ReadAllBytes`, `EnumerateFileSystemInfos`) | D (adapter) | — | S |
| `Doctor/ConfigFileToolSettings.cs:13-21` | `ConfigFileToolSettings` | takes `IKyberWeaveConfigRepository` | D | — | S |
| `Doctor/StdioMcpSessionFactory.cs:15-18` | `StdioMcpSessionFactory` | `private readonly StdioSession inner` (**B**) created with `new StdioSession(...)` (**C**) | **B + C** | `IMcpStdioSession` (exists) | S |
| `FileSystem/DocsRootPath.cs:90-201` | `DocsRootPath` | static; 7 sites of real-path containment and wildcard matching (`File.Exists`, `new DirectoryInfo`, `OperatingSystem.IsWindows`) — the *domain* half lives in `KyberWeave.Domain`, this is the IO half | A | should take `IFileSystemPathProbe`; today it is duplicated logic against the Domain copy | M |
| `Host/CheckoutProductRootLocator.cs:22-26` | `CheckoutProductRootLocator` | takes `IWritableFileSystem` (good); `Path.GetFullPath` on real paths remains | A (minor) | `IFileSystemPathSemantics` | S |
| `Host/ProcessEnvironment.cs:6-21` | `ProcessEnvironment` | **is** the `IProcessEnvironment` implementation — correctly isolated | D | — | S |
| `RunStart/RunCheckoutGuard.cs:35-217` | `RunCheckoutGuard` | takes `IWritableFileSystem` + `IRunStateStore` (good) but 23 sites: `Path.GetFullPath`/`GetDirectoryName`/`Combine`, `Path.IsPathRooted`, plus its own link-chain walk | A | `IFileSystemPathProbe` + `IFileSystemPathSemantics` | M |
| `RunStart/FileRunStateStore.cs:10-18` | `FileRunStateStore` | takes `IWritableFileSystem`; one `Path.GetDirectoryName` | D | — | S |
| `RunStart/GitCheckedOutBranchReader.cs:9` | `GitCheckedOutBranchReader` | takes `IGitCommandRunner` | D | — | S |
| `RunStart/ProcessGitCommandRunner.cs:14-38` | `ProcessGitCommandRunner` | physical adapter; `ProcessStartInfo` + static `ProcessRunner.Run` | D (adapter) | — | S |
| `Update/SelfUpdateSystem.cs:86,108,144,168-221,278` | `SelfUpdateSystem` | 32 sites: `Process.Start`/`Kill`, `Guid.NewGuid()` probe file, `FileStream` + `GZipStream` extraction, `xattr` process on macOS, `Directory.CreateDirectory`, `File.Move` | A | `IProcessRunner` + `ISquadFileAccess` + `IAtomicFileWriter` | M |
| `Update/GitHubReleaseClient.cs:44-67,170` | `GitHubReleaseClient` | handler + env reader injected (**good**); `File.Create` for the download | A (minor) | `ISquadFileAccess` | M |
| `Update/CopilotPromptRefresher.cs:23-46` | `CopilotPromptRefresher` | ctor takes a home directory string; `File.Exists`, `File.ReadAllText`, `File.GetAttributes` | A | `IFileSystem` | S |
| `Utilities/FileStatusLineFiles.cs:11-105` | `FileStatusLineFiles` | physical adapter, 19 sites incl. `ReadAllBytes`, `WriteAllBytes`, `IsDirectoryEmpty`, `EnumerateFileSystemInfos` — another place the missing `IFileSystem` members live | D (adapter) | — | S |
| `Utilities/StatusLineVersionProbeRunner.cs:1-18` | `StatusLineVersionProbeRunner` | static factory for the version probe | D | — | S |
| `Skills/ZipSkillArchiveWriter.cs:7-21` | `ZipSkillArchiveWriter` | `ISkillArchiveWriter` implementation; `ZipArchive` is sealed (acceptable) | D | — | S |
| `ServiceCollectionExtensions.cs:37-100` | `ServiceCollectionExtensions` | composition root — `new` of ~40 adapters is correct here | D (C acceptable) | — | S |
| `Squad/SquadServiceCollectionExtensions.cs:24-58` | `SquadServiceCollectionExtensions` | composition root; correctly registers `SquadStateStore` as both concrete and `ISquadStateStore` — but then injects the **concrete** into transactions (:38, :52) | **C** at the root (acceptable), **B** downstream | register only `ISquadStateStore` | M |
| `Squad/SquadServiceCollectionExtensions.cs:61-121` | `EnvironmentSquadUserPaths`, `EnvironmentSquadGlobalRoots` | read `HOME`/`USERPROFILE`/`APPDATA` from `IProcessEnvironment` but fall back to `Environment.GetFolderPath(...)` directly — bypasses the seam it was given | **A** (bypass) | `IProcessEnvironment` only | M |

---

## Per-project totals

Single project (`KyberWeave.Data`), so "per project" is broken out by folder. "Sites" are direct outside-resource
calls; "B/C" are counted per discrete finding.

| folder | files | resource sites | files with ≥1 site | B | C |
|---|---|---|---|---|---|
| `Squad/` (Deployment, Instances, Packaging, Parsing, Presets, Release, Rendering) | 45 | **716** | 38 | 9 | 2 |
| `Arbiter/` | 24 | 141 | 20 | 0 | 7 |
| `Review/` | 16 | 107 | 14 | 0 | 0 |
| `Hooks/` | 11 | 103 | 10 | 0 | 0 |
| `Docs/` | 17 | 101 | 13 | 1 | 2 |
| `Configuration/` | 2 | 43 | 2 | 0 | 0 |
| `Update/` | 3 | 38 | 3 | 0 | 0 |
| `Doctor/` | 5 | 29 | 3 | 1 | 1 |
| `RunStart/` | 4 | 25 | 3 | 0 | 0 |
| `Utilities/` | 2 | 19 | 1 | 0 | 0 |
| `Host/` | 2 | 10 | 2 | 0 | 0 |
| `CodeGraph/` | 2 | 8 | 1 | 1 | 1 |
| `FileSystem/` | 1 | 7 | 1 | 0 | 0 |
| `Skills/`, root | 3 | 0 | 0 | 0 | 0 |
| **total** | **137** | **1,347** | **111** | **12** | **13** |

**Category totals**

* **A — outside resource used directly:** 1,347 sites / 111 files. Of these, ~145 sites in 19 files belong to
  deliberate physical adapters (category D by design); **~1,200 sites in 92 files are logic classes that cannot be
  unit-tested**.
* **B — concrete service dependency:** 12 rows — `SquadTransaction`(:43), `SquadStateStore`(:15 field),
  `SquadDeploymentPlanner`(:13 field), `SquadStateMigration`(:23 field and :60 ctor),
  `SquadTransactionFactory`(:74), `SquadInstanceLinkSync`(:24 field), `SquadResponseGuidanceDeployer`(:53 field),
  `SquadModelPresetSelectionStore`(:20 field), `CodeGraphPortsFactory`(:12 local),
  `PhysicalRepositoryFileStore`(:12 field), `StdioMcpSessionFactory`(:15 field). Nine of the twelve are the same
  mistake: a `static readonly SquadFileSystemPathSemantics`, or a store injected as its own class.
* **C — `new` of a service inside a service:** 13 rows / 15 constructor calls — `FileArbiterStateFactory` ×3,
  `OperatingSystemCredentialStore` ×3 rows (5 constructors), `SystemOneProviderFactory` ×1,
  `SquadDeploymentAdapters` ×2, `DocumentLoaderFactory` ×1, `PhysicalRepositoryFileStore` ×1,
  `StdioMcpSessionFactory` ×1, `CodeGraphPortsFactory` ×1. The ~40 `new` calls at the two composition roots are
  correct and excluded.
* **D — acceptable:** 32 pure-data/pure-logic types and ~20 physical adapters (listed in the rows above).
* **Static classes that can never take a seam: 32.** They hold the bulk of the untestable Squad logic.

**Five classes with the most findings**

| rank | class | file | findings | why it matters |
|---|---|---|---|---|
| 1 | `SquadTransaction` | `Squad/Deployment/SquadTransaction.cs` | 117 A + 1 B + 2 static-state | the deployment commit/rollback engine; every test needs a real temp tree and a real named mutex |
| 2 | `SquadDeploymentPlanner` | `Squad/Deployment/SquadDeploymentPlanner.cs` | 79 A + 1 B (static) | install/update/uninstall preflight; reads the whole target tree twice |
| 3 | `SquadStateMigration` | `Squad/Deployment/SquadStateMigration.cs` | 49 A + 2 B | legacy-state migration; reaches into `SquadTransaction`'s static lease table |
| 4 | `SquadStateStore` | `Squad/Deployment/SquadStateStore.cs` | 48 A + 1 B | receipt/lock/registry; path resolution mixed with IO |
| 5 | `SquadSourceLoader` | `Squad/Parsing/SquadSourceLoader.cs` | 46 A (static) | reads and validates every Squad source file; 1,904 lines |

Next: `SquadInstanceLinkSync` 39, `GitHubSquadReleaseSource` 37, `SelfUpdateSystem` 32,
`FileKyberWeaveConfigRepository` 30, `WindowsSquadLinkFileSystem` 29, `GateReportFacts` 27,
`FileReviewCycleStore` 25, `SquadFileSystemPathSemantics` 24, `SquadPacker` 24, `RunCheckoutGuard` 23.

## Existing interfaces and how widely they are used

`KyberWeave.Contracts/Repositories` is already a substantial port set (~110 interfaces). Usage in this project:

| interface | implemented here (physical adapters) | injected into (consumers) | verdict |
|---|---|---|---|
| `IFileSystem` | `PhysicalFileSystem` | 5 classes (`FileArbiterModelTargetsReader`, `FileArbiterUserSettings`, `ConventionTestClassResolver`, `YamlHooksConfigSource`, and via `IWritableFileSystem`) | **far too thin, far too rarely used.** 5 of ~90 IO classes. |
| `IWritableFileSystem` | `PhysicalFileSystem` | 6 classes (`CheckoutProductRootLocator`, `RunCheckoutGuard`, `FileRunStateStore`, `FileArbiterUserOverrideWriter`, `PhysicalRepositoryFileStore` via `new`, `ServiceCollectionExtensions`) | used well where used; the only *complete* write seam |
| `IRepositoryFileStore` | `PhysicalRepositoryFileStore` | used by the engine (not this project) | the richest file port in the repo — a good template |
| `ISquadFileAccess` | `PhysicalSquadFileAccess` | 3 classes (`CheckoutOrSnapshotCanonicalProfileResolver`, `EnvironmentSquadGlobalRoots`, Squad lifecycle in the engine) | **closest existing match to what Squad needs**, but missing bytes-write, move, link creation, tree enumeration and temp-dir |
| `ISquadLinkFileSystem` | `UnixSquadLinkFileSystem`, `WindowsSquadLinkFileSystem` | `SquadInstanceLinkSync` (and then bypassed at 8 of its call sites) | covers links only; delete/exists/read/move still go around it |
| `IProcessRunner` | `KernelProcessRunner` | **only** `KernelProcessRunner`'s own registration; no consumer in this project | `GitFacts`, `GitCheckoutProbe`, `SqliteAnalysisPersistence`, `AnalysisCacheSafety` all shell out via the static Kernel `ProcessRunner` instead |
| `IProgramRunner` | `ProcessProgramRunner` | 3 classes (`GitChangeReader`, `FileRepositoryFileLister`, CLI gates) | correctly used where adopted |
| `IProcessEnvironment` | `ProcessEnvironment` | 4 classes (`YamlHooksConfigSource`, `EnvironmentSquadUserPaths`, `EnvironmentSquadGlobalRoots`, DI) | bypassed by `FileTestClassSources.GetCurrentDirectory`, `LaunchCommandResolver`, and the `Environment.GetFolderPath` fallbacks |
| `IFileStatReader` | `FileStatReader` | 2 classes (`FileFactoryTranscriptSource`, squad worker budget) | under-used: `FileInfo.Length`/`GetLastWriteTimeUtc` appear in 12 other classes |
| `IFileSystemPathSemantics` | `SquadFileSystemPathSemantics` | 6 classes, all via the **static** `Semantics` field | registered as an interface but its implementation does real IO, so faking it proves nothing |
| `IExclusiveFileWriter` | `ExclusiveFileWriter` | used by engine closeout only | would absorb ~10 hand-rolled stage+rename sites |
| `TimeProvider` (BCL) | n/a | `SquadDeploymentPlanner` only | the right pattern; 8 `DateTimeOffset.UtcNow` sites should use it |
| `ISquadStateStore`, `ISquadTransactionFactory`, `ISquadDeploymentPlanner`, `ISquadSourceLoader`, `ISquadPackager`, `ISquadStateMigration`, `ISquadSkillShadowing`, `ISquadHookJsonFile`, `ISquadTargetResolver`, `ISquadHarnessLauncher`, `ISquadReleaseSource`, `IZCodeMcpInspector`, `IDevinImportOverlapInspector`, `ISquadFactoryHooksShadowing`, `ISquadInstanceLinkSync`, `ISquadModelPreset*`, `ISquadCanonicalProfileStore`, `ISquadRenderInputReader`, `ISquadResponseGuidance*` | the `FileSquad*` adapters | injected widely across the engine | **the right shape, wrong depth**: almost every adapter is a one-line `=>` to a static class that does the IO |

**Classes already fully unit-testable with no real I/O** (fakes only): `SquadInstanceLinkPlanner`,
`GraphClaimCandidateSource`, `SparseLexicalCandidateSource`, `FileContainedFileReader`, `DocGraphExporter` (one write),
`CheckoutOrSnapshotCanonicalProfileResolver`, `YamlHooksConfigSource`, `ConventionTestClassResolver`,
`FileRunStateStore`, `RunCheckoutGuard` (mostly), `GitChangeReader`, `ConfigFileToolSettings`,
`FileArbiterModelTargetsReader`, `FileArbiterUserSettings`, `FileArbiterUserOverrideWriter`, plus the ~20 DTO/record
types. That is roughly **20 of 118 non-static types**.

## New interfaces this project needs

| proposed interface | members it must cover | call sites it would replace |
|---|---|---|
| **`IFileSystemPathProbe`** (new) | `bool FileExists/DirectoryExists/PathExists(string)`; `FileAttributes GetAttributes(string)`; `bool IsReparsePoint(string)`; `string? GetLinkTarget(string)`; `string? ResolveFinalLinkTarget(string)`; `long GetFileLength(string)`; `DateTime GetLastWriteTimeUtc(string)`; `IEnumerable<DirectoryEntryInfo> EnumerateEntries(string dir)` where `DirectoryEntryInfo = (Name, FullPath, IsDirectory, IsLink, Length, LastWriteUtc)`; `bool IsDirectoryEmpty(string)`; `string? GetParent(string)`/path helpers kept pure | ~180 sites: `SquadFileSystemPathSemantics` (24), `SquadTransaction` link/attr reads (14), `RunCheckoutGuard` (15), `PlanPathResolver`, `FileRepositoryTreeProbe`, `CopilotMcpServerProbe`, `FileLinkProbe`, `DocsRootPath`, `SquadWorktreeMcp`, `FileReviewCycleStore` |
| **`ISquadFileAccess` — extended** (exists; add members) | add `byte[] ReadAllBytes` (already), `void WriteAllBytes`, `void WriteAllText`, `IEnumerable<string> ReadLines`, `Stream OpenRead`, `void DeleteFile`, `void MoveNoOverwrite(from,to)`, `void CreateDirectory`, `void DeleteTreeNoFollow`, `void CreateFileLink/FolderLink`, `IEnumerable<string> EnumerateFiles(dir, pattern, recursive)`, `IEnumerable<EntryInfo> EnumerateTree(dir)`, `string NewScratchDirectoryPath(prefix)` | ~700 sites: the whole `Squad/Deployment` state layer (`SquadTransaction` 117, `SquadStateStore` 48, `SquadStateMigration` 49), plus `SquadSourceLoader` 46, `SquadInstanceLinkSync` 39, presets ~40, release sources ~46 |
| **`IAtomicFileWriter`** (new) | `void WriteAtomic(string path, ReadOnlySpan<byte>)`; `void WriteAtomic(string path, string text)`; `bool TryCreateExclusive(string path, ReadOnlySpan<byte>)`; `void MoveAtomic(src,dst)`; sync-to-storage option instead of a static `SyncToDisk` flag; no `Guid.NewGuid()` visible to callers | ~14 hand-rolled stage+rename sites: `SquadTransaction.PublishFileAtomic`/`RestoreEntry`, `SquadStateStore.WriteRegistry`, `SquadStateMigration.WriteAtomically`, `SquadResponseGuidanceDeployer.WriteShippedDefault`, `SquadCanonicalProfileStore.Write`, `SquadModelPresetSelectionStore.Write`, `FileHookSnapshotStore`, `FileBackgroundDispatchStore`, `FileDispatchClaimStore`, `SquadInstanceLinkSync.WriteLinks`, `SquadPacker`, `GitHubSquadReleaseSource`, `SelfUpdateSystem`, `FileReviewEvidenceStore` |
| **`ILockFile` / `ITransactionLease`** (new) | `IDisposable Acquire(string key, TimeSpan timeout)`; `IDisposable AcquireFileLock(string path)`; no `new Mutex` in business code; testable in-process implementation | `SquadTransaction.AcquireLease` (named `Mutex`), `TransactionLease.HoldJournal` (`FileStream`), `SquadInstanceLinkSync` (`Mutex`), `ArbiterLogFiles`/`FileHookSnapshotStore`/`FileDispatchClaimStore` (`File.Open(FileShare.None)` locks) |
| **`IArbiterLogStore`** (new) | `Append(path, line)`, `ReadTail(path, bytes)`, `ReadComplete(path)` | `ArbiterLogFiles` + `DecisionLog` + `InFlightLedger` + `ArbiterRunStore` (11 sites), all keyed on a directory string |
| **`ISqliteCommand`** (new) | `ProcessResult RunScript(string sql)`; `bool IsAvailable()` | `SqliteAnalysisPersistence` (:344-406) — the only reason that class needs a process |
| **`IProcessProbe`** (new) | `bool IsProcessAlive(int pid)` | `FileCodeGraphIndexWorkspace:88` (`Process.GetProcessById`) |
| **`TimeProvider`** (BCL, exists; inject it) | `GetUtcNow()`, `CreateTimer` | `SystemOneClient:116,155,368`, `FileBackgroundDispatchStore:94`, `FileDispatchClaimStore:118,130`, `FileCodeGraphIndexWorkspace:18,37`, `ArbiterRunStore` (already has `Func<DateTimeOffset>`) |
| **`IProcessRunner` / `IProgramRunner`** (exist; inject them) | unchanged | `GitFacts` (5), `GitCheckoutProbe` (2), `SqliteAnalysisPersistence` (2), `AnalysisCacheSafety` (1) — all use the static Kernel `ProcessRunner.Run` |
| **`IProcessEnvironment`** (exists; inject it) | unchanged | `FileTestClassSources:65`, `LaunchCommandResolver:68,78,85,203`, `EnvironmentSquadUserPaths:114-121`, `PhysicalSquadFileAccess:130,152` |
| **`IFileStatReader`** (exists; use it) | unchanged | `SquadWorkerBaselineFile`, `FileTextFacts:183`, `GateReportFacts:204-207`, `PlanRootResolver:79`, `FileCitedSourceFiles:16`, `FileCoverageReportSource:28`, `FileWorkerBudgetStore:57` |
| **`ISquadStateStore`** (exists; use it instead of the class) | unchanged | `SquadTransaction:43`, `SquadStateMigration:60`, `SquadTransactionFactory:74`, `SquadServiceCollectionExtensions:38,52` |
| **`IFileSystemPathSemantics`** (exists; fix the implementation) | unchanged signature; must take `IFileSystemPathProbe` | makes the 6 static `Semantics` fields (B findings) injectable and fakeable |

**The one structural change that matters more than any interface:** turn the 32 static classes into instance
classes registered against the `ISquad*`/`IFile*` ports that already exist. `SquadInstanceLinkPlanner` and
`SquadDeploymentPlanner`'s `TimeProvider` parameter show the target shape; `SquadInstanceLinkSync` and
`SquadSourceLoader` show what has to move.

---

## Coverage statement

**Every one of the 137 `.cs` files in `src/KyberWeave.Data` appears in a table above, so no folder was skipped.**
Depth caveats, stated honestly:

* For files under 400 lines I read the source in full (about 105 files).
* For the 22 files over 400 lines (11,700 lines total, including `SquadTransaction` 3,372, `SquadSourceLoader` 1,904,
  `SquadStateStore` 1,785, `SquadDeploymentPlanner` 1,341, `GitHubSquadReleaseSource` 784,
  `SqliteAnalysisPersistence` 724, `SquadStateMigration` 618, `GitFacts` 554, `SquadCanonicalProfileStore` 533,
  `SquadInstanceLinkSync` 507, `DocumentLoader` 472, `InFlightLedger` 412, `WindowsSquadLinkFileSystem` 406,
  `ClaudeHookCoverage` 401) I read the class/ctor/method outline plus **every** resource-using line, mapped to its
  owning method; I did not read their pure parsing/validation bodies line by line. A resource call inside a
  multi-line expression that my regex could not see (for example a `File.` call whose receiver is on the previous
  line) would be missed — I did not find any such spelling in the files I read in full.
* No edit, build, test, or commit was made. Nothing in the checkout was modified.

**Recommended next step, in priority order:** (1) `IFileSystemPathProbe` + fix `SquadFileSystemPathSemantics`;
(2) extend `ISquadFileAccess` and `IAtomicFileWriter`; (3) convert the Squad statics to instances taking those ports;
(4) replace the three `SquadStateStore` constructor dependencies with `ISquadStateStore`; (5) swap the 8 clock reads
and 12 process calls onto the `TimeProvider`, `IProcessRunner` and `IProgramRunner` ports that already exist. Steps
1–3 alone remove roughly 900 of the ~1,200 non-adapter resource sites.
