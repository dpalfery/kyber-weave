---
id: todo/shell-implies-write-live-verification-other-targets
title: Live verification of shell-implies-write structural findings across harness targets
doc-type: todo
component: KyberSquad
status: draft
owner: dpalfery
last-reviewed: 2026-09-23
---

# Live verification of shell-implies-write structural findings across harness targets

This is **context for planning the work, not a plan** — it records what is known, what is grounded in structural code inspection vs. live proof, and where verification would strengthen confidence in the degradation records.

## Why this exists

The plan [2026-09-21-pi-thinking-and-antigravity-native-agents](../archive/plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md) (§3 D10, D11, D12; §9 DS2) added a new degradation code `capability-not-isolable` to the Kyber-Squad contract. The finding that grounds it — `process.execute: allow` on a target that grants shell tools can write files through redirection, making withheld write-tool names an ineffective security boundary — was live-verified against Antigravity (`agy` 1.2.7) in the plan's C0 task (evidence in [antigravity-capability-verification-evidence.md](../archive/todo/antigravity-capability-verification-evidence.md) §5).

Five other renderers share the same structural shape: `ClaudeRenderer`, `PiRenderer`, `ZCodeRenderer`, `FactoryRenderer`, and `OpenCodeRenderer` all hold a single named shell-class tool distinct from a set of named write tools, both derived from capability-lattice mappings. The records emitted on these five targets are grounded in that structural code inspection, not in live redirection proof like Antigravity's.

## What is known and structurally grounded

- **Claude (`ClaudeRenderer.CapabilityTools`):** `Bash` and `PowerShell` are held distinct from `Edit`, `Write`, `NotebookEdit` (lines 73-79 of `src/KyberWeave.Core/Squad/Rendering/ClaudeRenderer.cs`).
- **Pi (`PiRenderer.CapabilityTools`):** `bash` is distinct from `edit`, `write` (lines 121-127 of `PiRenderer.cs`).
- **ZCode (`ZCodeRenderer.CapabilityTools`):** `Bash` is distinct from `Edit`, `Write` (lines 202-210 of `ZCodeRenderer.cs`).
- **Factory (`FactoryRenderer.CapabilityTools`):** `Execute` is distinct from `Create`, `Edit`, `ApplyPatch` (lines 59-66 of `FactoryRenderer.cs`).
- **OpenCode (`OpenCodeRenderer.CapabilityPermissions`):** `bash` is distinct from `edit` (lines 68-75 of `OpenCodeRenderer.cs`).

Each renderer's mapping is read from source and confirms the shell/write-tool separation. When a role grants `process.execute: allow` and withholds `filesystem.write` (both at the capability level), every one of these renderers narrows the rendered `tools` list to exclude the write-tool names. The structure is identical across all six (Antigravity through OpenCode); the shell-implies-write property — that a granted shell can write files via redirection — was proven live for Antigravity and is inferred to hold across these five targets until independently verified.

## What is not yet live-exercised

The actual write-through-shell capability was not independently verified on Claude, Pi, ZCode, Factory, or OpenCode. Each renderer's `capability-not-isolable` degradation record is therefore grounded in:
- **Structural evidence:** The distinct tool names are present in source.
- **Logical inference:** The shell-implies-write property holds for any shell, not just Antigravity's `agy`.
- **Transferred confidence:** Antigravity's live proof (C0, evidence doc §5) established the property at the capability level.

A live check on each harness — running a model with `process.execute` granted but named write tools withheld, then attempting to create a file via shell redirection — would upgrade each record from "structurally evident" to "live-verified" and eliminate any platform-specific edge cases (e.g. a hardened sandbox, a shell-call interceptor, or a tool-call gate that rewrites redirection syntax).

## Withheld tools and delegation on Antigravity (D12 consequences)

This pass, the following tools are withheld from every Antigravity profile and recorded as a known limitation (D12, plan §3):
- `grep_search`
- `replace_file_content`
- `multi_replace_file_content`
- `search_web`
- `read_url_content`

These five tools are not emitted in the `tools:` list regardless of the owning capability's permission decision. A follow-up live-validation pass would unlock their emission (pending results).

On delegation: the plan emits delegation (`invoke_subagent`, `manage_subagents`) on static evidence (21 hand-authored backup-corpus agents carry non-empty `delegates-to` rosters), without live exercise. A complementary degradation record is emitted naming the delegates-to roster as unenforceable, reusing the existing `permission-not-expressible` code because Antigravity offers no mechanism to restrict which agents may be invoked.

## How to verify live (short form)

The method used in C0 (evidence doc §5, methodology section):

1. **Load proof:** Inject a unique token (`AGT-<hex>`) as a required first line of every reply, plus copy the test agent from the backup corpus with a distinguishing marker in its frontmatter.
2. **Nonce and exec proof:** Create a fresh random nonce file, hash it with the harness's own command tool (`run_command` / `Bash` / `bash`), redirect to a proof file.
3. **Write proof:** Ask the model to write a file with specific content via shell redirection (`printf "..." > file`), not via the withheld write-tool names.
4. **Withheld-tool check:** In a separate turn, ask the model explicitly to use the withheld write tool (e.g. `write_to_file`, `Edit`), and inspect the harness record to verify that the tool call fails or is unavailable.
5. **Verification:** Check the file on disk and cross-check against the harness's own tool-call record (agy's conversation database; equivalent systems on other harnesses).

Together, these steps confirm that the withheld write tool name stays unreachable/uninvoked while the underlying file-write capability is achievable via shell redirection.

## Related

- [Plan: Emit Pi thinking levels and reclassify Antigravity as a native per-agent target](../archive/plans/2026-09-21-pi-thinking-and-antigravity-native-agents.md) — §3 D10, D11, D12; §9 DS2
- [Live verification evidence for Antigravity capability mapping against agy 1.2.7](../archive/todo/antigravity-capability-verification-evidence.md) — §5 D4 verdict and methodology
- [Kyber-Squad requirements and degradation contract](../kyber-squad/requirements.md) — Degradation Taxonomy, `capability-not-isolable` row
