# Harnesses

A **harness** is the AI coding tool the user runs agents in, such as Claude Code or GitHub
Copilot. The **squad** is Kyber-Weave's set of agents and skills (planner, developers, reviewers,
and the procedures they follow). Installing the squad for a harness copies those files into that
tool's home folder (or into this repository, for a project install). Nothing changes in a harness
the user does not choose.

The supported list comes from the product: the `--target` line of
`kyber-weave squad install --help`. A harness the CLI lists but this file does not: install it
and say no extra steps are known.

## Never drop a harness

`kyber-weave squad update --target <list>` treats the list as the **complete** set: it removes
the squad from every installed harness not named. Installing for Claude Code alone this way
removes an existing Copilot install. So:

- Every `--target` list you pass, on `install` or `update`, at global or project scope, is the
  **union** of the harnesses already installed at that scope (from first-look) and the ones the
  user chose now.
- Say so to the user in one line before running it: "Copilot is already installed, so I am
  passing `copilot,claude` to keep it."
- Scope comes first: the answer to question 2. Before running, say which scope you chose and
  why ("global, because you want it in every repository"). Without `--global`, both verbs act
  on the current directory, which is the project scope and writes harness files into this
  repository, so every command below names its scope. Verb: `update` when the squad already
  exists at that scope, `install` when it does not.

  | Scope | Squad exists there | Command |
  |---|---|---|
  | Global | yes | `kyber-weave squad update --global --target <union>` |
  | Global | no | `kyber-weave squad install --global --target <union>` |
  | This project | yes | `kyber-weave squad update . --target <union>` |
  | This project | no | `kyber-weave squad install . --target <union>` |

- Only refreshing what is installed: `kyber-weave squad update --global` or
  `kyber-weave squad update .`, with no `--target`.
- Removing a harness is a separate question, asked only when the user brings it up, with its own
  context (below). Never remove one as a side effect.

## Question 1: which harnesses

Context to give (the worked example below shows the full wording):

- *What*: a harness, defined as above, and what installing does.
- *Why*: the squad's agents only exist in the tools it is installed for; anywhere else the user
  gets the tool's plain behaviour.
- *Options*: at most four: the recommended set; keeping things as they are; adding a tool found
  on the machine but not in use; and "show me the other supported tools". Name each tool with a
  few words so the user recognises it. Choosing nothing installs nothing.
- *Cost and undo*: about a minute; writes the harness's home folder (global) or this repository
  (project); undo by re-running this skill, or `kyber-weave squad uninstall --global` (or `squad uninstall .` for a project install) for all.
- *Found*: which tools are on the machine and which already have the squad, with its version.
- *Recommend*: the tools in use (first-look: the squad is there, or its command is on `PATH`).
  Tools with only a home folder are named once as "also found", not recommended.

### Worked example

> **Which AI coding tools should get Kyber-Weave's agents?**
>
> A *harness* is the AI coding tool you run agents in. Kyber-Weave installs its agents and skills
> (planner, developers, reviewers) into each one you pick, by copying files into that tool's own
> folder in your home directory. Tools you don't pick are not touched, and their agents keep
> working as they do today.
>
> On this machine I found **Claude Code** (`~/.claude`) and **GitHub Copilot** (`~/.copilot`;
> this covers both the Copilot CLI and Copilot in VS Code). Copilot already has Kyber-Weave
> 1.4.0; Claude Code has none.
>
> I recommend **both**. Copilot stays installed either way: I always pass every installed tool
> along with the new ones, because naming only one would remove the others. It takes about a
> minute, and you can add or remove a tool later by running this setup again. Pick fewer if a
> tool is one you no longer use. Other supported tools (Codex, Cursor, OpenCode, Kilo and seven
> more) are available if you want them listed.
>
> 1. Claude Code and GitHub Copilot (recommended)
> 2. GitHub Copilot only (keep as is)
> 3. Show me the other supported tools

## Question 2: every repository or this one

- *What*: a global install puts the squad in each harness's home folder, so every repository on
  this machine gets it; a project install puts it inside this repository only.
- *Why*: global means one install to keep up to date; project means the repository carries its
  own copy, which teammates get when they pull.
- *Options*: global or project, each with the command from the table above for the verb that
  fits what question 1's detection found.
- *Cost and undo*: global writes under the home directory; project writes repository files that
  show in `git status`. `--dry-run` previews either. In an agent's shell the CLI does not prompt
  for confirmation, so the user's yes here is the only one. A global install with OpenCode also
  installs the KyberDash capture plugin when KyberDash is present; say so, and pass
  `--no-capture-plugin` if the user does not want it.
- *Found*: whether a global or project install already exists. When the squad is already
  installed at one scope and question 1 added nothing, skip this question.
- *Recommend*: global, unless the team wants the agents committed to the repository.

## Question 3: the decision gate (Arbiter hooks)

- *What*: the **Arbiter** is Kyber-Weave's decision gate. A small hook in the harness asks it,
  before each agent is started, whether that step matches the approved plan, and blocks it if not.
- *Why*: without it, an agent can start work the plan never approved; with it, runs stay on plan.
- *Options*: turn it on for each chosen harness, one harness per run, at the scope question 2
  chose: `kyber-weave arbiter install --global --target <one>` or
  `kyber-weave arbiter install --project --target <one>`; or leave it off.
- *Cost and undo*: adds a hook entry to the harness's settings in its home folder (or this
  repository with `--project`), seconds per harness; it keeps the decision model already set up
  (pass `--provider` only to change it). Undo with `kyber-weave arbiter uninstall --global --target <one>` (or `--project`).
- *Found*: the per-harness lines of `kyber-weave arbiter status` (on, off, or on for a harness
  with no squad).
- *Recommend*: on for every harness that gets the squad.

## Removing a harness (only when the user asks)

- *What it does*: deletes the squad's files from that harness; its other agents and settings stay.
- *How*, at the scope that harness is installed at: `kyber-weave squad update --global --target
  <the harnesses to keep>` or `kyber-weave squad update . --target <the harnesses to keep>`,
  after `--dry-run`; and `kyber-weave arbiter uninstall --global --target <removed>` (or
  `--project`) for its hook. With nothing left to keep, `kyber-weave squad uninstall --global`
  or `kyber-weave squad uninstall .`.
- *Undo*: install it again; local edits to the squad's files in that harness are lost.

## Per harness

After the install, give each chosen harness's "Trust step" (what the user must do once in the
tool, or the hooks never run) and "Needs" (a setting the squad cannot write for them). Each
"Needs" item that changes a file is its own question with the full context (what goes wrong
without it, the file, how to undo); change a user or harness settings file only with a yes.

| Harness (install name) | Trust step | Needs |
|---|---|---|
| Claude Code (`claude`) | Accept the workspace trust dialog; approve project MCP servers in `/mcp` on first run. Headless `claude -p` runs no project hooks. | Nothing else; nested sub-agents work by default. |
| Copilot CLI (`copilot`) | None documented. | `COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES=32000` in the shell Copilot CLI starts from (PowerShell: `$env:COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES = "32000"`), then a new session. Without it, large code-index answers are saved to files and agents stop using the index. Show the line; add it to the user's shell profile only if they ask you to. |
| VS Code Copilot (`copilot`) | Open the folder as a trusted workspace and keep `chat.useHooks` on. | User setting `chat.subagents.allowInvocationsFromSubagents: true`, so the reviewer can start its own sub-reviewers. Without it the reviewer applies every review lens itself, which is slower. |
| Cursor (`cursor`) | None documented. | Sub-agents inherit the session's MCP servers; wiring `.cursor/mcp.json` is enough. |
| OpenCode (`opencode`) | None documented. | `subagent_depth` of 2 or more in the OpenCode config (default 1), so the reviewer can start sub-reviewers. A global install also offers the KyberDash capture plugin (`--no-capture-plugin` skips it). |
| Kilo (`kilo`) | None documented. | `subagent_depth` of 2 or more in the Kilo config (default 1). MCP servers go in `kilo.jsonc` under `mcp`. |
| Pi (`pi`) | Trust the project so `.pi/extensions/` loads. | The `@tintinweb/pi-subagents` extension (0.19.0 or later) for native agents, and `pi-mcp-adapter` 6.0.0 or later (7.0.0 supported) for MCP tools. |
| Codex (`codex`) | Trust the project's `.codex/` layer, then review and trust the hook in `/hooks`. Repeat after any `squad update` that changes the hook. | MCP servers in `~/.codex/config.toml`; custom agents inherit them. |
| Factory (`factory`) | None documented. | A global install also offers the KyberDash capture plugin. Reviews run through the review-council skill, since droids cannot start droids. |
| ZCode (`zcode`) | Set `hooks.enabled: true` in `~/.zcode/cli/config.json` (the squad never flips it). Project scope uses the Arbiter MCP fallback. | `kw-docs` wired, or every agent fails `squad doctor`. |
| Antigravity (`antigravity`) | Allow the MCP tools through Antigravity's `mcp(server/*)` policy patterns; they default to Ask. | Must be named explicitly with `--target`; it is never auto-detected. |
| Warp (`warp`) | No hooks. Register the Arbiter server, `kyber-weave-arbiter serve --repo-root <root>`, in Warp's MCP settings. | Agents arrive as role skills. |
| Devin (`devin`) | None documented. | `squad doctor` warns about duplicate imports in `.devin/`. |
| Command Code (`commandcode`) | Register `kyber-weave-arbiter serve --repo-root <root>` through its MCP configuration. | None known. |

After any install, update, hook, or MCP change, the user restarts the harness so it reloads its
agents, hooks, and servers.
