---
id: kyber-squad/agent-rule-inventory
title: Technology agent rule inventory
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-11-17
keywords:
  - rule inventory
  - technology agents
  - developer tiers
  - standards templates
---

# Technology agent rule inventory

Every technology-specific rule carried in the body of the eleven technology agents,
`test-dev` and `azure-reader`, and the template file and heading that states it now. The
inventory exists so that removing those agents takes no knowledge with it: a row marked
**Moved** was stated only in an agent body and was added to the template by task 1.4; a row
marked **Stated** was already in the shipped template.

**The agents in the Agent column no longer ship.** The removal deleted all fourteen; this table is
the record that each of their technology rules has a home in a shipped template, which is why the
deletion took no knowledge with it. The template paths and headings below are current.

The shared worker baseline (task scope, task gate, build isolation, diagnostics, discovery
order, completion digest) is not technology-specific and is not listed. It moves to
`worker-baseline.md` when the developer agents are introduced.

Paths are relative to `products/kyber-squad/standards/`.

| Agent | Rule the agent body states | Template file | Heading | Status |
|---|---|---|---|---|
| `csharp-dev` | Implements ASP.NET Core backend code; the C# standard governs language, HTTP surface and stack. | `csharp/csharp.standards.md` | Naming and layout | Stated |
| `csharp-dev` | Clean Architecture, ASP.NET Core Web API, file upload, OpenTelemetry, BFF/YARP and Azure AI/RAG are the route the C# skill covers. | `csharp/csharp.skills.md` | Whole file | Moved |
| `csharp-dev` | A repository, SQL or migration change is written to the data-access standard; a schema need the task does not name is reported as an open item, naming the table and columns. | `csharp/csharp.rules.md` | Whole file | Moved |
| `csharp-dev` | The shared contract with `sql-database-architect` and `dal-dev` is a table definition: table name, columns and types. | `data-access-layer/data-access-layer.rules.md` | Whole file | Moved |
| `csharp-dev` | Never edit environment secrets or deployment credentials. | `shared-rules.md` | Security and data handling | Moved |
| `dal-dev` | Implements the persistence layer to the data-access, C# and SQL standards. | `data-access-layer/data-access-layer.standards.md` | Naming and layout | Stated |
| `dal-dev` | Scope is `ISqlConnectionFactory`, `IRepository<T>` implementations, FluentMigrator migrations that match an approved schema, and their tests. | `data-access-layer/data-access-layer.standards.md` | Coding standards | Stated |
| `dal-dev` | Never apply a migration to a shared database. | `data-access-layer/data-access-layer.rules.md` | Approved schema | Moved |
| `dal-dev` | Before repository or migration code, confirm the approved schema: column names, data types, nullability, keys and indexes. A script that would diverge is not applied; the divergence is reported. | `data-access-layer/data-access-layer.standards.md` | Coding standards | Moved |
| `dal-dev` | Deliver `IRepository<T>` implementations that satisfy the interfaces in the Contracts project the standard names. | `data-access-layer/data-access-layer.standards.md` | Naming and layout | Moved |
| `dal-dev` | Confirm `Microsoft.Data.SqlClient` and FluentMigrator APIs against current documentation before use. | `data-access-layer/data-access-layer.skills.md` | Whole file | Moved |
| `react-dev` | Implements React UI to the React standard for component, state, styling, accessibility and tooling decisions. | `react/react.standards.md` | Coding standards | Stated |
| `react-dev` | Component names, folders and the mechanism for styling are the standard's, not the agent's. | `react/react.standards.md` | Naming and layout | Stated |
| `react-dev` | Use semantic HTML and a responsive layout for every screen size. | `react/react.standards.md` | Coding standards | Stated |
| `react-dev` | Accessibility at the level the standard names, including the lint rules it enforces. | `react/react.standards.md` | Coding standards | Stated |
| `react-dev` | Optimise assets and code splitting for the bundle. | `react/react.skills.md` | Whole file | Moved |
| `react-dev` | Prefer design-system components when the repository has them. | `react/react.rules.md` | Whole file | Moved |
| `react-dev` | Implement error and loading states for asynchronous UI work. | `react/react.standards.md` | Coding standards | Moved |
| `react-dev` | Write unit tests for critical UI logic. | `react/react.standards.md` | Testing | Moved |
| `typescript-dev` | Implements TypeScript on Node to the TypeScript standard for typing, absent values, data access, errors, modules and gates. | `typescript/typescript.standards.md` | Coding standards | Stated |
| `typescript-dev` | Scope is `.ts`, `.mts` and `.cts` outside a React UI, plus its tests. | `typescript/typescript.standards.md` | Naming and layout | Stated |
| `typescript-dev` | Never turn an unmeasured value into `0`, `''`, or a default that looks like data. | `typescript/typescript.standards.md` | Coding standards | Stated |
| `typescript-dev` | Use ESM conventions and the typecheck, lint and build commands the standard names. | `typescript/typescript.standards.md` | Commands | Stated |
| `typescript-dev` | Data access avoids N+1 and unbounded reads. | `typescript/typescript.standards.md` | Coding standards | Stated |
| `typescript-dev` | Agree the contract a React UI consumes before editing a `.tsx` file; edit it only where the task names it. | `typescript/typescript.rules.md` | Whole file | Moved |
| `typescript-dev` | When ceding work to delivery, provide the Node version, install, build and test commands, and any native dependency. | `typescript/typescript.skills.md` | Whole file | Moved |
| `python-dev` | Implements Python application code to the Python standard for language, packaging, typing and environment decisions. | `python/python.standards.md` | Naming and layout | Stated |
| `python-dev` | Scope is modules, packages, type annotations, packaging metadata and their tests. | `python/python.standards.md` | Naming and layout | Stated |
| `python-dev` | Pylance, automated refactoring and Pylance documentation are the route the Python skill covers. | `python/python.skills.md` | Whole file | Moved |
| `python-dev` | When ceding work to delivery, provide the build, test and run commands and any image or runtime need. | `python/python.skills.md` | Whole file | Moved |
| `go-dev` | Implements Go application and library code to the Go standard for toolchain, errors, concurrency, modules and cross-compilation. | `go/go.standards.md` | Naming and layout | Stated |
| `go-dev` | Scope is packages, commands under `cmd/`, error types, `go.mod` and their tests. | `go/go.standards.md` | Naming and layout | Stated |
| `go-dev` | Errors and context propagate through the call chain; goroutines and channels have a stated lifecycle. | `go/go.standards.md` | Coding standards | Stated |
| `go-dev` | Run the format, vet, lint, build and test commands the standard names on each platform. | `go/go.standards.md` | Commands | Stated |
| `go-dev` | When ceding work to delivery, provide the `GOOS`/`GOARCH` targets, build, vet and test commands, and whether cgo is needed. | `go/go.skills.md` | Whole file | Moved |
| `rust-dev` | Implements Rust outside a Tauri app to the Rust standard for toolchain, errors, async, `unsafe`, dependencies and cross-compilation. | `rust/rust.standards.md` | Naming and layout | Stated |
| `rust-dev` | A crate under `src-tauri` or depending on `tauri` also follows the Tauri standard. | `rust/rust.rules.md` | Whole file | Moved |
| `rust-dev` | Scope is crates, modules, binaries, `Cargo.toml` and workspace layout, and their tests. | `rust/rust.standards.md` | Naming and layout | Stated |
| `rust-dev` | Agree a shared crate's public API before editing `src-tauri`; edit it only where the task names it. | `rust/rust.rules.md` | Whole file | Moved |
| `rust-dev` | When ceding work to delivery, provide the target triples, build, lint and test commands, and native toolchain needs. | `rust/rust.skills.md` | Whole file | Moved |
| `maui-dev` | Implements MAUI client UI to the MAUI standard, with language-level C# from the C# standard. | `maui/maui.standards.md` | Naming and layout | Stated |
| `maui-dev` | XAML pages, Shell navigation, MVVM/CommunityToolkit, CollectionView and binding are the route the MAUI skill covers. | `maui/maui.skills.md` | Whole file | Moved |
| `maui-dev` | Register every added type where the standard requires it; ViewModels take interfaces rather than constructing services. | `maui/maui.standards.md` | Coding standards | Stated |
| `maui-dev` | Never edit signing keys or publish credentials. | `shared-rules.md` | Security and data handling | Moved |
| `maui-dev` | Consume the API contracts the backend exposes; do not implement server endpoints. | `maui/maui.rules.md` | Whole file | Moved |
| `maui-dev` | When ceding work to delivery, provide the publish/build commands and RID/workload needs. | `maui/maui.skills.md` | Whole file | Moved |
| `tauri-dev` | Target Tauri v2; verify version-sensitive APIs, config keys and plugin behaviour against `v2.tauri.app` and `docs.rs/tauri`. | `tauri/tauri.standards.md` | Naming and layout | Stated |
| `tauri-dev` | Own everything on the Rust side of the IPC boundary: commands, contract, state, async, sidecars, capabilities, `tauri.conf.json` and the bundle. | `tauri/tauri.standards.md` | Naming and layout | Moved |
| `tauri-dev` | Treat the WebView as untrusted; validate every argument crossing the IPC boundary. | `tauri/tauri.standards.md` | Security and data handling | Stated |
| `tauri-dev` | Default-deny exposure: expose only the commands the frontend needs, granted through capabilities with the least permission and narrowest scope. | `tauri/tauri.standards.md` | Security and data handling | Stated |
| `tauri-dev` | Never expose a generic run-arbitrary-command, shell, SQL or path surface; wrap each privileged operation in a specific validated command. | `tauri/tauri.standards.md` | Security and data handling | Moved |
| `tauri-dev` | Keep secrets, tokens and credentials in the Rust core, never in frontend-reachable code. | `tauri/tauri.standards.md` | Security and data handling | Moved |
| `tauri-dev` | Keep `tauri` and plugin crates on the pinned versions and never widen the CSP to make something load. | `tauri/tauri.standards.md` | Security and data handling | Moved |
| `tauri-dev` | Register every command in a single `tauri::generate_handler!` call; a second `invoke_handler` silently wins. | `tauri/tauri.standards.md` | Coding standards | Moved |
| `tauri-dev` | Commands in `lib.rs` are not `pub`; commands in separate modules are `pub`; command names are unique across modules. | `tauri/tauri.standards.md` | Coding standards | Moved |
| `tauri-dev` | Arguments and returns implement `serde::Deserialize`/`Serialize`; arguments arrive camelCase and use `rename_all = "snake_case"` when the JS side differs. | `tauri/tauri.standards.md` | Coding standards | Moved |
| `tauri-dev` | A fallible command returns `Result<T, E>` with `E: serde::Serialize`. | `tauri/tauri.standards.md` | Coding standards | Moved |
| `tauri-dev` | Prefer async commands for I/O; do not take borrowed arguments in an async command; never block the async runtime. | `tauri/tauri.standards.md` | Coding standards | Moved |
| `tauri-dev` | Do not hold a `std::sync::Mutex`/`RwLock` guard across an await; use `tokio::sync::Mutex` for state that must. | `tauri/tauri.standards.md` | Coding standards | Moved |
| `tauri-dev` | Register state with `.manage()` and read it through `tauri::State<T>`; outside commands use the `Manager` trait. | `tauri/tauri.standards.md` | Coding standards | Moved |
| `tauri-dev` | Commands are request/response, events are fire-and-forget and untyped, channels stream progress; write the contract down as a versioned API. | `tauri/tauri.standards.md` | Coding standards | Moved |
| `tauri-dev` | A sidecar is listed under `bundle.externalBin`, built per target triple, spawned through the shell plugin by file name, and streamed via `CommandEvent`. | `tauri/tauri.standards.md` | Coding standards | Moved |
| `tauri-dev` | A sidecar's capability is scoped to the exact binary with argument validators; arbitrary args are not allowed. | `tauri/tauri.standards.md` | Security and data handling | Stated |
| `tauri-dev` | Parse and validate sidecar output as an untrusted boundary and surface failures through the error type. | `tauri/tauri.standards.md` | Security and data handling | Moved |
| `pulumi-dev` | Implements Azure infrastructure in C# with Pulumi to the Pulumi and C# standards. | `pulumi/pulumi.standards.md` | Naming and layout | Stated |
| `pulumi-dev` | Construct every Azure resource name through the `azure-naming` skill; do not improvise a convention. | `pulumi/pulumi.skills.md` | Whole file | Moved |
| `pulumi-dev` | Pin registry documentation to the package version in use when behaviour matters. | `pulumi/pulumi.skills.md` | Whole file | Moved |
| `pulumi-dev` | Run `pulumi preview` and keep the diff small, reviewable and idempotent; call out replacement, data-loss and cross-stack risk before apply. | `pulumi/pulumi.standards.md` | Commands | Stated |
| `pulumi-dev` | Never apply a change whose preview was not inspected, and never hide a replacement inside an unreviewed apply. | `pulumi/pulumi.rules.md` | Whole file | Stated |
| `pulumi-dev` | Never print, log, serialize or write decrypted secret values. | `shared-rules.md` | Security and data handling | Moved |
| `pulumi-dev` | Agree stack output names (registry login server, app name, managed-identity client id) with delivery before either side writes. | `pulumi/pulumi.skills.md` | Whole file | Moved |
| `pulumi-dev` | Live-state questions go to the platform research agent; intended state is the Pulumi program and its preview. | `pulumi/pulumi.rules.md` | Whole file | Moved |
| `pulumi-dev` | Provision the database resource; do not design its schema. | `pulumi/pulumi.rules.md` | Whole file | Moved |
| `sql-database-architect` | Design schema to the SQL standard for naming, T-SQL style, schema shape, indexing and delivery. | `sql-server/sql-server.standards.md` | Naming and layout | Moved |
| `sql-database-architect` | Verify version-sensitive behaviour against Microsoft Learn and cite the page; confirm the target engine first. | `sql-server/sql-server.skills.md` | Whole file | Moved |
| `sql-database-architect` | Inspect the current schema, indexes and constraints with the `mssql` tools before proposing edits. | `sql-server/sql-server.skills.md` | Whole file | Moved |
| `sql-database-architect` | Never build T-SQL by concatenating unvalidated input; parameterize through `sp_executesql` and validate type, length, format and range. | `sql-server/sql-server.standards.md` | Security and data handling | Moved |
| `sql-database-architect` | Review every `EXEC`, `EXECUTE` and `sp_executesql` for injection risk; avoid dynamic SQL when static SQL will do. | `sql-server/sql-server.standards.md` | Security and data handling | Moved |
| `sql-database-architect` | Never use `xp_cmdshell`; recommend SQLCLR or an external process instead. | `sql-server/sql-server.rules.md` | Security and data handling | Moved |
| `sql-database-architect` | Apply least privilege through Entra/Windows groups and granular permissions; do not hand out `sysadmin` by default. | `sql-server/sql-server.standards.md` | Security and data handling | Moved |
| `sql-database-architect` | Prefer Entra ID or Windows authentication; keep SQL authentication disabled unless justified. | `sql-server/sql-server.standards.md` | Security and data handling | Moved |
| `sql-database-architect` | Never hardcode credentials, connection strings or secrets; use secrets stores, managed identities and encrypted configuration, and recommend SQL Server Audit. | `sql-server/sql-server.rules.md` | Security and data handling | Moved |
| `sql-database-architect` | User procedures do not take the `sp_` prefix (SR0016). | `sql-server/sql-server.standards.md` | Naming and layout | Moved |
| `sql-database-architect` | `SCOPE_IDENTITY()`, not `@@IDENTITY`, returns the identity this scope inserted (SR0008). | `sql-server/sql-server.standards.md` | Coding standards | Moved |
| `sql-database-architect` | `text`, `ntext` and `image` are deprecated; use the `(max)` types. | `sql-server/sql-server.standards.md` | Naming and layout | Moved |
| `sql-database-architect` | A primary key creates a clustered unique index by default. | `sql-server/sql-server.standards.md` | Coding standards | Moved |
| `sql-database-architect` | Wrapping a column in a function makes the predicate non-sargable; inspect plans and existing indexes before tuning. | `sql-server/sql-server.standards.md` | Coding standards | Moved |
| `sql-database-architect` | Never run unreviewed DDL against production; back-port a hotfix into source control immediately. | `sql-server/sql-server.rules.md` | Whole file | Moved |
| `sql-database-architect` | Schema changes reach an environment by the standard's delivery path, not ad-hoc hand-run `ALTER` statements. | `sql-server/sql-server.standards.md` | Commands | Stated |
| `sql-database-architect` | Return the approved column names, types and constraints as the explicit contract the data-access layer consumes. | `data-access-layer/data-access-layer.rules.md` | Whole file | Moved |
| `sql-database-architect` | Flag a migration that diverges from the approved schema and provide corrected DDL rather than accepting drift. | `data-access-layer/data-access-layer.rules.md` | Whole file | Moved |
| `test-dev` | Test-only work follows the test standard for runners, isolation, naming, mocking and what to assert; C# tests also follow the C# standard. | `test/test.standards.md` | Testing | Stated |
| `test-dev` | Unit, integration and end-to-end patterns, Playwright, mock-usage analysis and maintainability are the route the testing skill covers. | `test/test.skills.md` | Whole file | Moved |
| `test-dev` | Do not change application code to make a test pass; report an implementation fault as an open item. | `test/test.rules.md` | Whole file | Moved |
| `test-dev` | Never weaken assertions or assert contradictory invariants to force a green run. | `test/test.rules.md` | Whole file | Moved |
| `test-dev` | A test for a value crossing the trust boundary has one case per input-site column; a defect test covers at least three other inputs of its class. | `test/test.standards.md` | Testing | Moved |
| `test-dev` | Report coverage gaps rather than skipping them silently. | `test/test.rules.md` | Whole file | Moved |
| `azure-reader` | Reads live Azure resource configuration, topology, health and runtime state through Azure MCP Server, Azure Resources and Microsoft Docs tools. | `azure/azure.skills.md` | Whole file | Moved |
| `azure-reader` | Cross-check Azure observations against Microsoft documentation. | `azure/azure.skills.md` | Whole file | Moved |
| `azure-reader` | Changes nothing: no resource modification, no deployment or provisioning, no repository edit. | `azure/azure.rules.md` | Read-only | Moved |
| `azure-reader` | Prints no secret. | `azure/azure.rules.md` | Security and data handling | Moved |
| `azure-reader` | Report concisely, with uncertainties called out. | `azure/azure.skills.md` | Whole file | Moved |
