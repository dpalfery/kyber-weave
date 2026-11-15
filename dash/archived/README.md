# Archived KyberDash TypeScript Engine & Tests

This directory holds the retired TypeScript/Node.js KyberDash collector, canonical store, HTTP/OTLP/MCP server, synthesizer, CLI, bundler, and Vitest engine files preserved for reference during the C# Clean Architecture cutover ([PR #612](https://github.com/dpalfery/kyber-weave/pull/612)).

- **Active server code lives in:** `../server/` (`dash/server/KyberDash.sln`)
- **Active server tests live in:** `../server.tests/`
- **Active UI code lives in:** `../web/` and `../tray/`

**No active build, runtime, CLI, UI, or test target may import, spawn, or execute anything under `dash/archived/`.**
