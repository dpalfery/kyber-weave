---
id: standards/csharp
title: "C# coding standard"
doc-type: coding-standard
status: draft
technology: csharp
owner: unassigned
last-reviewed: 2026-08-16
---

# C# coding standard

How C# is written in this repository. Agents and skills resolve this document as
`<csharp-coding-standard>`, so it outranks the defaults a portable agent shipped with.

> Template. Set `owner` to a row in `catalog.md`, replace `<Solution>` with the host's
> root namespace, review the decisions below, and promote `status` to `current`. Do not
> copy Kyber-Weave's own `docs/standards/csharp/` — that file records one repository's
> decisions, including ones a host may reasonably reverse.

## Stack

- **Target:** ASP.NET Core on .NET 10, C# 13.
- **HTTP surface:** MVC controllers (`ControllerBase`), not minimal APIs. Return
  `TypedResults` (not `Results.Ok` / bare `Ok()`). Request and response bodies are
  `sealed record` DTOs; never return a domain entity or persistence row from an action.
- **Data access:** parameterized ADO.NET in the persistence project. Dapper and Entity
  Framework are out of scope. FluentMigrator owns schema change.
- **Composition:** constructor DI. Configuration is the Options pattern, overridden per
  environment through `appsettings.{Environment}.json`.

Application and API code consumes `IRepository<T>` (or an equivalent persistence
abstraction). It does not open connections, write SQL, or author migrations.

## The build is the first reviewer

`.editorconfig` and the analyzers are authoritative for formatting and mechanical rules.
`TreatWarningsAsErrors` is on with `AnalysisMode=all`. A warning is a failed build.
Adding an id to `NoWarn` is a repository-wide decision and needs a reason you can state
in the file.

Do not merge code that fails the build, and do not restate analyzer rules here.

## Style the analyzers already enforce

`.editorconfig` is the source of truth. The choices worth stating because other .NET
repositories disagree:

- **Explicit types, never `var`** — including where the type is apparent.
  Encode this in `csharp_style_var_*`.
- **File-scoped namespaces.**
- **Predefined type keywords** — `string`, `int`, `bool`, never `String` or `Int32`.
- **Allman braces**, and a new line before `else`, `catch` and `finally`.

## Types

**Seal by default.** A public type is `sealed` unless something derives from it.
Inheritance is a deliberate extension point, not the default shape of a class.

**Records for values, classes for behaviour.** A record models a value the code passes
around and compares. Something with dependencies or lifetime is a class. Request and
response DTOs are `sealed record`.

**Nullable is enabled.** A nullable annotation is a claim; do not answer a warning with
`!` when the honest fix is a check or a different signature.

One top-level type per file, named after the file. Align filename, namespace, and suffix
with the classification below.

## Model classification and placement

Replace `<Solution>` with this repository's root namespace. A database key, a set of
public auto-properties, a default initializer, an attribute, or a property count does
not make a type an Entity.

| Kind | Where | Shape |
|---|---|---|
| Shared DTO | `<Solution>.Contracts.Models` | Property bag, no domain invariant. Suffix `Dto`. |
| Use-case DTO | `<Solution>.Application` | Same shape, same `Dto` suffix, not shared across layers. |
| Entity | `<Solution>.Domain/Entities` | Stable identity plus a business invariant or legal state transition. Controlled construction and mutation; no getter/setter-only padding. |
| Value object | `<Solution>.Domain/ValueObjects` | Immutable, equality by value. May carry domain behaviour; has no independent identity. |
| Persistence row | `<Solution>.Persistence` (private) | Storage/schema projection. Mapped at the adapter boundary. Not a shared contract and not a Domain entity. |

`<Solution>.Contracts` contains interfaces only; `<Solution>.Contracts.Models` is the
approved location for shared DTOs. Any exception is an architecture decision with
focused behaviour tests, not a convenience.

Do not add getter/setter-only tests to pad coverage.

## Async

- Methods that return a `Task` take the `Async` suffix.
- Async all the way down. `.Result`, `.Wait()`, and `Thread.Sleep` in async code are
  blocking calls; use `await` and `Task.Delay`.
- Accept a `CancellationToken` on I/O — including controller actions — and pass it
  through. A call that cannot be cancelled is a hang waiting for a bad network.

## Resources

`IDisposable` values are owned. Dispose them with `using` / `await using`, or the type
that holds them implements `IDisposable` and disposes them. A leaked `HttpClient`,
stream, or connection is not a later problem — it is a production incident.

Reuse outbound HTTP through `IHttpClientFactory`. Configure retries, timeouts, and
circuit breaking on the factory with Polly; do not new up `HttpClient` or wrap every
call site in its own policy.

## Errors

- **Catch what you can name.** Operational failures are caught by type and translated
  into a diagnostic, a log line, an exit code, or RFC 7807 `ProblemDetails`. A bare
  `catch (Exception)` around code that can fail for a reason you have not thought
  about hides the reason.
- **Never swallow.** A failure that produces no diagnostic, no log line and no exit
  code is indistinguishable from success. If swallowing is genuinely intended, the
  comment says why.
- Rethrow with `throw;`, never `throw ex;`.
- Invalid input is `TypedResults.ValidationProblem` / `ProblemDetails`, not a bare
  string. Register `AddProblemDetails()` and `UseExceptionHandler()`.

## Safety

- Parameterized ADO.NET commands, always. String concatenation into SQL is an
  injection, including in a migration.
- Secrets come from User Secrets locally and Key Vault (or the equivalent store) in
  deployed environments — never a literal, never a committed config file, never a
  log line.
- Log through `ILogger<T>` with structured properties and a correlation id.
  `Console.WriteLine` is not production logging. Configure providers per environment.
- HTTPS and HSTS are on. Authenticate, then authorize. CORS is explicit. CSRF
  protection is on wherever cookie-authenticated browser clients can POST. Persist
  Data Protection keys and rotate them.

## HTTP pipeline

Middleware order is:

`UseHttpsRedirection` → `UseCors` → `UseRateLimiter` → `UseAuthentication` →
`UseAuthorization` → `UseOutputCaching` / `UseResponseCaching` → endpoints.

Version the API. Emit OpenAPI with `Microsoft.AspNetCore.OpenApi` (`AddOpenApi` /
`MapOpenApi`) and serve the UI with Swashbuckle.

Expose `/health` with checks for the dependencies this process needs (database,
queue, downstream API) so an orchestrator can tell liveness from readiness.

Cache responses only where the data is safe to reuse. Rate-limit public endpoints.
Measure with the platform diagnostics rather than guessing.

## Dependencies

New dependencies need a reason: what it does, why the existing stack cannot, and who
maintains it. This repository's data stack is ADO.NET plus FluentMigrator — do not
import Dapper, Entity Framework, or a second migrator because a portable skill
shipped with them.

## Tests

Test authorship follows `<test-coding-standard>`. Language-level C# in test files
follows this document. New behaviour ships with the test that would fail without it.

## Commands

```bash
dotnet watch              # hot reload
dotnet build -c Release   # production build
dotnet test               # test run
dotnet run                # local run
```
