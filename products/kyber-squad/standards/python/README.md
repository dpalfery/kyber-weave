---
id: standards/python
title: "Python coding standard"
doc-type: coding-standard
status: draft
technology: python
owner: unassigned
last-reviewed: 2026-08-16
---

# Python coding standard

How Python is written in this repository. Agents and skills resolve this document as
`<python-coding-standard>`.

## Authority & status

When this standard is in `status: current`, what it says here outranks whatever defaults a
portable agent shipped with. While in `status: draft`, it serves as a non-authoritative
template/proposal and does NOT override portable agent defaults until reviewed and promoted
to `current`.

> Template. Set `owner` to a row in `catalog.md`, review the decisions below, and promote
> `status` to `current`. Every choice here is a guess about a repository this template has
> never seen — reversing one is the point of the standard being project-specific.

## Stack

- **Language:** Python 3.12 or later. New modules target the version pinned in
  `.python-version` / `requires-python` in `pyproject.toml` — those two must agree.
- **Packaging:** `pyproject.toml` (PEP 621) is the single source for metadata and tool
  configuration. Do not add a second `setup.py` or a competing `setup.cfg`.
- **Dependencies:** applications pin exact versions; libraries use compatible ranges.
  Split optional extras rather than one undifferentiated requirements file: runtime,
  `dev`, and `prod` where those environments actually differ.
- **Environments:** one virtual environment per repository (`venv`), created from the
  pinned interpreter. Conda is acceptable only when the host already uses it; do not
  introduce a second environment manager beside an existing `venv`.

Application code consumes collaborators through constructors or function parameters. It
does not create clients, connections, or clocks internally, and it does not read a
global singleton for something a caller could pass.

## The formatter is the first reviewer

Ruff (or Black plus isort) and the type checker are authoritative for formatting and
mechanical rules. Do not restate those rules here, and do not merge code that fails them.

The two settings worth stating because tools disagree about them:

- **Line length is 88** — the Black and Ruff default. PEP 8's 79 is also defensible; pick
  one in `pyproject.toml` and let the formatter apply it.
- **Imports are sorted and grouped** by Ruff or isort: standard library, third party,
  local. Prefer `from X import Y` for names used more than once; do not hide a module
  behind a star import.

snake_case for functions and variables, PascalCase for classes, SCREAMING_SNAKE_CASE for
constants.

## Types and documentation

- **Public functions are annotated.** Use built-in generics and `X | None` (PEP 604), not
  `typing.List` and `Optional`. New module-level names are annotated too; a bare `x = []`
  at module scope is not a type.
- **Docstrings on public API**, Google style, including Args, Returns, and Raises when
  those are not obvious from the signature. Document what a reader cannot infer: what
  raises, what is mutated, what the units are. NumPy style is the defensible alternative;
  pick one repository-wide.
- A type checker in CI (`mypy --strict`, or Ruff's type rules at the equivalent strictness)
  is worth more than either. Public-API type coverage is 100%; a warning is a failed check.

## Shape

- **One clear purpose per function or class.** A function that needs a comment to explain
  its middle is two functions.
- **Explicit over implicit.** Clear code beats cleverness. A one-liner that requires a
  pause to parse is not Pythonic; it is compressed.
- **Early returns.** Guard clauses reduce nesting. Do not wrap the real work in `if ok:`.
- **Inject dependencies.** Pass clients, clocks, and configuration in. A function that
  constructs its own `httpx.Client` or reads a process-global is not testable without
  patching internals.

Prefer a factory function when construction needs configuration. Prefer a context manager
(`@contextmanager` or a class with `__enter__` / `__aenter__`) for anything that must be
closed. A repository abstraction owns data access; a strategy abstraction owns an
algorithm the caller might swap. Do not introduce either pattern where a function would
do.

## Correctness

- **Never a mutable default argument.** `def f(x: list | None = None)` and build inside.
- **Prefer the right structure** — a set for membership, a dict for lookup, a generator
  for a stream that does not fit in memory.
- **`is` compares identity, `==` compares value.** `is` against a literal is a bug waiting
  for a Python version to change.
- Concurrent I/O uses `asyncio.gather` (or `TaskGroup`) rather than a sequential loop of
  awaits. Do not mix blocking I/O into an async function.

## Errors

- **No bare `except:`.** Catch the exception you can name and handle.
- **Log with `logger.exception`** inside a handler, so the traceback survives. A silent
  `pass` in an `except` block is a decision to hide a failure — if that is genuinely
  intended, the comment must say so.
- Let unexpected exceptions propagate. Wrapping everything in a top-level catch turns a
  diagnosable crash into a mystery.
- Production services log structured events (not `print`), expose a health check for the
  dependencies they need, and fail closed rather than returning a guessed value.

## Safety

- No `eval`, no `exec`, and no `pickle` on data you did not produce. `yaml.safe_load`,
  never `yaml.load`.
- `subprocess` takes an argument list; `shell=True` with interpolated input is a shell
  injection.
- Secrets come from the environment or a secret store, never a literal and never a
  committed config file. Do not log them.
- Database queries are parameterized. String concatenation into SQL is an injection.
- Validate and sanitize untrusted input at the boundary; do not trust a return value you
  have not typed.

## Images

`github-devops` owns Dockerfiles. When this service is containerized, the image is
multi-stage, runs as a non-root user, and puts the apt (or equivalent) layer before the
application layer so dependency changes do not bust the runtime cache. Say so when handing
the image need off; do not write the Dockerfile here.

## Tests

Test authorship follows `<test-coding-standard>`. This standard requires that the code
be testable: dependency injection, mockable interfaces, no global state. Test
authorship belongs to `test-dev`.

## Commands

```bash
python -m venv .venv
.venv/bin/python -m pip install -e ".[dev]"
ruff check . && ruff format --check .
mypy --strict
pytest
```
