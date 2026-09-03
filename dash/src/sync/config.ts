/**
 * codeburn sync — config file management.
 *
 * Stores non-secret sync configuration at ~/.config/codeburn/sync.json
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync, unlinkSync } from 'fs'
import { join } from 'path'
import { homedir } from 'os'

import type { AutoSyncConfig } from './consent.js'

export interface SyncConfig {
  baseUrl: string
  clientId: string
  tracesPath: string
  issuer: string
  lastSync?: string
  auto?: AutoSyncConfig
}

function configDir(): string {
  return join(homedir(), '.config', 'codeburn')
}

function configPath(): string {
  return join(configDir(), 'sync.json')
}

export function receiptsPath(): string {
  return join(configDir(), 'receipts.jsonl')
}

export function readSyncConfig(): SyncConfig | null {
  const path = configPath()
  if (!existsSync(path)) return null

  try {
    const raw = readFileSync(path, 'utf-8')
    const data = JSON.parse(raw) as Record<string, unknown>

    if (typeof data.baseUrl !== 'string' || typeof data.clientId !== 'string') {
      return null
    }

    return {
      baseUrl: data.baseUrl,
      clientId: data.clientId,
      tracesPath: typeof data.tracesPath === 'string' ? data.tracesPath : '/v1/traces',
      issuer: typeof data.issuer === 'string' ? data.issuer : '',
      lastSync: typeof data.lastSync === 'string' ? data.lastSync : undefined,
      auto: typeof data.auto === 'object' && data.auto ? (data.auto as AutoSyncConfig) : undefined,
    }
  } catch {
    return null
  }
}

export function writeSyncConfig(config: SyncConfig): void {
  const dir = configDir()
  mkdirSync(dir, { recursive: true })
  writeFileSync(configPath(), JSON.stringify(config, null, 2) + '\n')
}

export function updateLastSync(): void {
  const config = readSyncConfig()
  if (!config) return
  config.lastSync = new Date().toISOString()
  writeSyncConfig(config)
}

export function deleteSyncConfig(): void {
  try { unlinkSync(configPath()) } catch { /* may not exist */ }
}

export function readReceipts(limit?: number): Array<Record<string, unknown>> {
  const path = receiptsPath()
  if (!existsSync(path)) return []

  try {
    const raw = readFileSync(path, 'utf-8')
    const lines = raw.trim().split('\n').filter(Boolean)
    const entries = lines.map(line => JSON.parse(line) as Record<string, unknown>)
    if (limit && limit > 0) return entries.slice(-limit)
    return entries
  } catch {
    return []
  }
}

export function appendReceipt(receipt: Record<string, unknown>): void {
  const dir = configDir()
  mkdirSync(dir, { recursive: true })
  const path = receiptsPath()
  // Ensure directory exists immediately before write (handles race conditions)
  mkdirSync(dir, { recursive: true })
  const line = JSON.stringify(receipt) + '\n'
  writeFileSync(path, line, { flag: 'a' })
}
