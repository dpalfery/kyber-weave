---
id: standards/python
title: python coding standard
doc-type: coding-standard
status: draft
technology: python
owner: unassigned
last-reviewed: 2026-08-16
---

# python coding standard

How Python is written in this repository. Agents and skills resolve this document as
`<python-coding-standard>`, so it outranks the defaults a portable agent shipped with.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`.

## Style

PEP 8, enforced by the formatter and linter rather than by review. The two settings worth
stating because tools disagree about them:

- **Line length is 88** — the Black and Ruff default. PEP 8's 79 is also defensible; pick one
  in `pyproject.toml` and let the formatter apply it.
- **Imports are sorted and grouped** by isort or Ruff: standard library, third party, local.

snake_case for functions and variables, PascalCase for classes, SCREAMING_SNAKE_CASE for
constants.

## Types and documentation

- **Public functions are annotated.** Use built-in generics and `X | None` (PEP 604), not
  `typing.List` and `Optional`.
- **Docstrings on public API**, in one style across the repository — Google or NumPy, chosen
  once. Document what a reader cannot infer from the signature: what raises, what is mutated,
  what the units are.
- A type checker in CI is worth more than either.

## Correctness

- **Never a mutable default argument.** `def f(x: list | None = None)` and build inside.
- **Prefer the right structure** — a set for membership, a dict for lookup, a generator for a
  stream that does not fit in memory.
- **`is` compares identity, `==` compares value.** `is` against a literal is a bug waiting for
  a Python version to change.

## Errors

- **No bare `except:`.** Catch the exception you can name and handle.
- **Log with `logger.exception`** inside a handler, so the traceback survives. A silent
  `pass` in an `except` block is a decision to hide a failure — if that is genuinely intended,
  the comment must say so.
- Let unexpected exceptions propagate. Wrapping everything in a top-level catch turns a
  diagnosable crash into a mystery.

## Safety

- No `eval`, no `exec`, and no `pickle` on data you did not produce. `yaml.safe_load`, never
  `yaml.load`.
- `subprocess` takes an argument list; `shell=True` with interpolated input is a shell
  injection.
- Secrets come from the environment or a secret store, never a literal and never a committed
  config file.

## Structure

- `pyproject.toml` (PEP 621) is the single source for metadata and tool configuration.
- Dependencies are pinned for applications and ranged for libraries.
- A function that needs a comment to explain its middle is two functions.

## Tests

pytest, with tests that run in any order and do not depend on each other. New behaviour ships
with the test that would fail without it, and edge cases — empty, `None`, boundary — are the
ones worth writing down.
