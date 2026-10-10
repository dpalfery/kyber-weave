---
name: test-dev/integration-test-patterns
description: Integration test patterns — real SQL Server, LocalDB/Docker, WebApplicationFactory, repository and API contract tests.
---

# Integration Test Patterns (.NET)

## Database Testing

Never mocking the database in integration tests follows **<test-coding-standard>** §
Layer boundaries. Use LocalDB, Docker, or a dedicated test database. Mocked DB tests
pass while prod migrations break — this has caused incidents before.

---

## Test Layer Boundaries

| Layer | Type | Infrastructure |
|---|---|---|
| Domain | Unit | None — pure logic, no external deps |
| Application | Unit | Mocked repositories (NSubstitute) |
| Persistence (Repository) | Integration | Real SQL Server — LocalDB or Docker |
| API (Controller) | Integration | WebApplicationFactory + real DB |
| E2E | End-to-end | Full stack — see e2e-test-patterns.md |

---

## Repository Integration Tests

```csharp
public class IngestionJobRepositoryTests : IClassFixture<DatabaseFixture>
{
    private readonly DatabaseFixture _db;

    public IngestionJobRepositoryTests(DatabaseFixture db)
    {
        _db = db;
    }

    [Fact]
    public async Task SaveAsync_WithValidJob_PersistsToDatabase()
    {
        // Arrange
        var repo = new IngestionJobRepository(_db.ConnectionFactory);
        var job = new IngestionJobBuilder().WithStatus(JobStatus.Pending).Build();

        // Act
        var id = await repo.SaveAsync(job);

        // Assert
        var saved = await repo.GetByIdAsync(id);
        saved.Should().NotBeNull();
        saved!.Status.Should().Be(JobStatus.Pending);
    }
}
```

### DatabaseFixture Pattern

```csharp
public class DatabaseFixture : IAsyncLifetime
{
    public ISqlConnectionFactory ConnectionFactory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        string? connStr = Environment.GetEnvironmentVariable("SQL_TEST_CONNECTION_STRING")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=Host_Test;Integrated Security=true";
        ConnectionFactory = new SqlConnectionFactory(connStr);
        await ApplyMigrationsAsync();
    }

    public async Task DisposeAsync() => await CleanDatabaseAsync();
}
```

---

## API Integration Tests (WebApplicationFactory)

```csharp
public class IngestionJobsControllerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public IngestionJobsControllerTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Replace real services with test doubles if needed
            });
        }).CreateClient();
    }

    [Fact]
    public async Task PostJob_WithValidPayload_Returns201()
    {
        // Arrange
        var payload = new { ArtifactPath = "test.pdf" };

        // Act
        var response = await _client.PostAsJsonAsync("/api/ingestion-jobs", payload);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
```

---

## Test Isolation Rules

Data isolation, ordering independence, cleanup, and unique test-record identifiers all
follow **<test-coding-standard>** § Isolation.

---

## Hard Rules

- **Never mock the database** in integration tests. Real-instance requirements follow
  **<test-coding-standard>** § Layer boundaries.
- **No `Thread.Sleep`.** Timeout-safe waiting follows **<test-coding-standard>** § What to assert.
- Regression-test naming follows **<test-coding-standard>** § What to assert.

---

## Run Commands

```powershell
dotnet test --filter "Category=Integration"
dotnet test --filter FullyQualifiedName~RepositoryTests
```
