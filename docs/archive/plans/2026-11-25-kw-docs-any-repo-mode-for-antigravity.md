---
id: plans/2026-11-25-kw-docs-any-repo-mode-for-antigravity
title: kw-docs any-repo mode, and Squad wiring it for Antigravity
doc-type: plan
status: archived
component: DocGraph
owner: dpalfery
last-reviewed: 2026-11-25
development-mode: test-first
documentation-impact: extends-component
impact-components:
  - DocGraph
  - KyberSquad
---

# kw-docs any-repo mode, and Squad wiring it for Antigravity

**Status: Archived, 2026-11-25.**

Autonomy: autonomous. Development mode: test-first.

## Request

> Feature (approved by David): kw-docs "any-repo" mode so Antigravity (agy) and other harnesses that spawn MCP servers globally get kw-docs tools.
>
> Facts verified on David's Mac (2026-11-25, rc.31):
> - Antigravity reads ~/.gemini/config/mcp_config.json (global) and <workspace>/.agents/mcp_config.json; it spawns every MCP server once from its hub process (language_server) with cwd "/". PATH includes ~/.local/bin.
> - Squad's AntigravityRenderer writes per-agent `mcpServers: [{name: kw-docs, command: kyber-weave-mcp, args: [--repo-root, .]}]` into ~/.gemini/config/agents/<name>/agent.md. Started from "/", `kyber-weave-mcp --repo-root .` exits with KW-MCP-ROOT-001 (no host config at "/"), so agy sessions have no kw-docs tools. docs/docgraph/mcp-runbook.md#antigravity and .apm/skills/kw-onboarding/references/mcp-and-index.md prescribe the same broken entry.
> - Squad writes no harness MCP config by design (ADR 0052 decision 2: user scope never written). David approved changing this for agy.
>
> Approved design:
> 1. src/KyberWeave.Mcp: an explicit opt-in flag (e.g. --any-repo) that starts the server with no bound root. Every tool then requires projectPath naming a directory inside an initialized Kyber-Weave repo (host config present); calls without it get a clear refusal. Reuse DocsRootRegistry's LRU of per-root DocsTools (no reinit per call); capacity 4 total roots; cache path->root lookup to avoid a git spawn per call; build a new root's tools outside the registry lock so other roots are not blocked. Never guess a root; RepositoryRootResolver rules stay for bound mode. Server instructions must tell agents projectPath is required in this mode.
> 2. Squad agy: AntigravityRenderer emits the any-repo entry with the absolute kyber-weave-mcp path (agent files); squad install AND update (global scope) insert a `kw-docs` entry into ~/.gemini/config/mcp_config.json, insertion-only, preserving every other server byte-for-byte, refusing malformed files, idempotent, never echoing file contents (that file can hold secrets). In-memory unit tests through ISquadFileAccess/InMemorySquadFileAccess. Hook where FactoryMcpConfiguration.Warnings runs in SquadLifecycleService (install and update paths).
> 3. Amend ADR 0052 (or new ADR) for the agy user-scope write; update docs/kyber-squad/architecture.md, docs/docgraph/mcp-runbook.md, onboarding table.
> 4. Check other harnesses that spawn MCP globally (Warp, Copilot CLI global, Codex, Pi, ZCode, Kilo) for the same `--repo-root .` breakage; fix or raise each with a recommendation.
>
> Constraints: stay out of dash/ and SelfUpdater.cs. Worktree from fresh origin/main under the owner's usual worktree folder outside the repository. Branch/PR name includes [claude]. Gates in AGENTS.md must pass. PR draft->ready, Kilo review loop, never merge. Local agy config was already hand-fixed by Antigravity with a pinned --repo-root; don't touch ~/.gemini without David's yes.

## Problem and goal

**Problem.** The docs MCP server (`kyber-weave-mcp`, server key `kw-docs`) must be bound to one repository at
startup: `--repo-root`, then `KYBER_WEAVE_REPO_ROOT`, then the working directory, and that directory must hold a
host configuration (`.kyber-weave/kyber-weave.yml`). Antigravity starts every MCP server once, from its hub
process, with working directory `/`. The entry Squad renders into Antigravity agent files
(`--repo-root .`) therefore resolves to `/`, fails with `KW-MCP-ROOT-001`, and Antigravity sessions get no
kw-docs tools at all. A pinned absolute root works for one repository only.

**Goal.**

1. `kyber-weave-mcp --any-repo` starts with no bound root and serves any initialized Kyber-Weave repository the
   caller names per call with `projectPath`; a call without it is refused with a message that says so.
2. Squad renders that entry into Antigravity agent files and, on a global `squad install` or `squad update`,
   inserts the same `kw-docs` entry into Antigravity's user MCP config (`<agy global root>/mcp_config.json`)
   without touching anything else in that file.
3. The decision records, the Squad architecture, the MCP runbook and the onboarding table describe the new mode,
   and every other harness whose user-scope entry has the same `--repo-root .` problem is fixed in the runbook
   or raised in the pull request notes.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| 1. Opt-in flag (`--any-repo`) starting the server with no bound root | T1; D1, D2 | `McpServeModeTests` parse cases; `McpCompositionRootTests` any-repo container builds with no root |
| 1. Every tool requires `projectPath` inside an initialized repo; clear refusal without it | T1; D3, D4 | `McpCompositionRootTests` calls each tool without `projectPath` and gets the refusal; `DocsRootRegistryTests` refuses an uninitialized repo |
| 1. Reuse DocsRootRegistry's LRU of per-root DocsTools, no reinit per call | T1; D5 | `DocsRootRegistryTests`: two calls for the same root build tools once |
| 1. Capacity 4 total roots | T1; D5 | `DocsRootRegistryTests`: a fifth root evicts the least recently used; `Count` never exceeds 4 |
| 1. Cache path->root lookup to avoid a git spawn per call | T1; D6 | `DocsRootRegistryTests`: the scripted git double counts one `TryRoots` call for repeated calls on one path |
| 1. Build a new root's tools outside the registry lock | T1; D7 | `DocsRootRegistryTests`: a factory blocked on one root does not block a call for a cached root |
| 1. Never guess a root; RepositoryRootResolver rules stay for bound mode | T1; D3, D8 | `DocsRootRegistryTests` refuses a non-git directory; existing `RepositoryRootResolverTests` stay green, unchanged |
| 1. Server instructions say `projectPath` is required in this mode | T1; D9 | `McpCompositionRootTests` reads `ServerInstructions` of the any-repo server |
| 2. AntigravityRenderer emits the any-repo entry with the absolute `kyber-weave-mcp` path (agent files) | T3; D10, D11 | `AntigravityRendererContractTests` global render carries the absolute command and `[--any-repo]` |
| 2. Install AND update (global) insert `kw-docs` into `~/.gemini/config/mcp_config.json` | T2; D12, D13 | `AntigravityMcpConfigurationTests` lifecycle cases for install and update |
| 2. Insertion-only, every other server byte-for-byte | T2; D14 | `AntigravityMcpConfigurationTests`: bytes outside the inserted span equal the original |
| 2. Refuse malformed files | T2, T5; D15, D29 | `AntigravityMcpConfigurationTests`: invalid JSON, non-object root, non-object `mcpServers`, oversize file each leave the file unchanged and warn |
| 2. Idempotent | T2; D16 | `AntigravityMcpConfigurationTests`: second run writes nothing |
| 2. Never echo file contents | T2; D17 | `AntigravityMcpConfigurationTests`: a secret marker in the file never appears in any warning |
| 2. In-memory unit tests through ISquadFileAccess / InMemorySquadFileAccess | T2, T4, T5; D21, D28, D29, D30 | Unit tests run over `InMemorySquadFileAccess` only; the write member (T4) and the size probe (T5) are added to the seam with a contract suite, the fake follows a file link as the seam documents (T5), and the real-disk mode and link proof run in CI |
| 2. Hook where `FactoryMcpConfiguration.Warnings` runs (install and update) | T2; D13 | Lifecycle install and update tests see the entry inserted |
| 3. Amend ADR 0052 or a new ADR for the agy user-scope write | docs-dev closeout; D18 | New ADR plus a dated amendment line in ADR 0052 |
| 3. Update `docs/kyber-squad/architecture.md`, `docs/docgraph/mcp-runbook.md`, onboarding table | docs-dev closeout | Design harvest rows 2 to 6 |
| 4. Check Warp, Copilot CLI global, Codex, Pi, ZCode, Kilo; fix or raise each | Investigation findings; docs-dev closeout; Out-of-scope `PR note:` lines; D19 | Runbook recommends `--any-repo` for each user-scope entry; live spawn-directory probes raised as PR notes |
| Stay out of `dash/` and `SelfUpdater.cs` | Out-of-scope boundaries | No task names a file there |
| Don't touch `~/.gemini` without David's yes | Out-of-scope boundaries; D20 | Every test uses the in-memory file seam; no task runs `squad install --global` on a real home |
| Worktree, branch name, gates, PR draft->ready, Kilo loop, never merge | Conductor delivery; Verification gates; PR note | Branch `claude/feat/kw-docs-any-repo-mode-for-antigravity` from `origin/main` `b86a6b472`. The run worktree is under the repository's `.agent_worktrees/`, not the owner's usual worktree folder as asked: raised as a PR note, not changed by this plan. |

## Scale

`documentation-impact: extends-component`, components **DocGraph** (the MCP server's binding modes) and
**KyberSquad** (the Antigravity renderer and the first Squad write into a harness's user MCP config).

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Flag name and how it combines with other root inputs | `--any-repo`. Combined with `--repo-root` or `--expect-root` it refuses to start with new rule `KW-MCP-ROOT-003` on stderr, exit 1. In any-repo mode `KYBER_WEAVE_REPO_ROOT` and `KYBER_WEAVE_EXPECT_ROOT` are ignored. | The request names `--any-repo` as the example. A conflict refused at startup is clearer than one silently winning; an ambient environment variable must not turn an explicitly unbound server into a bound one. `KW-MCP-ROOT-003` is unused (searched `src` and `docs`). | autonomous: recommendation accepted |
| D2 | Where the argv parsing lives | A new `McpServeMode` type in `src/KyberWeave.Mcp/McpServeMode.cs` with a static `Parse(IReadOnlyList<string> args)`; `Program.cs` only branches on its result. | Keeps `Program.cs` a composition root and makes the parse unit-testable, the way `RepositoryRootResolver.HasExpectRootArgument` is. | autonomous: recommendation accepted |
| D3 | What "a directory inside an initialized repo" means | `projectPath` → full path (against the process directory) → must be an existing directory → git top level of that directory (`IGitWorktreeIdentity.TryRoots`) → that top level must have a host config (`IKyberWeaveConfigRepository.FindConfigPath` not null). Any failure is a refusal naming the step; no walking up past the git top level, no fallback root. | Mirrors bound mode's projectPath rules minus the same-repository check, which has no server root to compare to. "Never guess a root" is the request's words. | autonomous: recommendation accepted |
| D4 | Refusal text and provenance with no bound root | Every tool called without `projectPath` returns `provenance: root=(none) mode=any-repo` then `projectPath is required: this server runs in any-repo mode. Pass the absolute path of a directory inside the repository you are working in.` Refusals after a `projectPath` use the same provenance line plus `projectPath was refused: <reason>`. | Same two-line shape bound-mode refusals use (`Provenance()` + reason), so agents parse one shape. | autonomous: recommendation accepted |
| D5 | Capacity and LRU in any-repo mode | One `DocsRootRegistry`, built by a new `DocsRootRegistry.ForAnyRepo(...)`; in any-repo mode the server has no own root, so `Capacity` (4) is the total number of roots held. Bound mode keeps 4 roots besides the server root. An evicted entry is only unlinked from the registry: nothing is disposed, because neither `DocsTools` nor anything it holds (`DocumentIndexHost`, `CodeGraphFreshness`, the analysis reader) implements `IDisposable` (searched `src/KyberWeave.Mcp`: the only `Dispose` is the stream transport's). A call already holding the evicted `DocsTools` finishes on it; the next call for that root builds a new one. If a later change makes the tools disposable, the registry must not dispose an entry a call may still hold. | Reuses the existing linked-list LRU; no second cache type. | autonomous: recommendation accepted |
| D6 | The path-to-root cache | A dictionary from the full `projectPath` to its git top level, filled only on success, capped at 64 entries and cleared when full. The directory-exists check still runs every call. Applies in both modes (same `TryResolve` code). | Removes the git spawn on every call for the same path. 64 covers many worktrees; clear-on-full is simpler than a second LRU and the cost of a miss is one git spawn. A removed-and-recreated directory at the same path is the only stale case, and it resolves to the same top level. | autonomous: low-confidence, no authoritative source (cheap to reverse, local to one class) |
| D7 | Building a root's tools outside the lock | Look up under the lock; on a miss, release the lock, call the factory, re-take the lock; if another call inserted the same root meanwhile, use that entry and drop the one just built (nothing to dispose, D5); otherwise insert at the front and evict past capacity. Applies to both modes. | Request instruction. First-inserted wins, so two callers of one root always share one `DocsTools`. | autonomous: recommendation accepted |
| D8 | Bound mode | Unchanged except D6 and D7: `RepositoryRootResolver`, its precedence, `KW-MCP-ROOT-001`/`-002`, and the same-repository projectPath rule stay as they are. | Request: "RepositoryRootResolver rules stay for bound mode". | autonomous: recommendation accepted |
| D9 | Server instructions in any-repo mode | `DocsServerInstructions.Configure(options, anyRepo)` appends one paragraph in any-repo mode: every tool requires `projectPath`, the absolute path of a directory inside the repository being worked on; omitting it returns a refusal. Bound-mode text is unchanged. | Request instruction. | autonomous: recommendation accepted |
| D10 | How the renderer gets the absolute `kyber-weave-mcp` path | `SquadRenderRequest` gains an optional trailing `string? DocsServerCommand = null`. `SquadLifecycleService` gains an optional trailing constructor parameter `Func<string, string?>? findExecutable = null`, wired in `SquadServiceCollectionExtensions` to `IProcessRunner.FindOnPath`; for a Global or Instance render it sets `DocsServerCommand = findExecutable("kyber-weave-mcp")`. The path is used as `PATH` names it, symlinks not resolved, so a self-update that swaps the link target keeps working. | Keeps the renderer pure and the PATH lookup behind the existing process seam (AGENTS.md seam rule). Optional trailing parameters follow the existing `environmentReader` pattern, so the 190+ request call sites compile unchanged. | autonomous: low-confidence, no authoritative source (public record gains an optional member; additive) |
| D11 | Absolute path in project-scope agent files too? | No. Global (and Instance) renders use the absolute path; a project render, whose `.agents/agents/*` files may be committed, emits bare `kyber-weave-mcp`. Both emit `args: [--any-repo]`. When the global lookup finds nothing, the render falls back to bare `kyber-weave-mcp` and the lifecycle adds one warning naming the missing executable. | The request's instruction is about the agent files under `~/.gemini/config/agents`; a machine path in a committed file breaks every other clone (the delivery rule already forbids committing machine-specific paths). The verified agy PATH includes `~/.local/bin`, so the bare name works there. | autonomous: recommendation accepted (conflict with the literal instruction recorded here, user intent kept for the files named) |
| D12 | Which file Squad writes, and the args | `Path.Combine(ISquadGlobalRootResolver.ResolveGlobalRoot(SquadTarget.Antigravity), "mcp_config.json")` — `~/.gemini/config/mcp_config.json` by default, the override environment variable honoured. Entry: `"kw-docs": {"command": "<DocsServerCommand or kyber-weave-mcp>", "args": ["--any-repo"]}`. | Antigravity reads its global hooks from the same root (ADR 0054 rendering), so the resolver already knows it; tests redirect it to the in-memory home. | autonomous: recommendation accepted |
| D13 | Where and when the write happens | New `AntigravityMcpConfiguration.Ensure(...)` called directly after `FactoryMcpConfiguration.Warnings` in both `SquadLifecycleService` install and update paths, only when the scope is Global and the targets include Antigravity. On a dry run it reports what it would add and writes nothing. Instance and Project scope write nothing. | Request names the hook point. The insertion is harmless if the deployment transaction later fails: the entry points at the machine-level executable, which the transaction does not own. | autonomous: recommendation accepted |
| D14 | How "insertion-only, byte-for-byte" is done | Reuse `JsonServerSplice.Plan(document, ["mcpServers"], "kw-docs", entryJson)` (`src/KyberWeave.Application/Docs/Mcp/JsonServerSplice.cs`), the byte splice `docs mcp-wire` already uses: it locates the container's opening brace, inserts one indented member (or the absent `mcpServers` key), keeps every other byte and the BOM, and refuses rather than overwrite an existing entry. A missing file is planned against `{}`. `entryJson` is built with `System.Text.Json` so the command is escaped. | The splice is proven by `JsonServerSpliceTests` and the wiring hardening tests; a second splicer would duplicate it (change-scope: follow existing patterns). | autonomous: recommendation accepted |
| D15 | What counts as malformed, and what happens | Refuse with one warning and no write when: the file is over 1 MiB, or the splice returns `MalformedDocument`, `ForeignDocument` (root or `mcpServers` not an object) or `UnsafeSplice`. Comments and trailing commas are accepted, as `JsonServerSplice` reads leniently and still inserts only bytes of its own. | Same 1 MiB bound `CopilotMcpServerProbe` uses. A file agy already loads is not malformed for this purpose; refusing it would block a working setup for no safety gain. | autonomous: recommendation accepted |
| D16 | An existing `kw-docs` entry | Never changed. If its `args` do not contain `--any-repo`, one warning says the existing entry is left as is, binds the server to one root, and how to switch to `kyber-weave-mcp --any-repo`. | Insertion-only and idempotent. David's hand-pinned local entry is therefore never overwritten. | autonomous: recommendation accepted |
| D17 | What a warning may contain | The file path and a fixed reason text chosen from the splice outcome. Never file bytes, a property value, `JsonSplicePlan.Message`, or an exception message: a `System.Text.Json` reader message can quote the offending character. | The file can hold API keys for other servers. | autonomous: recommendation accepted |
| D18 | New ADR or amend ADR 0052 | New ADR (next free number, 0094 at plan time): "Squad inserts the kw-docs entry into Antigravity's user MCP config". ADR 0052 gains a dated amendment line in its Status pointing to it, stating decision 2 is narrowed by that ADR for Squad's Antigravity insertion only; ADR 0052's docs-branch writers stay unchanged. | ADR 0052 governs the docs branch's writers; this write is Squad's, so a separate record keeps 0052's closed list intact. docs-dev writes both at closeout. | autonomous: recommendation accepted |
| D19 | Other harnesses (request item 4) | Squad writes no MCP config for Warp, Copilot CLI, Codex, Pi, ZCode or Kilo; their entries are the runbook's recipes. Fix in docs: every user-scope (global) recipe in the runbook and onboarding table becomes `kyber-weave-mcp --any-repo`, which does not depend on the spawn directory; project-scope recipes keep `--repo-root .`, except Antigravity's workspace `.agents/mcp_config.json`, which agy also spawns from `/`, so it becomes `--any-repo` too. Raise as PR notes: the live spawn directory of each harness's global servers, which this run cannot probe. | `--any-repo` is correct whatever directory a harness starts the server in, so the docs fix does not depend on the unprobed fact. | autonomous: recommendation accepted |
| D20 | Touching the real `~/.gemini` | No task runs a real global install. All behaviour is proven in memory; the real-disk adapter is proven in the integration project, against its scratch folder, in CI. | Request constraint. | autonomous: recommendation accepted |
| D21 | `ISquadFileAccess` has no write method | Add `void WriteAllBytesAtomic(string path, byte[] contents)`. `AtomicFile` (`src/KyberWeave.Kernel/FileSystem/AtomicFile.cs`) gains a third public overload `public static void WriteAllBytes(string path, byte[] content, bool writeThroughSymlink, bool flushToDisk, bool preserveUnixFileMode)`, all three parameters required so no call is ambiguous. The private `Write(string path, bool writeThroughSymlink, Action<string> writeTemporary)` gains a trailing `bool preserveUnixFileMode = false`; when true it calls a new private helper `CopyUnixFileMode(string destination, string temporaryPath)` after `writeTemporary` and before `File.Move`. The helper reads the mode of `destination` after link resolution (the file the link names, not the link) and sets it on the temporary; it does nothing on Windows (`OperatingSystem.IsWindows()`) or when `destination` does not exist. The existing overloads `(path, content, bool writeThroughSymlink = false)` and `(path, content, bool, bool flushToDisk)` keep their signatures and bodies and pass `false`, so their callers (`PhysicalFileSystem`, `PhysicalMcpConfigFiles`, `PhysicalRepositoryFileStore`) are unchanged. `PhysicalSquadFileAccess.WriteAllBytesAtomic` calls `AtomicFile.WriteAllBytes(path, contents, writeThroughSymlink: true, flushToDisk: false, preserveUnixFileMode: true)`. The in-memory fake writes through `InMemoryFileSystem`. | Request names `ISquadFileAccess` as the seam, so it is widened rather than switching to `IMcpConfigFiles`. `AtomicFile` already gives atomic rename and link write-through, but its temporary takes the default mode, so a `0600` secrets file would become `0644`; a new overload keeps every other caller unchanged, and a five-argument overload cannot collide with the two existing ones. | autonomous: recommendation accepted |
| D28 | Where the file-mode and link proof runs | `AtomicFile` is the Kernel file-system adapter and calls the static `File` API; there is no seam under it, so the mode branch cannot run in memory. Its proof is two CI-only classes in `tests/KyberWeave.Integration.Tests`: `AtomicFileUnixModeTests` (the new overload with `preserveUnixFileMode: true` keeps a `0600` target at `0600`, also through a symlink; with `false` and through the two existing overloads the target takes the default mode as today; a new file gets the default mode; skipped on Windows) and `PhysicalSquadFileAccessWriteTests` (the adapter keeps `0600` and keeps a symlink as a symlink whose target gets the bytes). They are not in the T2 task gate. Review checks the pull request's CI integration job passed for both before approving. | Host rule, `AGENTS.md` "Three kinds of test" and "Integration tests live in `tests/KyberWeave.Integration.Tests`": a unit test touches no disk, and task gates and the council run unit and content projects only, never the integration project. The plan reviewer's alternative (a temp-file unit test, or the integration class in the gate) breaks that rule. | autonomous: recommendation accepted |
| D22 | `products/kyber-squad/mcp.json` | Unchanged. The Antigravity renderer replaces the `kw-docs` args with `[--any-repo]` (and the command per D11) because the spawn-from-`/` behaviour is a harness fact, like the entry shape it already pins. | Other consumers of `mcp.json` (Factory's server-name check, worktree MCP setup) read the declared entry; changing it would alter their behaviour, which the request does not ask for. | autonomous: recommendation accepted |
| D24 | How `DocsTools` is built with no root | `DocsTools` gains a second public constructor `DocsTools(DocsRootRegistry roots)` for unbound mode. The seven root-bound fields (`_query`, `_planLifecycle`, `_delivery`, `_toolingCheck`, `_prompts`, `_hostConfiguration`, `_repositoryRoot`) move into one private sealed record `BoundRoot`, held in a nullable `_bound` and read only through a private `Bound` property that returns it or throws `InvalidOperationException("an any-repo server reached a root-bound path")`; no `!` operator on it anywhere. Rule: in unbound mode `Redirect` never returns null (an empty `projectPath` is the D4 refusal, a refusal is the refusal text, and any-repo `TryResolve` success always yields non-null tools), so every tool body after the `Redirect` line runs only in bound mode; the existing ~29 uses become `Bound.Query` and so on. A new private `ProvenanceLine()` returns `Bound.Query.Provenance()` in bound mode and `provenance: root=(none) mode=any-repo` in unbound mode; `Redirect` and the "projectPath is required" refusals in the partials use it. `Program.cs` passes `DocsServerInstructions.Configure` as a method group today, so with the new `bool anyRepo` parameter it becomes a lambda `options => DocsServerInstructions.Configure(options, anyRepo)`. `AddAnyRepoDocsHostServices` registers the registry and none of the root-bound services. The SDK builds the type with `ActivatorUtilities`, which takes the longest constructor whose parameters the container can satisfy: the eight-parameter one in bound mode, the one-parameter one in any-repo mode. | Null-object services for six interfaces would be more code and could answer a call silently. `McpCompositionRootTests` proves both containers pick the intended constructor by calling a tool in each. | autonomous: low-confidence, no authoritative source (constructor choice relies on `ActivatorUtilities` resolution; proven by test) |
| D25 | Other render callers | Amended in review round 1: `SquadDoctorCommand` resolves the kw-docs command through `SquadDocsServerCommand.Resolve` (its constructor gains an optional trailing `IProcessRunner` parameter for the PATH lookup) and passes `DocsServerCommand` into its global render. Doctor's unmanaged-collision scan compares bytes, not only paths, so a render with different command text would flag a correct install as drift. The two `SquadLifecycleService` sites and doctor are the three render callers. | Read `SquadDoctorCommand.cs` around its `RenderAsync` call; `grep` of `RenderAsync(` outside the renderers lists three sites. | autonomous: recommendation accepted; review round 1 keeps the doctor change |
| D26 | Machine-specific rendered text | `ISquadGlobalRootResolver.HomeDirectory`'s doc says a render writes `~/…` so rendered text does not vary by user; the absolute command in global Antigravity agent files breaks that. The request wins: Antigravity does not expand `~` in `command`, and the request asks for the absolute path. | Global files are per machine already; project files stay machine-free (D11). | autonomous: recommendation accepted (conflict with a documented convention recorded) |
| D27 | `docs mcp-wire` catalog | No change. The repair catalog has no Antigravity row (searched `src/KyberWeave.Application/Docs/Mcp` and `src/KyberWeave.Domain/Docs`), so no code path writes the broken entry; only the runbook recipe does, which closeout fixes. | — | autonomous: recommendation accepted |
| D29 | T2 must refuse a file over 1 MiB before reading it, but `ISquadFileAccess` has no size probe (T2 worker stop, 2026-11-25) | Add `long GetLength(string path)` to `ISquadFileAccess` in a new task T5 before T2: the length in bytes of the file at `path`, a symbolic link followed, `FileNotFoundException` when it is missing. `PhysicalSquadFileAccess` returns `new FileInfo(path).Length`, as `PhysicalFileSystem.GetLength` does; `InMemorySquadFileAccess` forwards to `InMemoryFileSystem.GetLength`; `FileSet`, both `NoFiles` and `StubFiles` throw `NotSupportedException`. D15 stays as written: T2 calls `GetLength` before `ReadAllBytes`. | The alternative (check size after reading) loads an arbitrarily large file into memory once; the probe is one member following the pattern T4 just used to add `WriteAllBytesAtomic`, and every implementer is in this repository, so the compiler finds them all. | autonomous: recommendation accepted |
| D30 | The in-memory fake replaces a symlink with a regular file on `WriteAllBytesAtomic` (T4 report) | `InMemoryFileSystem` stays unchanged: its atomic write models `IWritableFileSystem`, whose physical adapter calls `AtomicFile` without link write-through, so link replacement is the right model there. `InMemorySquadFileAccess.WriteAllBytesAtomic` (in T5) writes to `files.GetFinalLinkTarget(path) ?? path`, so the fake keeps the link as `ISquadFileAccess` documents; a T5 unit test proves it on the fake. T2 adds no link case; the real-disk link proof stays in `PhysicalSquadFileAccessWriteTests` in CI (D28). | The fake disagreed with the seam contract T4 wrote, in a file T4 changed, so this run fixes it (own what you touch). Changing `InMemoryFileSystem` would change every other test that uses its atomic write. | autonomous: recommendation accepted |
| D23 | Rating of T1 and T2 | T1 `hard`: the registry's concurrent build-outside-lock and the changed tool-routing contract fail only under timing. T2 `hard`: a wrong splice silently loses another server's bytes or quotes a secret in a warning. T4 `hard`: a wrong atomic write silently widens a secret file's permissions or replaces a symlink, and its proof runs only in CI. T3 `easy`. T5 `easy`: one additive seam member following T4's pattern, plus a link lookup in a fake; an ordinary test fails if either is wrong. | Difficulty rubric: silent data or access damage no ordinary test shows. | autonomous: recommendation accepted |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute — autonomous: recommendation accepted (David approved the any-repo design in chat 2026-11-25), 2026-11-25 |

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| DocGraph | runbook | `docs/docgraph/mcp-runbook.md` — Resolving the repository root; Naming your worktree with `projectPath` | Any-repo mode: the flag, D1 conflicts and `KW-MCP-ROOT-003`, D3 root rules, D4 refusal, D5 capacity, D6 cache, D9 instructions |
| DocGraph | runbook | `docs/docgraph/mcp-runbook.md` — Antigravity; Warp; Codex CLI; Pi; ZCode; Kilo; Copilot CLI user config | Antigravity's spawn-from-`/` fact, the rendered any-repo entry, the global `mcp_config.json` insertion; every user-scope recipe becomes `--any-repo` (D19) |
| KyberSquad | architecture | `docs/kyber-squad/architecture.md` §3 (Squad does not own settings files) | The one exception: insertion-only `kw-docs` entry in Antigravity's user MCP config on global install and update (D12 to D17) |
| KyberSquad | adr | New ADR 0094; ADR 0052 Status amendment | D18 |
| KyberSquad | skill reference | `.apm/skills/kw-onboarding/references/mcp-and-index.md` onboarding table | Antigravity and user-scope rows use `--any-repo` |
| KyberSquad | architecture | `docs/kyber-squad/architecture.md` Antigravity user MCP config | `SquadDocsServerCommand` is the one resolver for install, update and `squad doctor`, and holds the shared `Executable` and `AnyRepoArgument` constants (D25) |
| DocGraph | reference | `src/KyberWeave.Mcp/AGENTS.md`, `docs/reference/seams.md` | Any-repo composition path; `ISquadFileAccess.WriteAllBytesAtomic` and `ISquadFileAccess.GetLength` (D29) in the seam register; the `AtomicFile` `preserveUnixFileMode` overload (D21) |

## Investigation findings

- Discovery read revision `b86a6b472` on `claude/feat/kw-docs-any-repo-mode-for-antigravity` (codegraph and kw-docs provenance matched the worktree).
- `Program.cs` resolves one root through `RepositoryRootResolver.Resolve` and prints `KW-MCP-ROOT-001` on failure; `AddDocsHostServices(repoRoot, ...)` registers every root-bound service; the SDK builds `DocsTools` from the container on each call (comment in `DocsToolsComposition.AddDocsHostServices`), so in any-repo mode the container must still satisfy `DocsTools`' constructor or offer one it can satisfy.
- `DocsTools.Redirect` returns null for an empty `projectPath` (the call answers for the server root) and refuses when `_roots` is null. The plan-lifecycle, delivery and tooling-check tools already require `projectPath`.
- `DocsRootRegistry.TryResolve` spawns git on every call (`_git.TryRoots`) and calls the factory inside `lock (_gate)`; `Capacity = 4` non-server roots.
- `AntigravityRenderer.ResolveMcpServer` copies `command`/`args` verbatim from `products/kyber-squad/mcp.json` (`kyber-weave-mcp --repo-root .`); the same renderer also emits `codegraph serve --mcp --path .`, which has the same spawn-from-`/` problem (PR note below).
- `ISquadFileAccess` has read probes only; implementers are `PhysicalSquadFileAccess` (`src/KyberWeave.Data/Squad/Deployment/SquadDeploymentAdapters.cs`), `InMemorySquadFileAccess` (`tests/KyberWeave.Testing/Squad/InMemorySquadFileAccess.cs`) and the test doubles `FileSet`, `NoFiles`, `StubFiles`.
- `SquadLifecycleService` builds `SquadRenderRequest` at two sites (install and update) and calls `FactoryMcpConfiguration.Warnings` at two sites, each right before the dry-run return.
- Runbook user-scope recipes using `--repo-root .` or a pinned path: Kilo (`kilo.jsonc`), Pi (`~/.config/mcp/mcp.json`), ZCode user tier, Codex (`~/.codex/config.toml`, pinned path), Warp (configured in Warp), Copilot CLI (`~/.copilot/mcp-config.json`, listed in `docs/install.md`).
- Reusable code already in the tree: `JsonServerSplice.Plan` (byte splice, BOM-preserving, never overwrites an existing entry, lenient read) and `AtomicFile.WriteAllBytes` (temp sibling + rename, optional write-through of a symlink). `AtomicFile` does not preserve the target's Unix mode, which D21 adds as an opt-in.
- Tests that pin `--repo-root .` for kw-docs (`McpPackagingTests`, `RequiredMcpServerGuardTests`, `SquadSourceTests`, `McpDeclarationSquadTree`) pin `products/kyber-squad/mcp.json`, which D22 leaves unchanged; only `AntigravityRendererContractTests` pins the rendered Antigravity entry.
- Test-contract runner cells escape `|` as `\|`, as archived plans do; the plan table reader (`DocumentSectionTable`) unescapes it.
- Plan-review revision: `AtomicFile` has two public `WriteAllBytes` overloads (lines 67 and 78) and no unit test class; nothing under `src/KyberWeave.Mcp` implements `IDisposable` except the stream transport; `DocsTools` reads `_query.Provenance()` after `Redirect` at lines 375 and 398; `Program.cs` line 106 passes `DocsServerInstructions.Configure` as a method group; `AGENTS.md` keeps integration tests out of task gates.
- T2 stop revision (run branch `12b869e31`, T4 integrated): `ISquadFileAccess` has `WriteAllBytesAtomic` but no length probe; `InMemoryFileSystem.GetLength` (line 188) and `GetFinalLinkTarget` (line 244) exist; `InMemoryFileSystem.WriteAtomic` (line 957) removes a link node and writes a plain file; seam implementers outside `src` are `InMemorySquadFileAccess`, `FileSet` (`FactoryMcpConfigurationTests`), and doubles in `SquadInstanceServiceTests`, `SquadInstanceRemoveCommandTests` and `SquadUpdateCommandTests`. Discovery for this revision: codegraph 1, docs 0, direct reads 3 (`InMemoryFileSystem.cs` 180-330 and grep of `WriteAtomic`, `SquadFileAccessWriteContract.cs` 14-63: test-support detail to place T5's cases; grep for `: ISquadFileAccess` implementers).
- Discovery: codegraph 11, docs 2 more (5 total), direct reads 15 (the three added: `DocsRootRegistry.cs` 76-171 and `DocsTools.cs` 360-455, ranges codegraph trimmed as gaps; `AGENTS.md` test rules, not indexed code; plus the earlier Program.cs, DocsTools.cs sections, SquadLifecycleService sections, AntigravityRenderer sections, ISquadFileAccess, plan index, ADR 0052, runbook sections, mcp.json: files or line ranges codegraph trimmed as gaps, or non-code files it does not index).

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/KyberWeave.Mcp.Tests/McpServeModeTests.cs` (new, `KyberWeave.Mcp.Tests.McpServeModeTests`); `tests/KyberWeave.Mcp.Tests/DocsRootRegistryTests.cs` (extend `KyberWeave.Tests.DocsRootRegistryTests`, reuse its `ScriptedGit`); `tests/KyberWeave.Mcp.Tests/McpCompositionRootTests.cs` (extend `KyberWeave.Mcp.Tests.McpCompositionRootTests`) | `dotnet test tests/KyberWeave.Mcp.Tests/KyberWeave.Mcp.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Mcp.Tests.McpServeModeTests\|FullyQualifiedName~KyberWeave.Tests.DocsRootRegistryTests\|FullyQualifiedName~KyberWeave.Mcp.Tests.McpCompositionRootTests"` | Parse: no flag → bound; `--any-repo` → any-repo; `--any-repo` with `--repo-root` or `--expect-root` → conflict naming `KW-MCP-ROOT-003`. Registry any-repo: a path inside an initialized repo resolves to its top level; a non-directory, a non-git directory, and a git repo without host config are each refused with their reason; repeated calls on one path call git once; two paths in one repo share one `DocsTools`; a fifth root evicts the least recently used and `Count` stays 4; a factory blocked on root A does not block a cached root B; two concurrent first calls for one root end with one shared instance; a call holding root A's tools while four other roots are resolved (evicting A) still completes on A's tools, and the next call for A builds a new instance (factory count for A is 2). Bound mode: existing cases unchanged. Composition: the any-repo container builds, every tool called without `projectPath` returns the D4 refusal, a call with a valid `projectPath` answers with that root's provenance, and `ServerInstructions` contains the D9 paragraph; the bound container's instructions do not. | `McpServeMode`, `DocsRootRegistry.ForAnyRepo` and the any-repo composition do not exist (compile failure in the three classes), or the new assertions fail | All named classes pass, count non-zero, no existing assertion weakened |
| T2 | `tests/KyberWeave.Application.Tests/Squad/AntigravityMcpConfigurationTests.cs` (new, `KyberWeave.Application.Tests.Squad.AntigravityMcpConfigurationTests`; copy the setup of `KyberWeave.Application.Tests.Squad.FactoryMcpConfigurationDeploymentTests`) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Application.Tests.Squad.AntigravityMcpConfigurationTests\|FullyQualifiedName~KyberWeave.Application.Tests.Squad.FactoryMcpConfigurationDeploymentTests"` | Missing file → created with only the `kw-docs` entry; directory missing → created. File with other servers → the entry is inserted and every byte outside the inserted span is identical (BOM case too), asserted on raw bytes as `before[..offset] + inserted + before[offset..]`, never by reparsing; root object with no `mcpServers` → key inserted; empty `mcpServers` → no stray comma. Existing `kw-docs` with `--any-repo` → no write, no warning; existing `kw-docs` without it → no write, one warning. A file with comments or a trailing comma → entry inserted, other bytes identical; a commented, BOM-prefixed file that already has `kw-docs` → no write, no exception. Invalid JSON, non-object root, non-object `mcpServers`, over 1 MiB (with no `ReadAllBytes` call on it) → no write, one warning each. A read or write that throws `IOException` or `UnauthorizedAccessException` → no write, one fixed-text warning. A secret marker string in the file never appears in any warning. Dry run → no write, one "would add" note. Second run → no write. Lifecycle: global install and global update with Antigravity insert the entry; project scope, instance scope and a target list without Antigravity write nothing. The command written is the resolved absolute path when given, `kyber-weave-mcp` otherwise. | `AntigravityMcpConfiguration` does not exist (compile failure), or the assertions fail | Both unit classes pass with non-zero count |
| T4 | `tests/KyberWeave.Application.Tests/Squad/InMemorySquadFileAccessWriteContractTests.cs` (new, `KyberWeave.Application.Tests.Squad.InMemorySquadFileAccessWriteContractTests`, over the new `SquadFileAccessWriteContract` in `KyberWeave.Testing`); `tests/KyberWeave.Integration.Tests/PhysicalSquadFileAccessWriteTests.cs` and `tests/KyberWeave.Integration.Tests/AtomicFileUnixModeTests.cs` (new, CI only, D28) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Application.Tests.Squad.InMemorySquadFileAccessWriteContractTests"` | Contract over the fake: a write creates a missing parent directory and file; a second write replaces the whole content; bytes read back equal bytes written. Integration (CI only): `PhysicalSquadFileAccessWriteTests` runs the same contract on disk, and a `0600` file keeps its mode and a symlinked file stays a symlink whose target gets the bytes; `AtomicFileUnixModeTests` — `preserveUnixFileMode: true` keeps a `0600` target at `0600`, also through a symlink; `false` and the two existing overloads behave as today; a new file gets the default mode. | `ISquadFileAccess.WriteAllBytesAtomic` and `SquadFileAccessWriteContract` do not exist (compile failure) | The unit class passes with non-zero count; both integration classes compile (they run in CI only, by the host rule in D28) |
| T5 | `tests/KyberWeave.Testing/Squad/SquadFileAccessWriteContract.cs` (extend, runs via `KyberWeave.Application.Tests.Squad.InMemorySquadFileAccessWriteContractTests`); `tests/KyberWeave.Application.Tests/Squad/InMemorySquadFileAccessWriteContractTests.cs` (extend with fake-only link cases); `tests/KyberWeave.Integration.Tests/PhysicalSquadFileAccessWriteTests.cs` (extend, CI only, D28) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Application.Tests.Squad.InMemorySquadFileAccessWriteContractTests"` | Contract: `GetLength` after a write returns the number of bytes written; `GetLength` on a missing file throws `FileNotFoundException`. Fake only: a write to a file link leaves the link a link (`GetLinkTarget` unchanged) and the target holds the bytes; `GetLength` on the link returns the target's length. Integration (CI only): the inherited contract cases run on disk, and `GetLength` on a symlink returns the target's length (skipped on Windows). | `ISquadFileAccess.GetLength` does not exist (compile failure), or the fake link case fails against T4's fake | The unit class passes with non-zero count, T4's cases unchanged; the integration project builds |
| T3 | `tests/KyberWeave.Application.Tests/AntigravityRendererContractTests.cs` (extend `KyberWeave.Tests.AntigravityRendererContractTests`); `tests/KyberWeave.Application.Tests/Squad/ArbiterMcpGrantRenderingTests.cs` (extend `KyberWeave.Tests.ArbiterMcpGrantRenderingTests` where it pins the kw-docs args) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KyberWeave.Tests.AntigravityRendererContractTests\|FullyQualifiedName~KyberWeave.Tests.ArbiterMcpGrantRenderingTests\|FullyQualifiedName~KyberWeave.Application.Tests.Squad.AntigravityMcpConfigurationTests"` | Global render with `DocsServerCommand` set → kw-docs entry `command: <that path>`, `args: [--any-repo]`; global render without it → `command: kyber-weave-mcp`; project render → `command: kyber-weave-mcp`, `args: [--any-repo]` even when a command is supplied; the codegraph entry is unchanged; a withheld agent still has no `mcpServers`. Lifecycle: a global install passes the resolved path into the render and into the config entry; an unresolved path adds one warning. | Assertions on `--any-repo` and the absolute command fail against the current `--repo-root .` output | All named classes pass, count non-zero |

An existing test keeps its assertions; a worker may update an assertion that pins the old `--repo-root .` Antigravity entry only, because that output is what this plan changes, and names each such edit in its report.

## Tasks

### T1: kw-docs any-repo mode

- **Objective:** `kyber-weave-mcp --any-repo` serves any initialized repository named per call by `projectPath`, and refuses calls without it.
- **Capabilities:** C# MCP server composition, concurrency-safe caching, xUnit unit tests over fakes
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `mcp-any-repo`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Mcp/McpServeMode.cs` (new): `McpServeMode` record and static `Parse(IReadOnlyList<string> args)`: `--any-repo` sets any-repo mode; with `--repo-root` or `--expect-root` it is a conflict.
  - `src/KyberWeave.Mcp/Program.cs`: top-level statements — branch on `McpServeMode.Parse` before `RepositoryRootResolver`; conflict → `KW-MCP-ROOT-003: <message>` on stderr, return 1; any-repo → ignore `KYBER_WEAVE_REPO_ROOT` and `KYBER_WEAVE_EXPECT_ROOT`, skip root resolution, register the any-repo services; bound path unchanged. `.AddMcpServer(DocsServerInstructions.Configure)` becomes `.AddMcpServer(options => DocsServerInstructions.Configure(options, anyRepo))` (D24).
  - `src/KyberWeave.Mcp/DocsToolsComposition.cs`: new `AddAnyRepoDocsHostServices(this IServiceCollection services, bool codeGraphRefreshEnabled)` registering only `DocsRootRegistry.ForAnyRepo(...)`, no root-bound service.
  - `src/KyberWeave.Mcp/DocsTools.cs`: second public constructor `DocsTools(DocsRootRegistry roots)`; the seven root-bound fields move into a private `BoundRoot` record read only through the guarded `Bound` accessor, never with `!`, and provenance goes through `ProvenanceLine()` (D24). Every tool body keeps its `Redirect` line first; in unbound mode `Redirect` never returns null. In unbound mode `Redirect` answers an empty `projectPath` with `provenance: root=(none) mode=any-repo` and `projectPath is required: this server runs in any-repo mode. Pass the absolute path of a directory inside the repository you are working in.`; a registry refusal uses the same provenance line plus `projectPath was refused: <reason>`. Bound behaviour unchanged. `ActivatorUtilities` picks the longest satisfiable constructor; prove both containers pick the intended one.
  - `src/KyberWeave.Mcp/DocsTools.PlanLifecycle.cs`, `src/KyberWeave.Mcp/DocsTools.Delivery.cs`, `src/KyberWeave.Mcp/DocsTools.ToolingCheck.cs`: their "projectPath is required" refusals use `ProvenanceLine()` (they call `_query.Provenance()` today), and their other uses of the bound fields go through `Bound`.
  - `src/KyberWeave.Mcp/DocsRootRegistry.cs`: `ForAnyRepo(bool codeGraphRefreshEnabled, IServiceProvider? services)`. Any-repo `TryResolve`: full path → existing directory → git top level (`IGitWorktreeIdentity.TryRoots`) → `IKyberWeaveConfigRepository.FindConfigPath(topLevel)` not null; each failure a refusal naming the step; no walking up, no fallback. `Capacity` (4) counts every root held. Both modes: a path→top-level dictionary (success only, 64 entries, cleared when full) so a repeated path spawns no git.
  - `src/KyberWeave.Mcp/DocsServerInstructions.cs`: `Configure(McpServerOptions options, bool anyRepo)`; any-repo appends one paragraph saying every tool requires `projectPath`, the absolute path of a directory inside the repository being worked on.
  - Tests: `tests/KyberWeave.Mcp.Tests/McpServeModeTests.cs` (new), `tests/KyberWeave.Mcp.Tests/DocsRootRegistryTests.cs`, `tests/KyberWeave.Mcp.Tests/McpCompositionRootTests.cs`.
- **Design (advanced):** failure mode — two calls for a new root race and build two `DocsTools` (two CodeGraph refresh schedules for one checkout), or a slow first build of one root stalls every other root.
  - Look up the root under `_gate`; on a miss leave the lock before calling the factory.
  - After the factory returns, re-take `_gate`; if the root is now present use the stored entry and drop the new one; otherwise add it at the front and evict past `Capacity`.
  - Neither a dropped duplicate nor an evicted entry is disposed: nothing in `DocsTools` is `IDisposable` (D5). An evicted entry is only unlinked, so a call already holding it finishes on it; the test that evicts a root during an in-flight call proves it.
  - Any-repo `TryResolve` returns true only with non-null tools, which is what lets `Redirect` never return null in unbound mode (D24).
  - The path cache is read and written under `_gate` too; git is never called while holding it.
  - A factory exception leaves no entry and is returned as a refusal for that call only.
- **Commands:**
  - `kyber-weave review gates . --agent <agent> --task T1 --red`
  - `kyber-weave review gates . --agent <agent> --task T1`
- **Acceptance:**
  - Every T1 Test-contract case passes; `RepositoryRootResolverTests` and the existing `DocsRootRegistryTests` cases pass unchanged.
  - `kyber-weave-mcp --any-repo` started from a directory with no host config does not exit with `KW-MCP-ROOT-001` (proven by the composition test building the any-repo container without any root).

### T2: Insert the kw-docs entry into Antigravity's user MCP config

- **Objective:** a global `squad install` or `squad update` that includes Antigravity inserts the any-repo `kw-docs` entry into `<agy global root>/mcp_config.json`, insertion-only, refusing malformed files and never echoing contents.
- **Capabilities:** byte-exact JSON text splicing through an existing helper; C# lifecycle wiring; xUnit unit tests over in-memory fakes
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `squad-agy`
- **Depends on:** T5 (uses `ISquadFileAccess.GetLength`; T5 follows T4, which added `ISquadFileAccess.WriteAllBytesAtomic`)
- **Files:**
  - `src/KyberWeave.Application/Squad/Deployment/AntigravityMcpConfiguration.cs` (new): `public static IReadOnlyList<string> Ensure(ISquadFileAccess files, IReadOnlyCollection<SquadTarget> targets, SquadDeploymentScope scope, ISquadGlobalRootResolver? globalRoots, string? docsServerCommand, bool dryRun)`; behaviour under Design below.
  - `src/KyberWeave.Application/Squad/Deployment/SquadLifecycleService.cs`: call `AntigravityMcpConfiguration.Ensure` directly after each `FactoryMcpConfiguration.Warnings` call (install and update paths), passing `_globalRoots`, `request.DryRun` and the docs server command (null until T3 supplies it).
  - Tests: `tests/KyberWeave.Application.Tests/Squad/AntigravityMcpConfigurationTests.cs` (new).
- **Design (advanced):** failure mode — the write loses or reorders another server's bytes, overwrites an existing entry, or quotes a key in a warning.
  - Acts only for Global scope with Antigravity targeted and a non-null `globalRoots`; all three are checked before `ResolveGlobalRoot` is called. The file is `mcp_config.json` under `ResolveGlobalRoot(SquadTarget.Antigravity)`. The entry is `kw-docs` with command `docsServerCommand`, or `kyber-weave-mcp` when null, and args `--any-repo`, serialized with `System.Text.Json`.
  - A file over 1 MiB gets one warning and no write; the size is checked with `files.GetLength` before `ReadAllBytes` (D29).
  - `JsonException`, `IOException` and `UnauthorizedAccessException` are caught around the size probe and read, the `args` check, and the write; each gives one fixed-text warning with the path and no write. The `FactoryMcpConfiguration` `{exception.Message}` warning pattern is not copied (D17).
  - Splice with `JsonServerSplice.Plan` at container `mcpServers`, server `kw-docs`; a missing file is planned against an empty object. Write `plan.Document` with `files.WriteAllBytesAtomic` only for `Inserted`. `ServerAlreadyPresent` writes nothing and warns once only when the existing args lack `--any-repo`. Every other outcome gets one fixed-text warning and no write. A dry run writes nothing and adds one "would add" note.
  - A warning carries the path and fixed text only: never file bytes, a value, `JsonSplicePlan.Message` or an exception message. Sanitize with `SquadLifecycleService.SanitizeTargetTokenForWarning`.
  - Bytes change only through `JsonServerSplice.Plan`'s `Inserted` document; nothing else is ever written.
  - The `args` check on an existing entry reads with `JsonDocument` as leniently as `JsonServerSplice` does (BOM stripped first, comments skipped, trailing commas allowed) and reports nothing of the value.
- **Commands:**
  - `kyber-weave review gates . --agent <agent> --task T2 --red`
  - `kyber-weave review gates . --agent <agent> --task T2`
- **Acceptance:**
  - Every T2 Test-contract case passes over `InMemorySquadFileAccess`; `FactoryMcpConfigurationDeploymentTests` passes unchanged.
  - No test or command touches the real home directory.

### T3: Render the any-repo entry with the absolute command

- **Objective:** Antigravity agent files carry `kw-docs` as `<absolute kyber-weave-mcp> --any-repo` in global scope and `kyber-weave-mcp --any-repo` in project scope, and the global config entry uses the same command.
- **Capabilities:** C# renderer change and composition wiring, xUnit renderer contract tests
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `squad-agy`
- **Depends on:** T2 (both edit `SquadLifecycleService` install and update paths; T3 fills the command T2 passes as null)
- **Files:**
  - `src/KyberWeave.Contracts/Squad/SquadRenderRequest.cs`: optional trailing `string? DocsServerCommand = null` (D10).
  - `src/KyberWeave.Application/Squad/Rendering/AntigravityRenderer.cs`: where agent `mcpServers` entries are built (`McpServerDeclaration` for `CapabilityDegradations.UniversalMcpServer`), use args `[--any-repo]` and, when `Request.Scope` is Global and `DocsServerCommand` is set, that command (D11, D22); update the class XML remarks that explain `--repo-root .`.
  - `src/KyberWeave.Application/Squad/Deployment/SquadLifecycleService.cs`: optional trailing constructor parameter `Func<string, string?>? findExecutable = null`; in both render-request sites set `DocsServerCommand` for Global and Instance scope; pass the same value to `AntigravityMcpConfiguration.Ensure`; add one warning when Antigravity is targeted globally and the lookup returns null.
  - `src/KyberWeave.Application/Squad/SquadServiceCollectionExtensions.cs`: wire `findExecutable` to `IProcessRunner.FindOnPath`.
  - `src/KyberWeave.Cli/Commands/Squad/SquadDoctorCommand.cs`: optional trailing `IProcessRunner` constructor parameter; resolve the command with `SquadDocsServerCommand.Resolve` and set `DocsServerCommand` on its global render, so doctor's byte comparison sees the same agent files as install (D25).
  - Tests: `tests/KyberWeave.Application.Tests/AntigravityRendererContractTests.cs`, `tests/KyberWeave.Application.Tests/Squad/ArbiterMcpGrantRenderingTests.cs`, `tests/KyberWeave.Application.Tests/Squad/AntigravityMcpConfigurationTests.cs` (lifecycle cases for the resolved path).
- **Commands:**
  - `kyber-weave review gates . --agent <agent> --task T3 --red`
  - `kyber-weave review gates . --agent <agent> --task T3`
- **Acceptance:**
  - Every T3 Test-contract case passes; no rendered Antigravity agent file contains `--repo-root` for `kw-docs`.

### T4: Atomic, mode-preserving write on the Squad file seam

- **Objective:** `ISquadFileAccess` can replace a file atomically, keeping the target's Unix mode and writing through a symbolic link, so T2 can edit a secrets-bearing config file safely.
- **Capabilities:** C# seam widening across contract, Kernel adapter, Data adapter and test fakes; xUnit contract suite
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `squad-agy`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Contracts/Repositories/ISquadFileAccess.cs`: add `void WriteAllBytesAtomic(string path, byte[] contents)` with an XML doc comment: atomic replace, parent directory created, existing Unix mode kept, a symbolic link written through (D21).
  - `src/KyberWeave.Kernel/FileSystem/AtomicFile.cs`: new overload `public static void WriteAllBytes(string path, byte[] content, bool writeThroughSymlink, bool flushToDisk, bool preserveUnixFileMode)`; private `Write` gains trailing `bool preserveUnixFileMode = false`; new private helper `CopyUnixFileMode(string destination, string temporaryPath)` runs after the temporary is written and before `File.Move`, reading the mode of the link-resolved destination, skipped on Windows and for a missing destination. The two existing overloads keep their signatures and bodies and pass `false`; no existing caller changes (D21). Update the class remarks for the opt-in.
  - `src/KyberWeave.Data/Squad/Deployment/SquadDeploymentAdapters.cs`: `PhysicalSquadFileAccess.WriteAllBytesAtomic` calls `AtomicFile.WriteAllBytes(path, contents, writeThroughSymlink: true, flushToDisk: false, preserveUnixFileMode: true)`.
  - `tests/KyberWeave.Testing/Squad/InMemorySquadFileAccess.cs`: `WriteAllBytesAtomic` through `InMemoryFileSystem`; every other `ISquadFileAccess` double (`FileSet`, `NoFiles`, `StubFiles`; find them with codegraph) gains the member, throwing `NotSupportedException` where it never writes.
  - `tests/KyberWeave.Testing/Squad/SquadFileAccessWriteContract.cs` (new): abstract contract suite — a write creates a missing parent directory and file; a second write replaces the whole content; the bytes read back equal the bytes written.
  - `tests/KyberWeave.Application.Tests/Squad/InMemorySquadFileAccessWriteContractTests.cs` (new): runs the contract over `InMemorySquadFileAccess`.
  - `tests/KyberWeave.Integration.Tests/PhysicalSquadFileAccessWriteTests.cs` (new, CI only): runs the contract over `PhysicalSquadFileAccess` in its scratch folder, plus a `0600` file keeps its mode and a symlinked file stays a symlink whose target gets the bytes; skip the mode and link cases on Windows.
  - `tests/KyberWeave.Integration.Tests/AtomicFileUnixModeTests.cs` (new, CI only): the D28 cases; skip on Windows.
- **Design (advanced):** failure mode — a `0600` secrets file silently becomes `0644`, or a symlink is replaced by a regular file.
  - The mode is set on the temporary before the rename, so the new content is never readable by others even briefly.
  - The mode is read from the link-resolved destination, not from the link.
- **Commands:**
  - `kyber-weave review gates . --agent <agent> --task T4 --red`
  - `kyber-weave review gates . --agent <agent> --task T4`
- **Acceptance:**
  - Every T4 Test-contract case passes over `InMemorySquadFileAccess`.
  - Both integration classes compile: the worker builds `tests/KyberWeave.Integration.Tests/KyberWeave.Integration.Tests.csproj -c Release` (a build, not a test run) and reports it; their pass is the CI integration job's, checked at review (D28).

### T5: Size probe and link-following fake on the Squad file seam

- **Objective:** `ISquadFileAccess` can report a file's length without reading it, and the in-memory fake writes through a file link as the seam documents, so T2 can refuse an oversized file before reading it.
- **Capabilities:** C# seam widening across contract, Data adapter and test doubles; xUnit contract suite
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `squad-agy`
- **Depends on:** T4 (extends the `WriteAllBytesAtomic` member, contract suite and fake it added)
- **Files:**
  - `src/KyberWeave.Contracts/Repositories/ISquadFileAccess.cs`: add `long GetLength(string path)` with an XML doc comment: the file's length in bytes, a symbolic link followed; `FileNotFoundException` when it is missing (D29).
  - `src/KyberWeave.Data/Squad/Deployment/SquadDeploymentAdapters.cs`: `PhysicalSquadFileAccess.GetLength` returns `new FileInfo(path).Length`, as `PhysicalFileSystem.GetLength` does.
  - `tests/KyberWeave.Testing/Squad/InMemorySquadFileAccess.cs`: `GetLength` forwards to `InMemoryFileSystem.GetLength`; `WriteAllBytesAtomic` writes to `files.GetFinalLinkTarget(path) ?? path` (D30). `InMemoryFileSystem` is not changed.
  - `tests/KyberWeave.Application.Tests/Squad/FactoryMcpConfigurationTests.cs` (`FileSet`), `tests/KyberWeave.Application.Tests/Squad/Instances/SquadInstanceServiceTests.cs`, `tests/KyberWeave.Cli.Tests/SquadInstanceRemoveCommandTests.cs`, `tests/KyberWeave.Cli.Tests/SquadUpdateCommandTests.cs` (the `NoFiles` and `StubFiles` doubles): `GetLength` throws `NotSupportedException`.
  - `tests/KyberWeave.Testing/Squad/SquadFileAccessWriteContract.cs`: two cases — length after a write equals the bytes written; length of a missing file throws `FileNotFoundException`.
  - `tests/KyberWeave.Application.Tests/Squad/InMemorySquadFileAccessWriteContractTests.cs`: fake-only cases — a write to a file link keeps the link and fills its target; `GetLength` on the link returns the target's length.
  - `tests/KyberWeave.Integration.Tests/PhysicalSquadFileAccessWriteTests.cs` (CI only): `GetLength` on a symlink returns the target's length; skipped on Windows.
- **Commands:**
  - `kyber-weave review gates . --agent <agent> --task T5 --red`
  - `kyber-weave review gates . --agent <agent> --task T5`
- **Acceptance:**
  - Every T5 Test-contract case passes over `InMemorySquadFileAccess`; T4's contract cases pass unchanged.
  - The integration project builds (`dotnet build tests/KyberWeave.Integration.Tests/KyberWeave.Integration.Tests.csproj -c Release`), reported by the worker; its run is CI's (D28).

## Dependency graph

```text
T1 (mcp-any-repo)   independent
T4 (squad-agy) ──▶ T5 (squad-agy) ──▶ T2 (squad-agy) ──▶ T3 (squad-agy)
```

T1 and T4 run in parallel. T5 follows T4, T2 follows T5 and T3 follows T2 in the same session group.

## Risks

- `ActivatorUtilities` picks a different `DocsTools` constructor than expected in one of the two modes → T1's composition tests build both containers and call a tool in each.
- Antigravity may prefer a same-named global entry over the agent entry (runbook probe) → both now carry the same any-repo command, so either wins correctly; a hand-pinned entry is left alone and warned about (D16).
- A self-update that moves `kyber-weave-mcp` off the `PATH` directory the render recorded → the next `squad update` re-renders and re-resolves; the config entry is insertion-only, so D16's warning names the stale case only when args differ. PR note below.
- Spliced JSON edge cases (BOM, CRLF, empty objects) → named in T2's Test contract.

## Out-of-scope boundaries

- `dash/` and `src/**/SelfUpdater.cs` are not touched.
- No task runs a real `squad install --global` or writes `~/.gemini` (D20).
- `squad uninstall` does not remove the inserted entry: insertion-only, and the entry points at the machine-level executable Squad does not own.
- PR note: `src/KyberWeave.Application/Squad/Rendering/AntigravityRenderer.cs` — the `codegraph` entry (`codegraph serve --mcp --path .`) is spawned from `/` by Antigravity just like kw-docs and likely gets no index — codegraph has no any-repo mode; recommend David decide between withholding codegraph from global Antigravity agent files or asking CodeGraph for a per-call path mode.
- PR note: Warp, Copilot CLI (`~/.copilot/mcp-config.json`), Codex (`~/.codex/config.toml`), Pi (`~/.config/mcp/mcp.json`), ZCode user tier, Kilo global config — the directory each harness starts a global MCP server in was not probed live in this run; the runbook now recommends `kyber-weave-mcp --any-repo` for every user-scope entry, which works whatever that directory is; recommend one live probe per harness before claiming `--repo-root .` works anywhere at user scope.
- PR note: `~/.gemini/config/mcp_config.json` on David's Mac — its hand-pinned `kw-docs --repo-root <path>` entry stays (insertion-only) and the next global install will warn; switching it to `--any-repo` is David's call.
- PR note: run worktree location — this run's worktree is `.agent_worktrees/20261125T173944Z-…` inside the repository, not under the owner's usual worktree folder as the request asked; the run start chose it, and moving a live run is not a plan step; David to decide whether run start should default there.
- PR note: a `kw-docs` entry written with an absolute command goes stale if the executable moves off that `PATH` directory; Squad never rewrites an existing entry, so the fix is manual.

## Verification gates

- Each task's own gate run (`kyber-weave review gates . --agent <agent> --task <id>`, red then green).
- `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` after closeout.
- CI runs the full unit suite and `KyberWeave.Integration.Tests`, which holds `AtomicFileUnixModeTests` and `PhysicalSquadFileAccessWriteTests`, the only proof of file-mode and symlink preservation (D28). The final review does not approve until that CI job has passed on the pull request.

## Review

The end-of-run review council reviews the whole change under the `code-review` skill (more than 300 changed lines expected, so the full pre-push council). Security lens focus: D17 (no contents in warnings) and D21 (file mode and link preservation).

## docs-dev closeout

docs-dev harvests every Design harvest row: the new ADR 0094 and the ADR 0052 amendment line (D18); `docs/kyber-squad/architecture.md` §3 exception; `docs/docgraph/mcp-runbook.md` (any-repo mode, Antigravity, every user-scope recipe per D19, `KW-MCP-ROOT-003`); `.apm/skills/kw-onboarding/references/mcp-and-index.md`; `src/KyberWeave.Mcp/AGENTS.md`; `docs/reference/seams.md`. It runs the content tests that read `mcp-and-index.md` (`KyberWeave.Content.Tests.WorkerRuleRefusedFolderTests`) and the docs gates, then fills the Closeout mapping.

## Completion summary

All five tasks are integrated on the run branch: T4 `12b869e31`, T1 `3064a64e6`, T5 `c3ed8c48d`, T2 `3b2b99079`, T3 `5d67a2c50`, and T3's rework `118a3597b`, which makes `squad doctor` render Antigravity with the same resolved command as install and update through the new shared `SquadDocsServerCommand`. Each task's gate evidence is recorded by its own gate run. Documentation was harvested into the ADR 0094 (new) and the ADR 0052 amendment, the MCP runbook, the Squad architecture, onboarding and requirements pages, the kw-onboarding table, `src/KyberWeave.Mcp/AGENTS.md` and the seam register.

NOT VERIFIED: file-mode and symbolic-link preservation on a real disk (`AtomicFileUnixModeTests`, `PhysicalSquadFileAccessWriteTests`) — they run only in the CI integration job (D28).

NOT VERIFIED: how each harness other than Antigravity starts a user-scope server (working directory) — not probed live (D19); the runbook recommends `--any-repo` for each.

## Closeout mapping

| ID | Destination |
|---|---|
| DocGraph, D1, D3, D4, D5, D6, D7, D8, D9 | [Any-repo mode: no bound root](../../docgraph/mcp-runbook.md#any-repo-mode-no-bound-root), [Naming your worktree with `projectPath`](../../docgraph/mcp-runbook.md#naming-your-worktree-with-projectpath) |
| D2, D24 | [Any-repo mode: no bound root](../../docgraph/mcp-runbook.md#any-repo-mode-no-bound-root); the composition detail is in `src/KyberWeave.Mcp/AGENTS.md` (an agent-guidance file, not a governed document) |
| KyberSquad, D10, D11, D12, D25, D26 | [Antigravity](../../docgraph/mcp-runbook.md#antigravity), [Squad architecture, Antigravity user MCP config](../../kyber-squad/architecture.md#4-deployment-scopes-and-state-topology) |
| D13, D14, D15, D16, D17, D18, D20 | [ADR 0094](../../adr/0094-squad-inserts-the-kw-docs-entry-into-antigravitys-user-mcp-config.md), [ADR 0052 amendment](../../adr/0052-docs-branch-writes-repository-scope-mcp-configuration.md) |
| D19 | [Wiring it up, user-scope entries](../../docgraph/mcp-runbook.md#wiring-it-up), [kw-onboarding wiring table](../../../.apm/skills/kw-onboarding/references/mcp-and-index.md) |
| D21, D28, D29, D30 | [Seam register](../../reference/seams.md) |
| D22, D27 | Documentation waiver — a decision to leave `products/kyber-squad/mcp.json` and the `docs mcp-wire` catalog unchanged; the runbook states that other clients keep `--repo-root .`. |
| D23, AP1 | Documentation waiver — task rating and approval record, no lasting design content. |
| Design harvest: DocGraph runbook (root and `projectPath`) | [Any-repo mode: no bound root](../../docgraph/mcp-runbook.md#any-repo-mode-no-bound-root) |
| Design harvest: DocGraph runbook (Antigravity, Warp, Codex, Pi, ZCode, Kilo, Copilot CLI) | [Antigravity](../../docgraph/mcp-runbook.md#antigravity), [Codex CLI](../../docgraph/mcp-runbook.md#codex-cli) |
| Design harvest: KyberSquad architecture | [Squad architecture](../../kyber-squad/architecture.md) |
| Design harvest: ADR 0094 and ADR 0052 amendment | [ADR 0094](../../adr/0094-squad-inserts-the-kw-docs-entry-into-antigravitys-user-mcp-config.md) |
| Design harvest: onboarding table | [kw-onboarding wiring table](../../../.apm/skills/kw-onboarding/references/mcp-and-index.md) |
| Design harvest: Mcp AGENTS.md and seams | [KyberWeave.Mcp AGENTS.md](../../../src/KyberWeave.Mcp/AGENTS.md), [Seam register](../../reference/seams.md) |
