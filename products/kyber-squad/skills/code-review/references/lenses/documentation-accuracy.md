# Lens: documentation-accuracy

## Applicability

Applies when the diff changes any prose that describes the code: documents under `docs/`,
READMEs, plans and specifications (live or archived), `AGENTS.md`, `REVIEW.md`, skill and agent
prose, doc comments and XML summaries, and test or fixture headers that state a count, a
guarantee, or a procedure. Also applies when the diff changes code that such prose is known to
describe, even if the prose itself is untouched.

Skip when the diff changes neither prose nor code that any prose describes.

## What this lens owns

Whether every factual statement in the changed documentation is **true of the code as it
ships in this diff**. The reference is the code, never the pull-request description and never
another document: a document that agrees with a second document that is also wrong is still
wrong.

## How to work

Do not read the prose for sense; verify it. For each changed document or doc comment:

1. List its checkable claims: a number, a size, a list of files or steps, a name, a path, a
   flag, a default, an ordering, an absolute ("once", "only", "never", "always", "all", "no
   other"), a described side effect (what is committed, copied, excluded, written).
2. Open the code that implements each claim and compare. Quote both sides in the finding.
3. Run the reverse pass: for each behaviour, name, count or default the diff **changes** in
   code, search the documentation for the old value and for descriptions of it. A document the
   diff did not touch can be the one that is now false.

## What to look for

**Counts and sizes.** "~8 files", "15 lenses", "203-file corpus", "3 skills". Count what the
code actually builds or declares and compare. Fixture and synthetic-data headers are
documents too: a header that says one number beside a table that says another is a finding
even before the code is opened.

**Enumerations.** A list of files created, steps run, agents or skills included, arguments
accepted. Check membership both ways: an item listed that the code does not produce, and an
item the code produces that is not listed.

**Described procedure versus real procedure.** What is created, committed, copied, excluded,
or run, and in what order. "Creates a committed repository once" when the code builds several
keyed templates; "copies the `.git` directory" when it copies the working tree; "untracked
files are ignored" when one variant tracks them. Where code has more than one variant, the prose
must say which variant it describes or cover each.

**Absolutes.** Every "once", "only", "always", "never" and "all" is a claim that no counter
example exists. Search for one.

**Plan and specification fidelity.** A plan or spec that states a minimal set, a design, or
acceptance steps must match what shipped, or be marked as superseded with the reason. This
applies to archived plans: they may be corrected to match reality ("corrected, not
rewritten"), and a stale archived plan that a live document links to as the source of truth is
a finding.

**Doc comments and summaries.** The comment says what the code does, not what the author meant.
"Keys or paths" when only paths are returned; "thread-safe" or "immutable" or "read-only" when
nothing enforces it (report the claim here, and leave the missing enforcement to
`test-infrastructure` or `correctness`).

**Cross-document agreement.** The same fact stated in two documents (a README row and the
architecture page, a catalogue and a ledger). When one is updated, find the other.

**Links and identifiers.** A relative link, anchor, rule id, or section number that no longer
resolves after a move, rename, or renumbering.

## Evidence required

A finding quotes the sentence and the code (file and line) that contradicts it, and states
the correct value. "This may be out of date" with no counter-evidence is not a finding.

## What this lens must not report

- Prose style, grammar, tone, tense, or formatting. Only a statement the code contradicts.
- Whether the pull-request description matches the diff: `intent-alignment` owns that
  correspondence. This lens owns document-versus-code.
- Whether the right documents were updated for a plan closeout (the Documentation impact
  section, waivers): `intent-alignment` owns that. Report a closeout document's *content* only
  where it misstates the code.
- Missing documentation for undocumented code, unless the change removes or renames something
  a document still describes.
- Defects in the code itself. If the document is right and the code is wrong, say so briefly
  and leave the defect to `correctness`.
