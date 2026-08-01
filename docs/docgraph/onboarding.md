---
id: docgraph/onboarding
title: Adopting DocGraph in an existing repository
doc-type: onboarding
status: current
component: DocGraph
source-root: src/KyberWeave.Core/Docs
owner: dpalfery
last-reviewed: 2026-08-01
---

# Adopting DocGraph in an existing repository

Getting from an ungoverned documentation tree to a corpus that
[validates](governance.md) and [retrieves](retrieval.md). The work splits cleanly: a
command does the mechanical half, a skill does the judgment half.

## 1. Initialize

```bash
kyber-weave docs init .
```

This writes three files and leaves any that already exist alone, so it is safe to re-run:

| File | Why |
|---|---|
| `.kyber-weave/kyber-weave.yml` | Host config, with `docs-root` set to your detected tree and the inherited `DevOps/*` exclusions cleared |
| `<docs-root>/documentation-ontology.md` | The schema. Emitted because every `KW-DOC-SPEC-001` diagnostic tells the author to read it |
| `<docs-root>/catalog.md` | The component and owner vocabulary, seeded with one example row |

The docs root is detected from the first conventional directory that exists — `docs`,
`6-Docs`, `doc`, `documentation` — or created as `docs`. Override with `--docs-root`.

### APM is an expected dependency

`docs init` also deploys the **`kyber-weave-docs` skill** through
[APM](https://microsoft.github.io/apm). APM is a **prerequisite you install yourself** —
Kyber-Weave never installs it for you, and never installs anything else on your machine.

```bash
curl -sSL https://aka.ms/apm-unix | sh     # macOS / Linux, once
apm --version
```

Windows PowerShell: `irm https://aka.ms/apm-windows | iex`. Homebrew:
`brew install microsoft/apm/apm`.

If APM is absent, `docs init` says so, prints the exact command to run later, and still
scaffolds the corpus — the skill is an accelerator, not a prerequisite for the corpus
itself. Use `--no-skill` to skip the attempt entirely.

The default target is `agent-skills`, which installs to `.agents/skills/` and is read by
every APM-supported client. Choose others with `--target`:

```bash
kyber-weave docs init . --target claude,cursor,agent-skills
kyber-weave docs init . --no-skill          # scaffold only
```

APM supports `copilot`, `claude`, `cursor`, `opencode`, `codex`, `gemini`, `antigravity`,
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
of the skill: ask your agent to apply **`kyber-weave-docs`** to the failing documents.

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

## 5. Serve it

```bash
kyber-weave-mcp --repo-root .
```

See the [MCP runbook](mcp-runbook.md) for client configuration. Agents should reach the
corpus through `docs_explore` rather than by reading files.

## 6. Gate it

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
- [Skill governance](../context-hygiene/skills.md) — the skill is itself a governed artifact
