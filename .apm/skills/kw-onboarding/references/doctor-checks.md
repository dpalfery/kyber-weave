# Doctor checks, in plain words

`kyber-weave doctor --check .` prints one line per check: `PASS <id> <reason>`, or
`FAIL <id> <reason>` with an indented `fix:` line. A `PASS` whose reason starts with `warning:` is
advice, not a failure. Run the printed fix only with the user's yes, and re-run `--check` after.

Exit codes: `0` all good, `1` a check failed, `2` the command was refused (not in a git
repository, the home directory, or two modes at once), `3` CodeGraph is not installed.

| Check | What it means for the user | Usual fix |
|---|---|---|
| `repository.root` | Doctor must run inside a git repository that is not the home directory. | `cd` into the repository and run it again. |
| `codegraph.binary` | The CodeGraph command-line tool is missing or broken. Agents then fall back to slow text search. | The user installs CodeGraph so `codegraph` is on `PATH`. Kyber-Weave names no installer; do not offer one. |
| `codegraph.index` | This repository has no usable code index, or it is busy or damaged. | `kyber-weave doctor --provision .` builds or refreshes it (ask first: indexing is the user's call). |
| `codegraph.explore` | Only after `--ensure`: the index exists but did not answer a query. | `codegraph explore README` from the root shows the tool's own error. |
| `codegraph.restore` | Only after `--ensure`/`--provision`: indexing rewrote a tracked file and doctor put it back. Nothing is wrong. | None. |
| `mcp.start` | The `kyber-weave-mcp` docs server would not start. | `kyber-weave update`. |
| `mcp.handshake` | The docs server started but did not answer in time, or refused because the repository has no `.kyber-weave/kyber-weave.yml`. | The printed fix: a larger `--mcp-timeout`, or `kyber-weave docs init .`. |
| `mcp.docs_explore` | The docs server answered but its question tool failed. | `kyber-weave update`, or a retry with a larger `--mcp-timeout`. |
| `mcp.root` | A docs server is answering for a different checkout, usually a stale process. | Stop the stale `kyber-weave-mcp` process and re-run the check. |
| `githook.post-checkout` | Advice about the git hook that indexes each new worktree in the background. | `kyber-weave doctor --ensure .` installs or refreshes it (writes `.git/hooks`, so ask). The `worktree-index-hook` setting turns it off. |
| `copilot.large-output-limit` | Copilot CLI only: large tool results are saved to a file instead of shown, so agents stop using the code index. | The user sets `COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES=32000` in the shell Copilot CLI starts from. See the Copilot CLI row in `harnesses.md`. |
| `copilot.prompt.*` | Only when the opt-in VS Code Copilot system-prompt override is installed: whether each VS Code build points at it. | `kyber-weave squad copilot-prompt install --yes`, or `uninstall` to drop the override. |

A check id not in this table: explain the printed reason in one sentence and offer the printed fix.
