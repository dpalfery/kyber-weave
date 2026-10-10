# Provider: GitHub

Read this file only when the skill's provider selection chose GitHub. It
holds every GitHub-specific command, tool name, and path the `create-pull-request` skill needs.
The skill itself decides the target branch, title, linked work, and description; this file says
how to carry those decisions out on GitHub.

## Identity

A GitHub repository is `<owner>/<repo>`, read from the `origin` remote:

- HTTPS: `https://github.com/<owner>/<repo>[.git]`
- SSH: `git@github.com:<owner>/<repo>[.git]` or `ssh://git@github.com/<owner>/<repo>[.git]`

Strip a trailing `.git` from `<repo>`. An explicit owner and repo from the user win over the
remote. A remote on any other host (a GitHub Enterprise Server host, for example) does not match
`github.com`; ask the user for the owner and repo, and pass `--repo <owner>/<repo>` to every `gh`
command. A pull request is identified by its number.

## Tool map

Prefer the GitHub MCP server when it is connected and its write tools are available; otherwise
use the `gh` CLI column. Use local `git` only for local work (branch, fetch, push). A harness may
prefix MCP tool names with the name the host gave the server; match on the bare name below.

| Step | MCP tool | CLI fallback |
|---|---|---|
| List long-lived branches | `list_branches` (`owner`, `repo`) | `git ls-remote --heads origin` |
| Read default branch | none | `gh repo view <owner>/<repo> --json defaultBranchRef --jq .defaultBranchRef.name` |
| Find an open PR for source and target | `list_pull_requests` (`owner`, `repo`, `head`, `base`, `state: open`) | `gh pr list --repo <owner>/<repo> --head <source> --base <target> --state open --json number,url` |
| Create the PR | `create_pull_request` (`owner`, `repo`, `title`, `head`, `base`, `body`, `draft`) | `gh pr create --repo <owner>/<repo> --head <source> --base <target> --title <title> --body-file <file>`, plus `--draft` for a draft |
| Update the PR | `update_pull_request` (`owner`, `repo`, `pullNumber`, `title`, `body`) | `gh pr edit <number> --repo <owner>/<repo> --title <title> --body-file <file>` |
| Read the PR back | `pull_request_read` with `method: get` | `gh pr view <number> --repo <owner>/<repo> --json title,body,url,isDraft` |
| Check CI status | `pull_request_read` with `method: get_status` or `get_check_runs` | `gh pr checks <number> --repo <owner>/<repo>`; add `--watch` to wait, `--required` for required checks only |
| Link work items | none; write the reference into the body (see Linking work below) | none; the body carries it |

`gh pr checks` exits with code 8 while checks are still pending and exits non-zero when a check
has failed, so read the exit code as well as the output.

## Templates

GitHub reads a default pull request template named `pull_request_template.md` from the repository
root, `docs/`, or `.github/`. A repository with several templates keeps them in
`.github/PULL_REQUEST_TEMPLATE/`; a template is chosen there with the `?template=<name>` query
parameter in the web UI, or started from with `gh pr create --template <file>`. Look in those
places, in that order, before falling back to the skill's default description sections.

## Linking work

A closing keyword followed by an issue reference closes the issue when the pull request merges.
The keywords are `close`, `closes`, `closed`, `fix`, `fixes`, `fixed`, `resolve`, `resolves`, and
`resolved`, as in `Closes #42` or `Closes owner/repo#42`. GitHub acts on them **only when the pull
request targets the repository's default branch**. Into any other branch the keyword is inert, so
the skill's non-closing form (`Part of #42`) says the same thing without implying an effect.

## Multi-line descriptions

Write the body to a UTF-8 file and pass it with `--body-file <file>` (`-` reads standard input).
Never pass a multi-line body as a quoted command argument, and never pass a string that contains
literal `\n` escapes. In PowerShell, build the body in a here-string (`@" ... "@`), save it with
`Set-Content -Encoding utf8`, and pass the file. The MCP `create_pull_request` and
`update_pull_request` tools take the body as a plain string.

## Read-only or missing MCP

First confirm the GitHub MCP server is healthy with one lightweight read, such as
`list_pull_requests` for the repository. The server has a read-only mode (its `--read-only`
option, which a host can also set through the server's environment) that removes every write
tool, even ones requested explicitly. If `create_pull_request` or `update_pull_request` is not
available, or the server is not connected at all, use the CLI column for every step of this run.
Do not mix MCP writes and CLI writes for one pull request. The CLI needs `gh auth login` (or
`GH_TOKEN`) with permission to create pull requests.

## Helper scripts

Two scripts carry out the create-or-update-then-verify sequence above with the CLI. They take
the target branch, title, and body file the skill already decided, and never derive a title or
issue reference from the branch name:

- [`github-create-pr.sh`](../scripts/github-create-pr.sh) for bash
- [`github-create-pr.ps1`](../scripts/github-create-pr.ps1) for PowerShell

Deployed files are not executable, so run them through their interpreter from the deployed skill
directory:

```bash
bash <skill-dir>/scripts/github-create-pr.sh --base <target> --title "<title>" --body-file <file> [--head <source>] [--repo <owner/name>] [--draft]
pwsh -NoProfile -NonInteractive -File <skill-dir>/scripts/github-create-pr.ps1 -Base <target> -Title "<title>" -BodyFile <file> [-Head <source>] [-Repo <owner/name>] [-Draft]
```

Both exit non-zero if the pull request's title or body does not match what was sent. `--draft`
applies only when a new pull request is created.

## After merge

`gh pr merge <number> --delete-branch` deletes the local and remote source branch after the merge.
GitHub refuses a branch deletion requested this way when the repository uses a merge queue; delete
the branch after the queue merges it instead.

## Link formats

- Pull request: `https://github.com/<owner>/<repo>/pull/<number>`
- Commit: `https://github.com/<owner>/<repo>/commit/<sha>`

## Sources

Verified on 2026-09-28 against each tool's own source or documentation.

- `gh pr create` flags: [cli/cli `pkg/cmd/pr/create/create.go`](https://github.com/cli/cli/blob/trunk/pkg/cmd/pr/create/create.go); manual: <https://cli.github.com/manual/gh_pr_create>
- `gh pr list`, `edit`, `view`: <https://cli.github.com/manual/gh_pr_list>, <https://cli.github.com/manual/gh_pr_edit>, <https://cli.github.com/manual/gh_pr_view>
- `gh pr checks` flags and exit code 8: [cli/cli `pkg/cmd/pr/checks/checks.go`](https://github.com/cli/cli/blob/trunk/pkg/cmd/pr/checks/checks.go)
- `gh pr merge --delete-branch` and the merge-queue refusal: [cli/cli `pkg/cmd/pr/merge/merge.go`](https://github.com/cli/cli/blob/trunk/pkg/cmd/pr/merge/merge.go)
- `gh repo view --json defaultBranchRef`: [cli/cli `api/query_builder.go`](https://github.com/cli/cli/blob/trunk/api/query_builder.go)
- GitHub MCP server tools and read-only mode: [github/github-mcp-server README](https://github.com/github/github-mcp-server/blob/main/README.md), [server configuration](https://github.com/github/github-mcp-server/blob/main/docs/server-configuration.md)
- Pull request templates: <https://docs.github.com/en/communities/using-templates-to-encourage-useful-issues-and-pull-requests>
- Closing keywords and the default-branch condition: <https://docs.github.com/en/get-started/writing-on-github/working-with-advanced-formatting/using-keywords-in-issues-and-pull-requests>
- Remote URL forms: <https://docs.github.com/en/get-started/git-basics/managing-remote-repositories>
