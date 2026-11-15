// The one way an adapter files content onto a record.
//
// WHY A SHARED MODULE: two plugin adapters file content the same way — the
// prompt as conversation history, the assembled system prompt, the serialized
// tool definitions — and each had its own copy of the three lines that append
// a part and recompute the flat content. A second copy is a second place to
// forget the order, so the helper lives here and both adapters use it.

import { contentFromParts, type CanonicalRecord, type ContentPart } from '../types.js'

/**
 * Appends a content part after every existing part, recomputing order and content.
 *
 * An empty text is not filed: `contentFromParts` drops it, so a part that
 * carried one would sit in the parts list and nowhere in the content, and the
 * two readers of a record would disagree.
 *
 * The order is the helper's own: it is the next free slot after the last
 * part, so it is always a finite integer and always after what is there.
 */
export function appendPart(record: CanonicalRecord, part: Omit<ContentPart, 'order'>): void {
  if (part.text === '') return
  const existing = record.parts ?? []
  const order = existing.reduce((max, entry) => Math.max(max, (entry.order ?? 0) + 1), 0)
  const parts: ContentPart[] = [...existing, { ...part, order }]
  record.parts = parts
  record.content = contentFromParts(parts)
}
