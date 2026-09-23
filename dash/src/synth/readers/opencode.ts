// OpenCode reader boundary.
//
// OpenCode is installed with experimental OpenTelemetry disabled. It has no
// supported session-file signal to read, so exposing an empty reader makes the
// limitation explicit without turning missing evidence into a zero-token turn.

import type { ContentReader } from './types.js'

/** Why OpenCode cannot currently contribute collectable session evidence. */
export const OPENCODE_NOT_COLLECTABLE_REASON =
  'OpenCode OpenTelemetry is disabled; telemetry is not enabled for collection.'

/** Public reader shape for the currently non-collectable OpenCode surface. */
export const opencodeReader: ContentReader = {
  async *read(): AsyncGenerator<never> {
    // No supported file-format evidence exists while telemetry is disabled.
  },
}

export default opencodeReader
