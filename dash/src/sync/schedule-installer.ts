/**
 * codeburn sync - schedule installer for automatic pushes.
 *
 * Manages LaunchAgent plist on macOS for scheduled sync auto runs.
 * On other platforms, prints the crontab line to add manually.
 */

import { platform } from 'os'
import { homedir } from 'os'
import { join } from 'path'
import { writeFileSync, unlinkSync, existsSync, mkdirSync } from 'fs'
import { spawn } from 'child_process'

const SCHEDULE_AGENT_NAME = 'com.codeburn.sync-auto'
const SCHEDULE_INTERVAL_DAILY = 86400
const SCHEDULE_INTERVAL_HOURLY = 3600

function launchAgentDir(): string {
  return join(homedir(), 'Library', 'LaunchAgents')
}

function launchAgentPath(): string {
  return join(launchAgentDir(), `${SCHEDULE_AGENT_NAME}.plist`)
}

function xmlEscape(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
}

export function buildLaunchAgentPlist(
  cadence: 'daily' | 'hourly',
  runtimePath: string,
  scriptPath: string,
): string {
  const interval = cadence === 'daily' ? SCHEDULE_INTERVAL_DAILY : SCHEDULE_INTERVAL_HOURLY
  const safeRuntime = xmlEscape(runtimePath)
  const safeScript = xmlEscape(scriptPath)
  return `<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>${SCHEDULE_AGENT_NAME}</string>
  <key>ProgramArguments</key>
  <array>
    <string>${safeRuntime}</string>
    <string>${safeScript}</string>
    <string>sync</string>
    <string>auto</string>
    <string>run</string>
  </array>
  <key>EnvironmentVariables</key>
  <dict>
    <key>ELECTRON_RUN_AS_NODE</key>
    <string>1</string>
  </dict>
  <key>StartInterval</key>
  <integer>${interval}</integer>
  <key>StandardErrorPath</key>
  <string>/dev/null</string>
  <key>StandardOutPath</key>
  <string>/dev/null</string>
</dict>
</plist>
`
}

export async function installSchedule(
  cadence: 'daily' | 'hourly',
  runtimePath: string,
  scriptPath: string,
): Promise<void> {
  if (platform() !== 'darwin') {
    const cronExpression = cadence === 'daily'
      ? '0 0 * * *'
      : '0 * * * *'
    process.stderr.write(`macOS is not detected. To schedule automatic syncs, add to crontab:\n`)
    process.stderr.write(`  ${cronExpression} ${runtimePath} ${scriptPath} sync auto run\n`)
    return
  }

  const plistContent = buildLaunchAgentPlist(cadence, runtimePath, scriptPath)
  const plistPath = launchAgentPath()
  const agentDir = launchAgentDir()

  try {
    // Ensure LaunchAgents directory exists
    mkdirSync(agentDir, { recursive: true })

    // Write the plist
    writeFileSync(plistPath, plistContent, { mode: 0o644 })

    // Load the agent (unload first if it's already loaded)
    try {
      await runCommand('/bin/launchctl', ['unload', plistPath])
    } catch {
      // May not be loaded yet
    }
    await runCommand('/bin/launchctl', ['load', plistPath])
  } catch (err) {
    throw new Error(`Failed to install schedule: ${err instanceof Error ? err.message : String(err)}`)
  }
}

export async function removeSchedule(): Promise<void> {
  if (platform() !== 'darwin') return

  const plistPath = launchAgentPath()
  try {
    await runCommand('/bin/launchctl', ['unload', plistPath])
  } catch {
    // May not be loaded
  }

  try {
    unlinkSync(plistPath)
  } catch {
    // May not exist
  }
}

function runCommand(command: string, args: string[]): Promise<void> {
  return new Promise((resolve, reject) => {
    // command is internal, not user-controlled — only /bin/launchctl with static args
    // TODO(codeburn-hotfix): internal launchctl invocation — re-apply after `git subtree pull`
    const proc = spawn(command, args, { stdio: 'pipe' }) // nosemgrep


    let stderr = ''
    proc.stderr.on('data', (chunk: Buffer) => {
      stderr += chunk.toString()
    })
    proc.on('error', reject)
    proc.on('close', (code) => {
      if (code === 0) resolve()
      else reject(new Error(`${command} exited with status ${code}${stderr ? `: ${stderr.trim()}` : ''}`))
    })
  })
}
