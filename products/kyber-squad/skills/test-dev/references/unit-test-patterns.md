---
name: test-dev/unit-test-patterns
description: xUnit unit test patterns — NSubstitute mocking, FluentAssertions, naming conventions, arrange-act-assert.
---

# Unit Test Patterns (.NET)

## Technology Stack

- **Framework:** xUnit
- **Mocking:** mock-library choice follows **<test-coding-standard>** § Stack
- **Assertions:** FluentAssertions (`result.Should().Be(...)`)
- **Coverage:** Meet the coverage floor the host declares under `review.coverage` in its Kyber-Weave configuration and `kyber-weave review gates` reports

---

## Naming Convention

Test naming follows **<test-coding-standard>** § Stack.

Examples:
- `CreateJob_WhenDuplicateArtifact_ThrowsConflictException`
- `GetById_WhenNotFound_ReturnsNull`
- `ProcessChunk_WithValidInput_ReturnsEmbedding`

---

## Arrange / Act / Assert Structure

```csharp
[Fact]
public async Task CreateJob_WhenDuplicateArtifact_ThrowsConflictException()
{
    // Arrange
    var repo = Substitute.For<IIngestionJobRepository>();
    repo.ExistsAsync(Arg.Any<string>()).Returns(true);
    var sut = new IngestionJobService(repo);

    // Act
    var act = () => sut.CreateAsync("duplicate-artifact.pdf");

    // Assert
    await act.Should().ThrowAsync<ConflictException>()
        .WithMessage("*already exists*");
}
```

**Rules:**
- Structural conventions (blank-line separation, no inherited test logic) follow
  **<test-coding-standard>** § Stack.
- One logical assertion cluster per test — don't assert multiple independent outcomes.

---

## NSubstitute Patterns

```csharp
// Create substitute
var repo = Substitute.For<IIngestionJobRepository>();

// Configure return value
repo.GetByIdAsync(Arg.Any<int>()).Returns(new IngestionJob { Id = 1 });

// Configure null return
repo.GetByIdAsync(999).Returns((IngestionJob?)null);

// Verify call was made
await repo.Received(1).SaveAsync(Arg.Is<IngestionJob>(j => j.Status == JobStatus.Complete));

// Configure to throw
repo.GetByIdAsync(-1).ThrowsAsync<InvalidOperationException>();
```

---

## Test Data Builders

Builder-class usage for complex domain objects follows **<test-coding-standard>** § What to assert:

```csharp
var job = new IngestionJobBuilder()
    .WithStatus(JobStatus.Pending)
    .WithArtifactPath("manual.pdf")
    .Build();
```

Builders live next to the tests they serve, typically under a `Builders/` folder in the test project.

---

## Hard Rules

- **No `Thread.Sleep` or arbitrary delays.** Timeout-safe waiting follows
  **<test-coding-standard>** § What to assert.
- **No test that only asserts it doesn't throw.** Assert the actual observable outcome.
- **Tests must be isolated and order-independent.** Each test arranges its own data; no shared mutable state between test methods.
- Regression-test naming follows **<test-coding-standard>** § What to assert (link to issue ID in a comment if one exists).
- Test domain logic and service classes only — no infrastructure, no DB, no HTTP.

---

## Python (when needed)

- **Framework:** pytest with `parametrize` for table-driven cases
- **Mocking:** `unittest.mock` or `pytest-mock`; mock-boundary rules follow **<test-coding-standard>** § Stack
- **Naming:** `test_<unit>_<scenario>` snake_case
- **Coverage:** meet the coverage floor the host declares under `review.coverage` in its Kyber-Weave configuration and `kyber-weave review gates` reports (`pytest-cov`)

---

## Run Command

```powershell
dotnet test --filter <TestClass>           # run specific class
dotnet test --filter "Category=Unit"       # run by category
dotnet test                                # run all tests
```
