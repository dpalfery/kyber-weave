NO FINDINGS: I traced the caller-controlled paths and ids in the new Arbiter surfaces and found no exploitable issue at confidence 8 or above.

- **Path traversal and symlinks:** `FileTextFacts.ResolveContainedFile` first rejects any path that leaves the root lexically. It then calls `DocsRootPath.IsContainedFile` with no documentation roots, so every path segment is checked for `LinkTarget` before the file is tested for existence. Both the MCP section tool and `CommentTriageService` (via `ReadExcerpt`) go through it.
- **Unbounded reads:** The MCP section tool caps the document at 1 MiB and the comment body at `MaxTextChars`. The excerpt read is bounded at 500 characters per line and 4000 characters overall. A very large file with no newlines would only cost CPU, which is resource exhaustion and excluded.
- **Run stores:** `ArbiterRunStore` builds directory names only from SHA-256 hex digests of the run id, plan or session, so a run id can never act as a path. Items are refused above 16 KiB, retention is 14 days, and the sweep skips symlinks and never leaves `stores/`.
- **Logs and audit:** The decision record holds only fact references, never the comment body or the file text. The one caller-controlled string written to it is `commitSha`, which is a log-spoofing concern and excluded. The only new log line is the provider-configuration failure in `ProviderForRoot`, which is a one-line exception message.
- **Prompt injection:** I could not build an end-to-end attack at the required confidence.
  - A bot comment body or document text could try to steer the model toward `already-fixed` or `not-relevant`. In the comment triage, an answer other than `nitpick` or `already-fixed` falls back to `real`. In the section selector, any answer that is missing, unsure or below the floor returns every section.
  - The injection route needs the provider's own handling of asserted facts to be weak, and that handling is existing code outside this diff.
  - The impact is also bounded: a worker skips a comment or a section, and no authorization or gate decision is touched.
- **CLI commands:** `arbiter sections` and `arbiter comment` run as the local operator, who already has filesystem access. `arbiter sections` uses `NoneProvider`, so nothing is sent out.

I read the diff's new files and `DocsRootPath` only; I did not open `ArbiterLogFiles` or the provider prompt builder, which are outside the diff.

REVIEW.md sections checked: [Ground rules; R2, R4, R5, R6, R12 applied on the diff only]. I did not open REVIEW.md, so I could not confirm those ids exist.
