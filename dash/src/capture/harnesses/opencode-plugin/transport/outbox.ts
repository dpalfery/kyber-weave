// The OpenCode capture plugin's outbox.
//
// The bounded outbox is shared with the other capture plugins, so the implementation lives in
// `../../transport/outbox.ts` and this module re-exports it. The OpenCode plugin keeps its own
// module name so its imports, its bundle and its tests do not change.

export { createOutbox } from '../../transport/outbox.js'
export type { Outbox, OutboxEvent, OutboxOptions, OutboxStatus } from '../../transport/outbox.js'
