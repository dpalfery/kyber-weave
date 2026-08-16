---
id: standards/pulumi
title: pulumi coding standard
doc-type: coding-standard
status: draft
technology: pulumi
owner: unassigned
last-reviewed: 2026-08-16
---

# pulumi coding standard

How infrastructure is written in this repository. Agents and skills resolve this document as
`<pulumi-coding-standard>`, so it outranks the defaults a portable agent shipped with.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`.

## Outputs carry the dependency graph

- **Pass an `Output` straight into the next resource's input.** That is what tells Pulumi one
  resource depends on another.
- **Never create a resource inside `apply()`.** The engine cannot see it, so it is not
  tracked, not diffed, and not reliably deleted. `apply()` transforms a value; it does not
  build infrastructure.
- **`dependsOn` is for dependencies the graph cannot see** — an ordering the provider requires
  but no value expresses. Adding it where an output would do serializes work for no reason.

## Structure

Group related resources into a `ComponentResource` with a clear name and a parent
relationship. A stack that is one flat file of two hundred resources has no unit anyone can
reuse or reason about, and its preview output is unreadable.

Check for an existing component before writing a new one.

## Names

Let the provider auto-name resources unless something outside the stack depends on the exact
name. Where a name must be stable, set it explicitly and treat it as an interface — changing
it destroys and recreates the resource.

Never derive a name from a timestamp or an unmanaged random value: every run produces a
different name and therefore a replacement.

## Secrets

- Secret configuration is set with `pulumi config set --secret` and read with
  `requireSecret` / `getSecret`, so it is encrypted in state.
- Nothing sensitive is written to a plain output, an export, or a log line — outputs land in
  state and in CI logs.
- State lives in a remote backend with access control. It is never committed.

## Tagging

Every resource that supports tags carries the ones this organization bills and audits on —
owner, environment, cost centre, system. Enforce it with a stack transformation rather than by
remembering.

## Sizing and quotas

Resource sizes are chosen deliberately and stated. A default that happens to be expensive is
still a decision.

## Before merging

- `pulumi preview` output goes in the pull request. A change that replaces a stateful resource
  must be visible before it is approved, not discovered on apply.
- Infrastructure logic — a component's computed properties, a naming helper — has unit tests.
  Testing the whole stack is not the point; testing the code that computes an argument is.
