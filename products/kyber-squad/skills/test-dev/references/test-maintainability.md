---
name: test-dev/test-maintainability
description: Detect and refactor brittle, duplicated, or magic-value tests — 5 categories with before/after examples.
source: https://github.com/dotnet/skills/tree/main/plugins/testing/skills/exp-test-maintainability
---

# Test Maintainability

Detect patterns that make tests expensive to maintain. Apply when a test suite has 3+ occurrences of a pattern — isolated cases may not warrant refactoring.

---

## 5 Detection Categories

### 1. Magic Values

**Symptom:** Literal strings/numbers with no explanation of why they're significant.

```csharp
// BEFORE — what does "3" mean? Why "Honda"?
var result = service.Search("Honda", 3);
Assert.Equal(3, result.Count);
```

**Fix:** Introduce named constants or clearly named variables:

```csharp
// AFTER
const int ExpectedResultCount = 3;
const string TestMake = "Honda";

var result = service.Search(TestMake, maxResults: ExpectedResultCount);
Assert.Equal(ExpectedResultCount, result.Count);
```

---

### 2. Duplicate Setup

**Symptom:** The same setup block copy-pasted across 3+ test methods.

```csharp
// BEFORE — repeated in 5 tests
var api = Substitute.For<IMotorcycleApiClient>();
var cache = Substitute.For<IDistributedCache>();
var service = new SearchService(api, cache, NullLogger<SearchService>.Instance);
```

**Fix:** Extract to a `[Fact]`-class constructor or a private factory:

```csharp
// AFTER
public class SearchServiceTests
{
    private readonly IMotorcycleApiClient _api = Substitute.For<IMotorcycleApiClient>();
    private readonly IDistributedCache _cache = Substitute.For<IDistributedCache>();
    private SearchService CreateSut() =>
        new(_api, _cache, NullLogger<SearchService>.Instance);
}
```

---

### 3. Assertion Overload

**Symptom:** A single test asserts 8+ properties or conditions, mixing concerns.

```csharp
// BEFORE — one test verifies search logic AND response mapping AND caching
Assert.Equal("Honda", result.Make);
Assert.Equal(2023, result.Year);
Assert.True(result.IsAvailable);
Assert.NotNull(result.Thumbnail);
await _cache.Received().SetAsync(...);
```

**Fix:** Split into focused tests — one concern per test:

```csharp
// AFTER — separate test per concern
[Fact] public async Task Search_Returns_Correct_Make() { ... }
[Fact] public async Task Search_Caches_Results() { ... }
[Fact] public async Task Search_Returns_Available_Manuals_Only() { ... }
```

---

### 4. Test Implementation Details

**Symptom:** Test breaks when internal implementation changes, even though behavior is unchanged.

```csharp
// BEFORE — testing that a private method was called (internal detail)
await _repository.Received().ExecuteAsync(
    Arg.Is<string>(s => s.Contains("LEFT JOIN")));  // SQL is internal
```

**Fix:** Test observable behavior (output, state, side effects on public contracts):

```csharp
// AFTER — test what the user cares about
var manuals = await service.GetManualsWithPartsAsync(make: "Yamaha");
Assert.All(manuals, m => Assert.NotEmpty(m.Parts));
```

---

### 5. Fragile Test Names

**Symptom:** Test names describe implementation (`Test_ExecuteAsync_CallsDatabase`) rather than behavior (`Search_With_Valid_Make_Returns_Matching_Manuals`).

**Pattern for good test names:**

```
[MethodOrScenario]_[Condition]_[ExpectedOutcome]
```

Examples:
- `GetById_WithUnknownId_ReturnsNull` — clear failure case
- `Search_WithEmptyQuery_ThrowsArgumentException` — exception scenario
- `Create_WithValidRequest_PersistsAndReturnsNewId` — happy path

---

## Refactoring Priority

| Category | Priority | Why |
|---|---|---|
| Duplicate setup | High | 3+ occurrences = immediate maintenance tax |
| Magic values | High | Blocks understanding and breaks on rename |
| Assertion overload | Medium | Slows diagnosis of failures |
| Implementation detail | Medium | Breaks on internal refactors that don't change behavior |
| Fragile names | Low | Cosmetic but accumulates over time |

---

## When Not to Refactor

- If a test is already clear and the "pattern" appears only once — leave it
- Integration tests that test a full request path may legitimately need wider assertions
- Tests covering security or data integrity often need to verify multiple conditions together

---

## Minimum Occurrence Threshold

Only flag a pattern for team-wide refactoring when it appears **3 or more times** in the same class or closely related tests. Single occurrences are judgment calls, not systemic issues.

---

## References

- [Unit testing best practices](https://learn.microsoft.com/dotnet/core/testing/unit-testing-best-practices)
- [xUnit shared context](https://xunit.net/docs/shared-context)
- [FluentAssertions](https://fluentassertions.com/introduction)
