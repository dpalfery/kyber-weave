# Lens: prior-art

## Applicability

Applies when the diff **adds** a type — a class, record, interface, or a service registration
that introduces one. Types the diff merely edits are out of scope: the question this lens asks
is whether a new thing should exist, and that is only askable about a new thing.

Skip when the diff adds no types. Say so, because "this change introduced nothing new to place"
is a different statement from "the new types are fine".

## What this lens owns

Whether the repository already contains something that does this job — and if it does, whether
the diff duplicates it or diverges from it.

Every other seat asks whether the code in front of it is correct. This one asks whether it
needed to be written. No other lens looks outside the diff for an answer: `di-composition`
reads the class, `model-placement` reads the standard, and both stop at the module boundary. A
new type that reimplements something three directories away passes both of them cleanly, and
will keep doing so however carefully they read.

`dependency-supply-chain` holds exactly this concern for *packages* — "a dependency that
duplicates something already present in the tree". You hold it for code.

## What to look for

**A type that re-implements an existing type.** A new validator, loader, renderer, store,
parser, or client alongside a family of them that already exists. Find the existing one and
read it. If both do the same job, the finding is duplication, and the failure is that the two
will disagree the first time only one of them is changed.

**A type that diverges from an existing idiom.** The repository already solves this class of
problem, and the new type solves it a different way — a second registration style, a second
error-reporting shape, a second way of loading the same kind of file. Two idioms for one job
is a real cost, but it is reportable only when you can name and quote the existing idiom.
Without that, you are reporting a preference.

**A family the new type declined to join.** A new type sitting beside three siblings that share
a base type or interface, implementing none of it. Either it belongs to the family and should
join it, or it does not and the resemblance is coincidental — establish which by reading the
base type, not by counting how similar the names look.

**Speculative generality.** An interface with one implementation and one caller. An abstract
base introduced together with its only derived type. A generic parameter instantiated at
exactly one type. A configuration knob with one setting and no reader. This is the YAGNI half
of the lens, and it is the half most likely to produce a finding the adjudicator deletes —
see the evidence rule below before reporting any of it.

**Search before concluding.** Recall here is a procedure, not an instinct. In order: query
CodeGraph for the new type's name stem and for each of its collaborators; read what comes back
rather than skimming the names; then search the tree for the naming family the new type joins
(`*Validator`, `*Loader`, `*Store`). A "no prior art" conclusion you reached without running
these is a guess, and `NO FINDINGS` is supposed to name what you checked.

## The evidence rule this lens lives or dies by

`failure_scenario` is never "this is not DRY" and never "this abstraction is unnecessary".
Those are opinions, and the adjudicating reviewer drops findings that carry them.

**The failure scenario is the second edit site.** Name it concretely:

> A change to the tenant-scoping rule at `TenantFilter.cs:40` must also be made at
> `ScopedQueryBuilder.cs:120`; nothing links the two, and the tests at
> `TenantFilterTests.cs` cover only the first.

For speculative generality the same rule applies, and the site is the indirection itself: every
future change to the one implementation has to be made through an interface that exists for no
second caller, and every reader has to establish there is no second caller before they can
follow the code.

If you cannot write the failure in that form, you do not have a finding.

## What this lens must not report

- Any finding whose existing counterpart you cannot quote by path and line. Without the other
  side you are producing taste, and this lens has the least standing of any seat to do that.
- Preference between two acceptable idioms that share no behaviour.
- A second implementation whose reason is stated in the change description or in a governing
  plan. Deliberate divergence is a decision that was already made; silent divergence is the
  finding.
- Pre-existing duplication between two types the diff merely touches.
- Test doubles, fakes, fixtures, and builders. They duplicate production shapes on purpose.
- Generated code, and types whose shape is dictated by a framework, a serialization contract,
  or an external schema.
- Duplicated *method bodies* — `duplicate-implementation` owns those, works from a gate
  artifact rather than from reading, and will report them with better evidence than you can.
  Your unit is the type.
- Which shared abstraction the author should extract instead. The finding is that two things do
  one job; the resolution is theirs.
