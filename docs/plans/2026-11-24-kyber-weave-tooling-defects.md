---
id: plans/2026-11-24-kyber-weave-tooling-defects
title: Fix ten Kyber-Weave tooling defects seen in today's runs
doc-type: plan
status: current
component: KyberArbiter
owner: dpalfery
last-reviewed: 2026-11-24
development-mode: test-first
documentation-impact: extends-component
impact-components:
  - KyberArbiter
  - ReviewCouncil
  - DocGraph
  - KyberSquad
---

# Fix ten Kyber-Weave tooling defects seen in today's runs

**Status: Ready, 2026-11-25.**

**Autonomy:** autonomous. **Development mode:** test-first.

## Request

The owner, replying to the conductor's list of ten Kyber-Weave tooling defects seen during today's runs:

> I don't want to create those bugs in GitHub. I want to address them right away. Can we assign those to Open Code? Can we group them maybe by difficulty, and you take on 5 and Open Code takes on 5? Let's talk about how we break that down. Don't do anything yet. Let's talk about how we're going to do it.

Then, replying to the conductor's proposal (one PR for all 10; one bug-fix run with a short plan; touched tests run on Linux before done):

> Do it.

The ten defects and the agreed routing, as the dispatch carried them:

Claude workers (judgment, security or hook-system):

1. The Arbiter's review seat-save, on the Claude Code harness, stores the harness wrapper line ("This agent's report was delivered to you as a message from …") instead of the reviewer seat's report, so `kyber-weave review verdict` refuses every seat file (KW-REVIEW-040). Blocks every council verdict on Claude Code. Do first.
9. A hook resolved a different run's worktree (a worker in run A had its `which` command evaluated against run B's worktree) when several runs were active.
8. The secret-file guard KW-ARB-SECRET-001 blocks harmless shell commands — greps whose pattern contains "token", a python heredoc edit — must become more precise without weakening it.
10. Arbiter rule KW-ARB-GATE-REPORT-001 fires on a docs-dev return (a docs-only task) asking whether the worker ran the task gate.
3. `--red` evidence is rejected with KW-REVIEW-052 when a task's declared gates run no tests (github-actions, react/dev-easy typecheck+lint only, docs) or when the failing TypeScript errors sit in test files ("not changed by this task"). Decide the rule for what counts as RED proof. Builds on 4 and 5.

OpenCode workers (mechanical):

4. `review.task-gates` has no entry for agent `dev-easy`, so its task gate runs zero gates (KW-REVIEW-027, NONE_DECLARED).
5. `review.technology-gates.csharp` builds/tests only KyberWeave.sln; no KyberDash project (dash/server/KyberDash.sln, dash/server.tests) is built or tested, so a passing C# task gate is no evidence for dash code.
6. The TASK_GATE line prints `runs=<k>` literally instead of the run count.
7. `docs tasks` parses prose in a task's Files section as file paths (e.g. `dash/tray/src-tauri/process.env.VERSION`, `matrix.target`).
2. `kyber-weave run integrate` prints "error: the task could not be integrated: Unable to find the specified file." and exits 1 after the commit has already landed on the run branch and the task worktree was removed.

Instructions carried with it: diagnose each defect from code before planning its fix, with path:line evidence, and say so when a defect does not reproduce or its cause differs; give each task its diagnosis, files, test-first test contract (in-memory unit tests through interfaces where possible, integration tests only where a process or disk is essential), a `Worker:` line (`claude` or `opencode`) and a difficulty; order 1 first and 4 and 5 before 3; run the touched test classes on Linux in a container before the run is done; one pull request for all ten.

## Problem and goal

Ten defects in the Kyber-Weave tooling slowed or blocked today's runs: council verdicts on Claude Code, cross-run hook context, an over-broad secret guard, a gate-report question asked of a docs-only worker, RED evidence that cannot be produced for some technologies, missing task gates for the tiered developer agents and for KyberDash C#, a placeholder in the gate digest, prose read as file paths, and a false failure from `run integrate`. The goal is every defect fixed at its root cause, each with a failing test first, delivered as one pull request, with the touched test classes passing on Linux.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| Fix the ten defects now, no GitHub issues | T1 to T9 | Each task's GREEN acceptance; one PR, no issue filed |
| Split five to Claude, five to OpenCode | `Worker:` line on each task; D2 | T1, T2, T3, T4, T9 claude; T5 (defects 4 and 5), T6, T7, T8 opencode |
| Group by difficulty | `Difficulty:` line on each task | Ratings follow the plan-authoring rubric |
| Diagnose each from code with path:line evidence; say when a cause differs | Investigation findings | One finding per defect with path:line; defects 2, 3 and 4 note where the cause differs from the report |
| Diagnosis, files, test-first contract, Worker and difficulty per task | Tasks; Test contract | Every task carries all five |
| Unit tests in memory through interfaces; integration tests only where a process or disk is essential | Test contract; D11 | Every row names a unit test class; none adds an integration test |
| Order: 1 first | Dependency graph | T1 heads the first wave |
| Order: 4 and 5 before 3 | Dependency graph | T9 (defect 3) depends on T5 (defects 4 and 5) |
| Touched test classes run on Linux in a container before done | T10; Verification gates, step 2 | T10's returned counts in the PR description |
| One PR for all ten | Review; D1 | One run branch, one draft PR |
| Note overlap with other runs on dash and release plumbing | Risks R1, R2 | Named with mitigation |

## Scale

`extends-component` over KyberArbiter (defects 1, 8, 9, 10), ReviewCouncil (defects 3, 4, 5, 6), DocGraph (defect 7) and KyberSquad (defect 2). No new component, dependency or public command.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | One plan or several? | One plan, one branch, one PR. | Owner's instruction; the plan index forbids a plan spanning several PRs, not several defects in one. | ANSWERED — owner instruction 2026-11-24 |
| D2 | How are defects 4 and 5 tasked? | One task (T5), worked by OpenCode. | Both edit the same blocks of the host gate configuration and the same repo-policy test class; two workers on one file would conflict. OpenCode still takes five defects (4, 5, 6, 7, 2) in four tasks. | ANSWERED — plan-authoring rule: a certain file overlap is one task |
| D3 | Defect 4 fix: change the resolver or the configuration? | Configuration: give dev-easy, dev-medium and dev-hard each a review.task-gates entry listing every technology gate by alias, and add a repo-policy test that each entry holds every gate id of review.technology-gates. | A resolver fallback to "all technology gates when no technology is named" would also make config.task-gates-declared true for non-developer agents (research-agent, architect) and make KW-ARB-GATE-REPORT-001 ask them about gates. Gates are selected by include, so a union entry selects the same gates the task's technology would. The drift test keeps the union from going stale. | autonomous: low-confidence, no authoritative source — cost: an agent entry outranks the technology entry, so a later technology gate missing from the union is caught only by the drift test |
| D4 | Defect 5: how do KyberDash C# gates join the csharp technology? | Add KyberDash format, build and test gates to review.technology-gates.csharp (and to test-dev), included by the existing dash-server-paths anchor, built into the task's artifacts folder; narrow the KyberWeave gates' include so a dash file does not select them. Exact globs and projects are in T5. | Copies the council's dash-server gates (kyber-weave.yml:285-300). Narrowing stops a dash change running ten minutes of KyberWeave tests and a format check that checks nothing. | ANSWERED — existing council gates are the pattern |
| D5 | Defect 5 RED setup: how does a csharp task run repo-policy tests? | Add the existing test-policy gate to review.technology-gates.csharp. | Without it no csharp task gate runs tests/KyberWeave.RepoPolicy.Tests, so T5's own RED cannot be produced. It reuses test-dev's gate as written. | ANSWERED — reuses an existing gate |
| D6 | Defect 6: where does the run count come from? | A counter file in the task's scratch folder (.agent_scratchpad/(task)/task-gate-runs), incremented by every task-gate run with a task id, read when printing; runs=1 when no task id is given. | The digest docstring says one invocation cannot know the count, which is why it printed a placeholder; the scratch folder is already the per-task evidence home. No report schema change. | autonomous: low-confidence, no authoritative source — cheap to reverse, local to one task |
| D7 | Defect 3: what counts as RED proof? | (a) When none of the task's selected gates is a test gate (RedClassifier.IsTestGate) and every gate exited 0, the run is accepted as RED not applicable (KW-REVIEW-051, evidence line names the gates). (b) A compiler path printed relative to the gate's own folder matches a changed file when the repository path ends with it on a path-segment boundary. (c) Everything else stays as today. | (a) A gate set that cannot observe a failing test cannot produce RED; refusing it blocks every github-actions, lint-only and docs task. Not weaker: any task with a test gate still needs a failing test or a missing-symbol build. (b) `npm run --prefix dash typecheck` prints paths relative to dash/, so the task's own dash/tests files read as "not changed by this task". | autonomous: low-confidence, no authoritative source — cost: a wrong rule lets a test-less task skip RED; caught at review |
| D8 | Defect 1: where does the real report come from on Claude Code? | The report arrives first, as the SubagentHandback message; the Agent tool's completed result then carries only the wrapper. So: (a) a handback with no followed dispatch keeps its message in the Arbiter's background store, keyed by session and agent id; (b) a completed PostToolUse whose response is the wrapper looks up that kept message by its response agentId and records it as the return, with the post's own prompt and target, so the seat is saved from the report; (c) a wrapper is never recorded as a return or a seat by any path; (d) when no kept message exists yet, the SubagentStop event's last_assistant_message is the fallback; (e) the seat recorder replaces a seat file whose whole text is a wrapper; (f) the recogniser matches the whole trimmed reply only, so a real report that quotes the phrase is never dropped; (g) a kept handback that no post takes is pruned by the store's existing seven-day sweep (FileBackgroundDispatchStore.cs:21, 87-119). Other harnesses: only Claude Code delivers the report through a separate SubagentHandback call. Copilot returns a background agent's report through its own DecideBackgroundReturn path (CopilotHookAdapter.cs:274); Factory skips its launch notice and decides the later return (FactoryHookAdapter.cs:236-257); Codex (CodexHookAdapter.cs:196-215), Cursor (CursorHookAdapter.cs:201-217), Devin (DevinHookAdapter.cs:234-253), Antigravity (AntigravityHookAdapter.cs:341-344) and Plugin (PluginHookAdapter.cs:227-233) decide one post whose tool result is the report. None is changed. | Evidence: a seat file from today holds only the wrapper, and the wrapper says the report went through "its SubagentHandback call" and "is not repeated here" (Investigation findings, defect 1); that agent has no background record, so the dispatch was foreground. Claude Code documents last_assistant_message on SubagentStop (code.claude.com/docs/en/hooks, read 2026-11-24). Wrapper recognition copies FactoryTranscript.IsBackgroundLaunch. | ANSWERED — run evidence and vendor documentation |
| D9 | Defect 9: how does a hook choose among several live runs? | Order: agent worktree; plan worktree; the payload cwd when it is inside a non-canonical worktree of this repository; the command's leading absolute `cd` target, used only when the cwd is absent or is the canonical root (a cd that names another run's worktree than the cwd's run is ignored, with a note); the single live run; else Ambiguous. A chosen worktree maps to its run in two ways: it is (or is inside) a run record's worktree, or it is a task worktree whose checked-out branch has exactly one live run's branch as an ancestor, the rule run integrate uses (RunIntegrateService.cs:473-497). Ambiguous keeps the canonical root for paths, sets no active run, adds a new Ambiguous source and logs a guard note naming every live run. `git -C` is not a signal. The checkout guard's refusal names every live run's worktree when several are live, instead of the first one. | Task worktrees are not made by run start (RunStartRunner.cs:258, 401-431 make only run worktrees under .agent_worktrees); the harness or conductor creates them as siblings, and RunRecord holds only repository, branch and run worktree (RunRecord.cs:9), so containment alone cannot find a task worktree's run; ancestry can. Ambiguous never relaxes a guard: see the consumer list in Investigation findings, defect 9. | ANSWERED — existing run integrate rule |
| D10 | Defect 10: how should the rule treat a task whose files no gate selects? | config.task-gates-declared is true only when a resolved gate applies to the task: no task files known, a gate with no include, or a gate whose include matches one of plan.task.files. | The docs-dev entry exists (review-config) but its include matches only the host configuration, so a docs-only task owes no gate, and the existing no-gates-declared answer then allows. No new answer id. | ANSWERED — keeps the rule's existing answers |
| D11 | Defect 2: how is the fix proven without a real deleted working directory? | An in-memory RunIntegrateServiceTests case throws the reported exception from the cleanup git call; the adapter fix (git resolved to a full path once) is reasoned and named in a PR note, not reproduced. | Reproducing needs the test process's current directory deleted, which is process-wide and unsafe in a parallel test run. | autonomous: low-confidence, no authoritative source — cheap to reverse |
| D12 | Defect 8: how is the secret rule made more precise, never weaker? | (a) Words (SecretRules.cs:493-501) stays unchanged, and so do ReadsSecretFile and PrintsWholeEnvironment, its callers. Only AnySecretFileAfterPattern reads the raw step text to find the pattern, so a quoted span is one pattern word there and nowhere else. (b) Options of grep and rg are parsed: a short bundle such as -nA3 or -nA is read letter by letter, and the last value-taking letter (e, f, A, B, C, m, d, D, g, t, T, M, j) takes the rest of the bundle or else the next word; long options (--regexp, --file, --after-context, --before-context, --context, --max-count, --glob, --iglob, --include, --exclude, --type, --type-not, and each with =) take their value. -e or --regexp supply the pattern, so the first free word is then a file. (c) Every option value that names a file is judged by IsSecretFile: -f and --file anywhere, in a bundle too, plus --exclude-from, --include-from, rg --ignore-file. (d) A heredoc body is exempt from the shell reader checks only when the step that opens it runs a known script interpreter (python, python3, pythonN.N, py, node, ruby, perl); its body is then judged by ScriptOpensSecretFile, WholeEnvironment and the printing checks. Any other program (bash, sh, zsh, ssh, cat, an unknown one) keeps its body lines as shell steps, as today. (e) open(...) fails closed: every quoted literal anywhere in the argument is judged, and so is every quoted value assigned to an identifier in that argument anywhere in the command. An argument is ruled not a path only when it is one bare identifier with no assignment anywhere in the command, and the match was its own name. Path(...) calls are judged the same way. (f) Every case refused today stays refused. | The reported greps fail because `grep -A 3 token f` reads 3 as the pattern and token as a file (SecretRules.cs:205-222). Quotes are read as separators (SecretRules.cs:497-501). A heredoc is split into shell steps at each newline (SecretRules.cs:521-552), so a script line starting with head, sort or type reads as a shell reader. open(token_path) reads the identifier as a path (SecretRules.cs:242-243). Forms (b) to (e) fail closed, so the guard only gains precision. | ANSWERED — code evidence and security review of this Draft; refusals only kept or added |
| D13 | Defect 5: which KyberDash test projects does the task gate run? | KyberDash.Application.Tests and KyberDash.Domain.Tests. KyberDash.Data.Tests and KyberDash.Presentation.Tests stay with the council's dash-server-test gate and CI. | The csharp standard keeps disk and host tests out of task gates; the Data and Presentation projects are the ones likely to use a database file or a test host, and this plan does not verify they are in-memory. | autonomous: low-confidence, no authoritative source — cheap to reverse: add the two projects later |
| D14 | Defect 1 and 2 are in the tools this run itself uses: how does the run proceed before its own fixes are installed? | Run the end-of-run council on OpenCode, where the Claude seat-save defect does not apply; a worker whose `run integrate` exits 1 after landing checks that the run branch head holds its commit and reports that sha as COMMIT with one line naming defect 2. | Installing this branch's build is a user-level change that needs the owner's confirmation (standing owner rule), so the run does not depend on it. | ANSWERED — owner's standing rule on user-level changes |
| D15 | Defect 9 test home: is the checkout guard part of T2? | Yes: the refusal text that named run B's worktree comes from WorkerGuard (WorkerGuard.cs:267-268, WorkerGuardRules.cs:250-253), so T2 owns both files and extends WorkerGuardCanonicalCheckoutCommandTests. | Seen live while planning this run: a step in the canonical checkout was refused with another run's worktree named as "your run" (Investigation findings, defect 9). | ANSWERED — run evidence |
| D16 | How does the Linux check see the repository from a linked worktree? | Mount the canonical checkout at its own absolute path, so the worktree's .git pointer to (canonical)/.git/worktrees/(name) resolves inside the container, and run from the worktree path. | A linked worktree's .git is a file naming a host path; mounting only the worktree leaves it dangling, and the repo-policy tests find the checkout through it. | ANSWERED — git worktree layout |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute (owner: "Do it."; plan review 7 findings fixed), 2026-11-25 |

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|
| KyberArbiter | architecture | Review seat files | The delivery wrapper is never a seat; a Claude background seat is recorded from SubagentStop's last_assistant_message (D8) |
| KyberArbiter | architecture | Run context resolution | Cwd outranks newest run; several live runs and no signal guess nothing (D9) |
| KyberArbiter | architecture | Rule table, KW-ARB-SECRET-001 | Option values and open(...) arguments as the rule reads them (D12) |
| KyberArbiter | architecture | Rule table, KW-ARB-GATE-REPORT-001 | no-gates-declared also covers a task whose files no gate selects (D10) |
| ReviewCouncil | architecture | Task gates and RED evidence | RED not applicable when no selected gate runs tests; relative compiler paths (D7); runs=k counted from the scratch folder (D6) |
| ReviewCouncil | operations | Host gate configuration | dev-* entries hold the union of technology gates; KyberDash C# gates under csharp (D3, D4, D5) |
| DocGraph | architecture | Plan parsing, Files label | Only a span with a slash is a path in a Files bullet's description (T7) |
| KyberSquad | runbook | run integrate | Cleanup after the commits land reports the landed sha; git is resolved by full path (D11) |

## Investigation findings

Discovery read the run worktree at revision 2f661f462 on branch claude/fix/kyber-weave-tooling-defects (worktree CodeGraph index, synced by `kyber-weave doctor --ensure`).

- **Defect 1 (reproduces; carrier confirmed).** Today's seat file /Users/dave/git/personal/claude/kyber-weave.worktrees/20261125T001401Z-claude-feat-sync-executable-versions/.agent_scratchpad/claude-feat-sync-executable-versions/review/cycle-1/seats/security.md holds one line: the wrapper, saying the report was delivered "as a message from "a52bf17cfe6305406" (its SubagentHandback call). Read it there; it is not repeated here." Seven other seats in that folder hold the same. No background record exists for that agent (the only record in artifacts/arbiter/background is this planner's own), so the lens dispatch was foreground. The order is therefore: the SubagentHandback arrives with no followed dispatch, is decided with an empty prompt (ClaudeHookAdapter.cs:495-501), so ReviewSeatRecorder finds no Packet line and saves nothing (ReviewSeatRecorder.cs:30-34); then the completed PostToolUse takes the else branch with the real prompt and the wrapper as output (ClaudeHookAdapter.cs:415-429), and the engine saves the wrapper as the seat through RecordReviewSeat (ArbiterHookDecisionEngine.cs:568, 735-739; ReviewSeatRecorder.ReplyText accepts any text, ReviewSeatRecorder.cs:102-177). The verdict refuses the file (ReviewVerdictService.cs:73, 640, 688). HandleSubagentStop only recovers lost returns (ClaudeHookAdapter.cs:176-190).
- **Defect 9 (reproduces; seen live).** While planning this run, a shell step that started with `cd` into the canonical checkout was refused with "KW-ARB-CHECKOUT-001: this command would run in the repository's canonical checkout … while your run works in /Users/dave/git/personal/claude/kyber-weave.worktrees/20261125T001725Z-claude-fix-droid-chart-and-per-harness-e2e", another run's worktree. The checkout guard names the first live run (WorkerGuard.cs:267-268; message WorkerGuardRules.cs:250-253). Separately, HookRunContextResolver.Candidate takes the newest live run before the payload's cwd (HookRunContextResolver.cs:282-292), and ActiveRun picks the newest record by time (HookRunContextResolver.cs:212-248), so a worker shell call in run A with no agent worktree and no PLAN_FILE is evaluated in run B's worktree when B started later.
  - Where the context comes from: HookCommand.ResolveRunContext passes the payload's workspace root, cwd and the process directory, with AgentWorktree always null (HookCommand.cs:308-324); ConductorGuard does the same with AgentWorktree null (ConductorGuard.cs:205-210). The cwd is read from the payload's "cwd" field for every harness: Plugin envelope (HarnessHookEventReader.cs:46), Copilot CLI (:68, :82), and the Claude family reader every other harness uses (:103). A harness that sends no cwd gives no cwd signal; the plan file, the cd target and the single-run rule still apply, else Ambiguous.
  - Consumers of the resolved context, and what each does when Ambiguous (no active run, canonical root as Worktree, Worktrees still listing every worktree):
    - Rule fact root, EvaluationRootFor (ArbiterHookDecisionEngine.cs:793-818): with PLAN_FILE in the prompt, the plan's own worktree is chosen through ResolvePlanRoot (unchanged); without one, facts are read at the canonical root, where no active plan lives. That is no looser than today's wrong-run answer, and the guard note is logged.
    - Planning-path Read guard, GuardRoots (ClaudeHookAdapter.cs:575-578), and the Copilot content guard root (CopilotHookAdapter.cs:461, 969): they judge against the root and every listed worktree, so planning paths in every run stay guarded.
    - Checkout guard (WorkerGuard.cs:262-269): reads the live run records itself, not the context, and refuses any step in the canonical checkout while any run is live. Unchanged, never relaxed.
    - Secret guard (SecretRules): reads only the call's text. Unaffected.
    - Seat recorder base folder (ReviewSeatRecorder.cs:59, 191-202): a relative packet path would resolve under the canonical root. T1 makes it refuse to save a relative packet path when the context has no active run, with a log line, so the missing seat is visible.
    - Autonomous plan nudge (ArbiterHookDecisionEngine.cs:700): needs the active run's autonomy, so it is not added. It is a note to the conductor, not a guard.
- **Defect 8 (reproduces from code; the exact refused commands were not kept).** AnySecretFileAfterPattern skips leading dashes, then takes the next word as the pattern (SecretRules.cs:205-222): in `grep -A 3 token file` the pattern is read as 3 and token as a file, which IsSecretFileName refuses (a name with no extension containing "token", SecretRules.cs:409-417). Words reads quotes as separators (SecretRules.cs:497-501), so `grep "bearer token" f` takes bearer as the pattern and token as a file. SplitSteps splits a heredoc body at each newline (SecretRules.cs:547-552), so a script line that starts with head, sort or type is read as a shell reader. ScriptOpensSecretFile reads any open(...) argument, including a bare identifier such as token_path, as a path (SecretRules.cs:241-243, 730-731).
- **Defect 10 (reproduces).** docs-dev has a task gate (kyber-weave.yml:632-633, review-config, include only the host configuration), so config.task-gates-declared is true (TriggerFactBuilder.cs:254-256), the step-0 no-gates-declared answer is skipped (products/kyber-squad/standards/conductor/conductor.arbiter-rules.yml:316-319), and the model asks; a docs-only report never mentions a gate, so missing escalates. The rule file's comment "the documentation developer has none" is stale.
- **Defect 3 (cause partly differs).** (a) RedClassifier.Evaluate fails with "the declared gates ran no tests" when no gate failed or ran tests (src/KyberWeave.Application/Review/RedEvidence.cs:587-594); the github-actions, react, typescript and docs-dev gate lists hold no test gate (kyber-weave.yml:511-573, 632-633). (b) The "not changed by this task" text comes from Describe when IsOwnFile fails (RedEvidence.cs:625-628); IsOwnFile only matches a compiler path that ends with the repository path (RedEvidence.cs:444-453), while `npm run --prefix dash typecheck` (kyber-weave.yml:539) prints paths relative to dash/. The likely cause is the path form, not the files' ownership; no failing report from today was kept to confirm it, so T9's RED test pins the path form, and its worker reports if the real output differs.
- **Defect 4 (cause differs in part).** review.task-gates has no dev-easy, dev-medium or dev-hard entry (kyber-weave.yml:579-633). TaskGateResolver.Resolve still resolves technology gates when the task names a technology (TaskGateResolver.cs:76-103); NONE_DECLARED comes when no technology is named, such as a `--paths` run without a plan task (ReviewGatesService.cs:705-737, TaskGateNextStep.cs:102-104). The tiered agents dev-medium and dev-hard have the same gap.
- **Defect 5 (reproduces).** Every csharp technology gate names KyberWeave.sln or a tests/KyberWeave.* project (kyber-weave.yml:433-505); the council's dash-server gates exist only in review.gates (kyber-weave.yml:285-300). The csharp include *net-paths (`**/*.cs`) also selects dash files, so a dash change runs the KyberWeave suite and a format check that checks nothing.
- **Defect 6 (reproduces).** The digest prints `runs=<k>` (src/KyberWeave.Application/Review/Commands/TaskGateNextStep.cs:113-114) and the RED line hard-codes runs=1 (ReviewGateReporting.cs:410).
- **Defect 7 (reproduces).** ArbiterPathRule.ExtractPaths treats a backticked span with a file-extension-like suffix as a path and prefixes the directory of an earlier span in the same bullet (src/KyberWeave.Application/Arbiter/Plans/PlanDocumentParser.cs:1042-1075), so `process.env.VERSION` after a dash/tray/src-tauri/ path becomes dash/tray/src-tauri/process.env.VERSION. The Files block reaches it through the Files-label branch (PlanDocumentParser.cs:500-507, 599-673).
- **Defect 2 (likely cause; not reproduced live).** The worker runs integrate from its task worktree (worker baseline). IntegrateUnderLock removes that worktree, then runs `git branch -D` (RunIntegrateService.cs:289-304). ProcessGitCommandRunner starts the bare name "git" (src/KyberWeave.Data/RunStart/ProcessGitCommandRunner.cs:16, 26). On Unix .NET resolves a bare program name and reads the process's current directory, which now no longer exists, and throws FileNotFoundException, whose default message is "Unable to find the specified file.". The catch at RunIntegrateService.cs:232-235 reports "the task could not be integrated: …" and exit 1 even though the commits landed; the task branch is left undeleted.
- **Standards.** csharp standard fetched through docs_get_coding_standard_for_technology: in-memory unit tests through interfaces; integration tests prove adapters in CI only.
- **Overlap with other runs.** No task edits dash/**. T5 edits .kyber-weave/kyber-weave.yml, which a release or version run may also touch (Risk R1).
- Discovery: codegraph 8, docs 1, direct reads 22 (worktree files whose exact lines the codegraph answers trimmed or elided: ReviewGateReporting.cs, ReviewGatesService.cs, RunIntegrateService.cs, RunIntegrateCommand.cs, ProcessGitCommandRunner.cs, ProcessRunner.cs, ReviewSeatRecorder.cs, ClaudeHookAdapter.cs, ArbiterHookDecisionEngine.cs, HookRunContextResolver.cs, SecretRules.cs, TaskGateNextStep.cs, conductor.arbiter-rules.yml, TriggerFactBuilder.cs, PlanDocumentParser.cs, TaskGatesRepositoryConfigTests.cs, kyber-weave.yml, catalog.md, plans README; plus rg searches for rule ids and test classes; then the run evidence for defect 1: one seat file and one background record outside the index), web 1 (Claude Code hooks documentation).

## Test contract (development-mode: test-first)

All runner commands run from the worktree root.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | tests/KyberWeave.Application.Tests/Arbiter/ClaudeHookAdapterTests.cs; tests/KyberWeave.Application.Tests/Arbiter/ReviewSeatRecorderTests.cs; tests/KyberWeave.Data.Tests/Arbiter/FileBackgroundDispatchStoreTests.cs (extend all three) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.ClaudeHookAdapterTests\|FullyQualifiedName~KyberWeave.Tests.Arbiter.ReviewSeatRecorderTests"` and `dotnet test tests/KyberWeave.Data.Tests/KyberWeave.Data.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.FileBackgroundDispatchStoreTests"` | Today's order (an unpaired SubagentHandback with the report, then a completed PostToolUse for a lens dispatch whose response is the wrapper): the seat file holds the report, once, and the return pairs with its pre event; the same pair in the reverse order: same result; a wrapper post with no kept handback and a SubagentStop carrying last_assistant_message: the seat holds that message; a wrapper post with neither: no seat, one log line; a followed background dispatch (async_launched) still records as today; the recorder refuses a wrapper-only reply; a seat file holding only a wrapper is replaced by the real reply; a normal foreground reply with real text still saves as today; a non-Claude reply that starts with similar wording ("This agent's report was delivered …" followed by findings) still saves, because only a whole wrapper reply is refused; a kept handback is taken once (a second post finds none); a kept handback older than the store's stale age is pruned; a relative packet path with no active run in the context is not saved and logs one line | Assertions fail: the seat holds the wrapper | All listed tests pass; existing tests keep their assertions |
| T2 | tests/KyberWeave.Application.Tests/Arbiter/HookRunContextResolverTests.cs; tests/KyberWeave.Application.Tests/Arbiter/WorkerGuardCanonicalCheckoutCommandTests.cs; tests/KyberWeave.Application.Tests/Arbiter/ArbiterBatchEvaluationRootTests.cs (extend all three) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Application.Tests.Arbiter.HookRunContextResolverTests\|FullyQualifiedName~KyberWeave.Application.Tests.Arbiter.WorkerGuardCanonicalCheckoutCommandTests\|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterBatchEvaluationRootTests"` | Two live runs, run B newer, in every case: cwd inside run A's worktree resolves run A's worktree and record; cwd in a sibling task worktree whose branch descends from run A's branch only resolves that task worktree with run A as the active run; a sibling task worktree whose branch descends from both runs' branches or neither is Ambiguous; cwd at the canonical root and a command starting `cd <run A worktree> &&` resolves run A; cwd inside run A and a command starting `cd <run B worktree> &&` stays run A with a note; cwd inside run A and `git -C <run B worktree> status` stays run A; no cwd, no cd, no plan: Ambiguous, no active run, Source Ambiguous, a note names both; one live run and no signal: that run as today; agent worktree and plan worktree still outrank cd and cwd. Per consumer family under Ambiguous: EvaluationRootFor with PLAN_FILE returns the plan's worktree and without it returns the canonical root; GuardRoots still lists every worktree; the checkout refusal with two live runs names both worktrees and still refuses, and with one live run reads as today | Assertions fail: run B's worktree is chosen; the refusal names only run B | All listed tests pass |
| T3 | tests/KyberWeave.Application.Tests/Arbiter/WorkerGuardSecretTests.cs (extend) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Application.Tests.Arbiter.WorkerGuardSecretTests"` | Allowed: `grep -A 3 token src/a.cs`, `grep -nA 3 token src/a.cs`, `grep -A3 token src/a.cs`, `grep -rn --include '*.cs' token src`, `rg -g '*.yml' token`, `rg -e token -n src`, `grep -n "bearer token" src/a.cs`, a `python3 - <<'EOF'` heredoc whose lines start with head =, sort( or type and mention token, a python heredoc that assigns token_path = 'out/report.txt' and calls open(token_path, 'w'). Refused (kept or added): `grep -A 3 x .env`, `grep "x" ".env"`, `rg -e x secrets.json`, `grep -f .env x`, `grep -nf .env x`, `grep --file=secrets.json x`, `grep --exclude-from=.env x`, `rg --ignore-file .env x`, `bash -c "cat .env"`, `sh -c 'grep x secrets.json'`, `eval "cat token.txt"`, `ls \| xargs grep x .env`, `cat token.txt`, `python3 -c "open('.env').read()"`, a `bash <<'EOF'` heredoc containing `cat .env`, an unknown program's heredoc containing `cat .env`, python open(os.path.join(home, '.aws', 'credentials')), open(d + '/.env'), open(f"{d}/.env"), open(Path('.env')) and Path('.env').read_text(), an identifier assigned twice in one command with one secret value then opened, a python heredoc that prints os.environ, and every case the class already refuses | Assertions fail: the allowed commands are refused | All listed tests pass; no existing refusal case changes |
| T4 | tests/KyberWeave.Application.Tests/Arbiter/ArbiterGateReportRuleTests.cs (extend) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterGateReportRuleTests"` | A docs-dev return for a task whose files are only docs answers no-gates-declared and allows; a docs-dev task that edits .kyber-weave/kyber-weave.yml still asks; a task with no files known keeps today's answer; a gate with no include always applies | Assertion fails: the docs-only return asks the model or escalates | All listed tests pass |
| T5 | tests/KyberWeave.RepoPolicy.Tests/TaskGatesRepositoryConfigTests.cs (extend) | `dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.TaskGatesRepositoryConfigTests"` | dev-easy, dev-medium and dev-hard each resolve gates with no technology named; each holds every gate id of review.technology-gates; a change to dash/server/KyberDash.Core/X.cs or dash/server.tests/KyberDash.Domain.Tests/XTests.cs under csharp selects the KyberDash build and test gates and no KyberWeave.sln gate; a change to src/KyberWeave.Core/X.cs selects no KyberDash gate; csharp selects the test-policy gate for tests/KyberWeave.RepoPolicy.Tests/X.cs; the existing file-kind theory still passes | Assertions fail: dev-easy resolves nothing; no KyberDash gate selected | All listed tests pass |
| T6 | tests/KyberWeave.Application.Tests/ReviewGatesTaskGateNextStepTests.cs (extend) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.ReviewGatesTaskGateNextStepTests"` | Third run for a task prints runs=3; a first run prints runs=1; a run with no task id prints runs=1; the RED line prints the same count; an unreadable counter counts as 0 and the run still reports. Written through the existing service entry point, so the failing run is an assertion, not a wrong-arity build error | Assertion fails: the line reads runs=<k> | All listed tests pass |
| T7 | tests/KyberWeave.Application.Tests/Arbiter/ArbiterPlanParserTests.cs (extend) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterPlanParserTests"` | A Files bullet whose description names process.env.VERSION or matrix.target yields only its lead path; a description span with a slash is still a path; a lead list of two paths before the colon yields both; a bare file name after a directory in the lead list still inherits the directory | Assertion fails: the prose span is returned as a path | All listed tests pass |
| T8 | tests/KyberWeave.Application.Tests/RunIntegrate/RunIntegrateServiceTests.cs (extend) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Application.Tests.RunIntegrate.RunIntegrateServiceTests"` | When the branch delete throws FileNotFoundException("Unable to find the specified file.") after the worktree is removed, the outcome names the landed sha ("integrated at <sha>, but …"), never "could not be integrated"; a throw before any commit lands keeps today's message | Assertion fails: the message is "the task could not be integrated: …" | All listed tests pass |
| T9 | tests/KyberWeave.Application.Tests/ReviewGatesRedModeTests.cs (extend) | `dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --filter "FullyQualifiedName~KyberWeave.Tests.ReviewGatesRedModeTests"` | Lint-and-typecheck gates that all pass: accepted as RED not applicable, KW-REVIEW-051, evidence names the gates; same gates with a failing typecheck on TS2305 in tests/foo.test.ts while the task changed dash/tests/foo.test.ts: accepted as compile RED; the same error in a file the task did not change: rejected; a test gate present and every test passing: rejected as today; a path match needs a segment boundary (dash/xtests/foo.test.ts does not match tests/foo.test.ts). Written through the existing ApplyRed and Evaluate signatures (any new parameter is optional with today's behaviour as its default), so the failing run is an assertion, not a wrong-arity build error | Assertions fail: KW-REVIEW-052 for both accepted cases | All listed tests pass |
| T10 | No test file: runs the touched classes of T1 to T9 | The two commands under T10 | Every touched class passes on Linux | Not applicable: verification only, no code change | Both runs report a non-zero total and zero failures |

## Tasks

### T1: Claude seat-save records the reviewer's report, not the delivery wrapper (defect 1)

- **Objective:** A Claude Code review seat file holds the lens's report, so the verdict reads it.
- **Worker:** claude
- **Capabilities:** C# Arbiter hook adapter and recorder with in-memory xUnit tests
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `T1`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Application/Arbiter/Hooks/Adapters/ClaudeHookAdapter.cs`: HandleHandback keeps an unpaired message; HandlePost joins a wrapper-only completed return to the kept message by agent id; HandleSubagentStop uses last_assistant_message as the fallback
  - `src/KyberWeave.Application/Arbiter/Hooks/Adapters/ClaudeDeliveryWrapper.cs`: new static recogniser for the wrapper text, copied from the FactoryTranscript IsBackgroundLaunch pattern
  - `src/KyberWeave.Application/Arbiter/Hooks/ReviewSeatRecorder.cs`: Record refuses a whole-reply wrapper, replaces a wrapper-only seat file, and refuses a relative packet path when the context has no active run
  - `src/KyberWeave.Application/Arbiter/Hooks/ArbiterHookDecisionEngine.cs`: keep and take an unpaired return through the background store
  - `src/KyberWeave.Contracts/Arbiter/IBackgroundDispatchEngine.cs`: the keep and take members
  - `src/KyberWeave.Contracts/Repositories/IBackgroundDispatchStore.cs`: the keep and take members
  - `src/KyberWeave.Data/Hooks/Arbiter/FileBackgroundDispatchStore.cs`: kept returns in a sibling folder, same session and agent keys, same atomic write and pruning
  - `tests/KyberWeave.Testing/Arbiter/ArbiterTestServices.cs`: the in-memory store gains the two members
  - `tests/KyberWeave.Application.Tests/Arbiter/ClaudeHookAdapterTests.cs`: new cases
  - `tests/KyberWeave.Application.Tests/Arbiter/ReviewSeatRecorderTests.cs`: new cases
  - `tests/KyberWeave.Data.Tests/Arbiter/FileBackgroundDispatchStoreTests.cs`: keep, take once, prune
- **Design (hard):** Failure mode: the wrapper is saved as the reply, the report is lost, or it is saved twice. Mitigation:
  - Recognise the wrapper only as a whole reply: the trimmed text starts with "This agent's report was delivered to you as a message from" and ends with "it is not repeated here." with nothing else, so a report that quotes the phrase is kept.
  - Only the Claude adapter changes; the other adapters' return paths are listed in D8 and stay as they are.
  - An unpaired handback (no followed dispatch) keeps its message under its session and agent id, then is decided as today.
  - A completed post whose response is the wrapper takes the kept message (taking removes it) and decides it with the post's prompt and target, so the seat and the pre-event pairing come from the real report; with none kept, it records nothing and logs one line.
  - SubagentStop with last_assistant_message decides it the same way only when the dispatch has no recorded return; the digest check in DecideBackgroundReturn and the recorder's AlreadyHolds stop a repeat.
  - A wrapper never sets LastResponseDigest and is never passed to the recorder.
- **Commands:**
  - `kyber-weave review gates . --agent dev-hard --task T1 --red`
  - `kyber-weave review gates . --agent dev-hard --task T1`
- **Acceptance:**
  - T1 test-contract row passes; existing adapter and recorder tests keep their assertions.

### T2: A hook resolves the run the worker is in, not the newest run (defect 9)

- **Objective:** With several live runs, a hook evaluates a call against the worktree its cwd is in.
- **Worker:** claude
- **Capabilities:** C# Arbiter run-context resolution with in-memory xUnit tests
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `T2`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Application/Arbiter/Hooks/HookRunContextResolver.cs`: Candidate order, run mapping by containment or ancestry, and the Ambiguous outcome per D9
  - `src/KyberWeave.Domain/Arbiter/Hooks/HookRunContext.cs`: HookRunContextSource gains Ambiguous; HookRunContextRequest carries the command's leading cd target
  - `src/KyberWeave.Application/Arbiter/Hooks/HookCommand.cs`: ResolveRunContext passes the leading cd target of a shell call and logs the guard note when the source is Ambiguous
  - `src/KyberWeave.Contracts/Repositories/ICheckedOutBranchReader.cs`: an ancestry member (is branch A an ancestor of branch B in this worktree; null when git fails)
  - `src/KyberWeave.Data/RunStart/GitCheckedOutBranchReader.cs`: the member through git merge-base --is-ancestor, as RunIntegrateService.IsAncestor does
  - `tests/KyberWeave.Testing/Fakes/FakeCheckedOutBranchReader.cs`: the scripted member
  - `src/KyberWeave.Application/Arbiter/Hooks/WorkerGuard.cs`: CheckoutRefusalAt passes every live run to the refusal
  - `src/KyberWeave.Application/Arbiter/Hooks/WorkerGuardRules.cs`: StrayCommandRefusal names every live run's worktree when several are live
  - `tests/KyberWeave.Application.Tests/Arbiter/HookRunContextResolverTests.cs`: new cases
  - `tests/KyberWeave.Application.Tests/Arbiter/WorkerGuardCanonicalCheckoutCommandTests.cs`: new cases
  - `tests/KyberWeave.Application.Tests/Arbiter/ArbiterBatchEvaluationRootTests.cs`: EvaluationRootFor under an Ambiguous context, with and without PLAN_FILE
  - `tests/KyberWeave.Application.Tests/RunStart/RunStateAndCheckoutTests.cs`: its private ScriptedBranchReader implements the new ancestry member (returns null)
- **Design (hard):** Failure mode: a guard allows or refuses a call by another run's plan and files. Mitigation:
  - Order and cd rule exactly as D9: cwd wins; a cd target counts only when the cwd is absent or is the canonical root; `git -C` is never a signal.
  - A chosen worktree maps to a run by containment (longest run worktree that equals or contains it), else by ancestry (exactly one live run's branch is an ancestor of the worktree's checked-out branch); anything else is Ambiguous.
  - Ambiguous: no active run, canonical root for paths, Worktrees still list every worktree, a note naming every live run, logged by HookCommand. No guard is relaxed; the consumer list in Investigation findings, defect 9, is the checklist, and each family has a test.
  - The live-record checks (directory exists, branch checked out) stay as they are.
- **Commands:**
  - `kyber-weave review gates . --agent dev-hard --task T2 --red`
  - `kyber-weave review gates . --agent dev-hard --task T2`
- **Acceptance:**
  - T2 test-contract row passes.

### T3: The secret guard reads grep options and script paths precisely (defect 8)

- **Objective:** Harmless greps and heredoc edits pass KW-ARB-SECRET-001; every real secret read is still refused.
- **Worker:** claude
- **Capabilities:** C# security rule parsing with in-memory xUnit tests
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `T3`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Application/Arbiter/Hooks/SecretRules.cs`: AnySecretFileAfterPattern parses grep and rg options and finds the pattern from the raw step; file-naming option values are judged; interpreter heredoc bodies leave the shell steps; ScriptOpensSecretFile and a Path(...) check fail closed per D12
  - `tests/KyberWeave.Application.Tests/Arbiter/WorkerGuardSecretTests.cs`: new allow and refuse cases
- **Design (hard):** Failure mode: a narrowing lets a real secret read through. Mitigation (D12 is the specification):
  - Words, ReadsSecretFile's word scan and PrintsWholeEnvironment are not changed; nested shell text (bash -c, sh -c, eval) is still matched as today.
  - Short bundles are read letter by letter; the last value-taking letter takes the rest of the bundle or the next word; f anywhere in a bundle makes its value a file that is judged.
  - Every file-naming option value is judged: -f, --file, --exclude-from, --include-from, --ignore-file.
  - Unknown options keep today's reading (treated as flags).
  - A heredoc body leaves the shell steps only for a known script interpreter; any other program keeps today's step split.
  - open(...) and Path(...) judge every quoted literal inside the argument and every quoted value assigned to an identifier in it; only a lone unassigned identifier whose own name matched is not a path.
  - Every existing refusal test stays unchanged; new cases only add refusals or allow the listed commands.
- **Commands:**
  - `kyber-weave review gates . --agent dev-hard --task T3 --red`
  - `kyber-weave review gates . --agent dev-hard --task T3`
- **Acceptance:**
  - T3 test-contract row passes; no existing test in WorkerGuardSecretTests changes.

### T4: The gate-report rule does not ask a task no gate applies to (defect 10)

- **Objective:** A docs-only return passes KW-ARB-GATE-REPORT-001 at step 0.
- **Worker:** claude
- **Capabilities:** C# Arbiter fact builder with in-memory xUnit tests
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `T4`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Application/Arbiter/Facts/TriggerFactBuilder.cs`: config.task-gates-declared per D10, reading plan.task.files and each resolved gate's include
  - `products/kyber-squad/standards/conductor/conductor.arbiter-rules.yml`: the KW-ARB-GATE-REPORT-001 comment for no-gates-declared, stale today
  - `tests/KyberWeave.Application.Tests/Arbiter/ArbiterGateReportRuleTests.cs`: new cases
- **Commands:**
  - `kyber-weave review gates . --agent dev-medium --task T4 --red`
  - `kyber-weave review gates . --agent dev-medium --task T4`
- **Acceptance:**
  - T4 test-contract row passes; the rule's answer ids and effects are unchanged.

### T5: Task gates for the tiered developer agents and KyberDash C# (defects 4 and 5)

- **Objective:** dev-easy, dev-medium and dev-hard always resolve gates, and a C# change under dash/server builds and tests KyberDash.
- **Worker:** opencode
- **Capabilities:** YAML gate configuration and a C# repo-policy xUnit test
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `T5`
- **Depends on:** none
- **Files:**
  - `.kyber-weave/kyber-weave.yml`: review.technology-gates.csharp gains test-policy (D5) and KyberDash gates (D4), the KyberWeave gates' include is narrowed off dash; review.task-gates gains dev-easy, dev-medium and dev-hard (D3) and test-dev gains the KyberDash gates
  - `tests/KyberWeave.RepoPolicy.Tests/TaskGatesRepositoryConfigTests.cs`: new cases
- **Order inside the task:** add test-policy to the csharp technology gates first, so the RED run executes the repo-policy tests; then write the tests; then run with --red; then finish the configuration.
- **KyberWeave include (exact):** the net-paths anchor becomes "src/**/*.cs", "tests/**/*.cs", "src/**/*.csproj", "tests/**/*.csproj", "**/*.props", "**/*.targets"; the net-build-paths anchor becomes those plus "KyberWeave.sln*", "**/NuGet.config", "**/.editorconfig". Every gate that aliases them (csharp, github-actions, test-dev, sql-database-architect) narrows with them, which is intended. net-test-paths is unchanged.
- **KyberDash gates (exact):** under review.technology-gates.csharp, after the KyberWeave gates, add gates with ids dash-format-whitespace, dash-format-style, dash-build, dash-unit-application and dash-unit-domain, each with include *dash-server-paths (the existing anchor at kyber-weave.yml:286). Format: copy the council's dash-server-format commands (kyber-weave.yml:287, 291) with --include "{changed}" and the fix forms, as the KyberWeave format gates do. Build: dotnet build dash/server/KyberDash.sln -c Release --artifacts-path ".agent_scratchpad/{task}/artifacts" -nr:false -p:UseSharedCompilation=false. Tests: dotnet test dash/server.tests/KyberDash.Application.Tests/KyberDash.Application.Tests.csproj and dash/server.tests/KyberDash.Domain.Tests/KyberDash.Domain.Tests.csproj, each -c Release --no-build --artifacts-path ".agent_scratchpad/{task}/artifacts", timeout 600 (D13). Anchor them and alias the same five in test-dev.
- **Tiered agents (exact):** review.task-gates gains dev-easy, dev-medium and dev-hard, each a list of aliases of every gate in review.technology-gates (csharp, github-actions, react, tauri, typescript, rust), each id once.
- **Commands:**
  - `kyber-weave review gates . --agent dev-easy --task T5 --red`
  - `kyber-weave review gates . --agent dev-easy --task T5`
  - `kyber-weave review catalogue .`
- **Acceptance:**
  - T5 test-contract row passes; `kyber-weave review catalogue .` loads the configuration with no error.

### T6: The TASK_GATE line prints the real run count (defect 6)

- **Objective:** runs=k is the number of task-gate runs for the task.
- **Worker:** opencode
- **Capabilities:** C# review command output with in-memory xUnit tests
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `T6`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Application/Review/Commands/TaskGateNextStep.cs`: AfterRun, NoneDeclared and Digest take the run count and print it
  - `src/KyberWeave.Application/Review/Commands/ReviewGateReporting.cs`: RedLines takes the run count
  - `src/KyberWeave.Application/Review/ReviewGatesService.cs`: the agent-gate path increments and reads the counter per D6 through the injected file system
  - `tests/KyberWeave.Application.Tests/ReviewGatesTaskGateNextStepTests.cs`: new cases
- **Commands:**
  - `kyber-weave review gates . --agent dev-easy --task T6 --red`
  - `kyber-weave review gates . --agent dev-easy --task T6`
- **Acceptance:**
  - T6 test-contract row passes; the class docstring no longer says the count is a placeholder.

### T7: docs tasks reads only paths from a Files bullet (defect 7)

- **Objective:** Prose names in a Files bullet's description are not file paths.
- **Worker:** opencode
- **Capabilities:** C# markdown parsing with in-memory xUnit tests
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `T7`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Application/Arbiter/Plans/PlanDocumentParser.cs`: in the Files-label block, a span after the bullet's first colon counts only when it contains a slash, and never inherits a directory; the lead spans before the colon keep today's rule; the shared rule used for delegation prompts is unchanged
  - `tests/KyberWeave.Application.Tests/Arbiter/ArbiterPlanParserTests.cs`: new cases
- **Commands:**
  - `kyber-weave review gates . --agent dev-easy --task T7 --red`
  - `kyber-weave review gates . --agent dev-easy --task T7`
- **Acceptance:**
  - T7 test-contract row passes; the archived-plan parser tests in tests/KyberWeave.Content.Tests still pass (run by the council).

### T8: run integrate reports the landed commit when cleanup fails (defect 2)

- **Objective:** Integration never reports failure for work that is on the run branch, and git runs with no working-directory dependence.
- **Worker:** opencode
- **Capabilities:** C# git integration service and process adapter with in-memory xUnit tests
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `T8`
- **Depends on:** none
- **Files:**
  - `src/KyberWeave.Application/RunIntegrate/RunIntegrateService.cs`: IntegrateUnderLock catches an IOException from the worktree removal or branch delete and returns the existing "the task is integrated at (sha), but …" failure; the outer catch keeps its message only for failures before the commits land
  - `src/KyberWeave.Data/RunStart/ProcessGitCommandRunner.cs`: resolve git to a full path once, falling back to the bare name when not found
  - `src/KyberWeave.Kernel/Processes/ProcessRunner.cs`: make FindOnPath reachable from the Data project if it is not already (public with an XML doc comment, or InternalsVisibleTo)
  - `tests/KyberWeave.Application.Tests/RunIntegrate/RunIntegrateServiceTests.cs`: new cases
- **Commands:**
  - `kyber-weave review gates . --agent dev-medium --task T8 --red`
  - `kyber-weave review gates . --agent dev-medium --task T8`
- **Acceptance:**
  - T8 test-contract row passes; the command's exit-code contract (RunIntegrateCommand remarks) is unchanged.

### T9: The RED rule accepts what a task's gates can prove (defect 3)

- **Objective:** RED is accepted as not applicable when no selected gate runs tests, and a relative compiler path matches the task's own file.
- **Worker:** claude
- **Capabilities:** C# review gate classifier with in-memory xUnit tests
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `T9`
- **Depends on:** T5 (the gate sets the rule is judged against), T6 (shares ReviewGateReporting.cs and ReviewGatesService.cs)
- **Files:**
  - `src/KyberWeave.Application/Review/RedEvidence.cs`: Evaluate takes whether any selected gate is a test gate and accepts per D7(a); IsOwnFile matches a relative compiler path on a segment boundary per D7(b)
  - `src/KyberWeave.Application/Review/Commands/ReviewGateReporting.cs`: ApplyRed passes the planned gates' test-gate flag
  - `src/KyberWeave.Application/Review/ReviewGatesService.cs`: passes the planned gates through unchanged
  - `src/KyberWeave.Cli/Commands/Review/ReviewSettings.cs`: the --red help text names the not-applicable case
  - `tests/KyberWeave.Application.Tests/ReviewGatesRedModeTests.cs`: new cases
- **Commands:**
  - `kyber-weave review gates . --agent dev-medium --task T9 --red`
  - `kyber-weave review gates . --agent dev-medium --task T9`
- **Acceptance:**
  - T9 test-contract row passes; existing RED tests keep their assertions.

### T10: Run every touched test class on Linux

- **Objective:** Prove the touched classes pass on Linux before the run is done.
- **Worker:** claude
- **Capabilities:** run dotnet tests in a Linux container and report counts
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `T10`
- **Depends on:** T1, T2, T3, T4, T5, T6, T7, T8, T9 (their integrated commits on the run branch)
- **Files:** the touched test classes, changed only for a Linux-only test defect such as a path separator; a failure in production code goes back to the owning task's session, and a clean run commits nothing.
  - `tests/KyberWeave.Application.Tests/Arbiter/ClaudeHookAdapterTests.cs`
  - `tests/KyberWeave.Application.Tests/Arbiter/ReviewSeatRecorderTests.cs`
  - `tests/KyberWeave.Data.Tests/Arbiter/FileBackgroundDispatchStoreTests.cs`
  - `tests/KyberWeave.Application.Tests/Arbiter/HookRunContextResolverTests.cs`
  - `tests/KyberWeave.Application.Tests/Arbiter/WorkerGuardCanonicalCheckoutCommandTests.cs`
  - `tests/KyberWeave.Application.Tests/Arbiter/ArbiterBatchEvaluationRootTests.cs`
  - `tests/KyberWeave.Application.Tests/Arbiter/WorkerGuardSecretTests.cs`
  - `tests/KyberWeave.Application.Tests/Arbiter/ArbiterGateReportRuleTests.cs`
  - `tests/KyberWeave.RepoPolicy.Tests/TaskGatesRepositoryConfigTests.cs`
  - `tests/KyberWeave.Application.Tests/ReviewGatesTaskGateNextStepTests.cs`
  - `tests/KyberWeave.Application.Tests/Arbiter/ArbiterPlanParserTests.cs`
  - `tests/KyberWeave.Application.Tests/RunIntegrate/RunIntegrateServiceTests.cs`
  - `tests/KyberWeave.Application.Tests/ReviewGatesRedModeTests.cs`
- **Commands:** from the run worktree, with C=/Users/dave/git/personal/claude/kyber-weave and W=$C/.agent_worktrees/20261125T011533Z-claude-fix-kyber-weave-tooling-defects:
  - `docker run --rm -v "$C":"$C" -w "$W" mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --artifacts-path /tmp/kw-artifacts --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.ClaudeHookAdapterTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ReviewSeatRecorderTests|FullyQualifiedName~KyberWeave.Application.Tests.Arbiter.HookRunContextResolverTests|FullyQualifiedName~KyberWeave.Application.Tests.Arbiter.WorkerGuardCanonicalCheckoutCommandTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterBatchEvaluationRootTests|FullyQualifiedName~KyberWeave.Application.Tests.Arbiter.WorkerGuardSecretTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterGateReportRuleTests|FullyQualifiedName~KyberWeave.Tests.ReviewGatesTaskGateNextStepTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterPlanParserTests|FullyQualifiedName~KyberWeave.Application.Tests.RunIntegrate.RunIntegrateServiceTests|FullyQualifiedName~KyberWeave.Tests.ReviewGatesRedModeTests" && dotnet test tests/KyberWeave.Data.Tests/KyberWeave.Data.Tests.csproj -c Release --artifacts-path /tmp/kw-artifacts --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.FileBackgroundDispatchStoreTests"'`
  - `docker run --rm -v "$C":"$C" -w "$W" mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --artifacts-path /tmp/kw-artifacts --filter "FullyQualifiedName~KyberWeave.Tests.TaskGatesRepositoryConfigTests"`
- **Acceptance:**
  - Both runs exit 0 and report a non-zero total and zero failures.
  - The worker returns each run's total, passed and failed counts; the conductor puts them in the PR description.

## Dependency graph

- Wave 1 (start first, T1 at the head of the queue): T1, T5, T6, T7.
- Wave 2 (as slots free; review.max-concurrent-workers is 4): T2, T3, T4, T8.
- Wave 3: T9 after T5 and T6.
- Wave 4: T10 after T1 to T9, then the council.
- No other file scopes overlap.

## Risks

- R1: Another run on release or version plumbing may edit .kyber-weave/kyber-weave.yml. Mitigation: T5 edits only the review.task-gates and review.technology-gates blocks; a conflict at merge is resolved on this branch.
- R2: Other runs edit dash/**; T5 adds gates that build and test KyberDash, so a broken dash tree fails a csharp task that touches dash. No task here touches dash files, so this run's gates do not select them.
- R3: The dev-* union entries outrank technology entries (D3). Mitigation: the T5 drift test fails when a technology gate is missing from a union.
- R4: Claude Code's wrapper text may change. Mitigation: SubagentStop records the report whatever the wrapper says; the recogniser only stops a wrapper being saved.
- R5: D7(a) could let a test-less task skip RED. Mitigation: applies only when no selected gate is a test gate and every gate passed; review checks the task's gate set.
- R6: This run's own tools still carry defect 1, so a council on Claude Code would get wrapper-only seats and a KW-REVIEW-040 refusal. Mitigation (D14): the conductor runs the council on OpenCode; installing this branch's build is a user-level change left to the owner.
- R7: This run's own tools still carry defect 2, so a worker's `run integrate` may exit 1 after its commit landed. Mitigation (D14): the worker checks the run branch head with `git -C <run worktree> log -1 --format=%H` holds its commit, returns that sha as COMMIT with one line naming defect 2, and the conductor accepts it; a task branch left behind is listed in the PR notes for deletion.
- R8: T1 adds store members that the in-memory test host also implements; a worker that misses the fake breaks the build. Mitigation: the fake is in T1's Files.

## Out-of-scope boundaries

- PR note: dash/web and dash/tests vitest — React and TypeScript tasks have no test-running task gate, so their RED is "not applicable" under D7; adding a vitest task gate is a new gate the request did not ask for — left to a human.
- PR note: run integrate with a deleted working directory — the adapter fix in T8 is reasoned, not reproduced, because reproducing needs the test process's own directory deleted (D11) — a human can confirm on the next real run.
- PR note: existing seat files that already hold a wrapper before the real report are not migrated; the next cycle writes clean files.

## Verification gates

1. Each task's own task gate, as its Commands list (RED then GREEN).
2. Linux, before the run is done (task T10): run every touched test class in a .NET 10 SDK container (global.json pins 10.0.100). The canonical checkout is mounted at its own absolute path so the worktree's .git pointer resolves (D16), and the build goes to a container-only folder so the macOS build output is untouched. With C set to /Users/dave/git/personal/claude/kyber-weave and W set to the run worktree (C/.agent_worktrees/20261125T011533Z-claude-fix-kyber-weave-tooling-defects):
   - `docker run --rm -v "$C":"$C" -w "$W" mcr.microsoft.com/dotnet/sdk:10.0 sh -c 'dotnet test tests/KyberWeave.Application.Tests/KyberWeave.Application.Tests.csproj -c Release --artifacts-path /tmp/kw-artifacts --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.ClaudeHookAdapterTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ReviewSeatRecorderTests|FullyQualifiedName~KyberWeave.Application.Tests.Arbiter.HookRunContextResolverTests|FullyQualifiedName~KyberWeave.Application.Tests.Arbiter.WorkerGuardCanonicalCheckoutCommandTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterBatchEvaluationRootTests|FullyQualifiedName~KyberWeave.Application.Tests.Arbiter.WorkerGuardSecretTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterGateReportRuleTests|FullyQualifiedName~KyberWeave.Tests.ReviewGatesTaskGateNextStepTests|FullyQualifiedName~KyberWeave.Tests.Arbiter.ArbiterPlanParserTests|FullyQualifiedName~KyberWeave.Application.Tests.RunIntegrate.RunIntegrateServiceTests|FullyQualifiedName~KyberWeave.Tests.ReviewGatesRedModeTests" && dotnet test tests/KyberWeave.Data.Tests/KyberWeave.Data.Tests.csproj -c Release --artifacts-path /tmp/kw-artifacts --filter "FullyQualifiedName~KyberWeave.Tests.Arbiter.FileBackgroundDispatchStoreTests"'`
   - `docker run --rm -v "$C":"$C" -w "$W" mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/KyberWeave.RepoPolicy.Tests/KyberWeave.RepoPolicy.Tests.csproj -c Release --artifacts-path /tmp/kw-artifacts --filter "FullyQualifiedName~KyberWeave.Tests.TaskGatesRepositoryConfigTests"`
   - Each run reports a non-zero test count and zero failures; the counts go in the PR description.
3. The end-of-run review council (below) and CI's full suite, including tests/KyberWeave.Integration.Tests.

## Review

One end-of-run council over the whole change, by code-reviewer under the `code-review` skill, after every task is integrated; its fix rounds go back to the task's session. The council derives its own command.

## docs-dev closeout

docs-dev harvests the Design harvest rows into docs/kyber-arbiter/architecture.md (seat files, run context, KW-ARB-SECRET-001 and KW-ARB-GATE-REPORT-001 rows), docs/code-review/architecture.md (task gates, RED, runs=k), docs/configuration.md where it describes review.task-gates and technology-gates, the DocGraph plan-parsing page, and the run integrate runbook text; fills the Closeout mapping; and runs `kyber-weave docs standards migrate` for the standards warning period. ADR waiver expected: each decision refines an existing rule without a new boundary.
