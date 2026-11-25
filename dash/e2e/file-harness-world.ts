// World helpers for the file-import harness suite (T5-T7): native transcript files are written
// under the isolated server's home, and the real folder import reads them back.
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, isAbsolute, join, resolve, sep } from 'node:path'

export type HarnessFile = { readonly relativePath: string; readonly content: string | Uint8Array }

/**
 * Writes each file under `homeDir` only. A path that is absolute, or that resolves outside
 * `homeDir` (a `..` segment), is refused before anything is written.
 */
export function writeHarnessFiles(homeDir: string, files: readonly HarnessFile[]): void {
  const root = resolve(homeDir)
  const targets = files.map((file) => {
    if (file.relativePath.trim() === '' || file.relativePath.includes('\0') || isAbsolute(file.relativePath)) {
      throw new Error(`refusing harness file path that is empty or absolute: ${file.relativePath}`)
    }
    const target = resolve(join(root, file.relativePath))
    if (!target.startsWith(`${root}${sep}`)) {
      throw new Error(`refusing harness file path that leaves the home directory: ${file.relativePath}`)
    }
    return { target, content: file.content }
  })
  for (const { target, content } of targets) {
    mkdirSync(dirname(target), { recursive: true })
    writeFileSync(target, content)
  }
}

/**
 * Posts the real folder import (`api/kyber/import-history`) and fails on a non-200 answer: the
 * import runs inside the job, so a 200 means the pass over every registered file source is done.
 */
export async function postImportHistory(baseUrl: string): Promise<void> {
  const response = await fetch(`${baseUrl}/api/kyber/import-history`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ weeks: 2 }),
    signal: AbortSignal.timeout(180_000),
  })
  if (response.status !== 200) {
    const body = await response.text().catch(() => '')
    throw new Error(`POST /api/kyber/import-history failed (${response.status}): ${body.slice(0, 400)}`)
  }
}
