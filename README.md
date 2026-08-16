# Kyber-Weave

**End-to-end agent governance and deployment system.**

Kyber-Weave is the unified control plane for engineering agent ecosystems. It couples
rigorous documentation governance (DocGraph), supply-chain skill and agent linting
(ContextHygiene), atomic multi-harness deployment (Kyber-Squad), and unified CI diagnostics
into a cohesive platform for teams operating coding agents at scale.

```bash
curl -fsSL https://raw.githubusercontent.com/dpalfery/kyber-weave/main/scripts/install.sh | sh
```

Installs the latest release as self-contained binaries in `~/.local/bin` — no .NET runtime,
no sudo, checksum-verified. Already installed? `kyber-weave update`. [Install details →](docs/install.md)

> **This repository governs its own documentation.** Everything under [`docs/`](docs/)
> carries conformant frontmatter and passes `kyber-weave docs validate` and `docs drift`
> with zero findings. If the ontology were unusable, you would see it here first.

---

## Four Premier Features

Kyber-Weave provides four integrated features for agent governance and lifecycle management:

1. **[DocGraph (Kyber-Docs)](#feature-1--docgraph)** — Typed, governed documentation corpus, drift detection, integrity analysis, and MCP graph retrieval.
2. **[ContextHygiene](#feature-2--contexthygiene)** — Skill and harness agent linting, routing readiness scoring, parity drift detection, and security scanning.
3. **[Kyber-Squad](#feature-3--kyber-squad)** — Unified multi-harness deployment and lifecycle control plane across 10 IDE/CLI harnesses with transactional recovery.
4. **[CI Pipelines](#feature-4--ci-pipelines)** — Unified diagnostic engine with stable `KW-*` rules, SARIF reporting, and GitHub Actions workflows.

---

## Feature 1 — DocGraph

A folder of Markdown is not a corpus. DocGraph makes documentation **typed, owned,
cross-referenced, and queryable** — then holds it to that.

**The opinion.** Eleven closed doc-types, four statuses, a required-key matrix that varies
by type, and a catalog that is the authoritative vocabulary for components and owners. A
`component` cannot be invented one document at a time until nobody can say how many exist.
[The ontology →](docs/documentation-ontology.md)

**The enforcement.** `docs validate` checks conformance. `docs drift` resolves every
documented symbol against a live code index, which catches the failure nothing else does:
after a rename, **prose still reads correctly**. No linter, reviewer, or test notices — only
resolution does. [Governance →](docs/docgraph/governance.md)

**The payoff.** An in-memory graph that ranks on *declared identity* rather than word
frequency, demotes plans and superseded documents so an agent is not handed a closed work
artifact as current guidance, and returns budgeted excerpts that name what they omitted.
[Retrieval →](docs/docgraph/retrieval.md)

```bash
kyber-weave docs init .        # scaffold config/catalog/ontology, protect local cache, deploy the skill
kyber-weave docs validate .
kyber-weave docs drift .
kyber-weave docs integrity-check .     # advisory duplicate/conflict/terminology findings
kyber-weave docs export-graph . --out ./build/doc-graph
```

### Adopting an existing tree

`docs init` does the mechanical half — host config, the catalog that supplies the
component and owner vocabularies, the ontology reference every diagnostic points at, and
the narrow ignored cache path analysis needs before it can persist verdicts or vectors.
It then deploys the **`kyber-weave-docs` skill** through
[APM](https://microsoft.github.io/apm), defaulting to `.agents/skills/` so every
APM-supported client picks it up.

The skill does the judgment half: choosing `doc-type`, verifying that `code-refs` symbols
actually resolve, and retrofitting a whole tree as `status: draft` so a partially adopted
corpus degrades gracefully rather than serving unreviewed metadata as current guidance.
[Adoption guide →](docs/docgraph/onboarding.md)

### The MCP server

`kyber-weave-mcp` is where the feature is actually consumed. Point any MCP client at it:

| Tool | What it does |
|---|---|
| `docs_explore(query, maxDocs, charBudget)` | Ranked documents with frontmatter identity, prose within budget, and code joins as `symbol → file:line` |
| `docs_for_symbol(symbol)` | Reverse lookup: documents whose `code-refs` **formally claim** a symbol — not those that merely mention it |
| `docs_analysis_candidates(kind, cursor, limit, charBudget)` | Capped, stable, read-only duplicate/conflict/terminology evidence with local cost metrics |
| `docs_glossary(term)` | Capped, read-only lookup of managed term senses, scopes, and aliases |

It is a separate binary from the CLI on purpose: JSON-RPC owns stdout and Spectre.Console
also writes there, so separate entry points make stream corruption structurally
impossible. [MCP runbook →](docs/docgraph/mcp-runbook.md)

### Documentation analysis and terminology

`docs integrity-check` extracts line-addressable claims from paragraphs, list items, table rows,
and code fences, then uses DocGraph and one-hop CodeGraph relationships before bounded
lexical search. Exact duplicates are deterministic; potential conflicts and distinct term
senses can be exported for agent review and imported as reusable, content-addressed
verdicts. Source documents are never rewritten.

```bash
kyber-weave docs integrity-check .
kyber-weave docs review export . --out candidates.json
kyber-weave docs review import . --in verdicts.json
kyber-weave docs glossary .          # preview
kyber-weave docs glossary . --write  # merge proposals into one reference document
```

Embeddings are off by default. When enabled, endpoints must resolve only to loopback,
redirects are disabled, and no document text is sent unless the local SQLite cache is
safely ignored. Default hybrid search avoids all-pairs work; `high-recall` is an explicit
quadratic first pass outside the default latency target.
[Analysis and review →](docs/docgraph/analysis.md)

### One external dependency, and it's optional

`docs drift` and `docs export-graph` resolve symbols against a **CodeGraph** index at
`.codegraph/codegraph.db`. CodeGraph is a separate, host-owned tool — Kyber-Weave opens
that index read-only and never writes to it. Without an index, retrieval and `docs validate`
work completely; only the code-join features degrade. [Architecture →](docs/docgraph/architecture.md)

---

## Feature 2 — ContextHygiene

Skills and harness agent definitions are supply-chain artifacts: their text becomes
instructions. ContextHygiene governs both, against different sources of truth.

| Branch | Answers to | Commands |
|---|---|---|
| Skills | the Agent Skills open-format spec | `validate`, `lint`, `scan`, `route`, `catalog`, `pack`, `new` |
| Agents | its own sibling copies across six harnesses | `validate`, `sync-check`, `scan`, `catalog` |

`skill lint` scores routing readiness out of 100 with an auditable per-dimension
breakdown, because a spec-valid skill that never fires is worthless and the spec has
nothing to say about that. `skill route` turns "which skill fires?" into a deterministic,
offline regression test that needs no API key in CI. `agent sync-check` catches the role
that was fixed in `.claude` and left broken in `.cursor`.

[Skill governance →](docs/context-hygiene/skills.md) · [Agent governance →](docs/context-hygiene/agents.md) · [Security scanning →](docs/context-hygiene/security-scanning.md)

---

## Feature 3 — Kyber-Squad

Managing agent roles and skill sets across disparate developer environments leads to
configuration drift, broken permissions, and fragmented workflows. Kyber-Squad provides a
**single, unified lifecycle and deployment control plane** for deploying 20 canonical agents
and 25 skills across 10 coding harnesses.

**The canonical tree.** Maintains 20 canonical agent bodies and 25 canonical skills under
[`products/kyber-squad/`](products/kyber-squad/README.md), governed by strict schemas, model
profiles, and capability profiles. Generated target trees and APM packages are never tracked.

**The compiler & lowering engine.** Normalizes definitions into AgentIR, enforces a semantic
permission lattice (`deny < ask < allow`), and applies deterministic role-skill lowering (with
shared conductor identities and prefixed `role-*` collision namespaces) for harnesses without
native agent primitives.

**Transactional deployment.** Atomic, write-ahead deployments with cross-process OS mutex
leasing (`kyber-weave-squad-<root-key>`), same-filesystem no-overwrite leaf claim/publish,
and compare-and-restore recovery that safely preserves local operator modifications.

```bash
kyber-weave squad install                    # auto-detects targets across 10 harnesses
kyber-weave squad update                     # updates deployments while preserving local edits
kyber-weave squad status                     # verifies file integrity and reports drift
kyber-weave squad doctor                     # checks toolchain prerequisites
kyber-weave squad pack --format all --out ./dist # builds APM and Agent Plugins release packages
```

| Harness Class | Supported Targets |
|---|---|
| Native Agents | Codex (`.codex`), Cursor (`.cursor`), Claude (`.claude`), GitHub Copilot (`.github`), OpenCode (`.opencode`), Kilo (`.kilo`), Warp (`.warp`), Factory Droids (`.factory`) |
| Role-Skill Lowering | Gemini CLI (`.gemini`), Antigravity |

[Adoption & usage guide →](docs/kyber-squad/onboarding.md) · [Architecture →](docs/kyber-squad/architecture.md) · [Requirements & degradation →](docs/kyber-squad/requirements.md)

---

## Feature 4 — CI Pipelines

One diagnostic engine behind all artifact classes: stable `KW-*` rule ids that never
get renumbered, four output formats including SARIF for GitHub code scanning, and severity
gating tuned per branch so adopting scanning does not immediately break every host build.

[Architecture →](docs/ci-pipelines/architecture.md) · [Rule reference →](docs/ci-pipelines/rule-reference.md) · [Workflow runbook →](docs/ci-pipelines/workflows-runbook.md) · [Templates](templates/github-actions/)

---

## Documentation

Full corpus in [`docs/`](docs/README.md). Start with
[the ontology](docs/documentation-ontology.md) if you want to understand the opinion, or
[install](docs/install.md) if you want to run it.

## Architecture

```
src/
  KyberWeave.Core/        shared engine
    Docs/                   Feature 1 — parsing, search, validation, export
    Skills/ Agents/         Feature 2 — governance per artifact class
    Security/               Feature 2 — shared instruction-surface scanner
    Squad/                  Feature 3 — multi-harness deployment engine & transaction store
    Diagnostics/            Feature 4 — rule ids, severities, reports
    CodeGraph/              read-only port over the CodeGraph index
    Configuration/ Text/ Parsing/
  KyberWeave.Cli/         kyber-weave — skill | agent | squad | docs | update
  KyberWeave.Mcp/         kyber-weave-mcp — stdio MCP server
products/
  kyber-squad/            canonical 20 agents, 25 skills, profiles, and schemas
tests/KyberWeave.Tests/
.apm/skills/              kyber-weave-docs — the authoring skill, shipped as an APM package
samples/                  exemplar and deliberately bad skills; routing eval set
templates/github-actions/ host gate workflows
docs/                     this project's own governed corpus
```

Hosts adapt the defaults with [`.kyber-weave/kyber-weave.yml`](docs/configuration.md).

## Develop from source

Requires .NET SDK 10 (`global.json`).

```bash
dotnet restore KyberWeave.sln
dotnet build KyberWeave.sln -c Release
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
```

## Caveats

- **The Agent Skills spec is young.** `allowed-tools` is experimental and **not a security control**.
- **The routing simulator approximates, not replicates** a real orchestrator.
- **`docs drift` needs a CodeGraph index and the `sqlite3` CLI.**
- **`docs init` expects [APM](https://microsoft.github.io/apm)** to deploy the authoring skill. Both it and CodeGraph are *expected* dependencies — Kyber-Weave detects them and degrades with a message, but never installs anything on your machine.
- **`squad install` and `squad pack` rely on [APM](https://microsoft.github.io/apm)** toolchain capabilities for multi-harness compilation.
- **Security scanning is necessary but not sufficient** — pair it with human review.
- **The document index is rebuilt, never persisted.** Editing one document rebuilds the whole corpus; comfortable at hundreds of documents, worth revisiting at thousands.
- **Documentation analysis persistence is a separate local cache.** `.kyber-weave/cache/docs-analysis.sqlite3` stores reusable vectors and verdicts only when the narrow cache path is safely ignored; it is never a source of retrieval prose.
- Some agent Core APIs exist without CLI verbs (`agent route` / `lint` / `new`) — known gap.

> **Naming note.** "Kyber" collides with CRYSTALS-Kyber / ML-KEM, the NIST post-quantum KEM
> standardised as FIPS 203. In a repository running Snyk, Trivy, CodeQL and Semgrep,
> searches and scan output for "kyber" will surface both. "Weave" is safely
> non-cryptographic — unlike Lattice, Module, Ring or Key, which read as the algorithm itself.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md).
Security reports go through [GitHub Security Advisories](SECURITY.md) — not public issues.

## Licence and attribution

MIT. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

**Thank you, [SkillForge](https://github.com/bonaniibm/SkillForge).** Kyber-Weave's
skill-governance feature — `skill validate` / `lint` / `scan` / `route` / `catalog` /
`pack` / `new` — originated as **[SkillForge](https://github.com/bonaniibm/SkillForge)**
([bonaniibm/SkillForge](https://github.com/bonaniibm/SkillForge)), an MIT-licensed
open-source project by the SkillForge contributors (`Copyright (c) 2026 SkillForge
contributors`). That work was absorbed into this repository and is maintained here; there
is no ongoing upstream sync. The MIT licence under which it was received is retained in
[LICENSE](LICENSE) and explained in [NOTICE](NOTICE).

Built on [Markdig](https://github.com/xoofx/markdig),
[YamlDotNet](https://github.com/aaubry/YamlDotNet),
[Spectre.Console](https://spectreconsole.net/), and the
[ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) C# SDK.
