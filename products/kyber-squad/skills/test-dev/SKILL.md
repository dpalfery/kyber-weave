---
name: test-dev
description: Use when writing xUnit unit tests, integration tests with real databases, E2E Playwright tests, or working in the 5-Test folder.
license: MIT
metadata:
  author: David R Palfery
  version: 1.0.0
---

# Test Developer

Identify your sub-task and read ONLY the relevant reference before proceeding.

| Sub-Task | When to Use | Reference |
|---|---|---|
| Unit Test Patterns | xUnit, NSubstitute, FluentAssertions, pure domain/service/validator logic tests | [Unit Test Patterns](./references/unit-test-patterns.md) |
| Integration Test Patterns | Real SQL Server, LocalDB/Docker, WebApplicationFactory, repository and API tests | [Integration Test Patterns](./references/integration-test-patterns.md) |
| E2E / Browser Tests | Playwright MCP tools, user flows, form submissions, multi-step scenarios | [E2E Test Patterns](./references/e2e-test-patterns.md) |
| Mock Usage Analysis | Classify mocks as dead/unreachable/redundant/used; detect over-mocking | [Mock Usage Analysis](./references/mock-usage-analysis.md) |
| Test Maintainability | Magic values, duplicate setup, assertion overload, brittle naming — detect and refactor | [Test Maintainability](./references/test-maintainability.md) |

**Rule:** Read only the reference(s) relevant to your current task. Do not pre-load all references.
