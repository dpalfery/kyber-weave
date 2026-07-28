# Kyber-Weave

**Governance for every artifact that shapes agent behaviour.** Skills, agent definitions, and documentation are all supply-chain artifacts, and Kyber-Weave gives all three the same treatment: parsed, validated against a closed spec, checked for drift against a source of truth, security-scanned, and made retrievable.

Each artifact class differs only in what its source of truth *is*:

| Artifact class | Source of truth it answers to |
|---|---|
| Documentation | the CodeGraph index — a documented symbol must still exist |
| Agent definitions | its sibling copies across the six supported harnesses |
| Skills | the Agent Skills open format spec |

> **Naming note.** "Kyber" collides with CRYSTALS-Kyber / ML-KEM, the NIST post-quantum KEM standardised as FIPS 203. In a repository running Snyk, Trivy, CodeQL and Semgrep, searches and scan output for "kyber" will surface both. "Weave" is safely non-cryptographic — unlike Lattice, Module, Ring or Key, which read as the algorithm itself.

---

## Install (frictionless — no .NET SDK required)

Kyber-Weave ships as **self-contained platform binaries** (`kyber-weave` and `kyber-weave-mcp` on your PATH). Pick one channel:

| Channel | Status | Command / how |
|---|---|---|
| **npm** | Primary (Phase 2b) | `npm i -g @dpalfery/kyber-weave` |
| **GitHub Releases** | Primary (Phase 2b) | Download the RID asset for your OS/arch from [Releases](https://github.com/dpalfery/kyber-weave/releases) |
| **Homebrew** | Primary (Phase 2b) | `brew install …` (formula will track Release tags) |
| GitHub Packages (`dotnet tool`) | Optional / advanced | .NET specialists only — **not** the default story; **nuget.org is not used** |

Until Phase 2b publishes those channels, build from source (below). Host CI should pin an **npm version** or **Release tag**, not a NuGet feed.

---

## What it does

Three symmetric CLI branches, one per artifact class.

| Command | What it answers | Gate |
|---|---|---|
| `skill validate` | Is this a spec-conformant skill? | fails on **error** |
| `skill lint` | Routing readiness + collision / overlap detection | fails on **error** (name collision) |
| `skill scan` | Trust-surface scan (prompt injection, secrets, risky scripts, …) | fails on **critical** (configurable) |
| `skill route` | Which skill fires for this prompt? (single prompt or eval set) | fails below **--min-accuracy** |
| `skill catalog` | Inventory with version / owner / score | — |
| `skill pack` | Bundle a skill into a Copilot Studio–compatible `.zip` | — |
| `skill new` | Scaffold a spec-correct skill from a template | — |
| `agent validate` | Are harness agent manifests spec-conformant? | fails on **error** |
| `agent sync-check` | Are roles synchronized across all 6 harness folders? | fails on **error** |
| `agent catalog` | Role × harness governance parity matrix | — |
| `docs validate` | Does documentation frontmatter conform to the ontology? | fails on **error** |
| `docs drift` | Do documented code references still resolve in CodeGraph? | fails on **error** |
| `docs graph` | Export `nodes.jsonl` / `edges.jsonl` joined to CodeGraph ids | — |
| `docs catalog` | Doc-type coverage by component | — |

---

## Run it

```bash
# from source (developers / until Phase 2b ships installers)
dotnet run --project src/KyberWeave.Cli -- <branch> <command> [args]

# after install (npm / Release / Homebrew) — binaries on PATH
kyber-weave skill validate ./samples/skills
kyber-weave skill lint ./samples/skills --explain
kyber-weave skill scan ./samples/skills --format sarif > kyber-weave-skills.sarif
kyber-weave skill route "I'm locked out and forgot my password" --skills ./samples/skills
kyber-weave skill route --eval ./samples/routing-tests.yml --skills ./samples/skills --min-accuracy 0.85
kyber-weave agent sync-check .
kyber-weave docs validate .
kyber-weave docs drift .
kyber-weave docs graph . --out ./build/doc-graph
```

### `skill lint --explain` makes the routing score auditable

```
password-reset — routing score 100/100
  Dimension          Score   Detail
  Trigger clause     25/25   States when to use the skill.
  Negative boundary  20/20   States when NOT to use the skill — prevents over-firing.
  Specific opening   15/15   Opens with a concrete action verb.
  Trigger keywords   20/20   25 distinct content terms — concrete nouns/keywords help routing.
  Length budget      20/20   289 chars — within a healthy routing budget.
```

### `skill route` turns "which skill fires?" into a test

```
✔  I'm locked out of my laptop and forgot my password   →  password-reset
✔  Checkout is throwing 500 errors for a lot of users    →  incident-triage
✔  What's the weather in Kolkata tomorrow?               →  (no fire)
Routing accuracy: 100% (7/7), threshold 85%.
```

The default routing strategy is **lexical** — deterministic and offline, so it runs in CI with no API key. Treat `skill route` as a pre-deployment signal and regression test.

---

## The MCP server

`kyber-weave-mcp` is a **separate executable** (stdio MCP). JSON-RPC owns stdout; the CLI uses Spectre.Console on stdout — keeping them separate makes stream corruption structurally impossible. All logging is pinned to stderr.

| Tool | What it does |
|---|---|
| `docs_explore(query, maxDocs = 5, charBudget = 12000)` | Ranked docs with frontmatter identity, prose within budget, and code joins `symbol → file:line` |
| `docs_for_symbol(symbol)` | Reverse lookup: docs whose `code-refs` **formally claim** a symbol (not a prose mention) |

Point your MCP client at `kyber-weave-mcp` on PATH (after npm / Release / brew install). From source: `dotnet run --project src/KyberWeave.Mcp`.

The index reloads when in-scope document mtimes, document count, or `.codegraph/codegraph.db` change.

`agent_explore` and `skill_explore` are planned follow-ons.

---

## Host GitHub Actions templates

Copy-ready workflows live under [`templates/github-actions/`](templates/github-actions/). Pin an **npm package version** or **Release tag**. See that directory’s README.

---

## Develop from source

Requires .NET SDK 10 (`global.json`).

```bash
dotnet restore KyberWeave.sln
dotnet build KyberWeave.sln -c Release
dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release
```

Self-contained publish (Phase 2b ships these per RID):

```bash
dotnet publish src/KyberWeave.Cli/KyberWeave.Cli.csproj -c Release \
  -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ./artifacts/cli-linux-x64
```

---

## Rule reference

Rule ids are segmented by feature. All are stable identifiers suitable for suppression and SARIF.

**Skill spec (`skill validate`)** — `KW-SKILL-SPEC-001`–`-012`  
**Skill lint (`skill lint`)** — `KW-SKILL-LINT-001`–`-011`  
**Skill security (`skill scan`)** — `KW-SKILL-SEC-001`…  
**Agent governance (`agent …`)** — `KW-AGENT-SPEC-*`, `KW-AGENT-SYNC-*`, `KW-AGENT-LINT-*`, `KW-AGENT-SEC-*`  
**Documentation governance (`docs …`)** — `KW-DOC-SPEC-001`–`-006`, `KW-DOC-DRIFT-001`–`-003`  
**Parsing** — `KW-PARSE-000`

Security scanning is **necessary but not sufficient**. Pair `skill scan` with human review.

---

## Architecture

```
src/
  KyberWeave.Core/        # shared engine + Skills / Agents / Docs
    Diagnostics/ Text/ Parsing/ CodeGraph/ Configuration/
  KyberWeave.Cli/         # kyber-weave — Spectre.Console.Cli: skill | agent | docs
  KyberWeave.Mcp/         # kyber-weave-mcp — stdio MCP server
tests/KyberWeave.Tests/
samples/                  # exemplar + deliberately bad skills; routing eval set
templates/github-actions/ # host gate workflows (npm / Release pin)
docs/                     # ALM & governance playbook
```

Hosts may drop a root `kyber-weave.yml` to override ontology defaults and harness capability profiles.

---

## Caveats

- **The Agent Skills spec is young.** `allowed-tools` is experimental and **not a security control**.
- **The routing simulator approximates, not replicates** the orchestrator.
- **`docs drift` needs a CodeGraph index and the `sqlite3` CLI.**
- Some agent Core APIs exist without CLI verbs (`agent route` / `lint` / `new`) — known gap.
- **Distribution channels (npm, Releases, Homebrew) land in Phase 2b.** This repo’s CI currently builds, tests, and smoke-publishes a single RID; it does not publish to registries.

## Licence

MIT. See [LICENSE](LICENSE) and [NOTICE](NOTICE). Built on [Markdig](https://github.com/xoofx/markdig), [YamlDotNet](https://github.com/aaubry/YamlDotNet), [Spectre.Console](https://spectreconsole.net/), and the [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) C# SDK.
