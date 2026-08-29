import { afterEach, describe, expect, it } from 'vitest'
import { join } from 'path'
import { homedir } from 'os'
import { getCodeburnCacheDir } from '../src/cache-dir.js'

describe('getCodeburnCacheDir', () => {
  const original = process.env['CODEBURN_CACHE_DIR']

  afterEach(() => {
    if (original === undefined) delete process.env['CODEBURN_CACHE_DIR']
    else process.env['CODEBURN_CACHE_DIR'] = original
  })

  it('resolves an explicit override at call time', () => {
    process.env['CODEBURN_CACHE_DIR'] = '/tmp/codeburn-one'
    expect(getCodeburnCacheDir()).toBe('/tmp/codeburn-one')
    process.env['CODEBURN_CACHE_DIR'] = '/tmp/codeburn-two'
    expect(getCodeburnCacheDir()).toBe('/tmp/codeburn-two')
  })

  it.each(['', '  ', '\n\t'])('treats a blank override as absent (%j)', value => {
    process.env['CODEBURN_CACHE_DIR'] = value
    expect(getCodeburnCacheDir()).toBe(join(homedir(), '.cache', 'codeburn'))
  })
})
