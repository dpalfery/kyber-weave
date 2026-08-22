---
id: standards/azure
title: azure coding standard
doc-type: coding-standard
status: draft
technology: azure
owner: unassigned
last-reviewed: 2026-08-16
---

# azure coding standard

How code that talks to Azure is written in this repository. Agents and skills resolve this
document as `<azure-coding-standard>`, so it outranks the defaults a portable agent shipped
with.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`.

## Identity before secrets

- **Managed identity or workload identity federation** is the default for service-to-service
  authentication. A connection string or account key is a fallback that needs a reason.
- Where a secret is unavoidable, it lives in Key Vault and is read at runtime. Never a
  literal, never a committed config file, never a log line.
- Credentials are acquired through the SDK's credential chain (`DefaultAzureCredential` or a
  narrower type), so local development and production differ by configuration rather than by
  code path.

## Use the SDK

The official SDK handles retries, throttling, paging and API versions. Raw REST calls are for
the case the SDK does not cover, and then they handle 429 responses with the `Retry-After`
header, follow continuation tokens, and pin an API version.

## Failure is normal

A network call to a cloud service fails routinely, so:

- Every call has a timeout. An operation with no deadline is one that can hang forever.
- Retries are bounded, exponential, and jittered — the SDK's own policy unless you can say why
  it is wrong.
- A retry on a non-idempotent operation needs an idempotency key or it is a duplicate.
- What is not retryable — a 403, a 404, a validation error — fails fast rather than burning
  the budget.

## Async and batching

I/O is async all the way down; a `.Result` or `.Wait()` on a cloud call is a deadlock waiting
for load. Prefer one batched operation to many small ones — for Storage and Cosmos DB this is
the difference between a request and a bill.

## Observability

Log the operation, the resource, the outcome and the correlation id, through the platform's
logging abstraction rather than console output. Never log the credential, the token, the
connection string, or the customer's data.

Failures carry enough context to identify which resource and which request, because the same
error message from three services is not diagnosable.

## Cost

An approach that scales in cost with something other than usage — a poll loop, a per-row
query, a container that idles hot — gets called out at design time. Cloud cost is a design
property, not an operations problem discovered later.

## Configuration

Everything environment-specific is configuration: endpoints, resource names, tenant ids. A
resource name compiled into the binary is a promise about someone else's subscription.
