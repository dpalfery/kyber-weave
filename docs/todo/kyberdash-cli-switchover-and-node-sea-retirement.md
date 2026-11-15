---
id: todo/kyberdash-cli-switchover-and-node-sea-retirement
title: Switch kyberdash CLI, MCP, and tray entrypoints to C# engine and retire Node SEA packaging
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-11-15
---

# Switch kyberdash CLI, MCP, and tray entrypoints to C# engine and retire Node SEA packaging

All five slices of the C# engine (`KyberWeave.Core.Dash` under `src/KyberWeave.Core/Dash/`) are implemented and verified against `CanonStoreParityTests`, `CanonIngestParityTests`, `CanonAnalysisParityTests`, `CanonServerParityTests`, and `CanonSynthParityTests` ([ADR 0084](../adr/0084-kyberdash-csharp-engine.md)).

## What remains

- Wire the `.NET` CLI host (`src/KyberWeave.Cli`) and MCP surface so `kyberdash web`, `kyberdash otel`, `kyberdash dash refresh`, `kyberdash dash clean`, `kyberdash dash settings`, and `kyberdash mcp` dispatch to `KyberWeave.Core.Dash`.
- Update `dash/tray/` (`Supervisor` launcher resolution) and `scripts/proc-guard.sh` / distribution workflows once the `.NET` executable hosts live traffic.
- Retire the Node Single Executable Application (SEA) build path in release scripts and update `docs/distribution.md` and `docs/install.md` accordingly.
