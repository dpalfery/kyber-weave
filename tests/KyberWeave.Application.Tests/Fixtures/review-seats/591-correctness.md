```yaml
- id: correctness/critical-severity-case
  lens: correctness
  severity: major
  confidence: 8
  file: src/KyberWeave.Core/Arbiter/Rules/default-rules.yml
  line: 396
  excerpt: |
    - { fact: finding.severity, equals: Critical }
  claim: The NITPICK-001 step-0 guard compares `finding.severity` to the capitalised "Critical", but lenses return lowercase `critical`.
  evidence: |
    products/kyber-squad/agents/review-lens.md:58 defines `severity: critical | major | minor`.
    ReturnedFinding.ParseAll passes the severity through verbatim, and TriggerFactBuilder.WithReturnedFinding does not normalise it.
    RulePredicate string equality uses StringComparison.Ordinal (RulePredicate.cs:457).
    ArbiterNitpickRuleTests only use "Critical" and "Minor", so no test covers lowercase.
  failure_scenario: |
    A lens returns `severity: critical`. The step-0 "Critical is always substantive" guard does not match, so the finding goes to the provider.
    If the provider answers nitpick at 0.85 or above, the effect is `drop` (once shadow is lifted).
    A critical finding is then dropped, which breaks the "never drops a real finding" invariant.
  guidance: [§1, §5, R8]
  suggestion: Normalise severity once (for example, lowercase it in ReturnedFinding) and make the rule compare against the lowercase value, or use a case-insensitive operand.

- id: correctness/refute-slug-vs-full-id
  lens: correctness
  severity: major
  confidence: 8
  file: src/KyberWeave.Core/Arbiter/Facts/TriggerFactBuilder.cs
  line: 266
  excerpt: |
    if (stored.Count > 0 && string.Equals(stored[0].Id, findingId, StringComparison.Ordinal))
  claim: Stored findings keep the id exactly as the lens returned it (`<lens>/<slug>`), but `findingId` is the bare slug after the last "/". The comparison never matches, here and at line 300.
  evidence: |
    review-lens.md:58 defines `id: <lens-name>/<short-slug>`.
    RecordReturnedFindingsAsync stores `returned[i].Text`, which starts at the `id:` line, so the stored id is the full `lens/slug`.
    ArbiterEvaluator passes `FindingSlug(refutedId)` (the bare slug) to WithDroppedStatus and WithPriorFindings.
    The tests pass only because they return bare ids ("nit-1") against `REFUTE: correctness/nit-2`.
  failure_scenario: |
    Line 266: `finding.dropped` is never set, so NITPICK-002 never fires and a dropped finding still gets a refuter.
    Line 300: the `break` at the finding's own item never triggers. The refuted finding's own stored item (same file, line offset 0) and any items stored after it become "earlier" candidates.
    DUP-001 then asks the provider whether the finding duplicates itself. Once shadow is lifted, a real finding's refutation is denied as a duplicate of itself.
  guidance: [§5, R8, §1]
  suggestion: Compare ids through one normaliser, such as the slug after the last "/", on both the stored and the refuted side. Add a test that uses `lens/slug` ids in the lens return.

- id: correctness/same-failure-loses-model-token
  lens: correctness
  severity: major
  confidence: 7
  file: src/KyberWeave.Arbiter/Hooks/ArbiterHookDecisionEngine.cs
  line: 276
  excerpt: |
    return new HookOutcome(HookOutcomeKind.AllowWithNote, advisory ?? "ARBITER_SAME_FAILURE: yes");
  claim: The same-failure advisory outcome returns before the Allow branch's ModelToken and ModelBlock are built, so a rework dispatch that gets the advisory loses model targeting.
  evidence: |
    Only the Allow, Applies and NotApplicable outcomes read `ModelBlockFor` and set `ModelToken` (lines 259-269).
    The same-failure branch returns an AllowWithNote with neither field.
    The Claude adapter's AllowWithNote path (ClaudeHookAdapter.cs:267) calls RenderPreAllowNote, not RenderPreAllowModel.
    A rework dispatch derives both delegate.model and delegate.failure secondaries, so both share the ledger id.
  failure_scenario: |
    A conductor sends a rework FAILURE-CLUSTER dispatch to an implementation specialist, and the failure repeats.
    The primary allows, and the delegate.model secondary resolves a token.
    Outcome becomes `same-failure`, so the model override is never written and the dispatch runs on the default model.
    This breaks the invariant that a rework dispatch still gets model targeting.
  guidance: [§1, §3]
  suggestion: In the same-failure branch, compute the model block as the Allow branch does, and deliver the note together with ModelToken and ModelBlock. This needs an outcome shape that carries both, or the note can ride beside the model write.

REVIEW.md sections checked: [Ground rules, §1, §2, §3, §5, §6, §7, §13, R2, R4, R5, R8]
```
