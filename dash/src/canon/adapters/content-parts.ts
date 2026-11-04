// The one way an adapter files content onto a record.
//
// WHY A SHARED MODULE: two plugin adapters file content the same way — the
// prompt as conversation history, the assembled system prompt, the serialized
// tool definitions — and each had its own copy of the three lines that append
// a part and recompute the flat content. A second copy is a second place to
// forget the order, so the helper lives here and both adapters use it.

import { contentFromParts, type CanonicalRecord, type ContentPart } from '../types.js'

/** Appends a content part after every existing part, recomputing order and content. */
export function appendPart(record: CanonicalRecord, part: Omit<ContentPart, 'order'>): void {
  const existing = record.parts ?? []
  const order = existing.reduce((max, entry) => Math.max(max, (entry.order ?? 0) + 1), 0)
  const parts: ContentPart[] = [...existing, { ...part, order }]
  record.parts = parts
  record.content = contentFromParts(parts)
}
