# Lens: blast-radius-revertibility

## Applicability

Applies when the diff changes a published contract, a stored data shape, a persisted schema,
a configuration default, a feature flag, or anything consumed outside the module that
defines it.

Skip for changes wholly contained within one module with no external consumer and no
persisted effect.

## What this lens owns

Two questions no other lens asks: **what else does this reach**, and **can it be undone**.

Revertibility is the more important of the two and the more often missed. Code that is wrong
but revertible is a bad hour. Code that is wrong and irreversible is an incident.

## What to look for

**Changes that cannot be reverted by reverting the commit.** A migration that drops or
transforms data. A message or event whose shape consumers have already persisted. A written
file, cache entry, or external record that survives a rollback. For each, ask what a revert
would actually do: often the code goes back and the data does not.

**Deployment ordering.** Does this require a specific order between schema and code, between
services, between producer and consumer? An unstated ordering requirement is a deployment
that works in one environment and breaks in the next. Backward-compatible in both directions
is what makes ordering irrelevant — check whether it holds.

**Contract changes.** A removed or renamed field, a narrowed type, a new required parameter,
a changed default, a stricter validation rule. Find the consumers. A change that is source
compatible can still be wire incompatible, and a change that is wire compatible can still be
semantically breaking.

**Silent behaviour changes.** A changed default value, a changed sort order, a changed
rounding or time-zone treatment, a changed error type. These pass every test written against
the new behaviour and break every caller that assumed the old one.

**Reach.** For each modified public symbol, establish who calls it. A change to something
with one caller and a change to something with two hundred are different changes wearing the
same diff size.

**Guarded rollout.** For risky behaviour changes, is there a flag, a staged path, or a way to
disable it without a deployment? Its absence is not automatically a finding — its absence on
something irreversible is.

## What this lens must not report

- Diff size as risk. A large mechanical change can be trivially revertible; a twelve-line
  migration may not be.
- Internal refactoring with no external consumer.
- Correctness of the change itself — the correctness lens owns that. You own what happens if
  it is wrong.
- Speculative future consumers.
