---
id: adr/0043-squad-lock-v2-and-instance-deployment-scope
title: Squad Lock v2 and Instance Deployment Scope
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-25
component: KyberSquad
code-refs:
  - SquadRenderSource
  - SquadModelPreset
  - SquadModelPresetStore
  - SquadInstanceCatalog
  - SquadInstanceLinkPlanner
  - SquadInstanceLinkSync
  - ISquadLinkFileSystem
---

# ADR 0043: Squad Lock v2 and Instance Deployment Scope

## Status

Accepted, 2026-10-25. Records the architectural decision to introduce Lock schema v2 (`kyber-squad.lock/v2`),
in-memory preset overlay rendering, the `SquadDeploymentScope.Instance` deployment scope, and non-administrative
cross-platform filesystem links tracked in `links.json`.

## Context

Kyber-Squad assigns models to agents based on profile categories (`fast`, `architect`, `general`, `reviewer`,
`deep-planning`) mapped to concrete model tokens in bundled `profiles/models.yml`. Operators switching
inference providers or model tiers previously had to wait for upstream releases or manually edit deployed
files, which Squad's receipt then reported as unmanaged drift.

Furthermore, when two independent projects on the same workstation run the same coding harness at the same time,
they frequently require different model presets (e.g. one project using an Anthropic direct endpoint and another
using an enterprise proxy or local inference plan). Deployed agents cannot be written into project source trees
because [ADR 0037](0037-squad-state-in-user-home.md) established that Squad state and deployed files must not
pollute repositories or version control. Similarly, mutating the user's global harness configuration directly
would cause interference between concurrent sessions.

Several alternative approaches were evaluated and rejected:
- **Rewriting frontmatter after render or outside the transaction**: Rejected because it invalidates receipt SHA-256 hashes and bypasses atomic rollback.
- **Editing `models.yml` in extracted release assets**: Rejected because it mutates release cache integrity (`bundle-digest`) and conceals overrides from validator and doctor checks.
- **Separate preset state files outside the lock**: Rejected because an older CLI would silently drop presets during updates, violating the requirement that presets survive upgrades.
- **Claude `--agents` CLI flag**: Rejected because interactive sessions take only inline JSON and Windows command lines are capped at 32,767 characters.
- **Content-addressed instance caches**: Rejected because changing directories on every release causes Keychain authentication and session tokens to be lost.
- **Windows symbolic links**: Rejected because they require elevated administrative rights or Developer Mode; hard links and directory junctions require neither.

## Decision

1. **Model Presets as In-Memory Overlays (`SquadRenderSource`)**:
   - Presets live under `~/.kyber-weave/squad/presets/<name>.yml` (schema `kyber-squad.model-preset/v1`).
   - Rather than rewriting on-disk files or mutating extracted release bundles, presets are applied in-memory through `SquadRenderSource.Load(request, target)`: `source with { ModelProfiles = preset.ApplyTo(source.ModelProfiles) }`.
   - Renderers remain unmodified; validation ensures unknown profile categories fail closed before any file writes.
   - An explicit `inherit` token omits the `model` frontmatter key on rendered agents, matching bundled behavior.

2. **Lock Schema Evolution (`kyber-squad.lock/v2`)**:
   - When any target has an active preset, `squad.lock.yml` serializes with `schema: kyber-squad.lock/v2`, adding `model-presets: { <target>: { name: <preset>, digest: <sha256> } }`.
   - v2 is written only when presets are active; otherwise the lock remains byte-identical v1. Clearing presets reverts the lock to v1.
   - Deserialization reads both v1 and v2. An older CLI without v2 support refuses v2 fail-closed (`RequireExactYamlFields`), preventing silent preset loss.

3. **Project Selections in User Home**:
   - Project preset selections live at `~/.kyber-weave/squad/selections/<root-key>.yml` (schema `kyber-squad.project-selection/v1`), keyed by the lowercase SHA-256 of the project's canonical physical path.
   - No preset files or selection metadata are written into project git trees, leaving repository `git status` completely clean.

4. **Instance Scope (`SquadDeploymentScope.Instance`)**:
   - Introduces a dedicated deployment scope alongside `Project` and `Global`.
   - Instance roots are stable: `~/.kyber-weave/squad/instances/<target>/<preset>/`, with state in `instance-state/<target>/<preset>/`.
   - Transactional mutex synchronization is keyed to the physical instance root key (`kyber-weave-squad-<root-key>`).
   - `squad run <target> [-- <args>...]` transparently provisions or refreshes the instance through the transaction engine, sets the harness configuration environment variable, and forwards arguments.

5. **Cross-Platform Filesystem Linking (`ISquadLinkFileSystem`)**:
   - To keep user credentials, settings, and command history available, instance folders link back to the harness main home.
   - Links are planned as a pure function by `SquadInstanceLinkPlanner`.
   - Unix/macOS uses symbolic links (`UnixSquadLinkFileSystem`).
   - Windows uses file hard links and directory junctions (`WindowsSquadLinkFileSystem`), requiring neither administrator privileges nor Windows Developer Mode.
   - Entries rendered by Squad are excluded from linking. Directories holding rendered files (such as `agents/`) are created as concrete folders in the instance with their non-rendered entries linked individually.

6. **Link Tracking and Safe Removal (`links.json`)**:
   - All links created are recorded in `links.json` in the instance state directory.
   - `squad instance remove` unlinks registered entries without following symlinks or junctions into destination folders, protecting user configuration from accidental deletion.
   - Split links resulting from out-of-band file renames are detected via file IDs; differing copies are preserved with warnings.

7. **Centralized Instance Catalog (`SquadInstanceCatalog`)**:
   - Encapsulates harness metadata: executable, redirection environment variable, kind (`HomeReplace`, `ConfigLayer`, `None`), extra links, never-linked lists, and support status (`Ready`, `SpikePending`, `Unsupported`).
   - Harnesses that cannot redirect configuration (`antigravity`, `factory`, `devin`, `commandcode`, `warp`) are cataloged as `Unsupported` and exit 2 with actionable explanations.

## Consequences

### Positive

- Operators can customize model assignments per provider or plan with zero manual frontmatter editing.
- Presets survive `squad update` runs across releases and are verified against release schemas before applying.
- Multiple projects can execute concurrent sessions on the same harness simultaneously with different presets.
- Repositories remain completely unmodified (`git status` is clean).
- Windows linking operates under standard user privileges without requiring Developer Mode.

### Negative / Trade-offs

- Harnesses without environment variable redirection cannot support instance scope.
- Hard links on Windows cannot cross filesystem volumes; attempting a cross-volume hard link fails closed with actionable diagnostic guidance.
- Out-of-band tools that rewrite files via atomic temp-and-rename will split hard links on Windows, requiring sync reconciliation.
