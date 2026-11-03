// The OpenCode capture plugin's consent writer.
//
// The consent file is shared with the other capture plugins, so the implementation lives in
// `../../consent-file.ts` and this module re-exports it. The OpenCode plugin keeps its own
// module name so its imports, its bundle and its tests do not change.

export { createConsentIfAbsent, readConsentState, writeContentConsent, writeFileAtomic } from '../../consent-file.js'
export type { ConsentState } from '../../consent-file.js'
