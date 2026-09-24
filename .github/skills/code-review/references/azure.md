# Azure Systems Code Review Best Practices

## Reliability & Resilience
- **Transient Failures:** Ensure the code handles transient failures gracefully (transient fault handling pattern). Verify use of appropriate retry policies (like `Azure.Messaging.ServiceBusClient` retry options, or exponential backoff).
- **Timeouts/Exceptions:** Confirm time-out and exception handling are in place for external calls to Azure services.

## Security & Credentials
- **Secret Management:** Confirm Azure credentials (keys, connection strings) are not hardcoded. Retrieve them from secure sources (Azure Key Vault, environment variables).
- **Managed Identities:** Where possible, ensure code uses Azure Managed Identities or OIDC tokens instead of manual secrets.
- **HTTPS:** Verify that any Azure service calls use HTTPS endpoints.

## Azure SDK & API Usage
- **SDK Over REST:** Check that code leverages official Azure SDK libraries rather than making raw HTTP REST calls.
- **REST Best Practices:** If REST API calls are necessary, ensure proper handling of paging, throttling (429 responses), and correct API versions.

## Operational Excellence (Logging & Monitoring)
- **Telemetry Integration:** Ensure code includes appropriate logging and telemetry (e.g., integrating with Application Insights). Use provided logging mechanisms like `ILogger` instead of silent failures.
- **Critical Events:** Log critical events and errors with enough context to trace issues in production.

## Performance & Scalability
- **Efficiency:** For Azure Storage or Cosmos DB, check for efficient operations (e.g., bulk operations instead of many small calls).
- **Caching:** Ensure caching features (e.g., Azure Cache for Redis) are used correctly if implemented for scalability.
- **Async I/O:** Confirm Azure-specific async I/O patterns are utilized to prevent blocking the main execution thread on slow operations.

## Cost Awareness
- **Cost Efficiency:** Highlight code approaches that could lead to unnecessarily high cloud costs (e.g., a loop making thousands of small function calls or DB queries where a batch solution is possible).
