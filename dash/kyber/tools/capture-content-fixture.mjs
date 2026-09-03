// Regenerate kyber/canon/adapters/__fixtures__/antigravity-span.json from a
// local canon.db. R12.3 forbids committing captured content, so this keeps
// only what the content mapping is actually tested on — the attribute KEYS,
// the value shapes, and the numeric counters — and replaces every free-text
// body with synthetic filler of the same order of length.
//
// The keys are the point. The mapping this fixture guards read `gen_ai.prompt`
// and wrote {} for all 20,445 stored records, because no harness in the corpus
// emits that attribute. Only real key names catch that; real prose does not.
//
//   node kyber/tools/capture-content-fixture.mjs [path-to-canon.db]

import { DatabaseSync } from 'node:sqlite'
import { inflateSync } from 'node:zlib'
import { writeFileSync, mkdirSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const dbPath = process.argv[2] ?? join(homedir(), '.kyberdash', 'canon.db')
const out = join(dirname(fileURLToPath(import.meta.url)), '..', 'canon', 'adapters', '__fixtures__', 'antigravity-span.json')

/** Attributes whose values are free text and must never be committed. */
const TEXT_ATTRIBUTES = new Set([
  'gen_ai.system_instructions',
  'gen_ai.rules',
  'gen_ai.skills',
  'gen_ai.input.messages',
  'gen_ai.prompt',
])

/** Attributes naming a person, machine, org or repository. */
const IDENTIFYING = /user|email|account|organization|session|trace|request|host|process|vcs|repo|branch|client/i

const filler = (n) => 'synthetic fixture text. '.repeat(Math.max(1, Math.ceil(n / 24))).slice(0, n)

function sanitize(key, value) {
  if (key === 'gen_ai.input.messages') {
    const messages = JSON.parse(value)
    return JSON.stringify(
      messages.map((m) => ({
        role: m.role,
        parts: (m.parts ?? []).map((p) => ({
          ...p,
          ...(typeof p.text === 'string' ? { text: filler(p.text.length) } : {}),
        })),
      })),
    )
  }
  if (TEXT_ATTRIBUTES.has(key)) return filler(value.length)
  if (typeof value === 'string' && IDENTIFYING.test(key)) return `synthetic-${key.replace(/\W+/g, '-')}`
  return value
}

const db = new DatabaseSync(dbPath, { open: true, readOnly: true })
const rows = db.prepare("SELECT raw FROM records WHERE harness = 'gemini' LIMIT 400").all()
let picked
for (const row of rows) {
  let attrs
  try {
    attrs = JSON.parse(inflateSync(row.raw).toString('utf8'))
  } catch {
    continue
  }
  if ('gen_ai.system_instructions' in attrs && 'gen_ai.input.messages' in attrs) {
    picked = attrs
    break
  }
}
db.close()

if (!picked) {
  console.error(`no span carrying content attributes found in ${dbPath}`)
  process.exit(1)
}

const clean = {}
for (const key of Object.keys(picked).sort()) clean[key] = sanitize(key, picked[key])

mkdirSync(dirname(out), { recursive: true })
writeFileSync(out, `${JSON.stringify(clean, null, 2)}\n`)
console.log(`wrote ${out} — ${Object.keys(clean).length} attributes, text bodies replaced`)
