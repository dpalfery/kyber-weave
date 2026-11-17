---
id: adr/0087-arbiter-rules-live-in-role-and-technology-folders
title: Arbiter rules live in role and technology folders
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-17
component: KyberArbiter
decided-by: []
supersedes: []
code-refs:
  - ArbiterConfigLoader
  - RuleValidator
  - WorkerGuardRules
  - TriggerFactBuilder
  - ModelTargetingRules
  - ArbiterRule
---

# ADR 0087: Arbiter rules live in role and technology folders

## Status

Accepted, 2026-11-17. Decided by the owner (decisions D10 and D11 of the
role-based-worker-agents specification). It changes where the Arbiter's rules are stored, not how
it decides: the three steps and the blocking contract of
[ADR 0053](0053-kyber-arbiter-three-step-decision-gates.md) and
[ADR 0072](0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md) are
unchanged.

## Context

The thirty-six shipped rules lived in one file embedded in the binary. A host could tune them
only through `arbiter.rules` in `.kyber-weave/kyber-weave.yml`, a second rules list far from the
standards those rules belong to, and nothing could express a rule that holds for one technology
only. The first such rule is a cloud platform's read-only command list, which has to sit beside
that platform's skills and be read where the platform's worker runs.

The roster work already gives a host one folder per role and per technology under
`<docs-root>/standards/`, each holding a standard, prose rules and skills. Machine rules belong
there too, where the person who owns the standard can see and change them.

## Decision

1. **Three role files hold the thirty-six shipped rules, unchanged.** The rules are split by
   role, not rewritten: each keeps the id, question, answers, `decide` clauses, `ask` block and
   effects it had in the single file.

   | Role file | Rules | Count |
   |---|---|---|
   | `conductor` | `KW-ARB-PLAN-001`, `SCOPE-001`, `SCOPE-002`, `READY-001`, `OWNER-001`, `OWNER-002`, `ROSTER-001`, `RUN-001`, `DIFF-001`, `PLANREAD-001`, `GATE-REPORT-001`, `DIFFICULTY-001`, `LADDER-001`, `FAILURE-001`, `CHECKOUT-001`, `MODE-002`, `READONLY-001`, `TRIAGE-001`, `INTAKE-001`, `DOCIMPACT-001`, `TRIM-001` | 21 |
   | `architect` | `KW-ARB-PLANNER-001`, `PLANNER-DIFF-001` | 2 |
   | `reviewer` | `KW-ARB-LENS-001`, `LENS-002`, `QUOTE-001`, `PREEX-001`, `CLAIM-001`, `GATE-CORROBORATED-001`, `GATE-001`, `NITPICK-001`, `NITPICK-002`, `DUP-001`, `COMMENT-001`, `SEVERITY-001`, `OVERRIDE-001` | 13 |

   The ids above carry the `KW-ARB-` prefix, which a host rule may not use. The files live in
   `products/kyber-squad/standards/<role>/<role>.arbiter-rules.yml` and are embedded in the
   binary; a host receives them only by installing the role folder template.

2. **A folder's machine rules are one file beside its standard.** The path is
   `<docs-root>/standards/<name>/<name>.arbiter-rules.yml`, where `<docs-root>` is each docs root
   the host declares and `<name>` is the role or technology. The file is YAML with no
   frontmatter and may hold three keys: `rules`, `test-file-patterns` and `lens-paths`. Each rule
   keeps the shape it had in the shipped file. An empty file is valid and adds nothing. A
   technology's parent is the one its `<name>.standards.md` frontmatter names in `builds-on`,
   which need not be declared in `ontology.technologies`.

3. **Loading order.** For one configuration read the Arbiter uses, in this order:

   1. the shipped `conductor`, `architect` and `reviewer` role files;
   2. the host's same three role folders, where a declaration of a shipped id takes that rule's
      place;
   3. the rule file of every technology the host declares, and of each technology's `builds-on`
      ancestors, root parent first so a child wins on a shared id;
   4. the host's `arbiter.rules` list, which keeps working and is applied last, so it wins over a
      folder.

   Nothing is read when `arbiter.enabled` is false, and a user-level configuration has no
   repository to read folders from, so folders apply to the repository's own configuration only.

4. **A folder declaration replaces a rule; it does not merge into it.** A folder rule with the
   id of a rule already loaded replaces that rule whole, keeping the first rule's position in
   the set. An id that is not loaded is added to the set; it may not carry the `KW-ARB-` prefix,
   which is reserved for the shipped rules. So the entry restates the rule: `id` and
   `enabled: false` alone is not a valid folder rule, because a rule without a trigger, question
   and answers is not a rule. `arbiter.rules` is the short route for a tune-up: `id` plus
   `enabled`, a threshold or `effects` adjusts the loaded rule in place. A folder declaration of
   a shipped rule id is reported as `KW-ARB-CONFIG-020` at informational severity, saying whether
   it replaces or disables the rule, so it is never silent.

5. **No rule is locked.** Every rule, including the six that guard the tree and the run order
   (`READY-001`, `DIFF-001`, `PLANNER-DIFF-001`, `READONLY-001`, `CHECKOUT-001`, `ROSTER-001`),
   can be turned down to log only. A folder declaration with `enabled: false`, an `arbiter.rules`
   entry with `enabled: false`, `shadow: false` on a rule that supports shadow, and the
   `model-targeting.mode` switch for the two model rules are all honoured. Disabling a rule
   widens nothing else: an answer the Arbiter cannot reach allows and is logged
   ([ADR 0072](0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md)).
   The shipped templates keep the six guard rules as they ship.

6. **A bad folder file narrows nothing.** `kyber-weave arbiter validate` reports, naming the file
   and the rule, and the shipped rules for that folder stay in force. A rule that reads a fact its
   trigger does not supply is `KW-ARB-CONFIG-002`, and a folder entry with a reserved id, a
   rule-level `for-each` or an effect only a shipped rule may use is `-005` or `-015`; those errors
   skip the file as `-017` does.

   | Finding | Severity | Meaning |
   |---|---|---|
   | `KW-ARB-CONFIG-017` | Error | The file cannot be read, does not parse, or holds a rule the Arbiter cannot use. The file's folder is skipped. |
   | `KW-ARB-CONFIG-018` | Warning | A standard's `builds-on` parent has no standard, so its rules are not read. |
   | `KW-ARB-CONFIG-019` | Error | `builds-on` standards name each other in a cycle. The chain stops there. |
   | `KW-ARB-CONFIG-020` | Info | A folder declares a shipped id, replacing or disabling it. It takes effect. |
   | `KW-ARB-CONFIG-021` | Warning | One file declares an id more than once. The last declaration wins, as it does across files. |
   | `KW-ARB-CONFIG-022` | Warning | A top-level or rule key the Arbiter does not know. The key is ignored and the rest of the file loads. |

7. **The tests-only exemption is a machine fact.** `KW-ARB-MODE-002` loses its clause that named
   `test-dev` and gains one on `plan.task.files-all-tests`. The fact is true when every file the
   task names matches the host's `test-file-patterns`; a task that names no files leaves it absent
   rather than vacuously true. A dispatch to `test-dev` under the old roster answers
   `not-required` when its task's files are all test files; the agent's name alone no longer
   exempts, so a task with no file list is judged on the failing-run fact like any other target.

8. **A technology's allowed commands bind a read-only worker.** A technology's rule file may
   declare rules on the new `worker.command` trigger, which the worker guard raises on the command
   call of a dispatch whose capability profile may run commands and may not write files
   (`platform-reader`, the `platform-researcher` agent). The facts are the command's text, its
   **form** (the program and leading words, with wrappers and options removed), whether it is one
   command at all, the form's **head** (its words before the first option), the program, and every
   word it names. A mutating verb in the head refuses the command even when a read pattern also
   matches: `az group delete list` ends in `list` but its head holds `delete`. A redirect,
   pipeline or compound command is not one command, so no listed read form can allow it, and
   every word is read so an absolute path, a quoted program, a wrapper or a subshell still names
   the tool. The `azure` and `aws` templates each ship one rule (`AZURE-READONLY-001`,
   `AWS-READONLY-001`); a technology that declares no such rule refuses nothing. The list is
   enforced where a worker guard runs (Claude Code and Copilot CLI) and is an instruction only
   on the harnesses that have no worker guard.

9. **The two model rules follow the developer tiers.** Under `worker-roster: tiered`,
   `KW-ARB-DIFFICULTY-001` answers from `plan.task.difficulty`, the rating the agent pick already
   recorded, so no second model call is made, and the ladder is the three tiers' model profiles
   (`fast`, `general`, `dev-hard`). `model-targeting.difficulty-profiles` and
   `model-targeting.ladder` take those values whether or not the host set its own. A dispatch the
   conductor already put on the next tier targets the token it is already rendered with, so
   nothing is written. Under `technology`, and with no roster, both rules behave as they did.

### Where the code is narrower than the specification

Stated so nobody reads a guarantee the code does not give.

- **Every declared technology's rule file is loaded for the whole run, not the current task's.**
  The configuration loader receives `ontology.technologies` and has no task in hand, so a
  technology's rules are evaluated against every event of the run. The shipped allow-lists are
  unaffected in practice because each narrows itself by its own tool word (`az`, `aws`), but a
  host's technology rule that does not narrow itself applies to tasks of every technology.
  Requirement 7.2 says the task's technology.
- **Only the `conductor`, `architect` and `reviewer` role folders are read.**
  `ArbiterConfigLoader` holds that list, and `ontology.roles` does not feed it. A host's
  `devops` role folder, or any other role folder, has no effect on the Arbiter. Requirement 6.6
  ships a `devops` role folder as a template.

## Alternatives rejected

- **Keep one file and add a technology key to each rule.** The rules stay far from the standards,
  and a host still edits one list instead of the folder it owns.
- **Thread the current task's technology through configuration loading.** Configuration is
  loaded once for the run and shared by every event, so this would make the rule set depend on
  which event happened to load it first.
- **Merge a folder rule into the shipped one field by field.** A rule's fields interact — effects
  and answers and `decide` clauses are one question — so a partial override would leave a rule
  nobody wrote. A whole-rule replacement is one thing to read.
- **Lock the six rules that guard the tree and the run order.** The owner's decision is that no
  rule is locked. A host that disables one gets the informational finding that says so, and the
  Arbiter's fail-safe still allows what it cannot decide.
- **Load technology rules only for a task whose technology names them, by filtering at
  evaluation.** No implementation was built for it in this phase; the limit is recorded above
  instead of claimed away.

## Consequences

- A host sees and changes an Arbiter rule in the folder whose standard it governs, and a
  technology's rule reaches only the registers that technology's folder.
- `kyber-weave arbiter validate` is the one check for a folder file: it names the file and the
  rule, and a run continues with the shipped rules for a folder whose file is broken.
- The rule set is assembled in one place (`ArbiterConfigLoader`) from four sources, in a fixed
  order, so a host can predict which declaration wins.
- A technology rule applies to the whole run, and a role folder other than the three does not
  apply at all; both are recorded above and are the first candidates for a later specification.
- Installing a role folder verbatim reports one informational `KW-ARB-CONFIG-020` per rule in it,
  because the copied template declares every shipped id. The findings change nothing and are
  hidden by `--no-info`; the alternative — silently treating an unchanged copy as no declaration
  — would make a real edit invisible too.
