// Tests for provider failure handling (task 9.2; R1.2, R1.3; design.md
// "Error Handling" table). The two load-bearing describe blocks:
//
//   * R1.2 — a provider whose session store is absent is omitted silently:
//     no records, no problem, no error. Most machines do not run most
//     agents, so absence is the ordinary shape of a first run.
//   * R1.3 — a store that exists but cannot be parsed records one problem
//     naming the provider and the file, and every other provider still
//     synthesizes: the corrupt store narrows the run, it does not abort it.
//
// Alongside them, the rest of the loader seam's contract: successful loads
// synthesize through 9.1's Synthesizer unchanged (one data path), a
// present-but-empty store is neither absence nor error, and only a negative
// existence signal (`ENOENT`) reads as absence — every other error is a
// present store with a problem.

import { describe, expect, it } from 'vitest'

import type { ParsedProviderCall } from '../../src/providers/types.js'
import { tokenValidator } from '../canon/adapters/quarantine.js'
import { Synthesizer } from './synth.js'
import { PROVIDER_PARSE_ERROR, ingestProviders } from './provider.js'

// ---------------------------------------------------------------------------
// Fixture kit
// ---------------------------------------------------------------------------

/** A complete upstream `ParsedProviderCall`, with the spec's fields defaulted. */
function call(spec: Partial<ParsedProviderCall> = {}): ParsedProviderCall {
  return {
    provider: 'claude',
    model: 'claude-sonnet-4.5',
    inputTokens: 1_000,
    outputTokens: 240,
    cacheCreationInputTokens: 120,
    cacheReadInputTokens: 3_800,
    cachedInputTokens: 3_800,
    reasoningTokens: 0,
    webSearchRequests: 0,
    costUSD: 0.0123,
    tools: ['Read', 'Bash'],
    bashCommands: [],
    timestamp: '2026-08-29T12:00:00.000Z',
    speed: 'standard',
    deduplicationKey: 'claude:s-1:m-1',
    userMessage: 'run the parity check',
    sessionId: 's-1',
    ...spec,
  }
}

/** Two calls for one provider, as a parsed store would yield them. */
function callsFor(provider: string, count = 2): ParsedProviderCall[] {
  return Array.from({ length: count }, (_, turn) =>
    call({
      provider,
      deduplicationKey: `${provider}:s-1:m-${turn}`,
    }),
  )
}

/** A parse failure a loader would surface for a corrupt JSONL session file. */
function corruptStoreError(file: string): Error {
  return Object.assign(new SyntaxError(`Unexpected token '<' in JSON at position 0`), {
    file,
  })
}

/** A Node-style fs error, as `fs.readFile` rejects with them. */
function fsError(code: string, path: string): Error {
  return Object.assign(new Error(`${code}: something went wrong, open '${path}'`), { code, path })
}

// ---------------------------------------------------------------------------
// R1.2 — absent store: omitted silently, never an error
// ---------------------------------------------------------------------------

describe('R1.2 — an absent provider store is omitted silently', () => {
  it('records no problem and no records when the loader reports null', () => {
    const result = ingestProviders(['codex'], () => null)
    expect(result.records).toEqual([])
    expect(result.problems).toEqual([])
  })

  it('treats an undefined loader result the same as null', () => {
    const result = ingestProviders(['kilo-code'], () => undefined)
    expect(result.records).toEqual([])
    expect(result.problems).toEqual([])
  })

  it('treats a thrown ENOENT as absence, not an error', () => {
    const loader = () => {
      throw fsError('ENOENT', '/home/dev/.codex/sessions')
    }
    const result = ingestProviders(['codex'], loader)
    expect(result.records).toEqual([])
    expect(result.problems).toEqual([])
  })

  it('treats a returned ENOENT error value as absence too', () => {
    const result = ingestProviders(['codex'], () => fsError('ENOENT', '/home/dev/.codex/sessions'))
    expect(result.records).toEqual([])
    expect(result.problems).toEqual([])
  })

  it('omits the absent provider while still ingesting the present one', () => {
    const claude = callsFor('claude')
    const result = ingestProviders(['codex', 'claude'], (provider) =>
      provider === 'claude' ? claude : null,
    )
    expect(result.problems).toEqual([])
    expect(result.records.map((record) => record.harness)).toEqual(['claude', 'claude'])
  })
})

// ---------------------------------------------------------------------------
// R1.3 — unparseable store: problem names provider and file, run continues
// ---------------------------------------------------------------------------

describe('R1.3 — an unparseable store is recorded and the run continues', () => {
  const corruptFile = '/home/dev/.codex/sessions/rollout-2026-08-29.jsonl'

  it('records a problem naming the provider and the file for a corrupt store', () => {
    const result = ingestProviders(['codex'], () => corruptStoreError(corruptFile))

    expect(result.records).toEqual([])
    expect(result.problems).toHaveLength(1)
    const problem = result.problems[0]!
    expect(problem.severity).toBe('error')
    expect(problem.code).toBe(PROVIDER_PARSE_ERROR)
    expect(problem.message).toContain('codex')
    expect(problem.message).toContain(corruptFile)
    expect(problem.location).toBe(corruptFile)
  })

  it('continues with every other provider — corruption does not abort the run', () => {
    // The corrupt store sits in the middle: providers after it must still
    // synthesize, which is the "continue" half of R1.3.
    const claude = callsFor('claude')
    const gemini = callsFor('gemini', 1)
    const result = ingestProviders(['claude', 'codex', 'gemini'], (provider) => {
      if (provider === 'claude') return claude
      if (provider === 'codex') return corruptStoreError(corruptFile)
      return gemini
    })

    expect(result.problems).toHaveLength(1)
    expect(result.problems[0]!.message).toContain('codex')
    expect(result.records.map((record) => record.harness)).toEqual(['claude', 'claude', 'gemini'])
  })

  it('records the same problem when the loader throws instead of returning', () => {
    const loader = () => {
      throw corruptStoreError(corruptFile)
    }
    const result = ingestProviders(['codex'], loader)
    expect(result.problems).toHaveLength(1)
    expect(result.problems[0]!.code).toBe(PROVIDER_PARSE_ERROR)
    expect(result.problems[0]!.message).toContain('codex')
    expect(result.problems[0]!.message).toContain(corruptFile)
  })

  it('takes the file from a Node fs error path for a present-but-unreadable store', () => {
    // EACCES is a store that exists but cannot be read — a problem, never
    // absence; only ENOENT is a negative existence check.
    const locked = '/home/dev/.claude/projects/x/session.jsonl'
    const result = ingestProviders(['claude'], () => fsError('EACCES', locked))
    expect(result.problems).toHaveLength(1)
    expect(result.problems[0]!.code).toBe(PROVIDER_PARSE_ERROR)
    expect(result.problems[0]!.message).toContain('claude')
    expect(result.problems[0]!.message).toContain(locked)
    expect(result.problems[0]!.location).toBe(locked)
  })

  it('never omits the provider from the problem, even when no file is attached', () => {
    // A loader that lets a bare SyntaxError escape names no file; the
    // problem must still point at the provider rather than at nothing.
    const result = ingestProviders(['pi'], () => new SyntaxError(`Unexpected end of JSON input`))
    expect(result.problems).toHaveLength(1)
    expect(result.problems[0]!.code).toBe(PROVIDER_PARSE_ERROR)
    expect(result.problems[0]!.message).toContain('pi')
    expect(result.problems[0]!.location).toBeUndefined()
  })

  it('records one problem per unparseable provider, not one for the whole pass', () => {
    const claude = callsFor('claude', 1)
    const result = ingestProviders(['codex', 'claude', 'pi'], (provider) => {
      if (provider === 'claude') return claude
      return corruptStoreError(`/home/dev/.${provider}/store.json`)
    })
    expect(result.problems).toHaveLength(2)
    expect(result.problems.map((problem) => problem.message)).toSatisfy((messages: string[]) =>
      messages.every((message) => message.includes('could not be parsed')),
    )
    expect(result.records.map((record) => record.harness)).toEqual(['claude'])
  })
})

// ---------------------------------------------------------------------------
// Contract — successful loads take the one data path through 9.1
// ---------------------------------------------------------------------------

describe('successful loads synthesize through the 9.1 Synthesizer', () => {
  it('produces exactly what calling the Synthesizer directly produces', () => {
    const claude = callsFor('claude')
    const gemini = callsFor('gemini', 3)
    const result = ingestProviders(['claude', 'gemini'], (provider) =>
      provider === 'claude' ? claude : gemini,
    )

    expect(result.problems).toEqual([])
    expect(result.records).toEqual(new Synthesizer().synthesize([...claude, ...gemini]))
    // The synthesized records hold as stored, same as the direct path (R4.1).
    for (const record of result.records) {
      expect(tokenValidator(record)).toBeUndefined()
    }
  })

  it('treats a present-but-empty store as neither absence nor error', () => {
    const result = ingestProviders(['cursor'], () => [])
    expect(result.records).toEqual([])
    expect(result.problems).toEqual([])
  })

  it('ingests nothing without drama when no providers are requested', () => {
    const result = ingestProviders([], () => {
      throw new Error('the loader must never be consulted')
    })
    expect(result).toEqual({ records: [], problems: [] })
  })
})
