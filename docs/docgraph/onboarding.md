---
id: docgraph/onboarding
title: Adopting DocGraph in an existing repository
doc-type: onboarding
status: current
component: DocGraph
source-root: src/KyberWeave.Core/Docs
owner: dpalfery
last-reviewed: 2026-10-31
---

# Adopting DocGraph in an existing repository

Getting from an ungoverned documentation tree to a corpus that
[validates](governance.md) and [retrieves](retrieval.md). The work splits cleanly: a
command does the mechanical half, a skill does the judgment half.

## 1. Initialize

```bash
kyber-weave docs init .
```

This considers four files and preserves existing operator-owned content, so it is safe to
re-run:

| File | Why |
|---|---|
| `.kyber-weave/kyber-weave.yml` | Host config, with `docs-root` set to your detected tree and the inherited `DevOps/*` exclusions cleared |
| `.kyber-weave/.gitignore` | Safely merged narrow `cache/` entry for local analysis vectors and verdicts |
| `<docs-root>/documentation-ontology.md` | The schema. Emitted because every `KW-DOC-SPEC-001` diagnostic tells the author to read it |
| `<docs-root>/catalog.md` | The component and owner vocabulary, seeded with one example row |

Initialization does **not** create an empty glossary. A managed glossary is created only
when terminology evidence exists and `docs glossary --write` has something to merge.

The docs root is detected from the first conventional directory that exists — `docs`,
`6-Docs`, `doc`, `documentation` — or created as `docs`. Re-running with an existing
`.kyber-weave/kyber-weave.yml` honors its `docs-root` first, so scaffolding lands in the
same tree `docs validate` reads. Override with `--docs-root`.

`--force` regenerates the two scaffolded documents. It never overwrites your host config:
that file is yours, and it holds settings — harness profiles, catalog column overrides,
closed vocabularies — that the emitted template knows nothing about and would silently
drop. The one key `docs init` will rewrite there is `ontology.docs-root`, in place and only
when `--docs-root` moves it, so the catalog and the validator never end up reading
different trees. Comments and every other key survive the edit.

Passing `--kyber-standards` scaffolds the 11 rich Kyber Squad coding standards templates
under `<docs-root>/standards/`, merges their technologies into `ontology.technologies`
in `.kyber-weave/kyber-weave.yml`, and registers all 11 `<{tech}-coding-standard>` properties
in Config Reg.

### Review gates

`docs init` also writes a `review:` section into `.kyber-weave/kyber-weave.yml` for the
declared technologies that ship gates — format, lint, typecheck, build and test commands
rendered from the product's embedded gate catalogue (see [Review council
architecture](../code-review/architecture.md#the-shipped-gate-catalogue)). A new host
config carries the block at creation; an existing config without one gains it with every
other byte untouched. Each gate carries a comment naming its technology, so a stack's
gates can be found and removed.

```bash
kyber-weave docs init . --no-review-gates   # write no gates
```

`--no-review-gates` opts out: no `review:` section is written and the output says so.
Re-running never edits an existing `review:` section, even with `--force` — the block is
host-owned from the moment it is written. A second run instead lists catalogue gate ids
missing from the host's section, as information only; the exit code is unchanged. A
declared technology with no catalogue entry reports `no gates shipped for <tech>` and
writes nothing. Upgrading Kyber-Weave never changes the gates a host runs; `kyber-weave
review catalogue` prints the catalogue for newer releases to copy in by hand.

Wire the gates into CI with the [review-gate
template](../ci-pipelines/workflows-runbook.md#the-review-gate-template).

The same conservative rule applies to `.kyber-weave/.gitignore`: an effective `cache/`
entry is preserved, a missing one is appended without disturbing other patterns, and a
later negation is countered with a final narrow entry. `--force` does not replace this
operator-owned file.

### APM is an expected dependency

`docs init` also deploys the **Kyber-Weave skills** in `.apm/skills/`, among them the
**`kyber-docs-authoring` skill**, through [APM](https://microsoft.github.io/apm). APM is a
**prerequisite you install yourself** — Kyber-Weave never installs it for you, and never installs
anything else on your machine.

```bash
curl -sSL https://aka.ms/apm-unix | sh     # macOS / Linux, once
apm --version
```

Windows PowerShell: `irm https://aka.ms/apm-windows | iex`. Homebrew:
`brew install microsoft/apm/apm`.

If APM is absent, `docs init` says so, prints the exact command to run later, and still
scaffolds the corpus — the skill is an accelerator, not a prerequisite for the corpus
itself. Use `--no-skill` to skip the attempt entirely.

Where [Kyber-Squad](../kyber-squad/onboarding.md#kyber-weave-skills-in-a-squad-deployment) is
deployed, the same skills already arrive through `kyber-weave squad install` and
`kyber-weave squad update`, so run `docs init` with `--no-skill` there:

```bash
kyber-weave docs init . --no-skill
```

`docs init` itself does not change: it has no new flag and no new default, and a repository that
uses Kyber-Docs without Kyber-Squad keeps this APM path. See
[ADR 0049](../adr/0049-kyber-squad-ships-the-kyber-weave-skills.md).

The default target is `agent-skills`, which installs to `.agents/skills/` and is read by
every APM-supported client. Choose others with `--target`:

```bash
kyber-weave docs init . --target claude,cursor,agent-skills
kyber-weave docs init . --no-skill          # scaffold only
```

APM supports `copilot`, `claude`, `cursor`, `opencode`, `codex`, `antigravity`,
`windsurf`, `kiro`, and `agent-skills`.

Deploying the skill is delegated to APM rather than reimplemented because APM already
resolves the directory layout for every runtime it supports; a second copy of that mapping
inside Kyber-Weave would drift from it.

## 2. Fill in the catalog

Nothing else can pass until `catalog.md` names the components and owners your documents
will claim, because `component` and `owner` are validated against it.

One row per unit of the system that genuinely exists. A catalog with forty rows is a list
of files, and the closed vocabulary stops meaning anything. Owners come from `CODEOWNERS`,
`git log`, or whoever answers pages — an honest `unassigned` beats a placeholder that
looks reviewed.

## 3. Retrofit the tree

```bash
kyber-weave docs validate .
```

Every ungoverned file reports `KW-DOC-SPEC-001`. This is the judgment half, and the point
of the skill: ask your agent to apply **`kyber-docs-authoring`** to the failing documents.

The skill's procedure, in short:

1. Fill the mechanical keys in bulk — `id` from path, `title` from the H1, `last-reviewed`
   from `git log`, `owner` from `CODEOWNERS`, `doc-type` guessed from the path
2. Set **`status: draft`** on everything retrofitted
3. Get `docs validate` clean
4. Add `code-refs` selectively, verifying each symbol resolves
5. Promote to `current` component by component, as humans confirm them

`status: draft` matters more than it looks. Retrieval demotes draft to 0.85, so a
half-retrofitted corpus degrades gracefully instead of serving unreviewed metadata as
current guidance. A corpus that stays entirely draft still works — everything is
retrievable, uniformly demoted. That is a stable resting place, not a failure.

## 4. Add code joins

Once the corpus validates, `code-refs` turns documentation into something checkable:

```bash
kyber-weave docs drift .
```

This step needs a [CodeGraph index](architecture.md#the-code-graph-join) and the `sqlite3`
CLI. Skip it if you have neither — everything else, including all of retrieval, works
without them.

Be selective. Architecture documents, runbooks for a named service, and API references
earn `code-refs`; narrative and onboarding prose do not. **A document with no `code-refs`
is completely valid** — an empty claim beats a false one.

## 5. Analyze claims and terminology

Once the corpus is valid, analysis can surface duplicated claims, potential conflicts,
and overloaded terms without editing any source document:

```bash
kyber-weave docs integrity-check .
kyber-weave docs review export . --out candidates.json
kyber-weave docs glossary .
```

The default path is advisory, deterministic/lexical, and bounded. Embeddings are off. If
you later enable a loopback embedding endpoint, Kyber-Weave sends no document text unless
the local cache path is proven safely ignored. See
[documentation analysis and review](analysis.md) before enabling `high-recall` or
importing reusable verdicts.

## 6. Serve it

```bash
kyber-weave-mcp --repo-root .
```

See the [MCP runbook](mcp-runbook.md) for client configuration. Agents should reach the
corpus through `docs_explore` rather than by reading files.

The MCP surface also exposes capped read-only `docs_analysis_candidates` and
`docs_glossary` tools; verdict and glossary writes remain CLI-only.

## 7. Gate it

Add the [docs gate workflow](../ci-pipelines/workflows-runbook.md) so the corpus cannot
regress. Start with `docs validate` alone — it needs no index — and add `docs drift` once
you have a CodeGraph index in CI.

## What not to do

**Do not widen the ontology to make errors disappear.** Adding a doc-type because six
files do not fit converts a closed vocabulary into a text field, which is the exact
failure the ontology exists to prevent. Fix the documents.

**Do not retrofit the archive.** `archive` is an excluded path segment by default, because
an archived plan is not current guidance.

**Do not mark everything `current` to finish faster.** The status is what tells retrieval
how far to trust a document; setting it untruthfully makes ranking worse than having no
ontology at all.

## Related

- [The documentation ontology](../documentation-ontology.md) — the schema being adopted
- [Governance gates](governance.md) — what `validate` and `drift` check
- [Retrieval and ranking](retrieval.md) — why doc-type and status affect results
- [Documentation analysis and review](analysis.md) — duplicates, conflicts, terms, and review
- [Skill governance](../context-hygiene/skills.md) — the skill is itself a governed artifact
