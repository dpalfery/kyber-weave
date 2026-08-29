import { posix, win32 } from 'node:path'
import { fileURLToPath } from 'node:url'

export function rootFromModuleUrl(moduleUrl, windows = process.platform === 'win32') {
  const path = windows ? win32 : posix
  const scriptPath = fileURLToPath(moduleUrl, { windows })
  return path.resolve(path.dirname(scriptPath), '..', '..')
}
