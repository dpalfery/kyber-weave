# KyberWeave.Core.Squad

The Kyber-Squad engine, split out of `KyberWeave.Core`
([ADR 0057](../../docs/adr/0057-squad-and-docs-are-their-own-assemblies.md)). It references
Core and never `KyberWeave.Core.Docs`.

The engine's rules live in [`../KyberWeave.Core/AGENTS.md`](../KyberWeave.Core/AGENTS.md) and
apply here unchanged. Read it, and [`/AGENTS.md`](../../AGENTS.md), first. The design is in
[`docs/kyber-squad/architecture.md`](../../docs/kyber-squad/architecture.md).
