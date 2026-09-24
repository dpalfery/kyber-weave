# Lens: model-placement

## Applicability

Applies when the diff adds or changes a type in a codebase whose layering is declared by a
project coding standard — the path declared as **<csharp-coding-standard>**, or the
corresponding `<technology>-coding-standard` property, in the root `AGENTS.md` registry.

Skip when the diff adds no types, or when the repository declares no standard for the
technology in question. Say which, so the reviewer knows the difference between "clean" and
"no standard to check against".

## What this lens owns

Whether each new or changed type is the kind of thing it is placed as, and sits where that
kind belongs.

**Read the standard.** Do not classify from convention, from folder names, or from memory of
how other projects do it. The repository's declared standard is the authority, and this lens
exists to enforce that specific document rather than a general opinion about architecture.
Where the standard is silent, this lens is silent.

## What to look for

**A data-transfer object placed as a domain type.** A class with public accessors, no
invariants, no behaviour, and no reason to exist beyond carrying values across a boundary is
a transfer object wherever it happens to be filed. Placed in the domain, it teaches every
subsequent contributor that the domain is a bag of properties.

**An entity with no invariant.** A domain type that enforces nothing — constructible into an
invalid state, mutable into one — is a transfer object with a domain type's name. Either the
invariant is missing or the placement is wrong; report which one the standard implies.

**A persistence shape crossing its boundary.** A row, record, or table-mapped type appearing
in a signature above the adapter that owns it. Once a persistence shape is visible to the
domain or the API, the storage schema becomes the public contract by accident.

**Leakage in the other direction.** Domain types with persistence or serialization concerns
attached — storage attributes, framework base types, wire-format annotations — where the
standard separates them.

**Boundary types that are not translated.** A boundary that passes the same object straight
through in both directions is not a boundary; it is a rename.

## What this lens must not report

- Placement in a repository with no declared standard. Absent a standard, this is preference,
  and preference reported as a finding is noise.
- Naming and folder conventions, unless the standard states them.
- Layering opinions the standard does not hold, however widely held elsewhere.
- Pre-existing misplacement of types the diff merely touches.
