---
name: test-dev/rust-go-test-patterns
description: Rust (cargo test, cargo nextest) and Go (go test) test patterns — placement, table-driven cases, fakes behind traits and interfaces, async and race detection.
---

# Rust and Go Test Patterns

Runners, naming, assertion libraries, and coverage policy are in **<test-coding-standard>**
§ Rust and § Go. Language-level rules come from **<rust-coding-standard>** and
**<go-coding-standard>**. This reference is how to apply them.

## Rust

### Placement

| Kind | Where | Sees |
|---|---|---|
| Unit | `#[cfg(test)] mod tests { use super::*; … }` at the bottom of the file under test | Private items |
| Integration | `tests/<area>.rs` in the crate root | Public API only |
| Doc | ` ```rust ` blocks in `///` comments | Public API; run by `cargo test --doc` |

A binary crate exposes its logic from `src/lib.rs` so integration tests can reach it; `main.rs`
stays a thin shell.

### Table-driven cases

```rust
#[test]
fn parse_port_rejects_out_of_range_values() {
    let cases = [("zero", "0"), ("too large", "70000"), ("not a number", "http")];
    for (name, input) in cases {
        let err = parse_port(input).expect_err(name);
        assert!(matches!(err, ConfigError::InvalidPort { .. }), "{name}: got {err:?}");
    }
}
```

Assert on the error **variant**, not its `Display` string.

### Fakes through traits

```rust
trait Clock { fn now(&self) -> SystemTime; }
struct FixedClock(SystemTime);
impl Clock for FixedClock { fn now(&self) -> SystemTime { self.0 } }
```

Production code takes `&dyn Clock` (or a generic `C: Clock`). If it constructs its own
dependency, report that to `rust-dev` as untestable rather than reaching for a global.

### Async and time

```rust
#[tokio::test(start_paused = true)]
async fn retry_waits_between_attempts() {
    // tokio::time::advance moves the clock; no real sleep happens
}
```

### Running

```bash
cargo nextest run --workspace --locked      # unit + integration
cargo test --doc --workspace --locked       # doctests (nextest skips these)
cargo nextest run -E 'test(parse_port)'     # one test by filter
```

## Go

### Table-driven subtests

```go
func TestParsePort(t *testing.T) {
    tests := []struct {
        name    string
        in      string
        want    int
        wantErr error
    }{
        {name: "valid", in: "8080", want: 8080},
        {name: "zero", in: "0", wantErr: ErrInvalidPort},
        {name: "not a number", in: "http", wantErr: ErrInvalidPort},
    }
    for _, tc := range tests {
        t.Run(tc.name, func(t *testing.T) {
            t.Parallel()
            got, err := ParsePort(tc.in)
            if !errors.Is(err, tc.wantErr) {
                t.Fatalf("ParsePort(%q) error = %v, want %v", tc.in, err, tc.wantErr)
            }
            if got != tc.want {
                t.Errorf("ParsePort(%q) = %d, want %d", tc.in, got, tc.want)
            }
        })
    }
}
```

- Failure messages read `Func(input) = got, want want`.
- `t.Fatalf` stops the subtest when continuing is meaningless; `t.Errorf` records and continues.
- On a `go` directive below 1.22, copy `tc := tc` before `t.Parallel()`.

### Fakes through interfaces

Define the smallest interface the code under test needs, in the consumer's package, and pass a
hand-written fake. Use `httptest.NewServer` for HTTP dependencies and `t.TempDir()` for files —
never a fixed path another parallel test could share.

### Race detector and leaks

- Run `go test -race ./...`. A test that only fails under `-race` is reporting a real bug in the
  code under test — hand it to `go-dev`, do not weaken the test.
- A test that starts goroutines cancels their context in `t.Cleanup` and waits for them, so a
  leak fails the test instead of the next one.

### Running

```bash
go test -race ./...
go test -race -run 'TestParsePort/zero' ./internal/config   # one subtest
go test -count=1 ./...                                      # bypass the test cache
```
