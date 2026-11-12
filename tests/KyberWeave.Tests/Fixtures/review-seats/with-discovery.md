```yaml
- id: security/unchecked-redirect-target
  lens: security
  severity: major
  confidence: 8
  file: src/KyberWeave.Core/Routing/RedirectTarget.cs
  line: 14
  excerpt: |
    return Redirect(returnUrl);
  claim: The redirect target is taken from the query string without a same-site check.
  evidence: |
    src/KyberWeave.Core/Routing/RedirectTarget.cs:14 passes the raw query value to Redirect.
  failure_scenario: |
    A crafted link sends a signed-in user to an attacker-controlled host after login.
  guidance: [§4]
  suggestion: Accept only local paths, or compare the target host with the allowed list.

- id: correctness/empty-page-size
  lens: correctness
  severity: minor
  confidence: 7
  file: src/KyberWeave.Core/Paging/PageRequest.cs
  line: 9
  excerpt: |
    int take = size;
  claim: A page size of zero is accepted and returns an empty page without an error.
  evidence: |
    src/KyberWeave.Core/Paging/PageRequest.cs:9 has no lower bound on size.
  failure_scenario: |
    A client that sends size=0 receives an empty list and cannot tell that the request was invalid.
  guidance: [§3]
  suggestion: Reject a size below one with a validation error.
Index fallback: projectPath: not-indexed
Discovery: codegraph 0, docs 0, direct reads 6
REVIEW.md sections checked: [Ground rules, §3, §4]
