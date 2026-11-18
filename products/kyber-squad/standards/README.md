# Coding standard templates

A starting folder per technology, role and delivery platform, for a repository adopting
`<docs-root>/standards/<name>/`. These are **templates, not governance**: `squad install`
does not deploy them, and nothing reads one until a host adopts it — every technology folder
at once with `kyber-weave docs init . --kyber-standards`, or exactly the folders named with
`kyber-weave docs init . --standards csharp,reviewer,github`.

Each folder holds four files, every one named after the folder:

| File | What it is |
|---|---|
| `<name>.standards.md` | The standard: six headings — Naming and layout, Coding standards, Testing, Commands, Security and data handling, Done checklist. |
| `<name>.rules.md` | Prose rules a model reads. They outrank the standard. |
| `<name>.skills.md` | How to do the work: commands, tool servers, query patterns, output shape. |
| `<name>.arbiter-rules.yml` | Machine rules the Arbiter evaluates. An empty list is valid, and most folders declare none. |

Technology and platform folders are scoped by `technology:`; role folders (`conductor`,
`architect`, `reviewer`, `devops`) by `role:`. A standard may name one parent with
`builds-on:` (React on TypeScript, Tauri on Rust, the data-access layer on C#), and a task in
the child receives the parent's rules and skills too.

The docs tool `docs_get_coding_standard_for_technology` returns one bundle for a technology:
the rule ids, `shared-rules.md`, the parent's rules and skills, the technology's rules and
skills, then the standard's sections for the topic asked for. The folder templates are
embeddings of the same files, so a host's copy and the shipped template are comparable with
`kyber-weave docs standards diff`.

## Using one

1. Name the folders to install:

   ```bash
   kyber-weave docs init . --standards csharp,typescript,reviewer,github
   ```

   This declares `csharp` and `typescript` in `ontology.technologies`, `reviewer` in
   `ontology.roles`, writes only those folders plus `shared-rules.md`, publishes the Config
   Reg property for each technology in the root `AGENTS.md`, and leaves any file the host
   already has byte-for-byte unchanged.

2. Set `owner` to a row in the host's `catalog.md`, set `last-reviewed`, and promote `status`
   to `current` once someone has actually read it.

Until step 2 is done the agents have nothing to follow. They ship no built-in standard: an
agent that finds `<react-coding-standard>` undeclared, or pointing at a `status: draft`
document, never stops: it follows the existing code's pattern or uses the draft as the best
available guidance, and names the gap or the draft in one line of its report. The code-review
checklists report the same gap rather than substituting rules of their own.

## What these are

Each template states decisions rather than describing the language: a standard that restates a
framework's own documentation is noise, and one that repeats what a linter already enforces is
worse — it goes stale the first time the linter's config changes and nobody notices.

They were rewritten from the per-technology review references in
[`../skills/code-review/references/`](../skills/code-review/references/), which were written
for a reviewer ("check that…") rather than for an author ("do this"). Since
[#126](https://github.com/dpalfery/kyber-weave/issues/126) those references only point at the
declared standard; every rule they held lives here.

The seven templates that match the seven review references are `csharp`, `react`, `python`,
`sql-server`, `azure`, `pulumi`, and `github-actions`. `maui`, `data-access-layer`, and
`tauri` are additional templates rewritten from agents rather than from a review reference. `rust` and `go`
were written with the `rust-dev` and `go-dev` agents
([#287](https://github.com/dpalfery/kyber-weave/issues/287)), which only name
**<rust-coding-standard>** and **<go-coding-standard>**; Rust under a Tauri app's `src-tauri`
follows `tauri` instead. `typescript` was written with the `typescript-dev` agent
([#237](https://github.com/dpalfery/kyber-weave/issues/237)), which only names
**<typescript-coding-standard>**; React UI TypeScript follows `react` instead. `pulumi` and
`python` were then rewritten from their agents so stack, packaging, environment, and
quality-gate policy live in the template — those agents only name **<pulumi-coding-standard>**
and **<python-coding-standard>**. `github-actions` and `sql-server` also carry the defaults the
delivery and SQL-planning agents the removal deleted used to embed, and testing lives in every
standard's Testing section and in `shared-rules.md` rather than in a `test` template. Kyber-Weave's own
[`docs/standards/csharp/`](../../../docs/standards/csharp/csharp.standards.md) is not a substitute for
the `csharp` template here — it records this repository's decisions, including ones a host
may reasonably reverse.

**A template is a guess about a repository it has never seen.** Every one of these carries
decisions a host may reasonably reverse — a line length, a state-management library, a naming
convention. Reversing one is the point of the standard being project-specific; what matters is
that the repository says which way it went.

A template holds only what applies to work in its technology; a statement that applies to every task and is never OK to break is a rule and belongs in the host's root `AGENTS.md` or rules folder (see "Rules and standards" in the host's documentation ontology).

## #128 additions

[Issue 128](https://github.com/dpalfery/kyber-weave/issues/128) moved the portable rules the
skill references stated but the templates lacked into the templates themselves: `xp_cmdshell`,
Entra ID / Kerberos authentication, `SCOPE_IDENTITY()`, sargable predicates, and the deprecated
`text`/`ntext`/`image` types in `sql`; a new `## Migrations` section in `data-access-layer`;
route constraints, the empty-collection response, and no direct `HttpContext` access, plus a
conditional `## Backend-for-frontend` section, in `csharp`; and per-test cleanup and unique
record ids in `test`'s `## Isolation`. The line-by-line record of what moved, what stayed in
the skill as technique, and what was superseded as one host's taste is the audit at
[`docs/kyber-squad/skill-resource-dispositions.md`](../../../docs/kyber-squad/skill-resource-dispositions.md).
