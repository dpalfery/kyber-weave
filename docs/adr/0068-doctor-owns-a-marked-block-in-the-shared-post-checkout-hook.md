---
id: adr/0068-doctor-owns-a-marked-block-in-the-shared-post-checkout-hook
title: doctor --ensure owns a marked block in the clone's shared post-checkout hook to index new worktrees
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-11
component: DocGraph
decided-by: []
supersedes: []
code-refs:
  - PostCheckoutHook
  - PostCheckoutHookText
  - CodeGraphRestore
  - WorktreeIndexGuard
  - CodeGraphProvisioner
  - RepositoryDoctor
---

# ADR 0068: doctor --ensure owns a marked block in the clone's shared post-checkout hook to index new worktrees

## Status

Accepted, 2026-11-11, by the owner's decision. This ADR records a decision that writes into a file the
user owns, the clone's git hooks, so it is the first record of Kyber-Weave changing git's own hook
behaviour. It extends [ADR 0061](0061-code-editing-runs-stop-on-unusable-tools.md), which requires each
worktree to be indexed through `doctor --ensure`, by covering the trees that no run or review starts.
It does not change the stop rule, the `doctor --ensure` steps, or any renderer's grant.

## Context

`.codegraph/` is not tracked by git, so a worktree made with a plain `git worktree add` starts with no
CodeGraph index, and nothing fails. `kyber-weave run start` and `kyber-weave review gates` index the tree
they use through `WorktreeIndexGuard`, so the trees they create or review are covered. A person, or an
agent in a session Kyber-Weave did not start, that runs `git worktree add` and then works in that tree
directly is not covered. Its agent falls back to raw file reads, or is answered from another branch's index.

The index is needed in the first seconds of a new worktree, before any command runs in it. The only
moment every route into a new worktree shares is git itself, so the question is which hook git runs and
who owns it.

## Decision

1. **A git `post-checkout` hook covers every harness and every person at a terminal.** One mechanism serves
   every agent harness and a shell alike, so no harness needs its own hook.
2. **`kyber-weave doctor --ensure` installs and repairs the hook. `doctor --check` and `--provision` only
   report it.** The check id is `githook.post-checkout`. It warns and never changes the exit code, because
   a hook is a convenience and not a required tool.
3. **The hook is one marked, owned block in the clone's shared hooks folder.** The folder is the one
   `git rev-parse --git-path hooks` names, so `core.hooksPath` is honoured and one install covers every
   linked worktree, which shares the common git directory's hooks. An existing hook is kept: the block is
   inserted directly after its shebang and replaced in place on later runs, and every other line is left
   byte for byte as it was. A hook that is not a POSIX shell script, has no shebang, uses CRLF line endings,
   or lacks the executable bit is reported and left alone. A hooks folder that git tracks is reported, not
   written.
4. **The block acts only on a new worktree's first checkout.** It runs only when the previous HEAD argument
   is all zeros and the third argument is `1`, so an ordinary branch switch costs one shell `case` test. It
   starts `kyber-weave doctor --post-checkout .` in the background, with its standard input, output and
   error redirected and its log written to `kyber-weave-index.log` in the worktree's own git directory.
5. **The block never fails a checkout.** It never calls `exit`, because it sits before the owner's body and
   an `exit` would skip it. Every command that can fail is inside a test or an AND-OR list, and `${1:-}` and
   `${3:-}` survive `set -u`. It does nothing when `kyber-weave` is not on `PATH`. Git's exit status for
   `checkout`, `switch`, `clone` and `worktree add` is therefore never the hook's.
6. **The background run restores only what `codegraph init` writes.** The run's index work restores tracked
   files on a fixed list, `CodeGraphProvisioner.InitConfigFiles`, and names every other newly dirty file in a
   passing `codegraph.restore` check, so an edit made in the new tree while the run is going is never
   reverted. The list is empty today because a measured `codegraph init .` modifies no tracked file, so the
   run restores nothing. `doctor --ensure` keeps its existing restore of every file it wrote.
7. **The hook and `run start` serialise on the existing index lock.** Whichever starts first builds the
   index; the other waits and only syncs. `run start` does not suppress the hook for the tree it creates.
8. **The setting is `worktree-index-hook:` in `.kyber-weave/kyber-weave.yml`,** `true` by default, parsed
   strictly like `stop-on-tool-failure`. With `false`, `--ensure` removes the block and the hook's run exits
   at once. See [configuration](../configuration.md#worktree-index-hook).

## Alternatives Considered

- **A hook per agent harness.** Rejected. It would mean one install and one repair per harness, and a
  person at a terminal would still have none. One git hook is one mechanism to install, repair and test.
- **Index only in `run start` and the review gates.** Rejected. This is the rule that already exists
  ([ADR 0061](0061-code-editing-runs-stop-on-unusable-tools.md)), and it leaves a worktree created by
  `git worktree add` without an index for anyone who does not start a run in it.
- **`doctor --ensure --keep-edits`, or calling `codegraph init` from the shell.** Rejected for the hook's
  run. The first still runs the MCP probe on every checkout; the second has no lock, no repair and no
  governed-docs check.
- **Skip every restore in the hook's run.** Rejected once measured. A later index run that writes a tracked
  file would leave it modified in every new worktree for good, and a run racing `run start` could leave
  that tree dirty. The restore is kept on a fixed list, so it reverts only files the index wrote.

## Consequences

- A clone with governed docs gets the index on every new worktree, after `doctor --ensure` has written
  the block once. A clone that has not run `doctor --ensure` keeps the old behaviour, and the doctor
  reports the missing block.
- Kyber-Weave now writes a file that belongs to the user. Writes are limited to the marked block, and any
  hook it cannot safely edit is reported rather than changed.
- The block is shell code that runs on every checkout of every worktree of the clone. It stays small: its
  work is one `case` test on the ordinary path.
- The index starts in the background, so the first agent in a new worktree can still find the index
  building. `kyber-weave-index.log` in the worktree's git directory says when it is ready.
- Windows is not live-verified. Git for Windows runs the same block through its own shell. If the
  `kyber-weave` on `PATH` is not found, the block does nothing, which is the safe failure.
- A `kyber-weave` older than the block rejects `--post-checkout` in the background, and the checkout is
  unaffected. The next `doctor --ensure` with a newer binary rewrites the block in place.
