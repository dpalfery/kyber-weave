---
name: go-dev/errors-and-context
description: Go error wrapping with %w, errors.Is and errors.As, sentinel and typed errors, and context.Context propagation and timeouts.
---

# Errors and Context

Policy — which error patterns the repository uses and how errors are logged — is in
**<go-coding-standard>**. This reference is how to apply it.

## Wrap, do not replace

```go
cfg, err := loadConfig(path)
if err != nil {
    return fmt.Errorf("load config %s: %w", path, err)
}
```

- `%w` keeps the chain so callers can use `errors.Is` / `errors.As`. `%v` severs it — use it
  only when you deliberately hide an implementation detail at an API boundary.
- The message adds what this frame knows (the operation and its key input), lowercase, no
  trailing punctuation, no "failed to" prefix repeated at every level.
- Handle an error once: either return it or log it, never both.

## Errors callers branch on

| Need | Shape |
|---|---|
| A fixed condition (`not found`, `closed`) | Exported sentinel: `var ErrNotFound = errors.New("not found")`, checked with `errors.Is` |
| A condition carrying data | Exported type implementing `error`, checked with `errors.As` |
| Nothing branches on it | A wrapped `fmt.Errorf` — do not export a type nobody inspects |

Never compare `err.Error()` strings.

## Panics

`panic` is for programmer errors that cannot be recovered (an impossible switch arm, a nil
dependency at construction). Library code does not panic on bad input; it returns an error.
`log.Fatal` belongs only in `main`.

## Context

- `ctx context.Context` is the **first** parameter of any function that does I/O, blocks, or
  calls something that does. Name it `ctx`.
- Pass the caller's context down. `context.Background()` appears in `main`, in tests, and at
  the root of a background job — nowhere else. `context.TODO()` is a marker to remove, not to
  ship.
- Never store a context in a struct; pass it per call.
- A timeout is set where the budget is known: `ctx, cancel := context.WithTimeout(ctx, d)`,
  then `defer cancel()` on the next line, always.
- Check `ctx.Err()` / `<-ctx.Done()` in long loops so cancellation is prompt.
- Context values carry request-scoped data (trace ids), never optional parameters.
