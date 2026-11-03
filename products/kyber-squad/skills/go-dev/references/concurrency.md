---
name: go-dev/concurrency
description: Go goroutine and channel lifecycle — every goroutine has an exit, the sender closes, errgroup for fan-out, graceful shutdown, leak avoidance.
---

# Concurrency

Whether a package may start goroutines at all, and which helpers it takes
(`golang.org/x/sync/errgroup`), are in **<go-coding-standard>**.

## Every goroutine has an exit

Before writing `go f()`, be able to say what makes it return. A goroutine blocked forever on a
channel nobody writes is a leak the race detector will not find.

- Tie it to a `context.Context` and `select` on `ctx.Done()` at every blocking point.
- The function that starts a goroutine is responsible for waiting for it (`sync.WaitGroup`,
  `errgroup.Group`) — do not fire and forget from library code.

## Channels

- The **sender** closes a channel, exactly once, and only when no other sender remains.
  Receivers never close.
- Prefer unbuffered channels; a buffer size is a claim about throughput that needs a reason.
- Return receive-only (`<-chan T`) or accept send-only (`chan<- T`) channel types so the
  compiler enforces direction.
- A `select` with a `default` is a busy loop unless something else blocks.

## Fan-out with errors

```go
g, ctx := errgroup.WithContext(ctx)
g.SetLimit(8)
for _, item := range items {
    item := item // required below Go 1.22; harmless (and removable) from 1.22 on
    g.Go(func() error { return process(ctx, item) })
}
if err := g.Wait(); err != nil {
    return fmt.Errorf("process items: %w", err)
}
```

The first error cancels `ctx` for the rest. Go 1.22+ gives each loop iteration its own
`item`; below 1.22 every closure would share one variable and process the last item, which is
what the `item := item` copy prevents. Check the module's `go` directive before deleting it.

## Shared state

- Protect shared mutable state with a `sync.Mutex` held for the shortest span, or confine it to
  one goroutine and communicate by channel. Pick one per piece of state.
- Do not copy a struct that contains a `sync.Mutex`; `go vet` reports it.
- `sync/atomic` types (`atomic.Int64`) for counters and flags only.

## Graceful shutdown (services and daemons)

```go
ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
defer stop()

srv := &http.Server{Addr: addr, Handler: h}
shutdownErr := make(chan error, 1)
go func() {
    <-ctx.Done()
    // Bound the drain: Shutdown waits for every in-flight connection, so an unbounded
    // context lets one hung client block the process from exiting.
    sctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
    defer cancel()
    shutdownErr <- srv.Shutdown(sctx)
}()
if err := srv.ListenAndServe(); !errors.Is(err, http.ErrServerClosed) {
    return err
}
return <-shutdownErr // wait for the drain to finish before main returns
```

`syscall.SIGTERM` exists on Windows but is never delivered there; `os.Interrupt` covers Ctrl+C
on every platform. A Windows service needs `golang.org/x/sys/windows/svc` if the standard
requires one.
