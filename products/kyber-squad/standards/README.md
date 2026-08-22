# Coding standard templates

A starting standard per technology, for a repository adopting
`<docs-root>/standards/<technology>/`. These are **templates, not governance**: nothing here
is installed by `squad install` today, and nothing reads them until a host copies one in.

## Using one

1. Declare the technology in the host repository's `.kyber-weave/kyber-weave.yml`:

   ```yaml
   ontology:
     technologies:
       - react
   ```

2. Run `kyber-weave docs init .`, which creates `<docs-root>/standards/react/`, publishes
   `<react-coding-standard>` in the Config Reg block of the root `AGENTS.md`, and writes a
   standard to fill in.

3. Replace that file with this template, set `owner` to a row in the host's `catalog.md`, set
   `last-reviewed`, and promote `status` to `current` once someone has actually read it.

## What these are

Each template states decisions rather than describing the language: a standard that restates a
framework's own documentation is noise, and one that repeats what a linter already enforces is
worse — it goes stale the first time the linter's config changes and nobody notices.

They were rewritten from the per-technology review references in
[`../skills/code-review/references/`](../skills/code-review/references/), which are written for
a reviewer ("check that…") rather than for an author ("do this"). Those references still exist
and still say roughly the same things; consolidating them is
[a todo](../../../docs/todo/portable-artifacts-carry-project-standards.md), not something this
folder did.

The seven templates that match the seven review references are `csharp`, `react`, `python`,
`sql`, `azure`, `pulumi`, and `github-actions`. `maui`, `data-access-layer`, and `test` are
additional templates rewritten from agents rather than from a review reference. `pulumi` and
`python` were then rewritten from their agents so stack, packaging, environment, and
quality-gate policy live in the template — those agents only name **<pulumi-coding-standard>**
and **<python-coding-standard>**. `test` was rewritten from the `test-dev` agent so runners,
isolation, naming, and assertion policy live in the template — the agent only names
**<test-coding-standard>**. Kyber-Weave's own
[`docs/standards/csharp/`](../../../docs/standards/csharp/README.md) is not a substitute for
the `csharp` template here — it records this repository's decisions, including ones a host
may reasonably reverse. The same is true of
[`docs/standards/test/`](../../../docs/standards/test/README.md) versus the `test` template.

**A template is a guess about a repository it has never seen.** Every one of these carries
decisions a host may reasonably reverse — a line length, a state-management library, a naming
convention. Reversing one is the point of the standard being project-specific; what matters is
that the repository says which way it went.
