---
name: rust-dev/cross-compilation
description: Cross-compiling Rust for other target triples, TLS via rustls instead of OpenSSL, pinned git dependencies, and feature flags.
---

# Cross-Compilation and Dependencies

Which targets ship, and which dependencies the repository accepts, are in
**<rust-coding-standard>**. This reference is how to build them.

## Targets

```bash
rustup target add aarch64-apple-darwin x86_64-unknown-linux-gnu x86_64-pc-windows-msvc
cargo build --release --target <triple>
```

- Pure-Rust crates cross-compile with only `rustup target add`. A C dependency is what makes
  cross-compilation hard — prefer a pure-Rust alternative when the standard allows one.
- For Linux targets from macOS or Windows, `cargo zigbuild` or `cross` (Docker) supply the
  linker and sysroot. Use whichever the standard names; do not add the other.
- A fully static Linux binary targets `x86_64-unknown-linux-musl`.
- Release automation is `github-devops`; hand them the list of triples and the build command.

## TLS without OpenSSL

OpenSSL is the most common reason a cross-build fails. Select rustls through features:

```toml
reqwest = { version = "…", default-features = false, features = ["rustls-tls", "json"] }
```

Then confirm OpenSSL is really gone:

```bash
cargo tree -i openssl-sys --target all
```

No output means no crate pulls it back in. Repeat for `native-tls` if the standard bans it.

## Pinned git dependencies

A git dependency without a pin is a moving target.

```toml
some-crate = { git = "https://github.com/org/some-crate", rev = "<full 40-char sha>" }
```

- Pin with `rev` to a full commit SHA. A `branch` moves; a `tag` can be re-pointed.
- Commit `Cargo.lock` for binaries, and build with `--locked`.
- Add a comment naming why the crate comes from git rather than crates.io, and when it can
  move back.

## Feature flags

- Features are additive. A feature that removes behaviour breaks the first crate that enables
  it alongside another dependent.
- Platform-specific dependencies go under `[target.'cfg(windows)'.dependencies]` (or the Unix
  equivalent), not behind a feature the user must remember to set.
