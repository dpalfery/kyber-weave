---
id: docgraph/onboarding
title: Adopting DocGraph in an existing repository
doc-type: onboarding
status: current
component: DocGraph
source-root: src/KyberWeave.Core/Docs
owner: dpalfery
last-reviewed: 2026-11-01
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
later negation is countered with a final narrow entry. An MCP wiring receipt additionally
earns a `receipts/` entry there, for the same reason. `--force` does not replace this
operator-owned file.

### MCP wiring

`docs init` also declares and checks the MCP servers your agent instructions rely on. The
declaration is the `mcp:` section of `.kyber-weave/kyber-weave.yml`, scaffolded with one
server — `kyber-weave`, launched `kyber-weave-mcp --repo-root .`, first-party — and yours
from the moment it exists:

```yaml
mcp:
  required-servers:
    kyber-weave:
      command: [kyber-weave-mcp, --repo-root, .]
      provenance: first-party
      level: required
```

After the scaffold and the skill deploy, `docs init` checks whether each declared server is
wired in each harness's **repository-scope** configuration — the committed file your team
shares — and prints one `MCP wiring:` section: an `ok` line per harness that is wired, a
`warn` line with a copy-ready `docs mcp-wire --target <harness>` command per fixable gap, and
an `info` line for a harness that is not in play. A gap the repair cannot close is reported
with the edit to make by hand instead of an offer — an entry whose launch command differs from
the declaration, because the repair never modifies an entry that already exists; a
configuration that arrived as a symbolic link, because the repair refuses a link for the same
reason the check does not read one; and a configuration this process may not inspect, because
a file it cannot read cannot be told from one that is not there. The Copilot coding agent is
the one undetectable harness whose advice is printed with its `info` line — its configuration
is repository settings, not a file, so the settings entry and the workflow step are the whole
answer. It reads no file under your home directory, follows no symbolic link into one, writes
nothing and **does not change the exit code**: a scaffold that succeeded exits 0 with findings,
exactly as the APM-missing and retired-skill paths do. A declaration the loader rejects fails
the run as `KW-CONFIG-001`, before the check.

```bash
kyber-weave docs init . --no-mcp-check   # skip the check, the prompt, and any repair
```

Where the check found a fixable gap **and** stdin is an interactive terminal, `docs init`
offers a yes/no question naming the files and servers it would write. It defaults to no;
confirming writes through the same bounded path as `docs mcp-wire` and reports each file it
wrote; declining writes nothing and leaves the printed command in place. **When stdin is not
an interactive terminal — redirected, piped, no TTY, as in CI — it never prompts, never
writes, and the output ends with the manual repair command.** `docs init` has no consent
flag; a script that wants the repair runs
[`docs mcp-wire`](mcp-runbook.md#docs-mcp-wire), whose invocation is the consent.

**Editing the declaration escalates for human review in this repository, and in any host
that copies the policy.** `.kyber-weave/kyber-weave.yml` is on this repository's
`review.policy.always-human` list, so **any** change to it — the MCP declaration included —
escalates by path alone, before any finding is weighed. That is deliberate rather than
incidental: the declaration decides which servers your instructions depend on, and the
scaffolded default is part of what a reviewer sees and approves. Adopting repositories that
carry the same policy inherit the same consequence.

The per-harness recipes, the two repair verbs, their flags and exit codes, and the harnesses
the repair will not write are in the [MCP runbook](mcp-runbook.md#checking-and-wiring). The
schema and its keys are in [configuration](../configuration.md#mcp-servers).

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

The APM deploy path itself does not change: no existing flag or default moves, and a
repository that uses Kyber-Docs without Kyber-Squad keeps this path. See
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
