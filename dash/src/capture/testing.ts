import { closeSync, fstatSync, openSync, readSync } from 'node:fs'

export function snapshotFile(path: string): { text: string; mtimeMs: number } {
  const fd = openSync(path, 'r')
  try {
    const stat = fstatSync(fd)
    const buffer = Buffer.alloc(stat.size)
    let offset = 0
    while (offset < stat.size) {
      const read = readSync(fd, buffer, offset, stat.size - offset, offset)
      if (read === 0) break
      offset += read
    }
    return { text: buffer.toString('utf8'), mtimeMs: stat.mtimeMs }
  } finally {
    closeSync(fd)
  }
}
