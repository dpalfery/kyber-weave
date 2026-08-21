---
name: test-dev/unit-test-patterns
description: xUnit unit test patterns — NSubstitute mocking, FluentAssertions, naming conventions, arrange-act-assert.
---

# Unit Test Patterns (.NET)

## Technology Stack

- **Framework:** xUnit
- **Mocking:** NSubstitute (prefer over Moq — leaner syntax, no `Setup`/`Returns` duplication)
- **Assertions:** FluentAssertions (`result.Should().Be(...)`)
- **Coverage:** Meet the coverage floor the host declares under `review.coverage` in its Kyber-Weave configuration and `kyber-weave review gates` reports

---

## Naming Convention

```
MethodName_StateUnderTest_ExpectedBehavior
```

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
- Blank lines between Arrange, Act, Assert sections.
- One logical assertion cluster per test — don't assert multiple independent outcomes.
- No test logic shared via inheritance — use fixtures and builders instead.

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

Prefer builder classes over inline object creation for complex domain objects:

```csharp
var job = new IngestionJobBuilder()
    .WithStatus(JobStatus.Pending)
    .WithArtifactPath("manual.pdf")
    .Build();
```

Builders live next to the tests they serve, typically under a `Builders/` folder in the test project.

---

## Hard Rules

- **No `Thread.Sleep` or arbitrary delays.** Use `await`, `WaitForAsync`, or polling helpers with timeout.
- **No test that only asserts it doesn't throw.** Assert the actual observable outcome.
- **Tests must be isolated and order-independent.** Each test arranges its own data; no shared mutable state between test methods.
- **Regression tests for every bug fix.** Test name must encode the broken scenario (link to issue ID in a comment if one exists).
- Test domain logic and service classes only — no infrastructure, no DB, no HTTP.

---

## Python (when needed)

- **Framework:** pytest with `parametrize` for table-driven cases
- **Mocking:** `unittest.mock` or `pytest-mock`; mock at I/O boundary, never inside domain logic
- **Naming:** `test_<unit>_<scenario>` snake_case
- **Coverage:** meet the coverage floor the host declares under `review.coverage` in its Kyber-Weave configuration and `kyber-weave review gates` reports (`pytest-cov`)

---

## Run Command

```powershell
dotnet test --filter <TestClass>           # run specific class
dotnet test --filter "Category=Unit"       # run by category
dotnet test                                # run all tests
```
