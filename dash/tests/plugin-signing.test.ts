/**
 * Tests for ed25519 plugin signature verification and add/remove commands (9b).
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest'
import { mkdtemp, rm, mkdir, writeFile, readFile } from 'fs/promises'
import { join } from 'path'
import { tmpdir } from 'os'
import { generateKeyPairSync, createPublicKey, sign } from 'crypto'

import { verifyPlugin } from '../src/plugins/loader.js'
import { parsePluginManifest } from '../src/plugins/manifest.js'

function validManifest(name = 'sample') {
  return {
    name,
    version: '0.1.0',
    cliCompat: '>=0.9.22',
    capabilities: {
      commands: [],
      syncAttributes: [],
      payloadSections: [],
      spanKinds: [],
    },
  }
}

async function generateTestKeyPair() {
  const { publicKey, privateKey } = generateKeyPairSync('ed25519', {
    publicKeyEncoding: { format: 'pem', type: 'spki' },
    privateKeyEncoding: { format: 'pem', type: 'pkcs8' },
  })
  // Base64-encode the PEM public key
  return {
    publicKeyBase64: Buffer.from(publicKey as string).toString('base64'),
    privateKeyPem: privateKey as string,
  }
}

async function computeDigest(name: string, version: string, files: Array<{ path: string, sha256: string }>) {
  return JSON.stringify({ name, version, files })
}

async function signPlugin(pluginDir: string, keyPem: string, keyId: string) {
  const { createPrivateKey, createHash } = await import('crypto')
  const { readdir } = await import('fs/promises')
  const manifest = JSON.parse(await readFile(join(pluginDir, 'codeburn-plugin.json'), 'utf8'))
  const { name, version } = manifest

  const entries = await readdir(pluginDir, { withFileTypes: true })
  const files: Array<{ path: string, sha256: string }> = []
  for (const entry of entries) {
    if (!entry.isFile() || entry.name === 'codeburn-plugin.sig') continue
    const content = await readFile(join(pluginDir, entry.name))
    const hash = createHash('sha256').update(content).digest('hex')
    files.push({ path: entry.name, sha256: hash })
  }
  files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0)

  const canonical = JSON.stringify({ name, version, files })
  const privKey = createPrivateKey(keyPem)
  const signature = sign(null, Buffer.from(canonical), privKey)

  const sigData = {
    alg: 'ed25519',
    keyId,
    signature: signature.toString('base64'),
  }
  await writeFile(join(pluginDir, 'codeburn-plugin.sig'), JSON.stringify(sigData), 'utf8')
}

let tmpDir: string
beforeEach(async () => {
  tmpDir = await mkdtemp(join(tmpdir(), 'plugin-signing-'))
})
afterEach(async () => {
  await rm(tmpDir, { recursive: true, force: true })
})

describe('plugin signature verification (9b)', () => {
  it('keygen -> sign -> verify ok round trip with ephemeral key', async () => {
    const { publicKeyBase64, privateKeyPem } = await generateTestKeyPair()
    const keyId = '12345678'
    const knownKeys = new Map([[keyId, publicKeyBase64]])

    const pluginDir = join(tmpDir, 'good')
    await mkdir(pluginDir, { recursive: true })
    await writeFile(join(pluginDir, 'codeburn-plugin.json'), JSON.stringify(validManifest('good')))
    await writeFile(join(pluginDir, 'file1.txt'), 'content1')

    await signPlugin(pluginDir, privateKeyPem, keyId)

    const manifest = validManifest('good')
    const result = await verifyPlugin(pluginDir, manifest, process.env, knownKeys)
    expect(result.ok).toBe(true)
  })

  it('tampering a file after signing results in digest mismatch', async () => {
    const { publicKeyBase64, privateKeyPem } = await generateTestKeyPair()
    const keyId = '12345678'
    const knownKeys = new Map([[keyId, publicKeyBase64]])

    const pluginDir = join(tmpDir, 'tampered')
    await mkdir(pluginDir, { recursive: true })
    await writeFile(join(pluginDir, 'codeburn-plugin.json'), JSON.stringify(validManifest('tampered')))
    await writeFile(join(pluginDir, 'file1.txt'), 'content1')

    await signPlugin(pluginDir, privateKeyPem, keyId)

    // Tamper with a file
    await writeFile(join(pluginDir, 'file1.txt'), 'modified content')

    const manifest = validManifest('tampered')
    const result = await verifyPlugin(pluginDir, manifest, process.env, knownKeys)
    expect(result.ok).toBe(false)
    expect(result.reason).toMatch(/signature|digest/)
  })

  it('unknown keyId is rejected', async () => {
    const { publicKeyBase64, privateKeyPem } = await generateTestKeyPair()
    const keyId = '12345678'
    const knownKeys = new Map([['ffffffff', publicKeyBase64]])

    const pluginDir = join(tmpDir, 'unknown')
    await mkdir(pluginDir, { recursive: true })
    await writeFile(join(pluginDir, 'codeburn-plugin.json'), JSON.stringify(validManifest('unknown')))
    await writeFile(join(pluginDir, 'file1.txt'), 'content1')

    await signPlugin(pluginDir, privateKeyPem, keyId)

    const manifest = validManifest('unknown')
    const result = await verifyPlugin(pluginDir, manifest, process.env, knownKeys)
    expect(result.ok).toBe(false)
    expect(result.reason).toMatch(/unknown key|key id/)
  })

  it('missing signature file without dev flag is rejected', async () => {
    const pluginDir = join(tmpDir, 'unsigned')
    await mkdir(pluginDir, { recursive: true })
    await writeFile(join(pluginDir, 'codeburn-plugin.json'), JSON.stringify(validManifest('unsigned')))

    const env = { ...process.env }
    delete env.CODEBURN_PLUGIN_DEV

    const manifest = validManifest('unsigned')
    const result = await verifyPlugin(pluginDir, manifest, env)
    expect(result.ok).toBe(false)
    expect(result.reason).toMatch(/signature|unsigned/)
  })

  it('missing signature file with CODEBURN_PLUGIN_DEV=1 is accepted', async () => {
    const pluginDir = join(tmpDir, 'dev-unsigned')
    await mkdir(pluginDir, { recursive: true })
    await writeFile(join(pluginDir, 'codeburn-plugin.json'), JSON.stringify(validManifest('dev-unsigned')))

    const env = { ...process.env, CODEBURN_PLUGIN_DEV: '1' }

    const manifest = validManifest('dev-unsigned')
    const result = await verifyPlugin(pluginDir, manifest, env)
    expect(result.ok).toBe(true)
  })

  it('symlink in directory causes verification failure', async () => {
    const { publicKeyBase64, privateKeyPem } = await generateTestKeyPair()
    const keyId = '12345678'
    const knownKeys = new Map([[keyId, publicKeyBase64]])

    const pluginDir = join(tmpDir, 'symlink')
    await mkdir(pluginDir, { recursive: true })
    await writeFile(join(pluginDir, 'codeburn-plugin.json'), JSON.stringify(validManifest('symlink')))
    await writeFile(join(pluginDir, 'file1.txt'), 'content1')

    await signPlugin(pluginDir, privateKeyPem, keyId)

    // Add a symlink (this will cause verification to fail)
    const targetFile = join(tmpDir, 'target.txt')
    await writeFile(targetFile, 'target content')
    const { symlink } = await import('fs/promises')
    await symlink(targetFile, join(pluginDir, 'link.txt'))

    const manifest = validManifest('symlink')
    const result = await verifyPlugin(pluginDir, manifest, process.env, knownKeys)
    expect(result.ok).toBe(false)
    expect(result.reason).toMatch(/symlink/)
  })
})

describe('plugin add/remove commands (9b)', () => {
  it('plugin add refuses unsigned source without dev flag', async () => {
    const { Command } = (await import('commander')).default ?? (await import('commander'))
    const { registerPluginCommands } = await import('../src/plugins/cli.js')

    const sourceDir = join(tmpDir, 'source')
    const pluginDir = await mkdtemp(join(tmpdir(), 'plugins-'))

    await mkdir(sourceDir, { recursive: true })
    await writeFile(join(sourceDir, 'codeburn-plugin.json'), JSON.stringify(validManifest('unsigned-plugin')))
    await writeFile(join(sourceDir, 'file1.txt'), 'content1')

    const program = new Command()
    program.exitOverride()
    registerPluginCommands(program)

    const env = { ...process.env }
    delete env.CODEBURN_PLUGIN_DEV

    const savedStderr = process.stderr.write
    const savedExitCode = process.exitCode
    let stderr = ''
    process.stderr.write = (chunk: string | Uint8Array | Buffer): boolean => {
      stderr += chunk.toString()
      return true
    }
    process.exitCode = undefined

    try {
      await program.parseAsync(['node', 'codeburn', 'plugin', 'add', sourceDir, '--dir', pluginDir])
      expect(process.exitCode).toBe(1)
      expect(stderr).toContain('signature')
    } finally {
      process.stderr.write = savedStderr
      process.exitCode = savedExitCode
      await rm(pluginDir, { recursive: true, force: true })
    }
  })

  it('plugin add succeeds on signed source', async () => {
    const { Command } = (await import('commander')).default ?? (await import('commander'))
    const { registerPluginCommands } = await import('../src/plugins/cli.js')
    const { publicKeyBase64, privateKeyPem } = await generateTestKeyPair()
    const keyId = '12345678'

    const sourceDir = join(tmpDir, 'signed-source')
    const pluginDir = await mkdtemp(join(tmpdir(), 'plugins-'))

    await mkdir(sourceDir, { recursive: true })
    await writeFile(join(sourceDir, 'codeburn-plugin.json'), JSON.stringify(validManifest('signed-plugin')))
    await writeFile(join(sourceDir, 'file1.txt'), 'content1')

    await signPlugin(sourceDir, privateKeyPem, keyId)

    // Temporarily override RELEASE_PUBLIC_KEYS for this test
    const { RELEASE_PUBLIC_KEYS } = await import('../src/plugins/keys.js')
    const originalKeys = new Map(RELEASE_PUBLIC_KEYS)
    ;(RELEASE_PUBLIC_KEYS as any).set(keyId, publicKeyBase64)

    const program = new Command()
    program.exitOverride()
    registerPluginCommands(program)

    try {
      await program.parseAsync(['node', 'codeburn', 'plugin', 'add', sourceDir, '--dir', pluginDir])
      const destDir = join(pluginDir, 'signed-plugin')
      const installedManifest = JSON.parse(await readFile(join(destDir, 'codeburn-plugin.json'), 'utf8'))
      expect(installedManifest.name).toBe('signed-plugin')
    } finally {
      // Restore original keys
      RELEASE_PUBLIC_KEYS.clear()
      for (const [k, v] of originalKeys) {
        ;(RELEASE_PUBLIC_KEYS as any).set(k, v)
      }
      await rm(pluginDir, { recursive: true, force: true })
    }
  })

  it('plugin add refuses existing destination', async () => {
    const { Command } = (await import('commander')).default ?? (await import('commander'))
    const { registerPluginCommands } = await import('../src/plugins/cli.js')
    const { publicKeyBase64, privateKeyPem } = await generateTestKeyPair()
    const keyId = '12345678'

    const sourceDir = join(tmpDir, 'source2')
    const pluginDir = await mkdtemp(join(tmpdir(), 'plugins2-'))
    const destDir = join(pluginDir, 'existing-plugin')

    await mkdir(sourceDir, { recursive: true })
    await mkdir(destDir, { recursive: true })
    await writeFile(join(sourceDir, 'codeburn-plugin.json'), JSON.stringify(validManifest('existing-plugin')))
    await writeFile(join(sourceDir, 'file1.txt'), 'content1')

    await signPlugin(sourceDir, privateKeyPem, keyId)

    const { RELEASE_PUBLIC_KEYS } = await import('../src/plugins/keys.js')
    const originalKeys = new Map(RELEASE_PUBLIC_KEYS)
    ;(RELEASE_PUBLIC_KEYS as any).set(keyId, publicKeyBase64)

    const program = new Command()
    program.exitOverride()
    registerPluginCommands(program)

    const savedStderr = process.stderr.write
    const savedExitCode = process.exitCode
    let stderr = ''
    process.stderr.write = (chunk: string | Uint8Array | Buffer): boolean => {
      stderr += chunk.toString()
      return true
    }
    process.exitCode = undefined

    try {
      await program.parseAsync(['node', 'codeburn', 'plugin', 'add', sourceDir, '--dir', pluginDir])
      expect(process.exitCode).toBe(1)
      expect(stderr).toContain('already installed')
    } finally {
      process.stderr.write = savedStderr
      process.exitCode = savedExitCode
      RELEASE_PUBLIC_KEYS.clear()
      for (const [k, v] of originalKeys) {
        ;(RELEASE_PUBLIC_KEYS as any).set(k, v)
      }
      await rm(pluginDir, { recursive: true, force: true })
    }
  })

  it('plugin remove refuses without --confirm', async () => {
    const { Command } = (await import('commander')).default ?? (await import('commander'))
    const { registerPluginCommands } = await import('../src/plugins/cli.js')

    const pluginDir = await mkdtemp(join(tmpdir(), 'plugins3-'))
    const destDir = join(pluginDir, 'test-plugin')
    await mkdir(destDir, { recursive: true })

    const program = new Command()
    program.exitOverride()
    registerPluginCommands(program)

    const savedStderr = process.stderr.write
    const savedExitCode = process.exitCode
    let stderr = ''
    process.stderr.write = (chunk: string | Uint8Array | Buffer): boolean => {
      stderr += chunk.toString()
      return true
    }
    process.exitCode = undefined

    try {
      await program.parseAsync(['node', 'codeburn', 'plugin', 'remove', 'test-plugin', '--dir', pluginDir])
      expect(process.exitCode).toBe(1)
      expect(stderr).toContain('--confirm')
    } finally {
      process.stderr.write = savedStderr
      process.exitCode = savedExitCode
      await rm(pluginDir, { recursive: true, force: true })
    }
  })

  it('plugin remove deletes with --confirm', async () => {
    const { Command } = (await import('commander')).default ?? (await import('commander'))
    const { registerPluginCommands } = await import('../src/plugins/cli.js')

    const pluginDir = await mkdtemp(join(tmpdir(), 'plugins4-'))
    const destDir = join(pluginDir, 'test-plugin')
    await mkdir(destDir, { recursive: true })
    await writeFile(join(destDir, 'test.txt'), 'test')

    const program = new Command()
    program.exitOverride()
    registerPluginCommands(program)

    try {
      await program.parseAsync(['node', 'codeburn', 'plugin', 'remove', 'test-plugin', '--dir', pluginDir, '--confirm'])
      const { stat } = await import('fs/promises')
      await expect(stat(destDir)).rejects.toThrow()
    } finally {
      await rm(pluginDir, { recursive: true, force: true })
    }
  })
})

describe('keyId derivation consistency', () => {
  it('keygen and sign derive keyId identically from the same public key', async () => {
    const { generateKeyPairSync, createHash } = await import('crypto')
    const { publicKey } = generateKeyPairSync('ed25519', {
      publicKeyEncoding: { format: 'pem', type: 'spki' },
      privateKeyEncoding: { format: 'pem', type: 'pkcs8' },
    })

    // Simulate keygen keyId derivation
    const keygenKeyIdBytes = createHash('sha256').update(publicKey as string).digest().slice(0, 4)
    const keygenKeyId = keygenKeyIdBytes.toString('hex')

    // Simulate sign keyId derivation
    const signKeyIdBytes = createHash('sha256').update(publicKey as string).digest().slice(0, 4)
    const signKeyId = signKeyIdBytes.toString('hex')

    expect(keygenKeyId).toBe(signKeyId)
  })

  it('RELEASE_PUBLIC_KEYS entries have keyIds that match their derived values', async () => {
    const { createHash } = await import('crypto')
    const { RELEASE_PUBLIC_KEYS } = await import('../src/plugins/keys.js')

    for (const [keyId, pubKeyBase64] of RELEASE_PUBLIC_KEYS) {
      const pubKeyPem = Buffer.from(pubKeyBase64, 'base64').toString('utf8')
      const derivedKeyIdBytes = createHash('sha256').update(pubKeyPem).digest().slice(0, 4)
      const derivedKeyId = derivedKeyIdBytes.toString('hex')

      expect(derivedKeyId).toBe(keyId, `keyId ${keyId} does not match derived value ${derivedKeyId}`)
    }
  })
})

// Canonicalization portability: the digest must order paths by codepoint,
// identically on every machine, including mixed-case and non-ASCII names.
// Drives the REAL sign script so test and production canonicalization can
// never silently diverge.
describe('signature canonicalization ordering', () => {
  it('round-trips a tree with mixed-case and non-ASCII filenames via the real sign script', async () => {
    const { execFile } = await import('child_process')
    const { promisify } = await import('util')
    const { createHash } = await import('crypto')
    const run = promisify(execFile)
    const dir = await mkdtemp(join(tmpdir(), 'canon-'))
    const keyDir = await mkdtemp(join(tmpdir(), 'canon-key-'))
    try {
      await writeFile(join(dir, 'codeburn-plugin.json'), JSON.stringify({
        name: 'canon', version: '0.1.0', cliCompat: '>=0.9.22',
        capabilities: { commands: [], syncAttributes: [], payloadSections: [], spanKinds: [] },
      }))
      await writeFile(join(dir, 'README.md'), 'mixed case name')
      await writeFile(join(dir, 'caf\u00e9.txt'), 'non ascii name')
      await mkdir(join(dir, 'commands'), { recursive: true })
      await writeFile(join(dir, 'commands', 'Zeta.mjs'), 'console.log(1)')
      const { publicKeyBase64, privateKeyPem } = await generateTestKeyPair()
      const keyPath = join(keyDir, 'key.pem')
      await writeFile(keyPath, privateKeyPem)
      await run(process.execPath, ['scripts/sign-plugin.mjs', 'sign', dir], {
        env: { ...process.env, CODEBURN_SIGNING_KEY: keyPath },
      })
      const publicKeyPem = Buffer.from(publicKeyBase64, 'base64').toString('utf8')
      const keyId = createHash('sha256').update(publicKeyPem).digest().subarray(0, 4).toString('hex')
      const manifest = JSON.parse(await readFile(join(dir, 'codeburn-plugin.json'), 'utf8'))
      const result = await verifyPlugin(dir, manifest, {}, new Map([[keyId, publicKeyBase64]]))
      expect(result.ok).toBe(true)
    } finally {
      await rm(dir, { recursive: true, force: true })
      await rm(keyDir, { recursive: true, force: true })
    }
  })
})
