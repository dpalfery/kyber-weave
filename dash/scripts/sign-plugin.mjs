#!/usr/bin/env node
/**
 * Plugin signing utility: keygen and sign.
 *
 * keygen --out <file>  - generate a keypair, print keyId + public key, write private PEM
 * sign <dir>           - sign a plugin directory using CODEBURN_SIGNING_KEY env var
 */

import { createPrivateKey, createPublicKey, randomBytes } from 'crypto'
import { readdir, readFile, stat, writeFile } from 'fs/promises'
import { join } from 'path'
import process from 'process'

const command = process.argv[2]

if (command === 'keygen') {
  await handleKeygen()
} else if (command === 'sign') {
  await handleSign()
} else {
  console.error('Usage: node scripts/sign-plugin.mjs keygen --out <file>')
  console.error('       node scripts/sign-plugin.mjs sign <dir>')
  process.exit(1)
}

async function handleKeygen() {
  const outIdx = process.argv.indexOf('--out')
  if (outIdx < 0 || outIdx + 1 >= process.argv.length) {
    console.error('Usage: node scripts/sign-plugin.mjs keygen --out <file>')
    process.exit(1)
  }
  const outFile = process.argv[outIdx + 1]

  // Generate a new keypair
  const { generateKeyPairSync } = await import('crypto')
  const { privateKey: privKeyObj, publicKey: pubKeyObj } = generateKeyPairSync('ed25519')

  // Export keys in appropriate formats
  const privateKeyPem = privKeyObj.export({ format: 'pem', type: 'pkcs8' })
  const publicKeyPem = pubKeyObj.export({ format: 'pem', type: 'spki' })

  // Base64 encode the PEM public key for storage
  const pubKeyBase64 = Buffer.from(publicKeyPem).toString('base64')

  // Derive keyId the same way sign does: sha256 of PEM, first 4 bytes hex
  const { createHash } = await import('crypto')
  const keyIdBytes = createHash('sha256').update(publicKeyPem).digest().slice(0, 4)
  const keyId = keyIdBytes.toString('hex')

  // Write private key PEM to file
  await writeFile(outFile, privateKeyPem, 'utf8')

  // Print to stdout
  console.log(`keyId: ${keyId}`)
  console.log(`public: ${pubKeyBase64}`)
}

async function handleSign() {
  const pluginDir = process.argv[3]
  if (!pluginDir) {
    console.error('Usage: node scripts/sign-plugin.mjs sign <dir>')
    process.exit(1)
  }

  const sigKeyPath = process.env.CODEBURN_SIGNING_KEY
  if (!sigKeyPath) {
    console.error('CODEBURN_SIGNING_KEY not set')
    process.exit(1)
  }

  // Read the plugin manifest
  const manifestFile = join(pluginDir, 'codeburn-plugin.json')
  const manifestRaw = JSON.parse(await readFile(manifestFile, 'utf8'))
  const { name, version } = manifestRaw
  if (!name || !version) {
    console.error('Plugin manifest missing name or version')
    process.exit(1)
  }

  // Get file list: all regular files except codeburn-plugin.sig
  const files = await getFilesList(pluginDir)

  // Build the canonical signing digest
  const digest = computeDigest(name, version, files)

  // Read private key and sign
  const { sign } = await import('crypto')
  const privKeyPem = await readFile(sigKeyPath, 'utf8')
  const privKey = createPrivateKey(privKeyPem)
  const signature = sign(null, Buffer.from(digest), privKey)
  const signatureBase64 = signature.toString('base64')

  // Extract keyId from the public key derived from the private key
  const pubKey = createPublicKey(privKey)
  const pubKeyPem = pubKey.export({ format: 'pem', type: 'spki' })
  // Hash the PEM to get a consistent keyId
  const { createHash } = await import('crypto')
  const keyIdBytes = createHash('sha256').update(pubKeyPem).digest().slice(0, 4)
  const keyId = keyIdBytes.toString('hex')

  // Write signature file
  const sigFile = join(pluginDir, 'codeburn-plugin.sig')
  const sigData = {
    alg: 'ed25519',
    keyId,
    signature: signatureBase64,
  }
  await writeFile(sigFile, JSON.stringify(sigData), 'utf8')
  console.log(`Signed ${pluginDir}`)
}

// Codepoint order, never locale order: this list feeds the signed digest,
// so canonicalization must be identical on every machine and ICU build.
async function getFilesList(dir) {
  const files = []

  async function walk(baseDir, relativePath) {
    try {
      const entries = await readdir(baseDir, { withFileTypes: true })
      for (const entry of entries.sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0)) {
        // Exclude signature file and sections directory (runtime-mutable plugin output)
        if (entry.name === 'codeburn-plugin.sig') continue
        if (entry.name === 'sections') continue

        const fullPath = join(baseDir, entry.name)
        const relPosixPath = relativePath ? `${relativePath}/${entry.name}` : entry.name

        if (entry.isFile()) {
          let content
          try {
            content = await readFile(fullPath)
          } catch (err) {
            // Signing over an incomplete file list would produce a valid
            // signature for a broken plugin. Fail loudly instead.
            throw new Error(`cannot read ${relPosixPath} while signing: ${err.message}`)
          }
          const sha256 = await hashSha256(content)
          files.push({ path: relPosixPath, sha256 })
        } else if (entry.isDirectory()) {
          await walk(fullPath, relPosixPath)
        }
      }
    } catch (err) {
      throw new Error(`cannot read directory while signing: ${err.message}`)
    }
  }

  await walk(dir, '')
  files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0)
  return files
}

function computeDigest(name, version, files) {
  const canonical = JSON.stringify({ name, version, files })
  return canonical
}

async function hashSha256(data) {
  const crypto = await import('crypto')
  return crypto.createHash('sha256').update(data).digest('hex')
}
