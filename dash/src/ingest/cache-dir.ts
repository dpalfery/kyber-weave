import { readFile } from 'fs/promises'
import { homedir } from 'os'
import { join } from 'path'

/**
 * Resolve KyberDash's shared cache directory at call time.
 *
 * Reading the environment on every call matters for embedded consumers and
 * tests that change KYBERDASH_CACHE_DIR after importing the CLI modules.
 *
 * The default moved from upstream's ~/.cache/codeburn to ~/.kyberdash/cache
 * when the fork took the KyberDash identity (R3.5): the old directory is
 * neither read nor removed, so an existing CodeBurn install keeps its cache
 * and KyberDash warms its own.
 */
export function getCacheDir(): string {
  const override = process.env['KYBERDASH_CACHE_DIR']
  return override?.trim() ? override : join(homedir(), '.kyberdash', 'cache')
}

/** A versioned cache file is the only source when it exists. Legacy adoption is ENOENT-only. */
export type ExistingTextFile =
  | { status: 'absent' }
  | { status: 'unreadable' }
  | { status: 'ok'; text: string }

export async function readExistingTextFile(path: string): Promise<ExistingTextFile> {
  try {
    return { status: 'ok', text: await readFile(path, 'utf-8') }
  } catch (err) {
    const code = err && typeof err === 'object' && 'code' in err ? (err as { code?: string }).code : undefined
    return { status: code === 'ENOENT' ? 'absent' : 'unreadable' }
  }
}
