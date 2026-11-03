---
name: typescript-dev/data-access
description: Querying SQLite or another store from TypeScript without SELECT *, N+1 loops, or unbounded reads; streaming large files; prepared statements and transactions.
---

# Data Access

The rules are in **<typescript-coding-standard>** § Data access. The examples use `node:sqlite`
(`DatabaseSync`); the shape is the same for any driver.

## Read only the columns you use

```ts
// Before: drags every column, including large JSON payloads, for a count and a name.
const rows = db.prepare('SELECT * FROM sessions WHERE project_id = ?').all(projectId);

// After: the query states what the caller needs.
const rows = db
  .prepare('SELECT id, title, started_at FROM sessions WHERE project_id = ?')
  .all(projectId);
```

A wide column — transcript text, raw JSON, blobs — is fetched by the one query that displays or
parses it, by primary key, not carried along by list queries.

## Remove N+1 loops

A query inside a loop over another query's rows runs once per row. It is invisible on a fixture
of ten rows and dominates on a real history of ten thousand.

```ts
// Before: one query per session.
for (const s of sessions) {
  s.toolCalls = db.prepare('SELECT name FROM tool_calls WHERE session_id = ?').all(s.id);
}

// After: one query for the set, grouped in memory.
function toolCallsBySession(db: DatabaseSync, ids: readonly string[]): Map<string, string[]> {
  const bySession = new Map<string, string[]>();
  if (ids.length === 0) return bySession; // `IN ()` is a syntax error

  const placeholders = ids.map(() => '?').join(', ');
  const calls = db
    .prepare(`SELECT session_id, name FROM tool_calls WHERE session_id IN (${placeholders})`)
    .all(...ids) as { session_id: string; name: string }[]; // row type matches the SELECT list

  for (const c of calls) {
    const list = bySession.get(c.session_id) ?? [];
    list.push(c.name);
    bySession.set(c.session_id, list);
  }
  return bySession;
}
```

The placeholder list is built from `?` marks only; the values stay bound. The early return
matters: with no ids the statement would be `IN ()`, which SQLite rejects. SQLite caps bound
parameters per statement (32,766 by default on current builds), so chunk very large id sets,
or use a join or a temporary table instead. A `JOIN` with a `GROUP BY` is often simpler still.

## Bound every read

Ask of each query: *what happens when this table has a year of history?*

- List views take `LIMIT ? OFFSET ?` or, better, a keyset cursor (`WHERE started_at < ? ORDER BY
  started_at DESC LIMIT ?`).
- Analysis takes a time window from the caller rather than scanning everything.
- `.all()` on an unbounded query is the defect; `.iterate()` streams rows when the caller
  genuinely needs all of them.

Files are the same. `readFile` on a log that grows without limit loads it whole. Stream it:

```ts
import { createReadStream } from 'node:fs';
import { createInterface } from 'node:readline';

const lines = createInterface({ input: createReadStream(path), crlfDelay: Infinity });
for await (const line of lines) {
  // parse one record at a time
}
```

## Prepare once, write in a transaction

```ts
const insert = db.prepare('INSERT INTO tool_calls (session_id, name, at) VALUES (?, ?, ?)');

db.exec('BEGIN');
try {
  for (const c of calls) insert.run(c.sessionId, c.name, c.at);
  db.exec('COMMIT');
} catch (err) {
  db.exec('ROLLBACK');
  throw err;
}
```

Preparing inside the loop re-parses the SQL every iteration; committing per row multiplies
fsyncs.

## Parameters and identifiers

Values are always bound. An identifier that has to vary — a sort column, a table chosen by
mode — is looked up in a fixed allow-list and never taken from input:

```ts
const SORT_COLUMNS = { started: 'started_at', cost: 'cost_usd' } as const;
const column = SORT_COLUMNS[sortKey]; // sortKey: keyof typeof SORT_COLUMNS
```

## Absent values in rows

A nullable column maps to `T | null` in the row type. Do not `COALESCE(x, 0)` a measured
quantity in SQL any more than `?? 0` in TypeScript — see
[Types and Absent Values](./types-and-absent-values.md).
