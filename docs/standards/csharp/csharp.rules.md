---
id: standards/csharp/rules
title: "csharp rules"
doc-type: rule
status: current
technology: csharp
owner: dpalfery
last-reviewed: 2026-11-26
---

# csharp rules

## Interface first, unit tested

The seam register ([`docs/reference/seams.md`](../../reference/seams.md)) is the authority for
which seams exist and where each adapter and fake lives. `AGENTS.md`'s "Seams" section is a summary
of that register; when the two disagree — the summary narrower, broader, or in conflict — the
register wins.

- Write the interface before the class. A class that does work depends on interfaces, not on a
  concrete service that touches an outside resource; the sealed framework types the register lists
  as exceptions may be used directly.
- Every outside resource — the disk, a child process, the environment, the console, the clock,
  identifiers (`Guid.NewGuid`, `Random`), a cross-process lease, an embedded resource,
  configuration, the network, the credential store and shipped content — is reached only through
  its seam, as the register lists it: one
  interface, its adapters where the resource lives, and its fake in the shared test support
  project. Only an adapter, or a composition root wiring one at startup, touches the resource.
- New behaviour ships with an in-memory unit test written against the interface.
- Code you can only test through a real resource is a design gap: add or widen the seam and
  write the unit test against it, rather than a slow test.
- There are three kinds of test. A unit test runs in memory. An integration test proves an adapter,
  or a whole flow, against the real resource (real disk, real program); it runs in CI and never in a
  developer's task gate or the review council's gates. A content test reads shipped content through
  the corpus seam and runs with the unit tests. `AGENTS.md` names the project for each.
- The Domain project holds data only. Logic goes in Application.

## Design and code (KW-STD-CS-001 to KW-STD-CS-021)

An id in brackets names the analyzer that enforces the rule. Until the codebase is clean of a rule,
`analyzers/ratchet.globalconfig` holds its analyzer at suggestion; new code follows the rule anyway.

- **KW-STD-CS-001 One source per literal fact.** A path, file name, status word, header, role, id,
  table or column name, environment variable, flag or meaningful number is a `const`, `enum` or
  `nameof()` in one place. Code that matches the fact uses that source, never a re-typed string or pattern.
- **KW-STD-CS-002 One responsibility.** One reason to change per class and per method; split a class
  that does two jobs. Guard clauses return or throw early instead of nesting. [CA1502, CA1506;
  product code only, the analyzers are off for test code]
- **KW-STD-CS-003 Encapsulated and immutable.** `private` fields, `init` properties, records for
  values, `IReadOnly*` collections exposed and no public collection setter. [CA2227]
- **KW-STD-CS-004 Null-safe.** Check with `is null` / `is not null`; guard public arguments with
  `ArgumentNullException.ThrowIfNull`. [CA1062] The null-forgiving `!` is not used in new code.
  No analyzer enforces that, so review does.
- **KW-STD-CS-005 Expected failures are values.** Not found, invalid input or a failed gate returns
  a `Try*` result, a result record or a diagnostic. An exception means a bug or the unexpected.
- **KW-STD-CS-006 Catch discipline.** No empty `catch`. Catch named types; catch bare `Exception`
  only to log and rethrow with `throw;`, never `throw ex;`. One exception: at a documented boundary
  where crashing the host is worse than continuing (a hook entry point, an install step, an Arbiter
  evaluation), `catch (Exception ex) when (ex is not OutOfMemoryException && ex is not
  OperationCanceledException)` is allowed, and only when the handler logs or records the error. [CA1031]
- **KW-STD-CS-007 Open/closed, substitutable.** New behaviour is a new implementation of an
  interface, not another branch in a `switch` over kinds. An implementation honours its interface:
  no `NotImplementedException` or `NotSupportedException` stubs.
- **KW-STD-CS-008 Small interfaces, composition.** Role-sized interfaces, so no class implements a
  member it does not use. Share behaviour by injecting a helper, not through a base class.
- **KW-STD-CS-009 Explicit comparison and culture.** Every comparison method names its
  `StringComparison` and every dictionary or set names its `StringComparer`; casing and formatting
  name their culture. `==` on strings is ordinal and stays allowed. Build strings in a loop with
  `StringBuilder`. [CA1304, CA1305, CA1307, CA1309, CA1310, CA1311] The analyzer set covers method
  calls and formatting only. The collection-construction clause and the `StringBuilder` clause
  (Sonar S1643, which is not referenced) are review-only.
- **KW-STD-CS-010 No primitive obsession.** A domain identifier or value (session key, plan id,
  harness id, money) is a `readonly record struct` wrapper, not a raw `string`, `int` or `Guid`.
- **KW-STD-CS-011 No boolean flag arguments.** A `bool` parameter that switches behaviour becomes an
  `enum` or two methods.
- **KW-STD-CS-012 Disposal.** Wrap every `IDisposable` and `IAsyncDisposable` you create in a `using`
  declaration. A type that owns a disposable field is itself disposable. The container disposes what
  it created. [CA1001, CA1063]
- **KW-STD-CS-013 Async all the way.** No `.Result`, `.Wait()` or `GetAwaiter().GetResult()` outside
  `Main`, and no `async void`. Every async I/O method takes a `CancellationToken` and passes it on.
  Where the work underneath is async, its callers become async up to the entry point. The reverse is
  banned: never wrap synchronous work in `Task.Run` or `Task.FromResult` to give it an async
  signature (a test fake for an async interface may return `Task.FromResult`).
- **KW-STD-CS-014 Collections.** Return `[]`, never `null`. Enumerate an `IEnumerable<T>` once,
  materializing it when it is read twice. Break a dense LINQ chain into named variables.
- **KW-STD-CS-015 Closed vocabularies.** A fixed set of words (status, tier, role) is an `enum` or one
  constants class, parsed fail-closed (KW-STD-INPUT-001).
- **KW-STD-CS-016 Deliberate lifetimes.** Every registration's lifetime is a choice. A singleton never
  captures a scoped or transient service. No `GetService` outside a composition root.
- **KW-STD-CS-017 Typed configuration.** No URL, timeout or path hard-coded in logic. Settings are read
  once through the configuration seam into a typed, validated, immutable record; `IOptions<T>` is
  not required.
- **KW-STD-CS-018 Long-lived HTTP handlers.** Never `new HttpClient()` per call. An adapter owns one
  long-lived `SocketsHttpHandler` with `PooledConnectionLifetime` set.
- **KW-STD-CS-019 No dead code, no duplication.** Delete commented-out code and unused members, and
  extract logic the second time it is written.
- **KW-STD-CS-020 Shared state.** No mutable static state. Shared state is immutable or guarded, and
  cross-process work takes the lease seam.
- **KW-STD-CS-021 Logging.** Log through one logging seam with message templates, under the rules in
  "Logging and untrusted input" below.

## Build and analyzers

- `TreatWarningsAsErrors` is on with `AnalysisMode=all`. A warning is a failure, and a new one
  is a change defect.
- Do not add to `NoWarn` in `Directory.Build.props` without a reason you can state. A
  suppression is a claim that the analyzer is wrong here; say why in the change.
- Public APIs carry XML doc comments. A public type or member without one is incomplete.
- The rule ids in this repository (`KW-*`) are permanent. Add a new id; never reuse or
  renumber one.

## Logging and untrusted input

- Never log tokens, secrets, connection strings, raw PII or prompts; log with message templates
  and strip newlines from a user-supplied value.
- Never build a path from a client-supplied filename, and never add a wildcard CORS origin or an
  `unsafe-inline` / `unsafe-eval` CSP.
