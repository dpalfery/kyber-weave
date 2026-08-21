# .NET (C#) Code Review Best Practices

## Coding Standards & Linters
- **Automation First:** Ensure code adheres to .NET recommended style conventions. Check that the PR doesn't introduce linter warnings or style violations (.editorconfig with .NET code style rules).
- **ReSharper InspectCode:** The mechanical half of this section is a declared gate, not a reading exercise. Its findings arrive through the `static-analysis-triage` lens; the `resharper-clt` skill owns how the gate is declared and what its inspections mean. Do not re-derive by eye what the tool already reported by rule id.

## Asynchronous & Concurrency Patterns
- **Async/Await:** Check correct usage of async/await and task-based patterns. Methods returning tasks should use the `Async` suffix.
- **No Blocking Calls:** Asynchronous code should never use blocking calls like `Thread.Sleep` – use `Task.Delay` or awaitable alternatives instead.
- **Thread Safety:** Watch for potential concurrency issues (e.g., shared state without locks, thread-unsafe collections). Ensure synchronization and cancellation tokens are handled properly.

## Memory Management and Resource Disposal
- **IDisposable:** Verify that `IDisposable` objects are properly disposed (using the `using` statement or implementing IDisposable pattern) to avoid resource leaks.
- **Garbage Collection:** Look for high garbage collection pressure (e.g., frequent creation of short-lived objects or boxing/unboxing) and suggest optimizations if needed.

## Dependency Injection & Architecture
- **DI:** Use Dependency Injection (DI) for managing external dependencies and services. Ensure proper DI configuration (no hard-coded dependencies).
- **Patterns:** Check that the code respects established design patterns (e.g., correct use of middleware, options pattern, etc. in ASP.NET).

## Error Handling & Exceptions
- **Specific Catch:** Ensure exceptions are properly handled – catch specific exception types rather than broad `catch (Exception)` blocks.
- **No Silent Failures:** Expected exceptions must be caught and logged. Unexpected exceptions should be allowed to surface. Code should not swallow exceptions silently.
- **Rethrowing:** Use recommended patterns for rethrowing (`throw;` without altering the stack).

## Performance Considerations
- **LINQ Usage:** Flag inappropriate use of LINQ that could degrade performance (e.g., replacing a trivial loop with LINQ that causes inefficiency).
- **External Calls:** Ensure no excessive database or API calls inside loops (suggest caching or batching).
- **Algorithms:** Confirm that algorithms are efficient.

## Security & Data Handling
- **Injection Prevention:** Use parameterized queries to avoid SQL injection. Check for input validation and XSS prevention.
- **Secrets:** Check that sensitive data is handled using secure primitives (do not store secrets in plain strings or config—use Azure Key Vault or SecureString).
- **Cryptography:** Verify usage of recommended algorithms and .NET security libraries.

## Logging & Telemetry (Observability)
- **Appropriate Logging:** Ensure that critical events, errors, and performance data are logged appropriately (without leaking sensitive info).
- **Conventions:** Confirm logging follows guidelines (e.g., using `ILogger` with appropriate log levels, not using `Console.WriteLine` for production code).

## Testing & CI Integration
- **Coverage:** Verify that new or changed code is accompanied by relevant unit or integration tests covering edge cases.
- **Async Tests:** Check that asynchronous methods have tests covering different timing scenarios, edge values, and exception conditions.
