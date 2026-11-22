---
id: standards/csharp/rules
title: "csharp rules"
doc-type: rule
status: draft
technology: csharp
owner: unassigned
last-reviewed: 2026-11-22
---

# csharp rules

## Scope

- A repository, SQL or migration change is written to the `data-access-layer` standard,
  not to this one. When a task needs a schema change it does not name, finish the C# part
  and report the need as an open item, naming the table and columns.


## Security floors

- Never allow `unsafe-inline` or `unsafe-eval` in a production CSP, disable HTTPS redirection or
  HSTS, or use a wildcard CORS origin (or `AllowAnyOrigin()` with `AllowCredentials()`).
- Never leave an endpoint that touches data without authorization, or trust client-side
  validation as the only validation.
- Never persist or build a path from a client-supplied filename.
- Never log tokens, secrets, connection strings, PII or raw user queries.
- Never expose access tokens to the browser.

## Build files

- Never write generated files into the source tree during a build, use backslash paths, or
  leave a property condition unquoted.
- Never put a version on a `PackageReference` where Central Package Management is on.
