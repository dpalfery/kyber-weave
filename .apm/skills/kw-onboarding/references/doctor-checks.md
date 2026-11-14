# Doctor checks, in plain words

`kyber-weave doctor --check .` checks that this machine and repository have the tools agents
rely on. It prints one line per check: `PASS <id> <reason>`, or `FAIL <id> <reason>` with an
indented `fix:` line. A `PASS` whose reason starts with `warning:` is advice, not a failure.

Exit codes: `0` all good, `1` a check failed, `2` the command was refused (not in a git
repository, the home directory, or two modes at once), `3` CodeGraph is not installed.

**Asking about a fix.** One question per failing or warning line, never the check id alone. Use
the row below for *what* and *why*; *options* are "apply the fix" and "leave it" (say what keeps
failing if left); *cost and undo* and *recommend* are in the last column; *found* is the line's
own reason. Re-run `--check` after each applied fix.

| Check | What it means; why it matters | Usual fix | Cost, undo, recommend |
|---|---|---|---|
| `repository.root` | Doctor must run inside a git repository that is not the home directory. | `cd` into the repository and run it again. | Nothing to ask. |
| `codegraph.binary` | CodeGraph, the code-search tool agents use to find code, is missing or broken. Agents then fall back to slow text search. | The user installs CodeGraph so `codegraph` is on `PATH`. | Kyber-Weave names no installer; do not offer one. Recommend installing it. |
| `codegraph.index` | This repository has no usable code index (the database CodeGraph searches), or it is busy or damaged. | `kyber-weave doctor --provision .` | Asked in step 2 with its full context. |
| `codegraph.explore` | Only after `--ensure`: the index exists but did not answer a query. | `codegraph explore README` from the root shows the tool's own error. | Read-only; recommend running it. |
| `codegraph.restore` | Only after `--ensure`/`--provision`: indexing rewrote a tracked file and doctor put it back. Nothing is wrong. | None. | Nothing to ask. |
| `mcp.start` | The docs server (`kyber-weave-mcp`, which answers agents' questions from this repository's documentation) would not start. | `kyber-weave update` | Replaces the CLI machine-wide, about a minute. Recommend it. |
| `mcp.handshake` | The docs server started but did not answer in time, or refused because the repository has no settings file. | The printed fix: a larger `--mcp-timeout`, or `kyber-weave docs init .`. | Timeout: read-only retry. `docs init`: see first-look.md. |
| `mcp.docs_explore` | The docs server answered but its question tool failed. | `kyber-weave update`, or a retry with a larger `--mcp-timeout`. | Retry first (read-only), then update. |
| `mcp.root` | A docs server is answering for a different checkout, usually a stale process, so agents get answers about the wrong code. | Stop the stale `kyber-weave-mcp` process and re-run the check. | Stopping it ends any session using it; the harness restarts it. Recommend it. |
| `githook.post-checkout` | Advice about a git hook that builds the code index for each new worktree (a second checkout of the repository that parallel agents work in) in the background. Without it, a new worktree starts with no index. | `kyber-weave doctor --ensure .` | Writes `.git/hooks/post-checkout` (shared by every worktree of this repository); the `worktree-index-hook` setting removes it. Recommend it when agents run in worktrees. |
| `copilot.large-output-limit` | Copilot CLI only: answers over 20 KiB are saved to a file instead of shown, so agents stop using the code index. | The user sets `COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES=32000` in the shell Copilot CLI starts from. | One line in a shell profile, outside the repository; undo by deleting it. Recommend it for Copilot CLI users; skip if they do not use Copilot CLI. |
| `copilot.prompt.*` | Only when the opt-in VS Code Copilot system-prompt override is installed: whether each VS Code build points at it. | `kyber-weave squad copilot-prompt install --yes`, or `uninstall` to drop the override. | Machine-wide; see the settings reference before asking. |

A check id not in this table: explain the printed reason in one sentence, say plainly when you do
not know what it affects, and offer the printed fix.
