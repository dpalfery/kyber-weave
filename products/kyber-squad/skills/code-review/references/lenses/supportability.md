# Lens: supportability

## Applicability

Applies when the diff adds or changes an error path, a catch or recovery block, a log or
trace emission, a background process, or an externally reachable failure surface.

Skip when the change has no failure mode anyone would need to diagnose.

## What this lens owns

Whether a failure in this code can be diagnosed after the fact by someone who was not there
when it happened, and whether the failure surface tells the caller the truth without telling
an attacker anything.

## What to look for

**Failures that leave no trace.** A caught exception that is swallowed. A failure path that
returns a default and logs nothing. A retry that exhausts silently. If this code fails at
three in the morning, what exists in the record to say it did? "Nothing" is a finding, and it
is usually a `major` one.

**Logs without correlation.** Structured events carrying enough identity to reconstruct one
request or one operation across components. A message with no correlating identifier is
unjoinable to anything else and is close to useless during an incident.

**Logs that carry what must not be logged.** Credentials, tokens, keys, personal data, full
request bodies, entire exception objects containing connection strings. Logging a
high-value secret in plaintext is a real finding; logging a URL is not.

**Context that is absent when it matters.** An error recording that something failed but not
which record, which tenant, which operation, or which input class. The message is the whole
value of the log line; a message that could describe a thousand different failures describes
none of them.

**Internals leaking to the caller.** Stack traces, framework exception text, database error
messages, or internal identifiers returned in a response. The caller needs to know that it
failed and what to do; it does not need the shape of the system.

**Severity that misrepresents.** Expected conditions logged as errors train responders to
ignore errors. Genuine failures logged at debug level hide them. Both are findings, and the
first is the more damaging.

**Unstructured emission in a structured system.** Interpolating values into a message string
where the logging system expects structured fields — the values become unqueryable at exactly
the moment someone needs to query them.

## What this lens must not report

- Log-line wording or house style.
- A request for more logging with no failure mode to justify it. Every log line is a cost;
  name the diagnosis it enables.
- Missing audit logs as a security finding — the security lens explicitly excludes those.
- Choice of logging or telemetry library.
- Whether the error handling is *correct* — the correctness lens owns that. You own whether
  the failure is diagnosable.
