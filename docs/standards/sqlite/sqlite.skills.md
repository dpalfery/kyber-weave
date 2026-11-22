---
id: standards/sqlite/skills
title: "sqlite skills"
doc-type: skill
status: current
technology: sqlite
owner: dpalfery
last-reviewed: 2026-11-20
---

# sqlite skills

## Read the standard first

Ask `docs_get_coding_standard_for_technology` for `sqlite`, or for
`<sqlite-coding-standard>`. The canon schema is `RelationalSchemaCatalog`. The DDL
writer is `SchemaDdlGenerator`. The open and version behaviour is
`SqliteRelationalStore`.

## After a canon schema change

```bash
dotnet test dash/server.tests/KyberDash.Domain.Tests/KyberDash.Domain.Tests.csproj -c Release --filter "FullyQualifiedName~KyberDash.Domain.Tests.Schema.RelationalSchemaCatalogTests"
dotnet test dash/server.tests/KyberDash.Data.Tests/KyberDash.Data.Tests.csproj -c Release --filter "FullyQualifiedName~KyberDash.Data.Tests.Repositories.SchemaVersionTests"
```

Run `dotnet format` on the changed C# files. Do not run the full suite.
