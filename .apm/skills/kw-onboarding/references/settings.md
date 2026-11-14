# Settings

Every user-facing setting. "Config" is the repository file `.kyber-weave/kyber-weave.yml`; a key
that is absent takes the default shown.

## How to show them

- Show one **common** group at a time, at most five settings, each as: name in plain words,
  current value, the "what" sentence, and the recommendation. Then ask which (if any) to change,
  with "change nothing in this group" first unless first-look found a reason to change one.
- For each setting the user picks, give the rest of its row (why change it, what each value
  does, cost and undo) and ask for the value, recommended first.
- **Advanced** settings are for teams tuning the product; most users never touch them. Do not
  list them. After the common groups, offer once: "There are also advanced settings (review
  thresholds, documentation vocabulary, decision-gate rules). Show them?" If yes, list names and
  "what" only, and for a chosen one read its section of the product's configuration guide
  (`configuration.md` in the Kyber-Weave docs) with the user and edit by hand.
- Leave out a group whose product is not installed, and say so in one line.
- A value the user already changed from its default is shown as it is and recommended as it is;
  never recommend reverting it, though you may say what the default would do.

**Undo and cost, for every config setting.** It is one line in a repository file, visible in
`git status`; change it back the same way. Nothing outside the repository changes.

**How a config change is applied.** Edit the key in place, keeping the user's comments and every
other key. For a setting marked *published* (agents read it from the generated block in the root
`AGENTS.md`), then run `kyber-weave docs init . --no-skill --no-review-gates --no-mcp-check`,
which re-renders that block and leaves everything else alone. Then run `kyber-weave docs
validate .` and fix any finding about the configuration or the generated block; report other
findings without fixing them (documentation is the docs-onboarding skill's job).

## Group 1: how runs behave (config)

A **run** is one piece of work the squad carries from request to pull request: plan, code,
review.

| Setting | Default | What it does | Why change it; what each value does | Recommend |
|---|---|---|---|---|
| `autonomy` *published* | `supervised` | Whether agents stop to ask you before decisions during a run. | `supervised`: agents stop at each decision point and wait for you; slower, you stay in control. `autonomous`: they decide and carry on, and you review the pull request at the end. | `supervised` for a first run; `autonomous` once you trust the squad on this repository. |
| `run.base-branch` *published* | the remote's default branch | The branch runs start from and compare against. | Change it when work branches from something else, such as `develop`; otherwise runs branch from and diff against the wrong place. | The default, unless the team branches from another branch. |
| `stop-on-tool-failure` | `true` | What a code-editing run does when the code index or docs server is broken and cannot be repaired. | `true`: the run stops and tells you, so agents never work blind. `false`: it carries on with a warning, and agents may miss code they cannot search. | `true`. Pick `false` only if the index is often unavailable and you accept weaker runs. |
| `worktree-index-hook` | `true` | A git hook that builds the code index for each new worktree (a second checkout parallel agents work in). | `true`: each new worktree is searchable within a minute or two. `false`: no git hook, and agents in a new worktree search by text until someone indexes it. Offered and applied only when the code index was accepted in step 2: the hook builds that index, and its command, `kyber-weave doctor --ensure .`, also refreshes the index, proves it and the docs server answer, repairs once, and writes `.git/hooks/post-checkout` (ask). If indexing was declined (a "no" stands for the run), say the hook has nothing to build, skip the row and run nothing; to turn both on later, re-run this skill and accept indexing. | `true` when indexing is on and the team allows git hooks; otherwise skip. |
| `ontology.intake` *published* | `false` | Whether, when you ask for work without saying "plan" or "spec", the planner first recommends which one. | `false`: such a request gets a short implementation plan. `true`: the planner suggests a plan or a full specification (requirements, design, tasks) and asks you to choose. | `false`; `true` if the team often does large features that need a specification. |

`onboarding` is not asked here: it records whether the repository's technologies, gates and
coding standards were set up, and every run stops while it is unanswered. The
`kyber-docs-onboarding-and-assessment` skill answers it (step 8).

## Group 2: reviews (config, `review:`)

The **review council** is the set of reviewer agents that check each change before it is
delivered; a **finding** is one problem they report, graded critical, major, minor or nitpick.

| Setting | Default | What it does | Why change it; what each value does | Recommend |
|---|---|---|---|---|
| `review.policy.fix-level` | `minor` | How much of what a review finds gets fixed in the same run. Each level includes the ones before it. | `critical`: only failed checks and critical findings (data loss, a security hole, silent breakage); fastest, most left for later. `major`: also changes that are wrong for normal use. `minor`: also narrow edge cases, misleading comments, missing tests. `nitpick`: also pure style. Anything below the level is listed in the pull request and filed as a todo. | `minor` (quality first). `major` for faster runs; `nitpick` for teams that want everything fixed. |
| `review.policy.strictness` | `balanced` | How many major findings block a change, and how sure a reviewer must be to report one. | `lenient`: blocks at 5 majors, reports only confident findings (fewer, surer). `balanced`: 3. `strict`: 1, and reports less certain findings too (more noise, fewer misses). | `balanced`; `strict` for security- or money-critical code. |
| `review.max-concurrent-workers` | `4` | How many agents work at once. | Lower it on a small machine or a tight model budget (slower runs, less load and cost); raise it on a large machine. | `4`; lower it if the machine slows down or model costs run high during runs. |
| `review.coverage.file-line-percent`, `class-line-percent` | `0` (off) | Minimum test coverage a change needs to pass review. | Above `0`, a change whose files or classes fall below that share of covered lines is blocked until tests are added. | Off, unless the team already enforces coverage. |

After `fix-level`, offer the severity-overrides section below.

### Severity overrides (what counts as critical, major, minor or nitpick here)

- *What*: a table in the review guidance file (**<review-guidance>** in the Config Reg, the
  generated paths block in the root `AGENTS.md`; `REVIEW.md` by default) that re-grades a class
  of finding for this repository, up or down.
- *Why*: `fix-level` and strictness read the overridden grade, so this is how a team says "a
  missing API doc is major here" or "naming style is a nitpick here".
- *Options*: add the empty section now (rows are added later, only with what the user states), or
  skip it; without it the shipped grading applies.
- *Cost and undo*: one section in a repository file (a new `REVIEW.md` holding only that section
  when there is none); delete it to undo. `kyber-weave docs validate .` checks the rows.
- *Recommend*: skip on a first setup unless the user already disagrees with a grading.

The section, added empty with the format and one commented example:

```markdown
## Severity overrides

Host grading that outranks the shipped lens guidance and the general severity definitions.
Severity: critical, major, minor or nitpick. Applies to: lens or technology names, comma-separated;
empty for every seat. A seat that applies a row writes `override: <Id>` on the finding.

| Id | Finding class | Severity | Applies to | Why |
|---|---|---|---|---|
<!-- | SO-1 | Public API change without a documentation update | major | documentation-accuracy | Docs ship with the API | -->
```

Never invent a row.

## Group 3: the decision gate (config `arbiter:`)

These go in the repository file. The same keys in `~/.kyber-weave/kyber-weave.yml` set them for
every repository on the machine; edit that file only when the user asks for a machine-wide change.

| Setting | Default | What it does | Why change it; what each value does | Recommend |
|---|---|---|---|---|
| `arbiter.dispatch-budget.minutes`, `tool-calls`, `call-minutes` | `15`, `150`, `5` | How long, and how many tool calls, one agent may use on one task before it is stopped; and how long one tool call may hang. | Higher: slow builds and large tests can finish, but a stuck agent wastes more time and tokens. Lower: stuck agents stop sooner, but slow tasks are cut off. | The defaults; raise `minutes` if builds or test runs regularly take over 10 minutes. |
| `arbiter.comment-triage.bot-logins` | none | Review bots whose summary comments on a pull request are sorted into real, nitpick or already fixed (logins ending `[bot]` always are). | Add a bot that posts reviews without a `[bot]` login, or its comments are not sorted. | None, unless such a bot reviews this repository. |

The decision model (`arbiter.provider.*`) is step 6; turning the gate on is step 3.

## Group 4: this machine (outside the repository: always ask)

| Setting | Default | What it does | Why change it; how | Recommend |
|---|---|---|---|---|
| Reply style of the conductor and bug-crusher | shipped default | How the two agents you talk to word their messages. | To make replies shorter, longer or differently structured: edit `~/.kyber-weave/response-guidance.md`, then `kyber-weave squad update --global` (no `--target`), which also refreshes every installed harness's squad files to the CLI's version, not only the reply style; say so and run `--dry-run` first. Undo by restoring the file. | The default. |
| `COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES` | unset (20 KiB) | Copilot CLI only: largest tool answer shown to the agent rather than saved to a file. | Unset, agents stop using the code index on large answers. See the Copilot CLI row in `harnesses.md`. | `32000` for Copilot CLI users. |
| Status line (Claude Code, Antigravity, Pi) | not deployed | Kyber-Weave's status line for the harness. What it displays is not documented in the CLI help: say so rather than describe it. | `kyber-weave utilities statusline deploy --target <claude\|agy\|pi>` stages its files and prints a snippet the user adds to the harness's settings. | Optional; skip on a first setup. |

## Group 5: KyberDash (only when `kyberdash` is installed)

KyberDash is the local dashboard of agent sessions, cost and tokens. Read with
`kyberdash dash settings show`; change with `kyberdash dash settings set <name> <value>`, never by
editing its store. Undo the same way.

| Setting | Default | What it does | Why change it | Recommend |
|---|---|---|---|---|
| `folder_import.scheduled` | `off` | Whether scheduled, tray and web refreshes read the harnesses' local session folders. | `on`: the dashboard fills itself from local history. `off`: it reads them only when you import on demand. | `off` until the user wants automatic history. |
| `jobs.paused` | `off` | Pauses the dashboard's scheduled refresh jobs. | `on` stops background work, for example on battery. | `off`. |
| `jobs.refresh_cadence_minutes` | `5` | How often the dashboard looks for new work (1 to 1440). | Higher: less background load, staler numbers. | `5`. |
| `dashboard.port` | `4747` | Local port of the web dashboard; applies on the next engine restart. | Change only if another program uses 4747. | `4747`. |

## Advanced settings (only on request)

| Setting | Default | What it does |
|---|---|---|
| `review.parallelism` | `2` | How many review checks run at once. |
| `review.council-deadline-seconds` | `600` | Time budget for the review's check run; checks still running at the deadline are reported as not run. |
| `review.policy.major-count-blocks`, `min-confidence`, `suppressions` | from strictness; none | Override the two strictness numbers, or silence a named finding until a date, with a reason. |
| `review.duplicates.minimum-lines` | `4` | Shortest duplicated block the duplicate-code check reports. |
| `review.gates`, `review.task-gates`, `review.test-map` | from onboarding | The build, test and lint commands each change must pass. Owned by the docs-onboarding skill; `kyber-weave review catalogue` prints the shipped set. |
| `mcp.required-servers` | `kw-docs` | Which MCP servers the repository's agents rely on; step 2 handles it. `kw-dash` (KyberDash analytics) is optional. |
| `ontology.docs-root`, `ontology.technologies` | `docs`, none | Where governed docs live and which technologies have coding standards. Owned by the docs-onboarding skill. |
| Other `ontology.*` keys | product vocabulary | Document types, statuses, required fields and paths the docs checks enforce. |
| `docs-analysis.*` | presets | Tuning for duplicate and conflict detection, and optional local embeddings (`docs-analysis.embeddings.mode`, default `off`). |
| `config-reg` | none | Extra named paths published to agents. |
| `harness.profiles` | none | Declares a harness's legitimate capability differences so the agent parity check does not report them. |
| `squad.targets`, `squad.exclusions` | detect from harness folders; none | Fix or exclude the harnesses a project install deploys to. |
| `squad.version` | the CLI's version | Pins the squad release; must equal the CLI's version. |
| `arbiter.model-targeting.mode` | `shadow` | Whether the gate picks a cheaper or stronger model tier per task by difficulty: `shadow` logs its choice only, `enforce` applies it, `off` disables it. |
| `arbiter.stores.max-item-bytes`, `retain-days` | `16384`, `14` | Size cap and retention of the gate's local records. |
| `arbiter.rules` | shipped rules | Turn a gate rule off, put it in shadow, or change its threshold (`kyber-weave arbiter rules`, `kyber-weave arbiter tune`). |
| `arbiter.provider.endpoint`, `model`, `timeout-ms` | `https://api.typesafe.ai/v1`, `jev-1.14.0`, `3000` | Where the decision model is called and how long to wait. OpenRouter: `https://openrouter.ai/api/v1`, model `typesafe/jev-1.14`. A per-user override may live in `~/.config/kyber-weave/arbiter.yml`. |
| VS Code Copilot system-prompt override | off | Replaces Copilot's top-level system prompt in VS Code for every chat on the machine. `kyber-weave squad copilot-prompt install` (`--dry-run` first; `status`, `uninstall`). |
| Operator channel (Copilot CLI, global) | off | Lets the conductor message the user during an unattended run through an MCP server the user runs. Needs `KYBER_SQUAD_OPERATOR_CHANNEL=true`, `KYBER_SQUAD_OPERATOR_CHANNEL_SERVER=<name>`, the server in Copilot CLI's `mcp-config.json`, then `kyber-weave squad update --global` (no `--target`), which also refreshes every installed harness's squad files to the CLI's version; say so and run `--dry-run` first. |
| KyberDash `receiver.hosted` | `off` | Whether the dashboard's web server also runs its telemetry receiver. |

## Decision-model key

Step 6 asks one question with this context.

- *What*: with `arbiter.provider.kind: systemone`, the decision gate asks a small hosted model
  (TypeSafe's Jev) narrow yes/no questions about text agents wrote, such as whether a step matches
  a task in the plan. `kyber-weave docs tasks --check` also uses it to ask whether each task is
  self-contained. The key is what lets the gate call that model.
- *Why*: without a key, questions that need the model are answered "undecidable", which always
  allows. Checks based on machine facts still run. Nothing breaks; the gate is weaker.
- *Options*: set up a key now (the user runs the command), or keep the gate without a model
  (`none`).
- *Cost and undo*: an API key from TypeSafe or OpenRouter (their pricing applies); stored in the OS credential store
  by the product's own command, never in a file by this skill. Undo with `kyber-weave arbiter
  uninstall --global --target <name> --remove-key` (or `--project`), or by deleting the credential.
- *Found*: `kyber-weave arbiter status` says whether a key is found, never its value, and which
  provider and endpoint are set.
- *Recommend*: keep what is set if a key is found; otherwise skip on a first setup and add one
  when runs are routine.

**Where the key comes from.** The `TYPESAFE_API_KEY` environment variable of the process
first, then the OS credential store entry for the endpoint. It is never written to a
configuration, hook or MCP file.

**How to set it (recommended: the OS credential store).** The user runs one of these
themselves, in their own terminal, and never pastes the key into the chat; this skill never
writes the key anywhere:

- `kyber-weave arbiter setup`: choose the provider; the key is entered at a masked prompt and
  stored in the OS credential store.
- `kyber-weave arbiter setup --provider systemone --key-stdin`, with the key piped in from a
  password manager's command line.
- With the hook install in one step: `kyber-weave arbiter install --global --target <name>
  --provider systemone --key-stdin` (or `--project`), the key piped the same way.

**Environment variable (only if already in place).** A user may already have `TYPESAFE_API_KEY`
set for the current process, or inject it from a secret manager or a CI secret; that works and
needs no step from this skill. Tell the user plainly that putting the key in a shell profile
stores it unencrypted on disk, where backups and dotfiles repositories can read it, and is not
recommended. Do not give an `export` line to copy into a profile.

Avoid `--arbiter-key <key>`: the key lands in shell history. Check the result with
`kyber-weave arbiter status`.
