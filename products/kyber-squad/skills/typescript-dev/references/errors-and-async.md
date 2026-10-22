---
name: typescript-dev/errors-and-async
description: Error subclasses with cause, what a catch block may do, floating promises, bounded concurrency, AbortSignal, timers, and graceful shutdown in Node.
---

# Errors and Async

The rules are in **<typescript-coding-standard>** § Errors and async. This is how to meet them.

## Typed errors with a cause

```ts
export class IngestError extends Error {
  constructor(
    message: string,
    readonly path: string,
    options?: { cause?: unknown },
  ) {
    super(message, options);
    this.name = 'IngestError';
  }
}

try {
  await parseFile(path);
} catch (err: unknown) {
  throw new IngestError(`could not ingest ${path}`, path, { cause: err });
}
```

Callers branch with `err instanceof IngestError`, never on `err.message`.

## What a `catch` may do

| Allowed | Not allowed |
|---|---|
| Handle the failure completely (retry, fall back with a *recorded* reason) | `catch {}` |
| Add context and rethrow with `cause` | `catch (e) { console.error(e); return 0; }` |
| Convert to a typed result (`{ ok: false, error }`) the caller must inspect | Log and rethrow the same error |
| Narrow `unknown` before reading fields | `catch (e: any) { e.code }` |

A fallback that hides a failure is an absent value disguised as data: if a reader cannot parse a
file, the result says "not parsed", not "zero sessions".

Reading a Node error code safely:

```ts
function isNodeError(err: unknown): err is NodeJS.ErrnoException {
  return err instanceof Error && 'code' in err;
}

try {
  return await readFile(path, 'utf8');
} catch (err: unknown) {
  if (isNodeError(err) && err.code === 'ENOENT') return null; // absent, and that is the answer
  throw err;
}
```

## No floating promises

```ts
// Before: a rejection becomes an unhandled-rejection crash, or vanishes.
refreshCache();

// After: awaited, or deliberately detached with a handler.
await refreshCache();
// or
refreshCache().catch((err: unknown) => log.error({ err }, 'background refresh failed'));
```

`@typescript-eslint/no-floating-promises` and `no-misused-promises` find these mechanically;
keep them on.

## Concurrency

Independent work runs together; dependent work runs in order.

```ts
const [sessions, prices] = await Promise.all([loadSessions(db), loadPrices()]);
```

Use `Promise.allSettled` when one failure must not discard the others' results — and then
report each rejected entry instead of dropping it. Over an unbounded set, cap the fan-out:

```ts
async function mapLimit<T, R>(items: readonly T[], limit: number, fn: (item: T) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length);
  const queue = items.entries(); // one iterator shared by every worker
  async function worker(): Promise<void> {
    for (const [i, item] of queue) {
      results[i] = await fn(item);
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, () => worker()));
  return results;
}
```

Sharing one iterator is safe because JavaScript runs one worker at a time between awaits, so
no two workers take the same item.

## Cancellation, timers, and shutdown

Long-running work takes an `AbortSignal` and passes it down to `fetch`, streams, and
`node:timers/promises`:

```ts
import { setTimeout as sleep } from 'node:timers/promises';

export async function poll(signal: AbortSignal): Promise<void> {
  while (!signal.aborted) {
    await tick();
    await sleep(30_000, undefined, { signal }).catch(() => undefined); // abort ends the wait
  }
}
```

A module that starts an interval or a listener returns a way to stop it. A server or daemon has
one shutdown path:

```ts
const controller = new AbortController();
const work = poll(controller.signal);

async function shutdown(): Promise<void> {
  controller.abort();
  const timeout = new Promise<void>((resolve) => setTimeout(resolve, 5_000).unref());
  await Promise.race([work, timeout]); // drain, bounded
  server.close();
}

process.once('SIGINT', () => void shutdown().finally(() => process.exit(0)));
process.once('SIGTERM', () => void shutdown().finally(() => process.exit(0)));
```

`process.exit` lives only in the entry point; libraries return or throw.
