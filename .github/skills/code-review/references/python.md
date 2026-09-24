# Python Code Review Best Practices

## PEP 8 Style & Readability
- **Conventions:** Python's PEP 8 is the canonical style guide. Check for indentation (4 spaces, no tabs), line length (≤ 79 or 88 chars), meaningful naming (snake_case variables, PascalCase classes), and proper import grouping.
- **Dead Code:** Remove dead code or debug statements (e.g., stray `print`). Rely on linting and formatting tools (Flake8, Pylint, Black, isort) in CI.

## Correctness & Logic
- **Expected Functionality:** Ensure the code actually implements the intended functionality and unit tests confirm the logic.
- **Edge Cases:** Verify edge cases are handled (especially around null/`None` values, error conditions).
- **Mutable Defaults:** Watch out for common bugs like mutable default arguments in functions (e.g. `def foo(x=[])`).
- **Data Structures:** Check for proper usage of data structures (e.g., using a set instead of a list for lookups).

## Error Handling
- **Specific Exceptions:** No bare `except:` clauses. Always catch specific exceptions or use `except Exception as e:`.
- **Logging Exceptions:** Use logging (with stack traces via `logger.exception`) for unexpected errors rather than silent pass.

## Security
- **Injection & Eval:** Ensure untrusted inputs are sanitized. Avoid `eval()` or unsafe deserialization (e.g., use `yaml.safe_load` over `pickle.loads` for untrusted data).
- **No Hard-Coded Secrets:** Confirm there are no credentials or API keys in the code. Inject via secure config (environment variables).
- **Secure Libraries:** Avoid insecure functions (e.g., `subprocess` with `shell=True` and unsanitized input).

## Performance & Efficiency
- **Loops & Queries:** Flag inefficient operations (nested loops over large data, repeated database queries in loops). Consider vectorization or comprehensions.
- **Memory Use:** For large datasets, verify the code uses generators or streaming to avoid memory blowups.
- **Concurrency:** Ensure concurrency primitives (`threading` or `asyncio`) are used correctly. Check that asynchronous methods yield control properly (use `await`, avoid blocking sync code).

## Maintainability & "Pythonic" Code
- **Clarity > Cleverness:** Code should favor readability. Avoid needlessly complex "one-liners".
- **DRY & Modularity:** Refactor duplicate code into functions. Watch for high cyclomatic complexity and long functions.
- **Type Hints:** Ensure new code includes appropriate type annotations (PEP 484).
- **Docstrings:** Verify that docstrings (PEP 257) are present for public functions/classes.

## Testing
- **Test Coverage:** Confirm the presence and adequacy of unit/integration tests for new code. Ensure tests cover corner cases and can run reliably in any order. Coverage on new code must meet the coverage floor the host declares under `review.coverage` in its Kyber-Weave configuration and `kyber-weave review gates` reports.
