import { readFile, writeFile, mkdir, rename } from 'fs/promises'
import { join } from 'path'
import { homedir } from 'os'
import { randomBytes } from 'crypto'

export type KyberdashConfig = {
  devin?: {
    acuUsdRate?: number
  }
  modelAliases?: Record<string, string>
  // Rates are stored as USD per 1,000,000 tokens; models.ts converts them to per-token ModelCosts.
  priceOverrides?: Record<string, { input: number; output: number; cacheRead?: number; cacheCreation?: number }>
  // Extra Claude config directories to aggregate usage across (e.g. work /
  // personal accounts). Honored by getClaudeConfigDirs() below the
  // CLAUDE_CONFIG_DIRS/CLAUDE_CONFIG_DIR env vars. Lets the macOS menubar (a
  // GUI app that doesn't inherit the user's shell env) configure multi-account
  // aggregation without injecting env into every spawned subprocess.
  claudeConfigDirs?: string[]
  // Map raw local-model names (e.g. "llama3.1:8b") to the paid model we would
  // price the call against (e.g. "gpt-4o"). The local call still costs $0; we
  // track what the same tokens would have cost on the baseline so the dashboard
  // can show "saved $X by running locally". Distinct from modelAliases which
  // rewrites actual spend.
  localModelSavings?: Record<string, string>
  // Model ids whose $0 cost is correct because they are billed as a
  // subscription / flat-rate product, not missing LiteLLM rows. Distinct from
  // modelAliases (which invent per-token spend) and localModelSavings
  // (counterfactual local baseline). Edited by hand: the subcommands that wrote
  // these keys went with the spend product, but loadConfig still reads them.
  flatRateModels?: string[]
  // Opt-outs from the built-in flat-rate classifier. Recording a built-in SKU
  // here lets a false positive warn again without waiting for a release.
  flatRateModelsRemoved?: string[]
  // Spend budgets are stored in the configured display currency, not USD.
  budget?: {
    daily?: number
    weekly?: number
    monthly?: number
  }
  // Absolute directory prefixes whose Claude Code sessions are routed through a
  // subscription-backed LLM proxy (e.g. GitHub Copilot via ANTHROPIC_BASE_URL;
  // tools like claude-code-over-github-copilot / claudegate). The JSONL records
  // the underlying model name and no endpoint, so codeburn cannot auto-detect
  // proxying — the user declares it here, scoped by the project's canonical cwd.
  // Matching projects keep their full API-rate `totalCostUSD` (the billable /
  // would-be figure is never destroyed) but expose `totalProxiedCostUSD` so the
  // report can show what was subscription-covered and the net out-of-pocket.
  // Matched against the canonical project path: prefix on a path-segment
  // boundary, case-insensitive, trailing-slash and backslash tolerant.
  proxyPaths?: string[]
}

// All KyberDash state lives under ~/.kyberdash (R3.5). The upstream
// ~/.config/codeburn directory is neither read nor removed: a user's declared
// proxy paths do not silently carry into the renamed product, and an existing
// CodeBurn install keeps its own config.
function getConfigDir(): string {
  return join(homedir(), '.kyberdash')
}

function getConfigPath(): string {
  return join(getConfigDir(), 'config.json')
}

export async function readConfig(): Promise<KyberdashConfig> {
  try {
    const raw = await readFile(getConfigPath(), 'utf-8')
    return JSON.parse(raw) as KyberdashConfig
  } catch {
    return {}
  }
}

export async function saveConfig(config: KyberdashConfig): Promise<void> {
  await mkdir(getConfigDir(), { recursive: true })
  const configPath = getConfigPath()
  // Randomize the temp path so two simultaneous saveConfig calls (from
  // overlapping menubar + CLI runs, for example) do not race on the same
  // staging file. The previous fixed `.tmp` suffix could leave one
  // process reading partial bytes the other was mid-writing.
  const tmpPath = `${configPath}.${randomBytes(8).toString('hex')}.tmp`
  await writeFile(tmpPath, JSON.stringify(config, null, 2) + '\n', 'utf-8')
  await rename(tmpPath, configPath)
}

export function getConfigFilePath(): string {
  return getConfigPath()
}
