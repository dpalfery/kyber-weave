# Pre-push self-review gate

"Pre-push" is the name of this gate, kept for its heading and its references. It does not mean the gate runs before the push: the draft pull request is pushed first, and a draft push never waits for this gate. Required CI decides readiness.

Run this gate on your own finished change. Its verdict is evidence for the pull request description; it does not block the push or the ready step. It works the same in any
harness — Claude, Pi, OpenCode, Antigravity, or others — because it uses only the
skill's Procedure and the host's declared gates.

1. **Sync.** Refresh the remote head and the base branch before the draft push. If another
   actor advanced either, reconcile their changes into your branch and revalidate the
   affected work. Merge, never rebase, never force-push. Confirm the refreshed remote
   head and base branch are reachable from your local `HEAD` before the draft push.
2. **Council over the whole change.** Push first, as a draft pull request where one can
   be opened. The council reviews the pushed branch, or the local branch when the push
   fails, and never waits for a pull request. Then run the skill's full Procedure steps 1–5, with [first-review.md](first-review.md) for the council, over the whole accumulated diff against its
   base, not just the last files touched. Fix major-or-worse findings where a bounded fix
   exists, re-review the fix as a verifier cycle (see [verifier-cycle.md](verifier-cycle.md)), and use that
   later verdict in the pull request description. A verdict computed before a fix is void. List any major-or-worse
   finding still open by id in the pull request description. Minor/Info findings never
   block. Required CI decides readiness; the verdict does not gate the push or the ready step. In this skill, a finding verified as real is one that survived the council's adjudication. A fix for a review finding verified as real ships with a test that fails without the fix, bot review rounds included; a finding classified as a nitpick needs no test.
   Run the gates command without `--council`. The CLI refuses the full-suite gates, and each refusal is a blocking failure that names the targeted alternatives; report those as "not run here, CI runs them", and do not count a refusal as a code finding in the verdict.
3. **Defect-class checklist.** Walk each class explicitly over the whole change, as part of this pre-push gate (the draft push does not wait for it):
   a. Invented/hidden values: `?? 0`, `|| N`, `?? []`, clamps, default window/price/size/duration, unmeasured shown as zero or "measured", partial data looking complete (honest-unobservability: absent stays absent with reason).
   b. Scope/cost: filters/dedupe too wide or single-caller-only; N+1/per-run loops; unbounded reads/copies; caps without paging; large payloads kept alive.
   c. Parity: guard added in one place not every caller/branch (store vs direct-DB, harness vs run scope, API vs UI, producer vs consumer).
   d. Stale text: comments/docs/test names/version-migration numbers/PR description mismatch code (grep old value).
   e. Tests only pure helpers never real render/serve path; assertions locking bug; new test passing even if fix reverted.
   f. Lint and type gates actually run.
4. **Evidence.** Paste the real gate output into the push report. A claim with no
   gate result is "unverified", never "passing".
