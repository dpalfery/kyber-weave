// The Kilo capture plugin, as the text `capture enable kilo` installs.
//
// Kilo (an OpenCode fork) loads any module in ~/.config/kilo/plugin/ whose default
// export is `{ id, server }` and calls its hooks while it builds each model request.
// Three hooks see what OTLP and kilo.db never carry: the assembled system prompt
// (persona, agent prompt, environment, skills catalog, instruction files), each tool's
// definition, and the model/provider/agent of the call.
//
// The plugin is a plain ES module that imports only node built-ins, because it runs
// inside Kilo, not inside KyberDash. It is kept as text so the installer can write it
// byte-for-byte and the receipt digest can tell an owner edit from an upgrade. Do not use
// backticks or `${` inside it: it lives in a template literal.
//
// Safety contract (a plugin fault must never break the owner's Kilo):
//  - every hook body runs inside try/catch and returns nothing;
//  - writes are small synchronous appends, no network, no timers;
//  - the first write error turns capture off for the rest of the process.
//
// Store layout under ~/.kyberdash/capture/kilo (override: KYBERDASH_KILO_CAPTURE_DIR):
//   blobs/<sha256>   one file per distinct system prompt / tool definition (deduped)
//   index.jsonl      one line per model call: { v, ts, sessionID, messageID, agent,
//                    model, provider, systemHash, toolHashes: { toolID: sha256 } }

/** First line of the installed file; ownership marker the installer checks. */
export const KILO_PLUGIN_MARKER = '// kyberdash-capture-plugin v1'

export const KILO_PLUGIN_SOURCE = `${KILO_PLUGIN_MARKER}
// Installed by "kyberdash kyber capture enable --harness kilo". Remove with "kyberdash kyber capture disable --harness kilo".
// Writes Kilo's system prompt and tool definitions to local files only. No network access.
import { appendFileSync, existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { homedir } from 'node:os'
import { join } from 'node:path'

const ROOT =
  process.env.KYBERDASH_KILO_CAPTURE_DIR || join(homedir(), '.kyberdash', 'capture', 'kilo')
const BLOBS = join(ROOT, 'blobs')
let off = false

function safely(fn) {
  if (off) return
  try {
    fn()
  } catch {
    off = true
  }
}

function blob(text) {
  const hash = createHash('sha256').update(text).digest('hex')
  const path = join(BLOBS, hash)
  if (!existsSync(path)) {
    mkdirSync(BLOBS, { recursive: true, mode: 0o700 })
    writeFileSync(path, text, { mode: 0o600 })
  }
  return hash
}

function text(value) {
  if (typeof value === 'string') return value
  try {
    return JSON.stringify(value) ?? ''
  } catch {
    return ''
  }
}

const systemBySession = new Map()
const toolHashes = new Map()

async function server() {
  return {
    'experimental.chat.system.transform': async (input, output) =>
      safely(() => {
        const parts = Array.isArray(output && output.system) ? output.system : []
        systemBySession.set(String((input && input.sessionID) || ''), blob(parts.join('\\n')))
      }),
    'tool.definition': async (input, output) =>
      safely(() => {
        const id = String((input && input.toolID) || '')
        if (!id) return
        const schema = output && (output.jsonSchema !== undefined ? output.jsonSchema : output.parameters)
        toolHashes.set(id, blob(text({ id, description: output && output.description, schema })))
      }),
    'chat.params': async (input, output) =>
      safely(() => {
        const sessionID = String((input && input.sessionID) || '')
        const line = {
          v: 1,
          ts: Date.now(),
          sessionID,
          messageID: input && input.message ? input.message.id : undefined,
          agent: input && input.agent,
          model: input && input.model ? input.model.id : undefined,
          provider: input && input.provider ? (input.provider.info ? input.provider.info.id : input.provider.id) : undefined,
          systemHash: systemBySession.get(sessionID),
          toolHashes: Object.fromEntries(toolHashes),
        }
        mkdirSync(ROOT, { recursive: true, mode: 0o700 })
        appendFileSync(join(ROOT, 'index.jsonl'), JSON.stringify(line) + '\\n', { mode: 0o600 })
      }),
  }
}

export default { id: 'kyberdash-capture', server }
`
