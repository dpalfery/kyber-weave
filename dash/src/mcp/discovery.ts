import { readFile } from 'node:fs/promises'
import { join } from 'node:path'
import { getConfigDir } from '../config.js'

const SERVER_FILE = 'server.json'

type ServerReceipt = { url?: unknown }

export function validateAnalyticsServerUrl(value: string): string {
  let url: URL
  try { url = new URL(value) } catch { throw new Error('engine_unavailable: provide a literal loopback HTTP server URL or run kyberdash web --no-open.') }
  if (url.protocol !== 'http:' || (url.hostname !== '127.0.0.1' && url.hostname !== '[::1]') || url.username !== '' || url.password !== '' || url.search !== '' || url.hash !== '' || url.pathname !== '/') {
    throw new Error('engine_unavailable: server URL must be http://127.0.0.1:<port> or http://[::1]:<port>.')
  }
  if (url.port === '') throw new Error('engine_unavailable: server URL must include a port.')
  return url.origin
}

async function receiptUrl(directory: string): Promise<string | undefined> {
  try {
    const parsed: unknown = JSON.parse(await readFile(join(directory, SERVER_FILE), 'utf8'))
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return undefined
    const value = (parsed as ServerReceipt).url
    return typeof value === 'string' ? validateAnalyticsServerUrl(value) : undefined
  } catch { return undefined }
}

export async function resolveAnalyticsServerUrl(options: { serverUrl?: string; stateDir?: string; configDir?: string } = {}): Promise<string> {
  if (options.serverUrl !== undefined) return validateAnalyticsServerUrl(options.serverUrl)
  if (options.stateDir !== undefined) {
    const selected = await receiptUrl(options.stateDir)
    if (selected !== undefined) return selected
  }
  const standard = await receiptUrl(options.configDir ?? getConfigDir())
  if (standard !== undefined) return standard
  throw new Error('engine_unavailable: no running KyberDash engine was discovered; run kyberdash web --no-open.')
}
