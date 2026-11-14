# Settings inventory

Every user-facing setting, grouped the way the walkthrough asks about them. "Config" is the
repository file `.kyber-weave/kyber-weave.yml`. A key that is absent takes the default shown.

**How a config change is applied.** Edit the key in place, keeping the user's comments and every
other key. For a setting marked *published*, then run
`kyber-weave docs init . --no-skill --no-review-gates --no-mcp-check`, which re-renders the
generated settings block in the repository root `AGENTS.md` and leaves everything else alone.
Then run `kyber-weave docs validate .`. A setting marked *advanced* is listed so the user knows it
exists; if selected, explain it, read its section in the product's configuration guide
(`configuration.md` in the Kyber-Weave docs) with the user, and edit by hand.

## Run behaviour (config)

| Setting | Default | What it does; when to change it | Options (recommended first) |
|---|---|---|---|
| `autonomy` *published* | `supervised` | Whether agents stop to ask before decisions. Change to let runs go end to end without questions. | `supervised`, `autonomous` |
| `onboarding` *published* | unanswered | Whether this repository chose its technologies, gates and coding standards. Unanswered stops every run. Do not change it here: the `kyber-docs-onboarding-and-assessment` skill answers it, or `kyber-weave docs onboarding declined` records a no. | `done`, `declined` |
| `stop-on-tool-failure` | `true` | Whether a code-editing run stops when the code index or the docs server's index read cannot be repaired. Change only if the user prefers to continue with possibly missing code lookups. | `true`, `false` |
| `worktree-index-hook` | `true` | A git hook that indexes each new worktree in the background. Change if the user does not want a git hook. Apply with `kyber-weave doctor --ensure .`, which adds or removes the hook block (ask first). | `true`, `false` |
| `run.base-branch` *published* | the remote's default branch (`origin/HEAD`) | The branch runs start from and compare against. Change when work should branch from something else, such as `develop`. | a branch name |
| `ontology.intake` *published* | `false` | Turns on the intake question the conductor asks when a request names no plan or specification. | `false`, `true` |

## Review (config, `review:`)

| Setting | Default | What it does; when to change it | Options |
|---|---|---|---|
| `review.max-concurrent-workers` | `4` | How many worker agents run at once. Lower it on a small machine or a tight model budget. | a positive whole number |
| `review.parallelism` | `2` | How many review gates run at once. Raise it only on a machine with spare cores. | a positive whole number |
| `review.council-deadline-seconds` | `600` | Time budget for the review council's gate run; gates still running at the deadline are reported as not run. | a positive whole number of seconds |
| `review.policy.strictness` | `balanced` | How many major findings block a change and how confident a finding must be: `lenient` (5 majors, confidence 8), `balanced` (3, 7), `strict` (1, 5). | `balanced`, `strict`, `lenient` |
| `review.policy.fix-level` | `minor` | How much of what a review finds is fixed in the run; each level includes the ones before it. `critical`: only failed blocking gates and critical findings (data loss, a security hole, corruption, behaviour that breaks silently). `major`: also major findings (the change is wrong or fails under conditions a normal user reaches). `minor`: also minor findings (a narrow edge case, a misleading comment, a missing test); the default, quality first. `nitpick`: also purely stylistic points such as formatting and naming style, for a team that wants everything fixed. Findings below the level are listed in the pull request and filed as todos (nitpicks only in the review report). It decides what is fixed, not what blocks; strictness decides that. A retired `minor-findings` key reads as `major` (`follow-up`) or `minor` (`fix`). | `critical`, `major`, `minor`, `nitpick` |
| `review.policy.major-count-blocks`, `min-confidence`, `suppressions` *advanced* | from strictness; none | Override the two strictness numbers directly, or suppress a named finding until an expiry date with a reason. | — |
| `review.coverage.file-line-percent`, `class-line-percent` | `0` (off) | Minimum line coverage the review verdict requires. | a percentage |
| `review.duplicates.minimum-lines` | `4` | Shortest duplicated block the duplicate-code check reports. | a whole number |
| `review.gates`, `review.task-gates`, `review.test-map` *advanced* | from onboarding | The build, test and lint commands each change must pass. Owned by the `kyber-docs-onboarding-and-assessment` skill; `kyber-weave review catalogue` prints the shipped set for the declared technologies. | — |

### What counts as critical, major, minor or nitpick here

`fix-level` acts on severities, and a repository can set its own: the **Severity overrides**
section of the review guidance file (**<review-guidance>** in the Config Reg, `REVIEW.md` by
default). Each row names a class of finding and the severity it is graded at, up or down; a row
outranks the shipped lens guidance and the general definitions, and the overridden severity is
the one `fix-level` and strictness read. `kyber-weave docs validate` checks the rows. When the user
wants one, add the section empty, with the format and one commented example:

```markdown
## Severity overrides

Host grading that outranks the shipped lens guidance and the general severity definitions.
Severity: critical, major, minor or nitpick. Applies to: lens or technology names, comma-separated;
empty for every seat. A seat that applies a row writes `override: <Id>` on the finding.

| Id | Finding class | Severity | Applies to | Why |
|---|---|---|---|---|
<!-- | SO-1 | Public API change without a documentation update | major | documentation-accuracy | Docs ship with the API | -->
```

Rows hold only what the user states; never invent one. A repository with no review guidance file
gets a `REVIEW.md` holding just this section (the product ships no other template for it).

## Documentation and tools (config)

| Setting | Default | What it does; when to change it |
|---|---|---|
| `mcp.required-servers` | `kw-docs` | Which MCP servers the repository's agents rely on. Handled in step 2; `kw-dash` (KyberDash analytics) is optional. |
| `ontology.docs-root`, `ontology.technologies` *advanced* | `docs` as `docs init` scaffolds it, none | Where governed docs live and which stacks have coding standards. Owned by the docs-onboarding skill. |
| Other `ontology.*` keys *advanced* | product vocabulary | The document types, statuses, required frontmatter keys, excluded paths and catalog layout the docs checks enforce. |
| `docs-analysis.*` *advanced* | presets | Tuning for duplicate and conflict detection, and optional local embeddings (`docs-analysis.embeddings.mode`, default `off`). |
| `config-reg` *advanced* | none | Extra named paths published to agents, for skills that look up repository documents by name. |
| `harness.profiles` *advanced* | none | Declares a harness's legitimate capability differences so the agent parity check does not report them. |

## Squad (config, `squad:`)

| Setting | Default | What it does; when to change it |
|---|---|---|
| `squad.targets` | empty: detect from harness folders | Fixes the harnesses a project-scope install deploys to. Set it when detection picks the wrong ones. |
| `squad.exclusions` | empty | Harnesses never to deploy to in this repository. |
| `squad.version` | the running CLI's version | Pins the squad release; must equal the CLI's version. Leave unset unless the team pins versions. |

## Arbiter (config `arbiter:`; the user-level `~/.kyber-weave/kyber-weave.yml` at global scope)

| Setting | Default | What it does; when to change it | Options |
|---|---|---|---|
| `arbiter.enabled` | `false` | Turns the dispatch gate on. Set by `kyber-weave arbiter install` in step 3, not by hand. | — |
| `arbiter.provider.kind` | `none` | Which decision model answers the questions that need reading text. `none` keeps only the checks that need no model. Change with `kyber-weave arbiter setup` or `arbiter install --provider systemone`. | `none`, `systemone` |
| `arbiter.provider.endpoint`, `model`, `timeout-ms` | `https://api.typesafe.ai/v1`, `jev-1.14.0`, `3000` | Where the model is called and how long to wait. Change for OpenRouter (`https://openrouter.ai/api/v1`, model `typesafe/jev-1.14`) or a local server. A per-user override may live in `~/.config/kyber-weave/arbiter.yml` (provider only). | — |
| `arbiter.model-targeting.mode` | `shadow` | Whether the Arbiter picks a cheaper or stronger model per task: `shadow` logs its choice only, `enforce` applies it, `off` disables it. | `shadow`, `enforce`, `off` |
| `arbiter.dispatch-budget.minutes`, `tool-calls`, `call-minutes` | `15`, `150`, `5` | How long, and how many tool calls, one worker may use before it is stopped, and the gap that counts as a hung call. Raise for slow builds. | whole numbers, at least 1 |
| `arbiter.stores.max-item-bytes`, `retain-days` | `16384`, `14` | Size cap and retention for the Arbiter's run stores. | whole numbers |
| `arbiter.comment-triage.bot-logins` | none | Review bots whose summary comments are triaged row by row (logins ending `[bot]` always are). | login names |
| `arbiter.rules` *advanced* | shipped rules | Turn a rule off, put it in shadow, or change its threshold. See `kyber-weave arbiter rules` and `arbiter tune`. | — |

## This machine

| Setting | Default | What it does; how to change it (ask: it is outside the repository) |
|---|---|---|
| Squad deployment and Arbiter hooks per harness | none | Step 3. |
| Model preset per harness | bundled models | Step 5, through the `kyber-weave-model-profile` skill. |
| Reply style of the conductor and bug-crusher | shipped default | Edit `~/.kyber-weave/response-guidance.md`, then `kyber-weave squad update --global`. |
| VS Code Copilot system-prompt override | off | Replaces Copilot's top-level system prompt in VS Code for every chat on the machine. `kyber-weave squad copilot-prompt install` (`--dry-run` first; `status`, `uninstall`). |
| Operator channel (Copilot CLI, global) | off | Lets the conductor message the user during an unattended run through an MCP server the user runs. Needs `KYBER_SQUAD_OPERATOR_CHANNEL=true`, `KYBER_SQUAD_OPERATOR_CHANNEL_SERVER=<name>`, the server declared in Copilot CLI's `mcp-config.json`, then `squad update --global`. |
| Status line (Claude Code, Antigravity, Pi) | not deployed | `kyber-weave utilities statusline deploy --target <claude\|agy\|pi>`, which prints the snippet the user adds to the harness. |
| `COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES` | unset (Copilot's 20 KiB) | Copilot CLI only; see `harnesses.md`. Recommended `32000`. |
| Decision-model key | none | Step 6. |

## KyberDash (only when `kyberdash` is installed)

Read with `kyberdash dash settings show`; change with `kyberdash dash settings set <name> <value>`,
never by editing the store.

| Setting | Default | What it does |
|---|---|---|
| `folder_import.scheduled` | `off` | Whether scheduled, tray and web refreshes read local harness session folders. |
| `jobs.paused` | `off` | Pauses the engine's scheduled jobs. |
| `jobs.refresh_cadence_minutes` | `5` | How often the job host looks for work (1 to 1440). |
| `receiver.hosted` | `off` | Whether the dashboard server runs the telemetry receiver. |
| `dashboard.port` | `4747` | Local port of the web dashboard; applies on the next engine restart. |

## Decision-model key

**What it is for.** With `arbiter.provider.kind: systemone`, the Arbiter asks TypeSafe's Jev
decision model narrow yes/no and multiple-choice questions about agent-written text, such as
whether a dispatch matches a task in the plan. `kyber-weave docs tasks --check` also uses it to
ask whether each task packet is self-contained.

**Without a key.** Questions that need the model are answered "undecidable", and undecidable
always allows. The checks based on machine facts still run. Nothing breaks; the gate is weaker.

**Where the key comes from.** The `TYPESAFE_API_KEY` environment variable first, then the OS
credential store entry for the endpoint. It is never written to a configuration, hook or MCP
file.

**How to set it.** The user runs one of these themselves:

- `kyber-weave arbiter setup`: choose the provider; the key is entered at a masked prompt and
  stored in the OS credential store.
- `kyber-weave arbiter install --target <token> --provider systemone --key-stdin`, with the key
  piped in from a password manager.
- `export TYPESAFE_API_KEY=...` in their own shell profile.

Avoid `--arbiter-key <key>`: the key lands in shell history. Check the result with
`kyber-weave arbiter status`, which prints whether a key is found and never the key.
