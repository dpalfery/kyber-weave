---
id: todo/macos-developer-id-signing
title: Sign and notarize the macOS binaries with the team Developer ID
doc-type: todo
component: Distribution
owner: dpalfery
last-reviewed: 2026-09-18
status: draft
---

# Sign and notarize the macOS binaries with the team Developer ID

This is **context for planning the work, not a plan**. It records what is known, what needs
deciding, and where the work connects to other work. It does not sequence tasks.

## Why this exists

Every macOS binary a release ships today is ad-hoc signed, with no team identity. On this
machine `codesign -dv` against the installed `kyber-weave` and `kyber-weave-mcp` reports
`Signature=adhoc` and `TeamIdentifier=not set`. Gatekeeper cannot tell those binaries from
anyone else's, so `install.sh` removes the quarantine attribute to make them run. That is a
workaround, not a trust chain.

The team now has an Apple Developer account, team id **`J2UNNQ466J`**. Two pieces of work
depend on it:

1. **The KyberDash tray.** The
   [KyberDash context surfaces specification](../specs/kyberdash-context-surfaces/requirements.md)
   requires a macOS tray signed with this team's Developer ID, notarized and stapled. Its
   release job must *fail* when the credentials are absent (Requirements 12.2–12.3), so that
   job cannot pass until part A below is done.
2. **The CLI binaries** — `kyber-weave`, `kyber-weave-mcp` and `kyberdash`. This is part B.
   The specification deliberately leaves it out, and it can proceed independently.

Part A is the shared prerequisite. It is the part to do first, because it runs in parallel
with the specification.

## Part A — credentials in the release environment

What has to exist:

- **A Developer ID Application certificate** for team `J2UNNQ466J`, exported with its
  private key as a password-protected `.p12`. Apple lets only the account holder create
  Developer ID certificates, so this step cannot be delegated to another team role.
- **An App Store Connect API key** with Developer access, created under Users and Access →
  Integrations. It is used for notarization. The `.p8` private key can be downloaded exactly
  once. The API key is preferable to an Apple ID with an app-specific password, because it
  is not tied to a person's login and can be revoked on its own.
- **Secrets on the `release` GitHub environment.** Both `release.yml` jobs that publish
  already declare `environment: release`, so secrets scoped there are not exposed to CI
  runs on pull requests.

Suggested secret names follow the environment variables Tauri 2's bundler reads, verified
against the Tauri documentation, so the tray job can pass them straight through:

| Secret | Holds |
|---|---|
| `APPLE_CERTIFICATE` | The `.p12`, base64-encoded |
| `APPLE_CERTIFICATE_PASSWORD` | The `.p12` export password |
| `APPLE_SIGNING_IDENTITY` | The identity string, e.g. `Developer ID Application: <name> (J2UNNQ466J)` |
| `APPLE_API_KEY` | The API key's Key ID |
| `APPLE_API_ISSUER` | The Issuer ID shown above the keys table |
| `APPLE_API_KEY_P8` | The `.p8` contents; the job writes it to a file and sets `APPLE_API_KEY_PATH` to that file |

A keychain password does not need to be a secret. The job can generate a random one for the
temporary keychain it creates and deletes.

**One trap to design around.** When notarization credentials are missing, Tauri's macOS
bundler logs a warning and skips notarization rather than failing. That is its documented
behaviour in `tauri-bundler`. A job that relies on the bundler to fail closed will
therefore publish an unnotarized app. The workflow has to check notarization itself, for
example with `spctl --assess` and `stapler validate` on the built app.

## Part B — signing the CLI binaries

What is known:

- **Where each binary is built.** The .NET `kyber-weave` and `kyber-weave-mcp` binaries for
  `osx-x64` and `osx-arm64` are cross-published on **`ubuntu-latest`** (the `build` job).
  The ad-hoc signature they carry comes from the .NET SDK, not from `codesign`. `kyberdash`
  is built on `macos-14` (the `build-kyberdash` job). There, `postject` invalidates the Node
  signature and the job re-signs it ad-hoc with `codesign --sign -`.
- **What notarization requires.** Every submitted binary needs the hardened runtime and a
  secure timestamp. A bare Mach-O executable is submitted inside a zip, and it cannot carry a
  stapled ticket. Gatekeeper then checks the ticket online on first run, so an offline first
  run of a freshly downloaded CLI can still be refused.
- **Entitlements.**
  - .NET apps not published as Native AOT need `com.apple.security.cs.allow-jit` under the
    hardened runtime ([Microsoft: publish .NET apps for macOS](https://learn.microsoft.com/dotnet/core/deploying/macos)).
  - The Node SEA binary is V8 and needs JIT-related entitlements too. Take the exact set
    from the official `nodejs.org` macOS build that `build-kyberdash` downloads
    (`codesign -d --entitlements -` on it), not from a Homebrew Node, which is ad-hoc signed
    and carries none.
- **`install.sh` removes quarantine** from all three binaries (the block after
  `# macOS quarantines files downloaded by some tools`).

What needs deciding:

- **Where the .NET binaries get signed.** `codesign` and `notarytool` are macOS tools. Either
  the `osx-*` legs of `build` move to a macOS runner, or signing happens on Linux with a
  cross-platform signer. The second option is a new tool dependency and needs the same
  justification any new dependency does.
- **Whether `install.sh` keeps removing quarantine** once the binaries are notarized. It
  should probably stop, and verify the team id instead, the way the specification's
  Requirement 12.6 does for the tray.
- **Whether the self-updater verifies signatures.** It checks SHA-256 today. Checking the
  team id as well would close the gap between "the file we published" and "a file signed by
  us".

## Where the seams are

- `.github/workflows/release.yml` — the `build` matrix (`osx-*` legs), `build-kyberdash`
  (the ad-hoc `codesign` step), and the future tray job.
- `scripts/install.sh` — quarantine handling and any team-id verification.
- `src/KyberWeave.Cli/Update/` — the self-updater, if it gains signature checks. Changing it
  triggers the local release loop in [distribution](../distribution.md#verifying-a-release-locally).
- [`kyberdash-local-release-loop`](kyberdash-local-release-loop.md) — the loop cannot build
  `kyberdash`, and cannot sign anything with a Developer ID either, so signed paths are
  verifiable only in a real release run.
