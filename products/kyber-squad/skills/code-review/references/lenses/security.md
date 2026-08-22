# Lens: security

## Applicability

Applies when the diff touches anything reachable by untrusted input, anything handling
credentials or secrets, anything performing authentication or cryptography, anything
constructing a query, command, path, or template from a value, or anything changing a
dependency, deployment, or workflow definition.

Skip when the change touches none of that — and name what it touches instead, so the skip
is auditable.

## What this lens owns

Real, exploitable vulnerabilities **newly introduced by this change**.

## Method — do not re-derive it

The `security-review` skill is the reference implementation for this lens and you apply it
rather than restating it. It carries the three-phase analysis, the hard exclusion list, the
established precedents, and the confidence threshold, and those have been tuned against
false positives that would otherwise flood this council.

Two of its rules govern your output and are worth restating because they are what make this
lens trustworthy:

- **Confidence 8 or above, or it does not ship as a finding.** Everything from 4 to 7 is
  investigation, not a report.
- **The hard exclusions are hard.** Denial of service, resource exhaustion, rate limiting,
  missing hardening, theoretical races, outdated third-party libraries, log spoofing,
  missing audit logs, and the rest of that list are not findings here regardless of how
  reasonable they look. They were excluded deliberately.

## Additional emphasis for this council

The council splits security across three seats, and you own the largest one. Two concerns
are explicitly **not yours**, so that they are reported once rather than three times:

- **Authorization, tenancy, and access control** belong to the `authz-tenancy` lens.
- **Dependency and supply-chain risk** belongs to the `dependency-supply-chain` lens.

Everything else is yours: injection in all its forms, deserialization, path traversal,
cryptographic misuse, hardcoded or logged secrets, certificate validation bypass, unsafe
template rendering, and cross-site scripting.

## What this lens must not report

- Anything on the `security-review` skill's hard exclusion list.
- Pre-existing exposure the change merely sits near.
- Defence-in-depth suggestions with no concrete exploit path — the standard is an attack
  you can describe end to end, not a control you would have liked to see.
- Findings in test files only.
