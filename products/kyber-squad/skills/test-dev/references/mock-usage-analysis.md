---
name: test-dev/mock-usage-analysis
description: Classify mocks as dead, unreachable, redundant, or used; detect over-mocking before it erodes test value.
source: https://github.com/dotnet/skills/tree/main/plugins/testing/skills/exp-mock-usage-analysis
---

# Mock Usage Analysis

This skill classifies mocks in a test suite into four categories to detect over-mocking before it masks real bugs.

**Requires:** Access to both test files and the production code they exercise.

---

## Four Categories

| Category | Meaning | Risk |
|---|---|---|
| **Dead** | Mock is set up but the system under test never calls it | Setup effort wasted; no assertion ever triggers |
| **Unreachable** | Mock is set up for a code path that cannot execute in the test scenario | False sense of coverage; code never actually tested |
| **Redundant** | Mock duplicates what another mock or a real implementation already provides | Brittle — two places to maintain when behavior changes |
| **Used** | Mock is called as expected; assertion verifies the call | Legitimate mock usage |

---

## Detection Workflow

### Step 1 — Inventory Mocks

For each test file, list every substitute/mock:

```csharp
// NSubstitute
var apiClient = Substitute.For<IMotorcycleApiClient>();
var cache = Substitute.For<IDistributedCache>();
var logger = Substitute.For<ILogger<SearchService>>();
```

For each, note:
- What interface it substitutes
- What setups are configured (`Returns(...)`, `ReturnsForAnyArgs(...)`)
- What assertions are made (`Received()`, `DidNotReceive()`)

### Step 2 — Trace Production Code

Read the system under test (SUT). For each mock, trace whether the SUT actually calls the mocked member in the test scenario.

```csharp
// SUT: SearchService.SearchAsync
public async Task<SearchResult> SearchAsync(string query, CancellationToken ct)
{
    var cached = await _cache.GetAsync(query, ct);  // calls IDistributedCache
    if (cached != null) return Deserialize(cached);

    var results = await _apiClient.SearchAsync(query, ct);  // calls IMotorcycleApiClient
    return results;
}
```

If a test sets up `apiClient.SearchAsync(...)` but also seeds `cache.GetAsync(...)` to return a value, the `apiClient` mock is **unreachable** — the cache hit returns early before the API is called.

### Step 3 — Check Each Setup

For each mock setup, ask:
1. Can the SUT reach the line that calls this member in this test's scenario? → If no: **Unreachable**
2. Does another mock/real-object already cover this? → **Redundant**
3. Is it called AND either `Received()` is asserted OR its return value affects observable output? → **Used**
4. Is the mock called during the test without an assertion and without affecting observable output? → **Dead** (unless it's a dependency that must exist to avoid NullReferenceException — then it's *structural*, which is fine)

### Step 4 — Report Findings

Group findings by category with file + line reference:

```
DEAD:
  SearchServiceTests.cs:42  — cache.GetAsync setup never called (cache returns null already)

UNREACHABLE:
  SearchServiceTests.cs:55  — apiClient.SearchAsync setup unreachable; cache hit at line 48 short-circuits

USED:
  SearchServiceTests.cs:38  — apiClient.SearchAsync called and Received() asserted
```

---

## Common Patterns That Indicate Dead Mocks

### Redundant null-return setup

`Substitute.For<T>()` returns `null`/`default` for reference types without any setup — explicit `Returns(null)` is dead:

```csharp
// Dead — Substitute already returns null by default
cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
     .Returns((byte[]?)null);
```

### Setup on unused overload

```csharp
// Dead — SUT calls GetAsync(string, ct) but setup targets GetStringAsync(string)
cache.GetStringAsync(Arg.Any<string>()).Returns("cached");
```

### Logger mock setups

Loggers are almost always structural dependencies (to avoid NPE). Setups on `ILogger` are usually dead unless the test explicitly asserts log output.

---

## Threshold

Apply this analysis when:
- A test suite has **3+ mocks** per test class
- Mocks outnumber real collaborators
- A test fails intermittently after an unrelated refactor (often caused by over-specified mocks)

---

## References

- [NSubstitute received calls](https://nsubstitute.github.io/help/received-calls/)
- [Test doubles best practices](https://learn.microsoft.com/dotnet/core/testing/unit-testing-best-practices#best-practices)
