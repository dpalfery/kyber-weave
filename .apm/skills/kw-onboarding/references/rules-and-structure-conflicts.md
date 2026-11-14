# Rules and structure conflicts

An assessment of every instruction an agent reads in this repository: where two instructions
disagree, where an instruction disagrees with the code, where it names something that no longer
exists, and where it sits in the wrong place. Strict models follow instructions literally, so each
of these costs real time on every run.

Run it **read-first**: phases A to D change nothing. Phase E asks the user which findings to act
on; phase F changes only those, each with consent. It is resumable: save the findings list (phase
D) to a scratch file that git ignores, such as the harness's scratch folder or the scratch path the
repository's `AGENTS.md` names (where there is none, keep it in the conversation). On a resumed run,
redo phase B and compare with the saved list: a finding that no longer appears is done.

## Rules and standards

Every statement in this assessment is either a rule or belongs in a standard.

- **Rules** apply to every task, whatever the technology, and breaking one is never acceptable:
  secrets, what may be committed or pushed, architecture boundaries, scope, what needs human
  approval. Every agent reads them on every dispatch, so they are few and short. Ideally each is
  checkable by a gate or the Arbiter, not only by reading. They live in the always-loaded sources:
  the root `AGENTS.md` and the rules folder (**<rules-index>** in the Config Reg).
- **Standards** are per technology: how C#, React, SQL or tests are written here. Only the agent
  working in that technology fetches one (the docs tool `docs_get_coding_standard_for_technology`).
  A standard can be long, with examples. It has a lifecycle (`status`, `owner`) and is what
  reviewers grade against. Standards live under **<standards-root>**.
- **The placement test:** does the statement apply to every task, and is breaking it never OK? Yes
  means a rule. Anything else belongs in a standard.
- **One home per statement.** A rule may point to a standard for detail, but the text lives in one
  place. Example: the layer boundary is a one-line rule; what goes in which project or folder is in
  the C# standard.
- **Size budget.** Everything always loaded should fit in about **3 KB**. A rule set larger than
  that contains standards. Report blocks a tool generates (the Config Reg, a CodeGraph block)
  separately from the hand-authored text, so the user can see which part to cut.

## Policies the resolutions follow

- **Documented structure wins over the code.** When a rule states a structure and the code does
  not match, the rule states the intent and the code is technical debt. The exception is a
  deliberate divergence, which must carry a code comment saying so. Never resolve a structure
  finding by rewriting the rule to describe the code, unless the user says the rule is wrong.
- **Out-of-scope issues are filed, not fixed.** An issue an agent finds in a file it touched, but
  outside its assigned scope, is filed as a todo (under **<todo-index>**). When the assigned scope
  is finished, the user is asked whether to address the todo as a fresh pull request and run. A rule
  that makes an agent "own every issue in a file it touches" contradicts this.
- **The product's own gates are the gates.** Kyber-Weave's flow already defines where a run stops
  for a human (intake, plan approval, review, the `autonomy` setting). A rule that adds a stop or an
  "ask first" inside that flow, without saying how it relates to the flow's gates, stops autonomous
  runs.
- **Generated blocks are never hand-edited.** Text between generator markers belongs to the tool
  that wrote it (see phase A).

## Categories

| # | Category | What it catches |
|---|---|---|
| 1 | Instruction vs instruction | The same subject stated twice with different content; a prohibition whose exception is buried later in the same bullet or paragraph; an `AGENTS.md` rule contradicted by a rules-folder file or a standard; the rules folder and a standard covering the same ground differently. |
| 2 | Rule vs code | A rule stating where code lives, what a project may contain or depend on, or which library or pattern to use, and code that does not match. |
| 3 | Stale references | Tool names, MCP server names, commands and flags that do not exist; paths that do not exist; links that do not resolve; project or namespace names that do not exist. |
| 4 | Broken or empty instructions | A heading or lead-in with nothing under it; a typo in a heading or keyword agents key on; an instruction that names no action. |
| 5 | Does not fit this repository | A statement about a technology, product, component or infrastructure that the repository does not contain. Report as "possibly inherited from another project" for the user to confirm. |
| 6 | Fights the agents' operating model | A rule that drives out-of-scope fixing; a blanket "ask before…" that stops an autonomous run; a stop or approval the flow already defines elsewhere. |
| 7 | Missing pieces | No review guidance declared; standards all `draft` with `owner: unassigned`; a declared technology without a standard, or a standard for a technology the code does not use; standards still carrying template text untrue for this repository; a harness instruction file that restates `AGENTS.md` and can drift from it. |
| 8 | Weight | Total size of the always-loaded sources against the budget; the heaviest sections and paragraphs; text that repeats what the squad's own agents, skills or MCP server instructions already say. |
| 9 | Rule or standard | Statements in the always-loaded sources that fail the placement test; the same statement in both a rule and a standard; rules that could be made checkable. |

## Phase A — Inventory

List every instruction source an agent can read, with its size in bytes, who owns it, and when
it is loaded. Read the repository's Config Reg (the generated block in the root `AGENTS.md`) first:
it names **<docs-root>**, **<rules-index>** (an index file; the rules folder is the folder that
holds it), **<standards-root>**, **<review-guidance>** when declared, and the coding standards. Use the files git tracks or would track, so vendored and ignored folders
(`node_modules`, package caches, `apm_modules`) drop out:

```bash
git ls-files -co --exclude-standard | grep -E '(^|/)(AGENTS|CLAUDE|GEMINI|REVIEW)\.md$|^\.github/copilot-instructions\.md$|^\.github/instructions/|^\.cursor/rules/|^\.cursorrules$|^\.windsurfrules$|^\.clinerules|^\.kiro/steering/|^\.kyber-weave/kyber-weave\.yml$' | xargs wc -c
git ls-files -co --exclude-standard -- '<rules folder>' '<standards root>' | xargs wc -c
```

Then search for any other instruction file the harnesses in use read (their documentation, or
`kyber-weave squad status .`), and leave out **<docs-root>**`/archive/`. List
`.kyber-weave/kyber-weave.yml` as configuration: agents do not load it, so it is not weight.

Record for each source:

- **Loaded:** *always* (root `AGENTS.md`, harness instruction files, and any file the root
  `AGENTS.md` tells every agent to read, such as the rules folder), *scoped* (a subtree's
  `AGENTS.md`), or *on demand* (standards, review guidance, other docs). Squad workers also get
  every root `AGENTS.md` rules section (headings naming rules, constraints, prohibitions,
  non-negotiables and the like; empty ones are skipped) copied into every task packet
  (`kyber-weave docs packet`), up to about 6,000 characters, so those sections count twice toward
  weight.
- **Generated blocks:** find marker pairs (`grep -n '<!-- [A-Z_]*_START -->' <file>`) and read
  the block's own text for who writes it. The Config Reg block is written by `kyber-weave docs init`
  (and `kyber-weave docs onboarding`) from `.kyber-weave/kyber-weave.yml`: it is changed through
  the configuration, never by hand. Any other block belongs to the tool that wrote it (the
  `CODEGRAPH_START` block is written by CodeGraph, not Kyber-Weave); say so and do not edit it
  without the user's explicit yes.

Print the inventory as a table, with the always-loaded total against the 3 KB budget, split into
hand-authored and generated bytes.

**Without a CodeGraph index** (no `.codegraph/`), the `codegraph explore` checks below fall back to
`git ls-files` and `git grep`, and `docs drift` reports *not verified*. Building an index is the
user's decision: offer `kyber-weave doctor --provision .`, do not run it unasked.

## Phase B — Mechanical checks

Run these before reading anything for meaning. Each produces candidate findings with file:line.

1. **Product checks.** `kyber-weave doctor --check .`, `kyber-weave docs validate .`,
   `kyber-weave docs drift .`. They validate governed-document frontmatter, links' `#fragments`
   and code references; they do not read `AGENTS.md` or harness files for meaning, so zero findings
   here does not mean zero conflicts.
2. **Docs conflict tool.** Call `docs_find_duplicates_conflicts_ambiguous_terms` with
   `kind: "conflict"`, then `kind: "duplicate"` (or its CLI twin,
   `kyber-weave docs integrity-check . --format json`, where conflicts are `KW-DOC-ANALYSIS-002` and
   duplicates `KW-DOC-ANALYSIS-001`), and keep only candidates whose evidence touches the rules
   folder or the standards root. Its limits, which you must state in the report: it reads only
   Markdown under **<docs-root>** whose `status` is in `docs-analysis.statuses` (default `current`),
   so it never sees the root `AGENTS.md`, harness instruction files, or `draft` standards; its
   conflict signal is lexical (different modal verbs, numbers, paths, commands), so most candidates
   are noise and it stops at a candidate cap (`truncated: true`). Treat it as a worklist, not as
   findings; when every standard is `draft`, standards conflicts come only from phase C.
3. **Tool and server names (category 3).** Extract every tool reference from the always-loaded
   sources: `grep -noE 'mcp__[A-Za-z0-9_-]+__[A-Za-z0-9_]+' <file>` plus backticked snake_case names
   that look like tools (`docs_…`, `codegraph_…`). Compare each **bare tool name** with the tool list
   this session actually has; the MCP server instructions name the current tools. Compare each
   **server prefix** (`mcp__<server>__`) with the server keys the repository declares
   (`mcp.required-servers` in the configuration and the harnesses' MCP files such as `.mcp.json`).
   A missing tool and a wrong prefix are separate findings. Never judge a name against a list
   written in this skill.
4. **Commands and flags (category 3).** For every `kyber-weave …` command quoted, run
   `kyber-weave <verb> [<sub>] --help` and check the verb and each flag. For other tools, check with
   `--help` only when the tool is installed; otherwise mark the finding unverified. Also check that
   a quoted command's arguments select something: a test filter on a trait or category the code
   never declares runs zero tests.
5. **Paths, links and names (category 3).** For each relative Markdown link and each backticked
   token that looks like a path, check it exists (`test -e`, or `git ls-files '<glob>'` for a glob).
   For a dotted project or namespace name (`Company.Product.Part`), search the tracked file list for
   a folder, project file or namespace of that name (`git ls-files | grep -i '<name>'`, and
   `codegraph explore "<name>"`). A name with no match is a finding.
6. **Empty lead-ins and headings (category 4).** A line ending in `:` whose next non-blank line is
   a heading or another lead-in; a heading with no text before the next heading of the same or higher
   level; a bold label such as `**CRITICAL**:` with nothing after it on the line or below it.
   Then list the root `AGENTS.md` headings that name rules (*rules*, *constraints*, *prohibitions*,
   *non-negotiable*, *mandatory*, *policies*, *guardrails*): Squad task packets copy those sections
   in order, skip empty ones, and stop at about 6,000 characters. Report an empty or misspelled
   rules heading, and any rules section that falls past the limit, because workers will not see it.
7. **Buried exceptions (category 1).** Bullets or paragraphs holding both a prohibition and an
   exception:
   `grep -nEi "(do not|don't|never|must not|forbidden|prohibited|not permitted)" <file> | grep -Ei "(except|unless|exception|only when|carries|however|but )"`.
   Each hit is a candidate; the judgement pass decides whether a strict reader of the first sentence
   would miss the exception.
8. **Normative worklist (categories 1 and 9).** List every normative line in the always-loaded
   sources (`must`, `never`, `do not`, `always`, `only`, `required`, `forbidden`), with file:line,
   grouped by the subject it names (a product, tool, folder, layer, action); group by reading, not
   by pattern. A subject that appears
   in two or more lines is a candidate for "same subject stated twice".
9. **Technology presence (categories 5 and 7).** Take every technology in `ontology.technologies`
   and every product, language, component or infrastructure named in the always-loaded sources.
   For each, look for evidence outside the documentation: matching file extensions, project or
   manifest files, package references, or the name in code
   (`git grep -il '<name>' -- ':!<docs-root>' ':!*.md'`, and `codegraph explore "<name>"`). Report
   each as *in the code*, *only in configuration or docs*, or *absent*. Absent or docs-only means
   category 5 for a rule and category 7 for a declared technology or standard.
10. **Rules about the code (category 2).** For each statement of where something lives or what a
    project may contain ("X contains only Y", "Y belongs in Z", "layer A never depends on B"): find
    the things it names (files, folders, type names, project references) with the tracked file list
    and CodeGraph, and count the ones outside the stated place. For a dependency rule, read the
    project references (`*.csproj`, `package.json`, imports). For a rule requiring a library or
    pattern, check the code uses it. For each offending path, check whether a comment in it marks the
    divergence as deliberate.
11. **Standards state (category 7).** For each standard: its `status` and `owner`; the libraries,
    tools and versions it names, each checked against the package manifests (step 9's search);
    text that reads like a template default rather than a fact of this repository.
12. **Review guidance (category 7).** Whether **<review-guidance>** is declared in the Config Reg
    and the file exists.
13. **Severity overrides (category 1).** Each row of the review guidance's "Severity overrides"
    section, read against the rules and standards: a row that contradicts one (the standard says
    "public APIs must be documented", a row grades a missing public API doc `nitpick`) is a
    finding for the owner to resolve, never resolved by editing either side unasked.
14. **Weight (category 8).** Bytes per always-loaded file, per `##` section, and the longest
    paragraphs (`awk '{ print length, FILENAME":"FNR }' <files> | sort -rn | head`). Mark sections
    that restate what the squad already carries: tool-usage guidance that the MCP servers' own
    instructions or the deployed skills give (for example a CodeGraph usage block when the
    `codegraph` skill and server instructions are deployed).

## Phase C — Judgement pass

Read the always-loaded sources in full, then the standards and rules-folder files the worklists
point to. Decide each candidate from phase B, and look for what no pattern catches:

- **Category 1:** two statements about one subject that a strict agent cannot both obey; a rule in
  `AGENTS.md` that a rules-folder file or a standard contradicts; a rules-folder file and a
  standard that give different instructions for the same thing.
- **Category 4:** misspelled headings or keywords; instructions that say what to "follow" or
  "consider" without naming the action or the file.
- **Category 6:** rules about ownership of every issue in a touched file; "ask before" or "stop"
  rules with no carve-out for an approved or autonomous run; approvals that duplicate the flow's
  own gates.
- **Category 9:** apply the placement test to each statement in the always-loaded sources. For
  each statement that fails it, name the target standard (an existing technology standard, or a new
  one) and the one-line rule, if any, that should remain and point to it. Flag statements present in
  both a rule and a standard, and rules that are not checkable but could be (by a review gate, a
  test, or an Arbiter rule). Compute the always-loaded size now and after the proposed moves.

## Phase D — Findings list

Present **one** list, grouped by category in the table's order, numbered across the whole list so
the user can answer with numbers. Each finding:

```text
<n>. [<category>] <short title>
   Says:        "<quoted text>" (<file>:<line>)
   Conflicts:   "<quoted text>" (<file>:<line>)  |  <tree fact, with paths and counts>
   Cost:        <what a strict agent does wrong because of it, in one sentence>
   Options:     a) <recommended>  b) <alternative>  c) <alternative, if any>
   Recommended: a, because <one line>
```

For category 2, "Conflicts" lists the offending paths with a count, and says whether an exception
comment exists. Its options are always: file a todo (or a plan through the conductor) to fix the
code; add an exception comment to each deliberate divergence; change the rule only if the user says
the rule is wrong. For category 5, the recommended option is to ask the user whether the statement
was inherited from another project. For category 9, include the target standard and the remaining
one-line rule. End the list with the inventory total, the budget, and the projected total after the
recommended moves.

## Phase E — Selection

Before the question, tell the user in a few lines:

- *What*: the numbered findings above are places where agents get contradictory, stale or
  misplaced instructions in this repository.
- *Why*: a strict agent obeys the first instruction it reads, so each finding costs time or a
  wrong turn on every run (each finding's "Cost" line says how).
- *What selecting does*: nothing is applied yet. For each selected finding you show the exact
  change as a diff and apply it only after a yes. A code-structure finding (category 2) is never
  fixed by refactoring here: it becomes a todo, or a plan through the conductor, for a separate
  run. Not selecting a finding leaves the files as they are.
- *Cost and undo*: edits stay in repository files, visible in `git status`, and can be reverted
  before committing.
- *Recommend*: the findings marked recommended (name their numbers), and say which ones the user
  can safely leave.

Then ask which findings to act on, the way the settings step asks: the harness's structured
multi-select question tool where it has one (one question per category, split a long category
across questions, recommended choices first), otherwise the numbered list and an answer such as
`2, 5, 7` or `none`. For each selected finding with more than one option, ask which option, with
the recommended one first and one line on what each option changes.

Write findings in plain words: name the file and quote the text, explain any product term (the
Config Reg is the generated paths block in the root `AGENTS.md`; a standard is a per-technology
coding guide), and drop rule ids unless the user needs one to look something up.

## Phase F — Apply the selected findings

- **Edits to instruction files** (root or scoped `AGENTS.md`, rules-folder files, harness
  instruction files): show the change as a diff, apply it only after the user's yes.
- **Generated blocks:** change the Config Reg block only through `.kyber-weave/kyber-weave.yml`,
  then `kyber-weave docs init . --no-skill --no-review-gates --no-mcp-check` to re-render it. A block
  another tool writes is changed with that tool, or by hand only with the user's explicit yes.
- **Structure findings (category 2):** never refactor code in this session. File a todo under
  **<todo-index>** (the `kyber-docs-authoring` skill writes it), or hand the user to the conductor to
  plan the move; or add the exception comments the user chose.
- **Moves to standards (category 9) and other governed documents:** follow the docs conventions
  (frontmatter, `kyber-docs-authoring`). A new technology standard starts by adding the technology to
  `ontology.technologies` and running `kyber-weave docs init .`, which scaffolds its folder and
  publishes it in the Config Reg.
- **Inherited statements (category 5):** delete or rewrite only after the user confirms the
  statement does not belong.

## Phase G — Finish

1. Re-run phase B's checks 1 to 9 and 13, then `kyber-weave docs validate .` and
   `kyber-weave docs drift .`. Fix any finding the edits introduced.
2. Summarise in three short lists: done (with the always-loaded size before and after), deferred
   (each with the todo or plan it became), declined (each with the user's reason, if given).
3. Remind the user that agents pick up instruction changes on their next dispatch, and that a
   harness that caches instructions needs a restart.
