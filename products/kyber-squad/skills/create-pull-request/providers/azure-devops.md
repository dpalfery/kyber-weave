# Provider: Azure DevOps

Read this file only when the skill's provider selection chose Azure DevOps. It holds every Azure
Repos command, tool name, and path the `create-pull-request` skill needs. The skill itself
decides the target branch, title, linked work, and description; this file says how to carry
those decisions out in Azure Repos.

## Identity

An Azure Repos repository is `<organization>/<project>/<repository>`, read from the `origin`
remote:

- HTTPS: `https://dev.azure.com/<organization>/<project>/_git/<repository>`
- Legacy HTTPS: `https://<organization>.visualstudio.com/[DefaultCollection/]<project>/_git/<repository>`
- SSH: `git@ssh.dev.azure.com:v3/<organization>/<project>/<repository>`
- Legacy SSH remotes use a `vs-ssh.visualstudio.com` host; read the organization, project,
  and repository from the path the same way.

An explicit organization, project, or repository from the user wins over the remote. The CLI
infers all three from the remote with `--detect` (on by default); set lasting defaults with
`az devops configure --defaults organization=https://dev.azure.com/<organization> project=<project>`.
The MCP tools take a `repositoryId`, which `repo_repository` (`get`) returns as `id`. Branches are passed to the
MCP tools as full refs (`refs/heads/<branch>`) and to the CLI as plain names. A pull request is
identified by its numeric ID.

## Tool map

Prefer the Azure DevOps MCP server (`microsoft/azure-devops-mcp`) when it is connected and its
write tool is available; otherwise use the `az` CLI column. Use local `git` only for local work
(branch, fetch, push). A harness may prefix MCP tool names with the name the host gave the
server; match on the bare name below.

| Step | MCP tool | CLI fallback |
|---|---|---|
| List long-lived branches | `repo_branch` (`list`, `repositoryId`); names come back without `refs/heads/` | `az repos ref list --repository <repository> --filter heads/` |
| Read default branch | `repo_repository` (`get`, `project`, `repositoryNameOrId`); read `defaultBranch`, a full `refs/heads/<branch>` ref. The `list` action omits it | `az repos show --repository <repository> --query defaultBranch` |
| Find an open PR for source and target | `repo_pull_request` (`list`) | `az repos pr list --repository <repository> --source-branch <source> --target-branch <target> --status active` |
| Create the PR | `repo_pull_request_write` (`create`: `repositoryId`, `sourceRefName`, `targetRefName`, `title`, `description`, `isDraft`, `workItems`) | `az repos pr create --repository <repository> --source-branch <source> --target-branch <target> --title <title> --description <line> [<line> ...]`, plus `--draft true` for a draft |
| Update the PR | `repo_pull_request_write` (`update`) | `az repos pr update --id <id> --title <title> --description <line> [<line> ...]` |
| Read the PR back | `repo_pull_request` (`get`) | `az repos pr show --id <id>` |
| Check CI status | none; the server documents no policy-evaluation tool | `az repos pr policy list --id <id>`; `az repos pr policy queue` re-runs one evaluation |
| Link work items | `repo_pull_request_write` (`create` with `workItems`) | `--work-items <id> [<id> ...]` on `az repos pr create`, or `az repos pr work-item add --id <id> --work-items <id> [<id> ...]` |

`az repos pr update` has no `--work-items`; link work items to an existing pull request with
`az repos pr work-item add`.

## Templates

Azure Repos reads a default pull request template from the first of these folders that has one:
`.azuredevops/`, `.vsts/`, `docs/`, then the repository root. A branch-specific template lives at
`pull_request_template/branches/<target-branch>.md` under the same folders and is used for pull
requests into that branch. Additional, optional templates live in
`<folder>/pull_request_template/`. Look in those places, in that order, before falling back to
the skill's default description sections.

## Linking work

Link the work items the pull request addresses with `workItems` (MCP) or `--work-items` (CLI).
To resolve an item when the pull request completes, write a resolution mention in the
description: `fix`, `fixes`, or `fixed` followed by `#<work item id>`, as in `Fixes #123`. Azure
Repos resolves it only when the pull request completes into the **default branch** with
**Complete associated work items after merging** selected, and only while the repository's
**Commit mention work item resolution** setting is on (it is by default).
`--transition-work-items true` on create or update asks the completion to transition linked items.
`AB#123` is the GitHub-to-Azure Boards syntax; do not use it in Azure Repos.

## Multi-line descriptions

Prefer the MCP `description`, which takes the body as one string. On the CLI, `--description`
takes one or more values and joins them one per line, so pass each line of the body as its own
value; in bash, `mapfile -t lines < <file>` then `--description "${lines[@]}"`. Read the pull
request back and compare the description, because shell quoting is where multi-line text is most
often lost.

## Read-only or missing MCP

First confirm the Azure DevOps MCP server is healthy with one lightweight read, such as
`repo_repository` for the repository. If `repo_pull_request_write` is not available (the server
is not connected, or the host did not enable its repository tools), use the CLI column for every
step of this run. Do not mix MCP writes and CLI writes for one pull request. The CLI needs the
`azure-devops` extension (`az extension add --name azure-devops`) and a signed-in identity:
`az login`, `az devops login` with a personal access token, or the `AZURE_DEVOPS_EXT_PAT`
environment variable.

## After merge

`--delete-source-branch true` on `az repos pr create` or `az repos pr update` deletes the source
branch when the pull request completes. Set it when opening the pull request if the host's
convention is to delete merged branches.

## Link formats

- Pull request: `https://dev.azure.com/<organization>/<project>/_git/<repository>/pullrequest/<id>`
- Commit: `https://dev.azure.com/<organization>/<project>/_git/<repository>/commit/<sha>`

## Sources

Verified on 2026-09-28 against each tool's own source or documentation. `learn.microsoft.com`
pages were checked through their published Markdown source in `MicrosoftDocs/azure-devops-docs`.

- `az repos pr create`, `update`, `list`, `show`, `work-item add`, `policy list`: [azure-devops-cli-extension `pull_request.py`](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/repos/pull_request.py) and [`commands.py`](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/repos/commands.py); reference: <https://learn.microsoft.com/cli/azure/repos/pr>
- `az repos show` and `az repos ref list --filter`: [azure-devops-cli-extension `commands.py`](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/repos/commands.py) and [`ref.py`](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/repos/ref.py)
- `az devops configure --defaults`: [azure-devops-cli-extension `configure.py`](https://github.com/Azure/azure-devops-cli-extension/blob/master/azure-devops/azext_devops/dev/team/configure.py)
- CLI sign-in and `--detect`: <https://learn.microsoft.com/azure/devops/cli/>
- Azure DevOps MCP server tools, actions, and parameters (`repo_repository`, `repo_branch`, `repo_pull_request`, `repo_pull_request_write`): [microsoft/azure-devops-mcp `docs/TOOLSET.md`](https://github.com/microsoft/azure-devops-mcp/blob/main/docs/TOOLSET.md) and [`src/tools/repositories.ts`](https://github.com/microsoft/azure-devops-mcp/blob/main/src/tools/repositories.ts)
- `defaultBranch` on the repository object: [azure-devops-node-api `GitInterfaces.ts`](https://github.com/microsoft/azure-devops-node-api/blob/master/api/interfaces/GitInterfaces.ts)
- Pull request templates: [`pull-request-templates.md`](https://github.com/MicrosoftDocs/azure-devops-docs/blob/main/docs/repos/git/pull-request-templates.md); page: <https://learn.microsoft.com/azure/devops/repos/git/pull-request-templates>
- Resolution mentions: [`resolution-mentions.md`](https://github.com/MicrosoftDocs/azure-devops-docs/blob/main/docs/repos/git/resolution-mentions.md); page: <https://learn.microsoft.com/azure/devops/repos/git/resolution-mentions>
- Remote URL forms: <https://learn.microsoft.com/azure/devops/repos/git/use-ssh-keys-to-authenticate>
