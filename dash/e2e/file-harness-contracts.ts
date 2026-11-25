// The file-import harness fixtures: native files per harness plus the contract stating every
// dashboard section as measured or as an explicit not-measurable state (D5, D6).
import { FILE_HARNESS_JSON_FIXTURES } from './file-harness-json.js'
import { FILE_HARNESS_JSONL_FIXTURES } from './file-harness-jsonl.js'
import { FILE_HARNESS_SQLITE_FIXTURES } from './file-harness-sqlite.js'
import type { HarnessFile } from './file-harness-world.js'
import type { HarnessExpectationContract } from './otel-harness-payloads.js'

/** A file-import contract: the shared contract with the harness id the registry stores. */
export type FileHarnessContract = Omit<HarnessExpectationContract, 'harness'> & { readonly harness: string }

export type FileHarnessFixture = {
  readonly contract: FileHarnessContract
  /** Native files this session needs, relative to the isolated home. */
  readonly files?: (baseTimeMs: number) => readonly HarnessFile[]
  /** Builds binary or SQLite sources under the isolated home instead of returning files. */
  readonly build?: (homeDir: string, baseTimeMs: number) => void
}

export const FILE_HARNESS_FIXTURES: readonly FileHarnessFixture[] = [
  ...FILE_HARNESS_JSONL_FIXTURES,
  ...FILE_HARNESS_JSON_FIXTURES,
  ...FILE_HARNESS_SQLITE_FIXTURES,
]
