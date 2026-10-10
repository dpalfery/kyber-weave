---
name: create-pull-request
description: 'Opens or updates a pull request on GitHub or Azure DevOps: selects the provider from the git remote, resolves the target branch from evidence, writes the title and description from the host conventions and template, creates or updates the PR through the provider MCP server or CLI, verifies it, and follows CI and review through merge. Use when asked to create, open, submit, or update a PR. Do not use for addressing review comments on an existing PR (use pr-review-fix-comments) or for writing the implementation being merged.'
license: MIT
---

# Create Pull Request

Use this skill to open or update a pull request in the host repository, on GitHub or Azure
DevOps. This file is provider-neutral: it decides the target branch, title, linked work, and
description once, the same way on every provider. Every command, tool name, and template path
lives in the provider file selected below.

## Provider Selection

Resolve the provider before anything else, in this order:

1. Explicit provider in `$ARGUMENTS` (`github` / `gh` / `azdo` / `ado` / `azure-devops`).
2. Remote URL of the current git repo:
   - contains `dev.azure.com` or `visualstudio.com` -> Azure DevOps
   - contains `github.com` -> GitHub
3. Only one of the two provider MCP servers is connected -> that provider.
4. Otherwise ask the user which provider and stop.

Then read the matching provider file and use only its tools:

| Provider | Provider file |
|---|---|
| Azure DevOps | [providers/azure-devops.md](providers/azure-devops.md) |
| GitHub | [providers/github.md](providers/github.md) |

Rules:

- Read exactly one provider file per run. Do not mix tools across providers.
- Use the provider's MCP tool for a step when the server is connected and the tool is available;
  otherwise use the CLI fallback the provider file names for that step, for the whole run.
- Use local `git` only for local source-control work (branch, commit, fetch, push).
- If a step cannot be completed with the named tools, say exactly what is missing and stop.

## Inputs

All inputs are optional; each has a resolution rule below.

- **Provider**: from Provider Selection.
- **Source branch**: the branch to merge; defaults to the current branch.
- **Target branch**: see Target branch.
- **Title**: see Title.
- **Draft**: open the pull request as a draft. Ask when the user has not said and the work is
  not ready for review.
- **Linked work**: the issue or work item the change addresses; see Linked work.
- **Extra notes**: free text appended to the description's Notes.
- **Repository identity**: overrides for what the provider file reads from the remote (GitHub
  owner and repository; Azure DevOps organization, project, and repository).

## Before opening

Verify each item, and fix or report anything that fails before creating the pull request:

- [ ] **The source branch is pushed** and up to date with its remote.
- [ ] **The host's declared gates pass.** Run the commands the root `AGENTS.md` lists, and the
  build, test, lint, and format commands the declared **<technology>-coding-standard** names for
  each technology the change touches (for example **<csharp-coding-standard>**). When a standard
  is undeclared or still a draft, say so and ask whether to proceed; do not substitute commands
  of your own.
- [ ] **Scoped instructions are followed.** Read the nearest `AGENTS.md` to every changed file
  and confirm the change complies.
- [ ] **Architecture rules hold.** Check the change against the rules declared under
  **<rules-index>** in the root `AGENTS.md`.
- [ ] **Canonical documentation is current.** A change to a component's public interface,
  configuration, architecture, runtime, operations, or workflow updates its documentation in the
  shape the **<documentation-ontology>** declares.
- [ ] **The catalog is current.** A change that adds, moves, renames, or materially alters a
  component updates the **<component-catalog>**.
- [ ] **No secrets or credentials.** The change and the description contain no tokens,
  connection strings, passwords, `.env` files, customer data, or unsafe deployment commands.
- [ ] **The environment is ready.** When tooling is missing, use the `setup-dev-environment`
  skill.

## Target branch

Resolve the target branch in this order:

1. The explicit target branch the user gave.
2. Conclusive evidence of the source branch's parent:
   - the user named it earlier in the conversation;
   - a branching rule the host declares (the root `AGENTS.md`, a contributing guide, or a rule
     under **<rules-index>**) that covers this branch's type;
   - the source branch's upstream or tracking configuration;
   - an existing pull request for the same source branch.
3. Otherwise, list the repository's long-lived branches (the provider file's "List long-lived
   branches" step), then ask the user to choose one or name another.

Never assume the default branch is the target, and never assume a branch such as `develop` or
`main` exists or is the integration branch. The branching model is host policy, and a pull
request into the wrong base merges silently into the wrong place.

## Title

1. The explicit title the user gave.
2. A host convention wins when present: guidance in the host's pull request template, a
   contributing guide, or a rule under **<rules-index>** (a component prefix, a ticket prefix, a
   conventional-commit type).
3. Default: a concise imperative summary of what the change does, first word capitalized, no
   trailing period, about 72 characters at most, with no prefix unless the host uses one.

Do not build the title by title-casing the branch name: a branch name is an identifier, not a
summary of the change.

## Linked work

A host convention wins: use the host's issue or work-item reference style when it declares one.
Otherwise:

- Detect the item from the branch name only when it carries one unambiguously: a leading number
  (`42-add-retry`) or a key such as `ABC-123`. Otherwise ask, or omit the section.
- When this pull request completes the item, use the provider's closing mechanism, which the
  provider file describes along with where it takes effect.
- When this pull request is one step of a larger item, use a non-closing reference such as
  `Part of #42`.
- Keep an alphanumeric key from another tracker as-is, for example `Ticket: ABC-123`.

Whether to close an item is a statement about intent; the closing syntax and when it takes effect
are provider facts.

## Description

If the host has a pull request template, fill in every section of the host's pull request
template; the provider file lists where each provider looks for one. Leave no placeholder text.
Otherwise use these sections:

- **Summary**: the problem and the outcome, in one to three sentences; why more than how.
- **Type of change**: bug fix, feature, breaking change, refactor, or documentation.
- **Linked work**: as decided under Linked work; omit when there is none.
- **Changes**: a short list of what changed, summarized from the commits and the diff.
- **Validation**: which suites ran, which tests were added, and any manual verification and its
  environment. Never write "tests pass" without naming them.
- **Documentation impact**: what documentation changed, or why none was needed.
- **Notes**: extra notes, follow-ups, and anything reviewers should know.

Do not paste raw commit lists or file lists; the provider shows both. Write the description to a
UTF-8 file and pass the file wherever the provider's CLI allows; the provider file says how.

## Create or update

1. Look for an open pull request from the source branch into the target branch.
2. If one exists, update its title and description. Otherwise create one, as a draft if asked.
3. Read the pull request back and confirm its title and description match what was sent. Retry
   once on a mismatch, then report it.
4. Report the pull request link.

Never create a test pull request unless the user asked for one. Ask before requesting specific
reviewers, and before enabling automatic completion or merge.

## CI checks

Checks run on the pull request as the host configured them. Use the provider file's "Check CI
status" step and wait for the result before requesting review.

When a check fails:

1. **Build or test**: read the log, reproduce locally with the command the host declares, fix the
   cause, and push.
2. **Documentation or lint**: run the same validation locally and fix what it reports.
3. **Security scan**: read the findings; fix high and critical ones. Suppress a confirmed false
   positive only the way the host's policy allows, with the reason recorded.
4. **Infrastructure**: when a failure is clearly the runner or an outside service and not the
   change, re-run the job once, or record it in a comment for a maintainer.

## Review

- A human reviewer, or the `code-review` skill, reviews correctness, security, performance,
  maintainability, test coverage, and compliance with the host's rules.
- Answer every review comment, with a change or with the reason the current approach stands. Use
  the `pr-review-fix-comments` skill to work through them.
- Request review again after pushing changes. Do not merge while a conversation is unresolved or a
  required check is failing.

## After merge

- **Plan-backed work**: the pull request is not the last step. A `docs-dev` closeout verifies the
  plan's acceptance criteria against what was built, updates the canonical documentation, and
  archives the plan through the index the **<plan-index>** declares.
- **Other work**: confirm the description's documentation impact still matches what merged.
- Delete the source branch when the host's convention is to delete merged branches; the provider
  file names the step.

## Common pitfalls

| Pitfall | Resolution |
|---|---|
| The pull request targets the wrong base branch | Change the target branch on the pull request before merging, then re-run checks |
| A template section is left as a placeholder | Fill it in; reviewers will ask for it |
| A closing reference did not close the item | Check the provider's rule for where closing takes effect; the target may not be the default branch |
| The description lost its line breaks | Pass the description as a file, or as one value per line, as the provider file says |
| The MCP server cannot create the pull request | It may be read-only or not connected; use the provider's CLI fallback for the whole run |
| A check passes locally but fails in CI | Look for platform differences, missing SDK workloads, or environment assumptions |

## Resources

- [providers/github.md](providers/github.md): GitHub tools, CLI commands, templates, and closing
  keywords.
- [providers/azure-devops.md](providers/azure-devops.md): Azure DevOps tools, CLI commands,
  templates, and work-item linking.
- [scripts/github-create-pr.sh](scripts/github-create-pr.sh) and
  [scripts/github-create-pr.ps1](scripts/github-create-pr.ps1): create or update one GitHub pull
  request with the CLI and verify it.
