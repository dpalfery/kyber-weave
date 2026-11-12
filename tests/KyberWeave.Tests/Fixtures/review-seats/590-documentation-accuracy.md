```yaml
- id: documentation-accuracy/eligibility-omits-documentation-profile
  severity: minor
  confidence: 8
  file: docs/kyber-arbiter/architecture.md
  line: 159
  excerpt: |
    **Eligibility.** Only an implementation-class target reaches `delegate.model`: the `worker` and
    `publishing-worker` capability profiles, from `ArbiterSquadCatalog`.
  evidence: |
    src/KyberWeave.Core/Arbiter/Squad/ArbiterSquadCatalog.cs:83 maps
    `"worker" or "publishing-worker" or "documentation" => "implementation"`.
    SecondaryTriggers.cs:31-35 attaches `delegate.model` to every implementation-class target.
  failure_scenario: |
    A reader believes a dispatch to a `documentation`-profile agent is never retargeted.
    It is: the profile classifies as implementation and gets the secondary `delegate.model`
    evaluation. Under `enforce`, an unexpected model gets written onto it.
  guidance: [§11]

- id: documentation-accuracy/stale-21-rules-doc-comment
  severity: minor
  confidence: 8
  file: src/KyberWeave.Core/Arbiter/ArbiterConfig.cs
  line: 126
  excerpt: |
    /// Product defaults for an unconfigured host: disabled, no provider, all 21
    /// shipped rules enabled.
  evidence: |
    This diff raises the shipped count to 23. The docs it updates say 23: configuration.md:503,
    architecture.md, ADR 0053, and the 23 `- id: KW-ARB-` entries in default-rules.yml.
    The doc comment sits on `ProductDefaults`, which loads that same file, and was left at 21.
    This is the reverse-pass miss from the R16 count rule.
  failure_scenario: |
    A developer reading `ProductDefaults` is told 21 rules are enabled by default.
    The file now contains 23, so the comment contradicts the docs this change edited.
  guidance: [§11, R16]
```

REVIEW.md sections checked: [Ground rules, §11, R16, Scope discipline]
