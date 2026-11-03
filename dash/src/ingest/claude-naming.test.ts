import { mkdtemp, mkdir, rm, writeFile } from 'fs/promises'
import { tmpdir } from 'os'
import { join } from 'path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { formatSessionTitle, isMachineLabel } from '../canon/naming.js'
import * as claudeProvider from '../providers/claude.js'
import { parseClaudeFileFull, readAgentType } from './parser.js'
import * as parser from './parser.js'

interface ClaudeNamingExtractor {
  extractCustomTitle?: (folderOrFilePath: string) => Promise<string | undefined>
  extractFirstPromptFallback?: (transcriptPath: string) => Promise<string | undefined>
  extractFirstPrompt?: (transcriptPath: string) => Promise<string | undefined>
  readSubagentMeta?: (filePath: string) => Promise<{ agentType?: string; description?: string } | undefined>
  readSubagentMetadata?: (filePath: string) => Promise<{ agentType?: string; description?: string } | undefined>
}

const parserExt = parser as unknown as ClaudeNamingExtractor
const claudeExt = claudeProvider as unknown as ClaudeNamingExtractor

const extractCustomTitle = parserExt.extractCustomTitle ?? claudeExt.extractCustomTitle
const extractFirstPromptFallback =
  parserExt.extractFirstPromptFallback ??
  parserExt.extractFirstPrompt ??
  claudeExt.extractFirstPromptFallback ??
  claudeExt.extractFirstPrompt
const readSubagentMeta =
  parserExt.readSubagentMeta ??
  parserExt.readSubagentMetadata ??
  claudeExt.readSubagentMeta ??
  claudeExt.readSubagentMetadata

describe('Claude session title and metadata extraction (Contract T2.1)', () => {
  let tmpDir: string

  beforeEach(async () => {
    tmpDir = await mkdtemp(join(tmpdir(), 'claude-naming-test-'))
  })

  afterEach(async () => {
    await rm(tmpDir, { recursive: true, force: true })
  })

  describe('custom-title.json extraction', () => {
    it('extracts custom title from custom-title.json containing customTitle property', async () => {
      const sessionDir = join(tmpDir, 'session-custom-title')
      await mkdir(sessionDir, { recursive: true })
      await writeFile(
        join(sessionDir, 'custom-title.json'),
        JSON.stringify({ customTitle: 'Feature Plan' }),
      )

      const transcriptPath = join(sessionDir, 'session.jsonl')
      await writeFile(
        transcriptPath,
        JSON.stringify({
          type: 'user',
          sessionId: 'session-custom-title',
          timestamp: '2026-10-14T12:00:00.000Z',
          message: { role: 'user', content: 'Some initial user prompt' },
        }) + '\n' +
        JSON.stringify({
          type: 'assistant',
          sessionId: 'session-custom-title',
          uuid: 'turn-1',
          timestamp: '2026-10-14T12:00:05.000Z',
          message: {
            id: 'msg-1',
            model: 'claude-sonnet-4-5',
            role: 'assistant',
            content: [{ type: 'text', text: 'I will help with that.' }],
            usage: { input_tokens: 100, output_tokens: 20 },
          },
        }) + '\n',
      )

      expect(extractCustomTitle, 'extractCustomTitle must be implemented and exported').toBeDefined()
      const customTitle = await extractCustomTitle!(sessionDir)
      expect(customTitle).toBe('Feature Plan')

      const parsed = await parseClaudeFileFull(transcriptPath, new Set())
      expect(parsed?.title).toBe('Feature Plan')
    })

    it('extracts custom title from custom-title.json containing title property', async () => {
      const sessionDir = join(tmpDir, 'session-title-prop')
      await mkdir(sessionDir, { recursive: true })
      await writeFile(
        join(sessionDir, 'custom-title.json'),
        JSON.stringify({ title: 'Refactoring Architecture' }),
      )

      const transcriptPath = join(sessionDir, 'session.jsonl')
      await writeFile(
        transcriptPath,
        JSON.stringify({
          type: 'user',
          sessionId: 'session-title-prop',
          timestamp: '2026-10-14T12:00:00.000Z',
          message: { role: 'user', content: 'Refactor architecture' },
        }) + '\n' +
        JSON.stringify({
          type: 'assistant',
          sessionId: 'session-title-prop',
          uuid: 'turn-1',
          timestamp: '2026-10-14T12:00:05.000Z',
          message: {
            id: 'msg-1',
            model: 'claude-sonnet-4-5',
            role: 'assistant',
            content: [{ type: 'text', text: 'Refactoring now.' }],
            usage: { input_tokens: 100, output_tokens: 20 },
          },
        }) + '\n',
      )

      expect(extractCustomTitle, 'extractCustomTitle must be implemented and exported').toBeDefined()
      const customTitle = await extractCustomTitle!(sessionDir)
      expect(customTitle).toBe('Refactoring Architecture')

      const parsed = await parseClaudeFileFull(transcriptPath, new Set())
      expect(parsed?.title).toBe('Refactoring Architecture')
    })
  })

  describe('first prompt fallback extraction', () => {
    it('falls back to first user prompt truncated to 80 chars when custom-title.json is missing', async () => {
      const sessionDir = join(tmpDir, 'session-fallback-long')
      await mkdir(sessionDir, { recursive: true })
      const transcriptPath = join(sessionDir, 'session.jsonl')

      const fullPrompt = 'Implement Claude session title extraction and subagent metadata handling in the parser and providers'
      const expectedTruncated = fullPrompt.slice(0, 80)
      expect(expectedTruncated.length).toBe(80)

      await writeFile(
        transcriptPath,
        JSON.stringify({
          type: 'user',
          sessionId: 'session-fallback-long',
          timestamp: '2026-10-14T12:00:00.000Z',
          message: { role: 'user', content: fullPrompt },
        }) + '\n' +
        JSON.stringify({
          type: 'assistant',
          sessionId: 'session-fallback-long',
          uuid: 'turn-1',
          timestamp: '2026-10-14T12:00:05.000Z',
          message: {
            id: 'msg-1',
            model: 'claude-sonnet-4-5',
            role: 'assistant',
            content: [{ type: 'text', text: 'Working on it.' }],
            usage: { input_tokens: 100, output_tokens: 20 },
          },
        }) + '\n',
      )

      expect(extractFirstPromptFallback, 'extractFirstPromptFallback must be implemented and exported').toBeDefined()
      const extracted = await extractFirstPromptFallback!(transcriptPath)
      expect(extracted).toBe(expectedTruncated)

      const parsed = await parseClaudeFileFull(transcriptPath, new Set())
      expect(parsed?.title).toBe(expectedTruncated)
    })

    it('preserves user prompt under 80 chars verbatim as title fallback', async () => {
      const sessionDir = join(tmpDir, 'session-fallback-short')
      await mkdir(sessionDir, { recursive: true })
      const transcriptPath = join(sessionDir, 'session.jsonl')

      const shortPrompt = 'Fix unit test assertion for subagents'
      await writeFile(
        transcriptPath,
        JSON.stringify({
          type: 'user',
          sessionId: 'session-fallback-short',
          timestamp: '2026-10-14T12:00:00.000Z',
          message: { role: 'user', content: shortPrompt },
        }) + '\n' +
        JSON.stringify({
          type: 'assistant',
          sessionId: 'session-fallback-short',
          uuid: 'turn-1',
          timestamp: '2026-10-14T12:00:05.000Z',
          message: {
            id: 'msg-1',
            model: 'claude-sonnet-4-5',
            role: 'assistant',
            content: [{ type: 'text', text: 'Done.' }],
            usage: { input_tokens: 100, output_tokens: 20 },
          },
        }) + '\n',
      )

      expect(extractFirstPromptFallback, 'extractFirstPromptFallback must be implemented and exported').toBeDefined()
      const extracted = await extractFirstPromptFallback!(transcriptPath)
      expect(extracted).toBe(shortPrompt)

      const parsed = await parseClaudeFileFull(transcriptPath, new Set())
      expect(parsed?.title).toBe(shortPrompt)
    })
  })

  describe('subagent sidecar metadata and formatted title', () => {
    it('extracts subagent metadata (agentType, description) from .meta.json sidecar', async () => {
      const sessionDir = join(tmpDir, 'session-parent')
      const subagentsDir = join(sessionDir, 'subagents')
      await mkdir(subagentsDir, { recursive: true })

      const subagentTranscriptPath = join(subagentsDir, 'agent-1234.jsonl')
      const subagentMetaPath = join(subagentsDir, 'agent-1234.meta.json')

      await writeFile(
        subagentMetaPath,
        JSON.stringify({
          agentType: 'Codebase Researcher',
          description: 'Scan session parser',
        }),
      )

      await writeFile(
        subagentTranscriptPath,
        JSON.stringify({
          type: 'user',
          sessionId: 'agent-1234',
          timestamp: '2026-10-14T12:00:00.000Z',
          message: { role: 'user', content: 'Scan session parser' },
        }) + '\n' +
        JSON.stringify({
          type: 'assistant',
          sessionId: 'agent-1234',
          uuid: 'turn-1',
          timestamp: '2026-10-14T12:00:05.000Z',
          message: {
            id: 'msg-1',
            model: 'claude-sonnet-4-5',
            role: 'assistant',
            content: [{ type: 'text', text: 'Scanned.' }],
            usage: { input_tokens: 100, output_tokens: 20 },
          },
        }) + '\n',
      )

      expect(readSubagentMeta, 'readSubagentMeta must be implemented and exported').toBeDefined()
      const meta = await readSubagentMeta!(subagentTranscriptPath)
      expect(meta).toEqual({
        agentType: 'Codebase Researcher',
        description: 'Scan session parser',
      })

      const agentType = await readAgentType(subagentTranscriptPath)
      expect(agentType).toBe('Codebase Researcher')

      const formattedTitle = formatSessionTitle({
        agentName: meta?.agentType,
        taskSummary: meta?.description,
      })
      expect(formattedTitle).toBe('Codebase Researcher · Scan session parser')

      const parsed = await parseClaudeFileFull(subagentTranscriptPath, new Set())
      expect(parsed?.title).toBe('Codebase Researcher · Scan session parser')
    })

    it('extracts subagent metadata and formats title for Windows-style paths with backslashes', async () => {
      const winSubagentsDir = process.platform === 'win32'
        ? join(tmpDir, 'win-session', 'subagents')
        : tmpDir
      if (process.platform === 'win32') {
        await mkdir(winSubagentsDir, { recursive: true })
      }
      const winTranscriptPath = process.platform === 'win32'
        ? join(winSubagentsDir, 'agent-1234.jsonl')
        : join(tmpDir, 'win-session\\subagents\\agent-1234.jsonl')
      const winMetaPath = process.platform === 'win32'
        ? join(winSubagentsDir, 'agent-1234.meta.json')
        : join(tmpDir, 'win-session\\subagents\\agent-1234.meta.json')

      await writeFile(
        winMetaPath,
        JSON.stringify({
          agentType: 'Codebase Researcher',
          description: 'Scan Windows session parser',
        }),
      )

      await writeFile(
        winTranscriptPath,
        JSON.stringify({
          type: 'user',
          sessionId: 'agent-1234',
          timestamp: '2026-10-14T12:00:00.000Z',
          message: { role: 'user', content: 'Scan Windows session parser' },
        }) + '\n' +
        JSON.stringify({
          type: 'assistant',
          sessionId: 'agent-1234',
          uuid: 'turn-1',
          timestamp: '2026-10-14T12:00:05.000Z',
          message: {
            id: 'msg-1',
            model: 'claude-sonnet-4-5',
            role: 'assistant',
            content: [{ type: 'text', text: 'Scanned on Windows.' }],
            usage: { input_tokens: 100, output_tokens: 20 },
          },
        }) + '\n',
      )

      expect(winTranscriptPath).toMatch(/[\\/]subagents[\\/]/)

      expect(readSubagentMeta, 'readSubagentMeta must be implemented and exported').toBeDefined()
      const meta = await readSubagentMeta!(winTranscriptPath)
      expect(meta).toEqual({
        agentType: 'Codebase Researcher',
        description: 'Scan Windows session parser',
      })

      const agentType = await readAgentType(winTranscriptPath)
      expect(agentType).toBe('Codebase Researcher')

      const parsed = await parseClaudeFileFull(winTranscriptPath, new Set())
      expect(parsed?.title).toBe('Codebase Researcher · Scan Windows session parser')
    })
  })

  describe('formatSessionTitle and machine label suppression', () => {
    it('formats subagent session title as "<agentType> · <description>" via formatSessionTitle', () => {
      const formatted = formatSessionTitle({
        agentName: 'Codebase Researcher',
        taskSummary: 'Scan session parser',
      })
      expect(formatted).toBe('Codebase Researcher · Scan session parser')
    })

    it('formats subagent title as "<agentType> · session" when description is absent', () => {
      const formatted = formatSessionTitle({
        agentName: 'Codebase Researcher',
      })
      expect(formatted).toBe('Codebase Researcher · session')
    })

    it('suppresses machine labels (e.g. claude_code.llm_request) from session titles', () => {
      expect(isMachineLabel('claude_code.llm_request')).toBe(true)

      const titleWithMachineLabel = formatSessionTitle({
        rawLabel: 'claude_code.llm_request',
        harness: 'claude-code',
      })
      expect(titleWithMachineLabel).not.toContain('claude_code.llm_request')
      expect(titleWithMachineLabel).toBe('Claude Code session')

      const titleWithAgent = formatSessionTitle({
        agentName: 'Codebase Researcher',
        rawLabel: 'claude_code.llm_request',
        harness: 'claude-code',
      })
      expect(titleWithAgent).toBe('Codebase Researcher · session')
    })
  })
})
