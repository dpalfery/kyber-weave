import { mkdtemp, rm, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it } from 'vitest'

import { extractFirstPromptFallback } from './claude.js'

const dirs: string[] = []
afterEach(async () => {
  while (dirs.length > 0) await rm(dirs.pop()!, { recursive: true, force: true })
})

async function transcript(prompt: string): Promise<string> {
  const dir = await mkdtemp(join(tmpdir(), 'claude-first-prompt-'))
  dirs.push(dir)
  const path = join(dir, 'session.jsonl')
  await writeFile(path, `${JSON.stringify({ type: 'user', message: { content: prompt } })}\n`)
  return path
}

describe('extractFirstPromptFallback truncation', () => {
  it('cuts on a code point, never inside a surrogate pair', async () => {
    const prompt = `${'a'.repeat(79)}🎉 and then some more words`
    const title = await extractFirstPromptFallback(await transcript(prompt))
    expect(title).toBe(`${'a'.repeat(79)}🎉`)
    expect(title).not.toMatch(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])/)
  })

  it('keeps a short prompt whole', async () => {
    expect(await extractFirstPromptFallback(await transcript('Fix the parser'))).toBe('Fix the parser')
  })
})
