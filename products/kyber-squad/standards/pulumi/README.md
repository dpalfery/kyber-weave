---
id: standards/pulumi
title: "Pulumi coding standard"
doc-type: coding-standard
status: draft
technology: pulumi
owner: unassigned
last-reviewed: 2026-08-16
---

# Pulumi coding standard

How infrastructure-as-code is written in this repository. Agents and skills resolve this
document as `<pulumi-coding-standard>`, so it outranks the defaults a portable agent shipped
with. Language-level C# decisions live in `<csharp-coding-standard>`; this file is the Pulumi
and Azure IaC overlay.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Stack

- **Language:** C#. Programs use `await Deployment.RunAsync(() => { ... })` and strongly
  typed resource args.
- **Provider:** Pulumi Azure Native. It maps to Azure Resource Manager APIs and is the
  first choice for new Azure work. Azure Classic or a direct Azure SDK call is a documented
  exception for the case Azure Native cannot model the resource or behavior.
- **Layout:** one Pulumi project per service or bounded platform slice, with a separate
  stack per environment. Split into further projects or micro-stacks only when deployment
  cadence, ownership, RBAC, blast radius, or performance clearly differ.
- **Cross-stack contracts:** `StackReference`. Keep the exported surface small and explicit.

## Program shape

Prefer small helper methods and `ComponentResource` types over a large top-level program.
A stack that is one flat file of two hundred resources has no unit anyone can reuse or
reason about, and its preview is unreadable.

Every reusable component has a narrow input surface, explicit outputs, and consistent
parent/child relationships (`ResourceOptions.Parent`). Check for an existing component
before writing a new one.

Export stack outputs at the top level of the program, not from inside `Apply`.

## Outputs and dependencies

- **Pass an `Output<T>` straight into the next resource's input.** That is what tells Pulumi
  one resource depends on another.
- **Never create a resource inside `Apply`.** The engine cannot see it, so it is not
  tracked, not diffed, and not reliably deleted. `Apply` transforms a value; it does not
  build infrastructure.
- **`DependsOn` is for dependencies the graph cannot see** — an ordering the provider
  requires but no value expresses. Adding it where an output would do serializes work for
  no reason.

## Configuration and secrets

Environment-specific values live in stack config, not hardcoded constants. Region,
subscription, and environment are explicit config, not compiled-in facts.

- Secret configuration is set with `pulumi config set --secret` and read with
  `RequireSecret` / `GetSecret`, so it is encrypted in state.
- Preserve secret flow end to end. If an output is secret, assume every value derived from
  it is secret too.
- Nothing sensitive is written to a plain output, an export, a log line, or a serialized
  diagnostic — outputs land in state and in CI logs.
- State lives in a remote backend with access control. It is never committed.

## Names

Azure resource names are deterministic from app, environment, region, and purpose, and they
follow the host's Azure naming standard (resolved through the `azure-naming` skill). A
timestamp or an unmanaged random value produces a different name every run and therefore a
replacement.

Let Pulumi auto-name the *logical* resource only when nothing outside the stack depends on
the Azure name. Where a name must be stable — a DNS label, a Key Vault, a storage account —
set it explicitly and treat it as an interface. Changing it destroys and recreates the
resource unless an alias records the rename.

## Resource safety and lifecycle

- Use `Protect = true` for critical shared resources where accidental deletion would be
  costly.
- Use `ResourceOptions` deliberately: `Parent`, `DependsOn`, `Protect`, aliases for
  refactors, and a custom provider only when the default is wrong.
- Prefer imports, aliases, or a controlled migration over replacement when adopting or
  renaming existing infrastructure.
- Keep previews clean and understandable before proposing `pulumi up`. A change that
  replaces a stateful resource must be visible before it is approved, not discovered on
  apply.

## Azure composition

Start from resource groups, identities, networking, and compute as composable components.
Apply consistent Azure tags — owner, environment, cost centre, system — through a stack
transformation rather than by remembering at each call site.

Resource sizes are chosen deliberately and stated. A default that happens to be expensive
is still a decision.

## Before merging

- `pulumi preview` output goes in the pull request.
- Call out replacement risk, data-loss risk, and cross-stack impact in the same change.
- Infrastructure logic — a component's computed properties, a naming helper — has unit
  tests. Testing the whole stack is not the point; testing the code that computes an
  argument is.
- When a provider or API-version choice is non-obvious, cite the Pulumi docs or the
  `azure-native` registry page, pinned to the package version in use.

## Commands

```bash
pulumi preview              # required before any apply path
pulumi up                   # apply a reviewed preview
pulumi stack output         # read agreed cross-stack / CI contracts
dotnet test                 # component and helper unit tests
```
