---
name: dotnet-dev
description: Use when writing C#/.NET code, ASP.NET Core APIs, Dapper repositories, Azure AI integrations, or working in the 0-4 layer folders.
license: MIT
metadata:
  author: David R Palfery
  version: 2.1.0
---

# .NET Developer

Identify your sub-task and read ONLY the relevant reference before proceeding.

| Sub-Task | When to Use | Reference |
|---|---|---|
| Clean Architecture | Layer placement, dependency direction, project structure, file location, SOLID principles | Refer to the path defined by the **Clean Architecture Rules** property in the root `AGENTS.md`. |
| Data Access (Dapper) | Repositories, SQL queries, ISqlConnectionFactory, parameterized SQL, transactions | [Dapper SQL](./references/dapper-sql.md) |
| Azure AI / RAG | Azure OpenAI, AI Search, agent orchestration, data ingestion pipelines | [Azure AI RAG](./references/azure-ai-rag.md) |
| BFF / YARP | WebUI BFF project, YARP reverse proxy, OIDC auth flow, token forwarding, SPA serving | [BFF YARP](./references/bff-yarp.md) |
| Build & Verification | dotnet build, test, run commands; data ingestion endpoints | [Build Commands](./references/build-commands.md) |
| ASP.NET Core Web API | Controllers, TypedResults, sealed record DTOs, RFC 7807, .NET 9+ OpenAPI | [Web API](./references/aspnetcore-webapi.md) |
| File Upload | IFormFile, size limits, magic byte validation, safe filenames | [File Upload](./references/file-upload.md) |
| OpenTelemetry | ActivitySource, traces, metrics, OTLP export, log-trace correlation | [OpenTelemetry](./references/opentelemetry.md) |
| MSBuild Modernization | Migrate legacy `.csproj` (ToolsVersion, packages.config, explicit file lists) to SDK-style | Refer to the path defined by the **MSBuild Modernization** property in the root `AGENTS.md`. |
| MSBuild Anti-patterns | Cross-platform failures, hardcoded paths, `<Exec>` shell commands, unquoted conditions, non-deterministic builds | Refer to the path defined by the **MSBuild Anti-patterns** property in the root `AGENTS.md`. |
| Directory.Build Organization | `Directory.Build.props`/`.targets`/`Directory.Packages.props`, Central Package Management, multi-level hierarchy | Refer to the path defined by the **Directory.Build Organization** property in the root `AGENTS.md`. |
| Build Performance | MSBuild parallelism (`-m`), `/graph` mode, RAR slowness, analyzer overhead, bottleneck diagnosis | Refer to the path defined by the **Build Performance** property in the root `AGENTS.md`. |
| Incremental Build | Fix targets that always rebuild; `Inputs`/`Outputs` attributes; `FileWrites` registration; volatile output paths | Refer to the path defined by the **Incremental Build** property in the root `AGENTS.md`. |

**Rule:** Read only the reference(s) relevant to your current task. Do not pre-load all references.
