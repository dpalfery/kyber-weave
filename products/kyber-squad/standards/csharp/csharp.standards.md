---
id: standards/csharp
title: "C# coding standard"
doc-type: coding-standard
status: draft
technology: csharp
owner: unassigned
last-reviewed: 2026-11-22
---

# C# coding standard

How C# is written in this repository. Agents and skills resolve this document as
`<csharp-coding-standard>`.


When this standard is in `status: current`, it is the rule for this technology in this
repository. Portable agents ship no built-in default to fall back on. While it is in
`status: draft` it is a proposal, but a worker never stops for it: it uses the draft as the
best available guidance and names the draft standard it relied on in one line of its report.

> Template. Set `owner` to a row in `catalog.md`, replace `<Solution>` with the host's
> root namespace, review the decisions below, and promote `status` to `current`. Do not
> copy Kyber-Weave's own `docs/standards/csharp/` — that file records one repository's
> decisions, including ones a host may reasonably reverse.

## Naming and layout

### Stack

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

### Model classification and placement

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

### Backend-for-frontend

Applies when the host uses a backend-for-frontend (BFF) pattern in front of a YARP reverse
proxy. Skip this section for a host with no BFF.

- Keep access tokens server-side in encrypted cookies.
- Validate incoming Host headers against an allowlist to prevent header injection.
- CORS uses explicit frontend origins only, with `AllowCredentials` on for cookie auth.
- The BFF serves a fallback route for client-side routing.
- Do not bypass YARP for direct API calls from the BFF.
- Forward Bearer tokens via YARP transforms on every proxied request.
- Browser sessions use cookie authentication with OpenID Connect. The BFF exposes login,
  logout and current-user endpoints and serves no other API responses from its own controllers.
- Keep YARP routes and clusters in configuration, not in `Program.cs`. A new proxied route
  needs its match, cluster, path transform, token forwarding and CORS methods checked.
- Apply security headers to every response, static files included. Order: security headers →
  host-header validation → HTTPS/HSTS → CORS → static files → authentication → authorization →
  `MapReverseProxy` → SPA fallback.
- Static assets get production cache headers. Development may relax the CSP for hot reload.

### Dependencies

New dependencies need a reason: what it does, why the existing stack cannot, and who
maintains it. This repository's data stack is ADO.NET plus FluentMigrator — do not
import Dapper, Entity Framework, or a second migrator because a portable skill
shipped with them.

### Build files

- Projects are SDK-style (`Microsoft.NET.Sdk`, or `.Web`): no `ToolsVersion`, `xmlns`, project
  GUIDs or `ProjectTypeGuids`, explicit `Microsoft.Common.props` / `Microsoft.CSharp.targets`
  imports, `Compile`/`None` lists, `packages.config` or `HintPath` references. Keep an item only
  for non-default metadata (`Update` with `CopyToOutputDirectory`). Remove SDK-generated
  `AssemblyInfo.cs` attributes, or set `GenerateAssemblyInfo` false during a phased migration.
- One root `Directory.Build.props` holds the baseline: target framework, `LangVersion`,
  `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, analysis level and mode,
  `EnforceCodeStyleInBuild` (so `.editorconfig` style fails the build), `Deterministic`, and
  `ContinuousIntegrationBuild` on CI. Project files do not repeat it.
- A nested `Directory.Build.props` (for example `tests/`) chains the parent explicitly with
  `GetPathOfFileAbove`; otherwise MSBuild loads only the nearest file.
- Properties a project may override go in `.props`; anything that reads final project
  properties, and every `<Target>`, goes in `Directory.Build.targets`.
- Central Package Management: one root `Directory.Packages.props` with
  `ManagePackageVersionsCentrally` and `CentralPackageTransitivePinningEnabled`. Use
  `VersionOverride` only for a stated exception. Shared analyzers are `GlobalPackageReference`.
- Project references form a DAG. Shared contracts go in a contracts library, never a cycle.
- Multi-target only a shared library, with target-specific packages under a `TargetFramework`
  condition.

### MSBuild correctness

- Paths carry no drive letter; build them with `NormalizePath` or `Path.Combine`.
- A property condition reads `'$(Prop)' == 'true'`; an unquoted empty property is an
  evaluation error.
- Use built-in tasks (`Copy`, `Delete`, `MakeDir`, `RemoveDir`, `WriteLinesToFile`) rather
  than `<Exec>` with shell commands.
- A target that runs a tool or generator is guarded with
  `'$(DesignTimeBuild)' != 'true' and '$(BuildingProject)' == 'true'`.
- Generated files are written under `$(IntermediateOutputPath)` and added to `Compile` and
  `FileWrites`.
- Every custom target declares `Inputs` and `Outputs` so it skips when up to date. A target
  with unpredictable outputs writes a stamp file with `Touch` and names it as `Outputs`. Make
  generation deterministic (no `DateTime.UtcNow` in generated code).
- Tell the Visual Studio fast up-to-date check about custom files with `UpToDateCheckInput` and
  `UpToDateCheckOutput`.
- Use MSBuild property and item functions instead of `Exec` scripts for path and string work.
- Target `DependsOnTargets` has no cycles; hook the lifecycle with `BeforeTargets="BeforeBuild"`
  or `AfterTargets="Build"`.

## Coding standards

### Style the analyzers already enforce

`.editorconfig` is the source of truth. The choices worth stating because other .NET
repositories disagree:

- **Explicit types, never `var`** — including where the type is apparent.
  Encode this in `csharp_style_var_*`.
- **File-scoped namespaces.**
- **Predefined type keywords** — `string`, `int`, `bool`, never `String` or `Int32`.
- **Allman braces**, and a new line before `else`, `catch` and `finally`.

### Types

**Seal by default.** A public type is `sealed` unless something derives from it.
Inheritance is a deliberate extension point, not the default shape of a class.

**Records for values, classes for behaviour.** A record models a value the code passes
around and compares. Something with dependencies or lifetime is a class. Request and
response DTOs are `sealed record`.

**Nullable is enabled.** A nullable annotation is a claim; do not answer a warning with
`!` when the honest fix is a check or a different signature.

One top-level type per file, named after the file. Align filename, namespace, and suffix
with the classification below.

### Async

- Methods that return a `Task` take the `Async` suffix.
- Async all the way down. `.Result`, `.Wait()`, and `Thread.Sleep` in async code are
  blocking calls; use `await` and `Task.Delay`.
- Accept a `CancellationToken` on I/O — including controller actions — and pass it
  through. A call that cannot be cancelled is a hang waiting for a bad network.

### Resources

`IDisposable` values are owned. Dispose them with `using` / `await using`, or the type
that holds them implements `IDisposable` and disposes them. A leaked `HttpClient`,
stream, or connection is not a later problem — it is a production incident.

Reuse outbound HTTP through `IHttpClientFactory`. Configure retries, timeouts, and
circuit breaking on the factory with Polly; do not new up `HttpClient` or wrap every
call site in its own policy.

### Errors

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

### Performance

- No database or remote call inside a loop. Batch it, or cache what is safe to reuse.
- LINQ where it reads better; a plain loop where LINQ allocates or enumerates twice on a hot path.
- Watch allocation on hot paths: short-lived objects and boxing drive GC pressure.
- Choose the algorithm for the input size the code will actually see.

### Web API surface

`[ProducesResponseType]` attributes match the `TypedResults` the action returns. Controllers
hold no business logic: they map the request, call an application service and map the result.
Use `[ApiController]` and `[Route("api/[controller]")]`.

### Observability

- Configure OpenTelemetry tracing, metrics and logging against one OTLP exporter with
  `AddAspNetCoreInstrumentation` and `AddHttpClientInstrumentation`, and set the service name
  (`ResourceBuilder.AddService`) on all three. To show `TraceId` and `SpanId` in log output use
  the JSON console formatter with `IncludeScopes`. Endpoints and connection strings come from
  configuration.
- Define one `ActivitySource` per component and register it with `AddSource` (a wildcard such
  as `<Solution>.*` is fine). An unregistered source emits nothing.
- Use OpenTelemetry semantic-convention names for well-known tags. Never put a
  high-cardinality value (a request body, a free-text query) in a tag; use an event.
- Production samples with `ParentBased(TraceIdRatioBased(...))`. To verify spans, add the
  console exporter temporarily and use `AlwaysOnSampler`; remove both before shipping.
- Azure Monitor uses `UseAzureMonitor` with `APPLICATIONINSIGHTS_CONNECTION_STRING`.

### Azure AI and retrieval

- Default SDKs: `Azure.AI.OpenAI`, `Azure.Search.Documents`, `Azure.AI.DocumentIntelligence`,
  and `HtmlAgilityPack` for HTML; the host may change them.
- Name the search index through options, never a literal. Do not create an index without
  approval.
- Search is hybrid (vector, keyword, semantic ranking). Batch embeddings per call and index
  documents in batches; continue past a single bad chunk. Format embedded content as
  `Field: Value | Field: Value`.
- Chunk by structure (rows grouped by natural key; layout-aware PDF chunks with overlap, using
  a vision-capable model when diagrams carry meaning) and keep section hierarchy and page,
  section or figure references so answers cite their source.
- Fuse results by de-duplicating on document id, then re-rank. One failed source does not fail the
  answer: the answer says it is partial and names the missing source, and the failure is logged. Azure calls retry transient failures, with a circuit breaker
  where call volume warrants one, and health checks cover the Azure services the app uses.
- Web sources need credibility validation and a rate limit. Agent tool definitions are
  parameterized, and every agent operation is logged with structured operation context.

## Testing

### Tests

Test authorship follows `<test-coding-standard>`. Language-level C# in test files
follows this document.

Test the BFF auth flow end to end: login, cookie, proxy, bearer, API.

## Commands

### The build is the first reviewer

`.editorconfig` and the analyzers are authoritative for formatting and mechanical rules.
`TreatWarningsAsErrors` is on with `AnalysisMode=all`. A warning is a failed build.
Adding an id to `NoWarn` is a repository-wide decision and needs a reason you can state
in the file.

Do not merge code that fails the build, and do not restate analyzer rules here.

### Build speed

- Leave `-m` at its default locally. On CI, match `-m:N` to the runner's vCPUs; more nodes than
  cores thrashes.
- Restore once, then `--no-restore` for build and `--no-build` for test in multi-step CI. Cache
  NuGet through `actions/setup-dotnet` (`cache: true`) keyed on the lock files or projects.
- Disable node reuse (`-p:NodeReuse=false`) at the end of a CI job so workers do not hold file
  locks. `-p:RunAnalyzersDuringBuild=false` is for a fast local loop only; CI runs analyzers,
  and `dotnet format --verify-no-changes` may run as its own job.

## Security and data handling
### HTTP pipeline

Middleware order is:

Security headers → host-header validation → `UseHttpsRedirection` / `UseHsts` → `UseCors` →
`UseStaticFiles` → `UseAuthentication` → `UseAuthorization` → `UseRateLimiter` →
`UseOutputCaching` / `UseResponseCaching` → endpoints. That `UseRateLimiter` position is for a
limiter that partitions by user identity, which needs the authenticated user. A limiter that
partitions only by client IP goes before `UseAuthentication` instead, so a flood is rejected
before any authentication work runs.

Version the API; emit OpenAPI with `Microsoft.AspNetCore.OpenApi` (`AddOpenApi` /
`MapOpenApi`) and serve the UI with Swashbuckle.

Expose `/health` with checks for the dependencies this process needs (database,
queue, downstream API) so an orchestrator can tell liveness from readiness.

Cache responses only where the data is safe to reuse; rate-limit public endpoints.
Measure with the platform diagnostics rather than guessing.

Use route constraints to prevent wrong-type requests from reaching action logic — a route
like `{id:int}` fails routing instead of failing model binding inside the action. A collection
endpoint returns 200 with an empty array, not 404, when the collection is empty; an empty
result is not a missing resource. Actions do not access HttpContext directly; use action
method parameters (`[FromRoute]`, `[FromQuery]`, `[FromBody]`, `CancellationToken`) instead,
so the action stays testable without a fake `HttpContext`.


### Safety

- Parameterized ADO.NET commands, always. String concatenation into SQL is an
  injection, including in a migration.
- Secrets come from User Secrets locally and Key Vault (or the equivalent store) in
  deployed environments.
- Log through `ILogger<T>` with structured properties and a correlation id.
  `Console.WriteLine` is not production logging. Configure providers per environment.
- HTTPS and HSTS are on. Authenticate, then authorize. CORS is explicit. CSRF
  protection is on wherever cookie-authenticated browser clients can POST. Persist
  Data Protection keys and rotate them.

### Security headers and CSP

This template targets OWASP ASVS Level 2; a host may choose another level and should then
revise this section. Add one security-headers middleware, first in the pipeline, so every
response carries:

- `Content-Security-Policy`: `default-src 'self'` with `script-src`, `style-src`, `font-src` and
  `connect-src` limited to `'self'`, `img-src 'self' data:`, `frame-ancestors 'none'`,
  `base-uri 'self'` and `form-action 'self'`.
- `Strict-Transport-Security: max-age=63072000; includeSubDomains; preload`.
- `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `X-XSS-Protection: 0`,
  `Referrer-Policy: strict-origin-when-cross-origin`, and a `Permissions-Policy` that blocks
  camera, microphone, geolocation and other unused features.

Validate the `Host` header against a configured allowlist.

### Authorization, rate limiting and input

- An endpoint that touches data takes `[Authorize]`; admin endpoints add an admin policy and
  validate the `azp` claim for client isolation.
  Policy names, scopes and roles come from the host's auth design, not from this standard.
- Rate-limit with `RateLimitPartition`, partitioned by user identity where there is one (and
  by IP where there is not; the user OID is the key), using role-based, descriptively named
  policies applied with `[EnableRateLimiting]`. Limits come from configuration, never literals.
- Validate every input before use: `[Required]`, `[StringLength]`, `[Range]` at minimum;
  FluentValidation for business rules. Encode user input before rendering it into HTML.
- CORS: explicit origins from configuration, explicit methods and headers.

### Logging

Never log prompts. A user tag on a span or log is pseudonymous or policy-approved, never raw
PII. Strip newlines and tabs from user-supplied values before logging. Log with
message-template placeholders, never string concatenation.

### File upload

- Raise both size limits together: Kestrel `MaxRequestBodySize` and `FormOptions`
  `MultipartBodyLengthLimit` (or the matching `[RequestSizeLimit]` / `[RequestFormLimits]`).
  Raising one is the common bug.
- Gate in order: extension allowlist, then magic-byte check against the file header. An
  extension alone is spoofable.
- Store a generated name (`Guid` plus the allowed extension) and keep the client's filename,
  which can carry path traversal or reserved names, only as display metadata.
- Browser form uploads need anti-forgery (`AddAntiforgery`, `UseAntiforgery`); non-browser
  clients do not.
- Above roughly 100 MB, stream with `MultipartReader` instead of `IFormFile`, which buffers
  to disk. That path carries `[DisableRequestSizeLimit]` and enforces its own limit.
- With several files (`IFormFileCollection`), validate and store each one independently.

## Done checklist

### Completion

A finishing worker proves its change with three commands: the build, the tests covering the
change, and the formatter check. Build first — a format pass against a stale binary proves
nothing, and the build is also where the analyzers run.

```bash
dotnet build -c Release                                 # the build; warnings are errors
dotnet test --filter "FullyQualifiedName~<TestClass>"   # the tests covering your change
dotnet format --verify-no-changes                       # the formatter check
```

`dotnet build` is the build gate, and where `TreatWarningsAsErrors` is on a warning fails it.
The `dotnet test` line above is filtered to the classes your change covers: the unfiltered run is
the gate the review council and CI run, and a dev agent never runs it. `dotnet format
--verify-no-changes` is the format check — it reports a whitespace or style difference the build
does not, and prints nothing when the tree is formatted.
