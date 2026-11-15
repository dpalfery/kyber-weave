# KyberDash

The KyberDash UI and tray tree: the web dashboard (`dash/web/`), the Tauri tray
(`dash/tray/`), Playwright UI tests (`dash/e2e/`), and web asset packaging scripts
(`dash/scripts/`). The C# collector, canonical store, analysis engine, and the `kyberdash`
CLI/API/MCP hosts live in the Clean Architecture solution `dash/server/KyberDash.sln`.
Documentation lives in the repository's governed corpus, not here:

- [KyberDash overview](../docs/dash/README.md)
- [Architecture](../docs/dash/architecture.md)
- [Runbook](../docs/dash/runbook.md): building, running and testing locally

KyberDash began as a one-time fork of the MIT-licensed CodeBurn project
([ADR 0020](../docs/adr/0020-kyberdash-one-time-fork.md)); its copyright notice is kept in
[LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
