---
name: typescript-dev/types-and-absent-values
description: Narrowing unknown input, discriminated unions with exhaustiveness checks, removing any, non-null assertions, and casts, and keeping unmeasured values absent instead of coercing them to zero.
---

# Types and Absent Values

The rules are in **<typescript-coding-standard>** § Types and § Absent is not zero. This is how
to meet them.

## Narrow at the boundary

Anything from outside the process is `unknown` until a guard proves its shape. Narrow once, at
the edge, and pass the typed value inward.

```ts
interface SessionRow {
  id: string;
  startedAt: string;
  tokens: number | null;
}

function isSessionRow(value: unknown): value is SessionRow {
  if (typeof value !== 'object' || value === null) return false;
  if (!('id' in value) || !('startedAt' in value) || !('tokens' in value)) return false;
  const { id, startedAt, tokens } = value; // `in` narrows each key to unknown, no cast needed
  return (
    typeof id === 'string' &&
    typeof startedAt === 'string' &&
    (tokens === null || typeof tokens === 'number')
  );
}

const parsed: unknown = JSON.parse(line);
if (!isSessionRow(parsed)) {
  throw new ParseError(`session row has unexpected shape at line ${lineNo}`);
}
```

Use the validator library the standard names instead of hand-written guards when the shape is
large. A `JSON.parse(text) as SessionRow` is not narrowing — it is a promise the data will
never break.

## Replace `!` and `as` with a decision

A non-null assertion says "this cannot be missing". If that were provable the compiler would
know. Decide what missing means and write it:

```ts
// Before: crashes far from the cause when the key is absent.
const model = byId.get(id)!.model;

// After: the absent case is named where it happens.
const entry = byId.get(id);
if (entry === undefined) {
  throw new Error(`unknown session id ${id}`);
}
const model = entry.model;
```

## Model states, not bags of optionals

```ts
type Pricing =
  | { kind: 'priced'; usd: number }
  | { kind: 'unpriced'; reason: 'unknown-model' | 'no-usage' };

function describe(p: Pricing): string {
  switch (p.kind) {
    case 'priced':
      return `$${p.usd.toFixed(2)}`;
    case 'unpriced':
      return 'not priced';
    default: {
      const exhaustive: never = p;
      return exhaustive;
    }
  }
}
```

Adding a variant now fails the build at every `switch` that has not handled it.

## Absent stays absent

The recurring defect: an unmeasured value is coerced to `0` early, and every later sum, rate,
and chart treats "we do not know" as "nothing happened".

| Looks harmless | What it does | Write instead |
|---|---|---|
| `row.tokens ?? 0` | Unreported becomes zero | Keep `number \| null`; decide at the display |
| `Number(raw) \|\| 0` | `NaN` *and* a real `0` both become `0` | `const n = Number(raw); Number.isFinite(n) ? n : null` |
| `parseInt(s) \|\| 0` | Parse failure looks like data | Return `null` or throw a parse error |
| `rows.reduce((a, r) => a + (r.cost ?? 0), 0)` | Missing members silently counted | Sum the present members and report how many were absent |
| `avg = total / rows.length` | Divides by members that had no value | Divide by the count of present members; absent when that is 0 |

An aggregate that honors absence:

```ts
interface Total {
  value: number | null; // null when no member reported a value
  missing: number;      // members excluded because they reported nothing
}

function sumReported(values: readonly (number | null)[]): Total {
  let value: number | null = null;
  let missing = 0;
  for (const v of values) {
    if (v === null) {
      missing += 1;
    } else {
      value = (value ?? 0) + v;
    }
  }
  return { value, missing };
}
```

The `?? 0` inside is fine: it seeds an accumulator once a real value exists, it does not stand
in for a missing one. At the surface, render `null` as `—` or "not reported", and show
`missing` when it is not zero.

## Compiler flags that help

`strict`, `noUncheckedIndexedAccess`, `exactOptionalPropertyTypes`, and
`noImplicitOverride` each catch a class of these defects. Turning a new one on is its own
change; when the standard names them, keep them on.
