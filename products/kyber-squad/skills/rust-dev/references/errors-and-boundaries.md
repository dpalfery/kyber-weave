---
name: rust-dev/errors-and-boundaries
description: Rust error types and crate boundaries — thiserror for library errors, anyhow at the application edge, no unwrap in non-test code.
---

# Errors and Crate Boundaries

Policy — which error crates the repository takes and where the boundary sits — is in
**<rust-coding-standard>**. This reference is how to apply it.

## Two kinds of error

| Where | Shape | Why |
|---|---|---|
| A library crate, or a module other code matches on | A typed `enum` deriving `thiserror::Error` | Callers can branch on the variant; the type is part of the API |
| A binary's `main`, a CLI command handler, a top-level task | `anyhow::Result<T>` with `.context(...)` | Nothing matches on it; it only needs a readable chain for the operator |

Convert at the boundary, not in the middle: a library function returns its own error type, and
the binary adds context as it propagates.

```rust
#[derive(Debug, thiserror::Error)]
pub enum ConfigError {
    #[error("config file {path} not found")]
    NotFound { path: PathBuf },
    #[error("config file {path} is not valid TOML")]
    Parse { path: PathBuf, #[source] source: toml::de::Error },
}

// binary edge
fn main() -> anyhow::Result<()> {
    let cfg = load_config(&path).with_context(|| format!("loading {}", path.display()))?;
    run(cfg)
}
```

- Keep `#[source]` (or `#[from]`) on wrapped errors so the chain survives; do not flatten an
  error into a `String`.
- Do not expose `anyhow::Error` from a library's public API.
- An error message is lowercase, has no trailing period, and names the thing that failed.

## No `unwrap` outside tests

`unwrap()` and `expect()` are a panic in production. In non-test code:

- Propagate with `?`.
- When a value is provably present, prefer restructuring so the type says so (`let Some(x) = …
  else { return Err(…) };`) over asserting it.
- Where the standard allows `expect` for a true invariant, the message states the invariant
  (`expect("regex literal is valid")`), not the symptom.

Enforce it rather than remembering it: the standard's clippy configuration denies
`clippy::unwrap_used` and `clippy::expect_used` outside `#[cfg(test)]` when it adopts the rule.

## Public API

- Make types and functions `pub` only when another crate needs them; `pub(crate)` otherwise.
- Take borrowed inputs (`&str`, `&Path`, `impl AsRef<Path>`) and return owned outputs.
- Accept dependencies (clients, clocks, writers) as parameters or trait objects so `test-dev`
  can substitute them.
- Mark enums other crates match on `#[non_exhaustive]` when you expect to add variants.
