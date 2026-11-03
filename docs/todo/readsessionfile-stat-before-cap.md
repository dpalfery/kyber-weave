---
id: todo/readsessionfile-stat-before-cap
title: readSessionFile reads the whole file before it checks the size cap
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-21
---

# readSessionFile reads the whole file before it checks the size cap

`readSessionFile` (`dash/src/ingest/fs-utils.ts`, lines 32 to 53) reads the entire file into a buffer,
and only then compares its length with `MAX_SESSION_FILE_BYTES` (128 MiB). A file over the cap is
therefore fully allocated before it is rejected. The comment says this is deliberate, to avoid a
stat-then-read race. The race matters less than the memory cost on a very large file.

The function has about 47 callers, so the change is shared. Options: stat first and reject from the
stat, or stream and stop at the cap. Either option needs a test that an oversize file is refused
without being read whole. Weigh the race the comment describes against the allocation.
