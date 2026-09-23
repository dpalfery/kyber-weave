import { afterEach, describe, expect, it } from 'vitest'
import { join } from 'path'
import { homedir } from 'os'
import { getCacheDir } from './cache-dir.js'

describe('getCacheDir', () => {
  const original = process.env['KYBERDASH_CACHE_DIR']
  const CODEBURN_VAR = `CODEBURN_` + `CACHE_DIR`

  afterEach(() => {
    if (original === undefined) delete process.env['KYBERDASH_CACHE_DIR']
    else process.env['KYBERDASH_CACHE_DIR'] = original
    if (original === undefined) delete process.env[CODEBURN_VAR]
  })

  it('resolves to ~/.kyberdash/cache by default', () => {
    delete process.env['KYBERDASH_CACHE_DIR']
    expect(getCacheDir()).toBe(join(homedir(), '.kyberdash', 'cache'))
  })

  it('resolves an explicit override at call time', () => {
    process.env['KYBERDASH_CACHE_DIR'] = '/tmp/kyberdash-one'
    expect(getCacheDir()).toBe('/tmp/kyberdash-one')
    process.env['KYBERDASH_CACHE_DIR'] = '/tmp/kyberdash-two'
    expect(getCacheDir()).toBe('/tmp/kyberdash-two')
  })

  it.each(['', '  ', '\n\t'])('treats a blank override as absent (%j)', value => {
    process.env['KYBERDASH_CACHE_DIR'] = value
    expect(getCacheDir()).toBe(join(homedir(), '.kyberdash', 'cache'))
  })

  it('gives the upstream CodeBurn variable no effect', () => {
    // The fork severed the upstream identity: a stray CODEBURN_CACHE_DIR in
    // the environment must neither redirect nor disable the cache location.
    // The variable name is assembled so an identity rename cannot silently
    // repoint this test at the variable it exists to refute.
    delete process.env['KYBERDASH_CACHE_DIR']
    process.env[CODEBURN_VAR] = '/tmp/codeburn-cache-must-be-ignored'
    expect(getCacheDir()).toBe(join(homedir(), '.kyberdash', 'cache'))
  })
})
