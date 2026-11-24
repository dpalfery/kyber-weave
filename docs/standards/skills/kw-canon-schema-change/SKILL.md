---
name: kw-canon-schema-change
description: "Change the KyberDash SQLite canon schema safely: where the schema, DDL writer and open behaviour live, and the two targeted test classes to run afterwards. Load when changing RelationalSchemaCatalog, SchemaDdlGenerator or SqliteRelationalStore."
---

# Canon schema change

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
