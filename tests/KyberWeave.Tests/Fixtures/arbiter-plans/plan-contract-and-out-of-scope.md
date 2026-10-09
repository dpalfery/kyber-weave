---
id: fixtures/arbiter-plans/plan-contract-and-out-of-scope
title: Contract table and out of scope
doc-type: plan
status: draft
development-mode: standard
---

# Contract table and out of scope

## Test contract

| Task | Test project or file | Runner filter | Observable behaviour | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `tests/One.cs` | `dotnet test --filter One` | One behaves. | Fails before One exists. | One passes. |
| T2 | `tests/Two.cs` | `dotnet test --filter Two` | Two behaves. | Fails before Two exists. | Two passes. |
| Not-a-task | skipped | skipped | skipped | skipped | skipped |

## Tasks

### T1: one
**Files:** `src/One.cs`

### T2: two
**Files:** `src/Two.cs`

## Out of scope

- `docs/generated/**`
- `artifacts/`
