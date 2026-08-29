# DeepSeek Harness (dsh)

DeepSeek's open-source agent harness (`dsh`, npm `@deepseek-ai/dsh`). Unrelated to the [CodeWhale](codewhale.md) provider, which reads the DeepSeek desktop app.

- **Source:** `src/providers/dsh.ts`
- **Loading:** eager (`src/providers/index.ts`)
- **Test:** `tests/providers/dsh.test.ts`

## Where it reads from

| Level | Env var | Default |
|---|---|---|
| sessions | — | `<root>/sessions` |
| root | `DSH_HOME` | `~/.dsh` |

An empty `DSH_HOME` is treated as unset. `probeRoots()` reports the resolved sessions dir, so `codeburn doctor` distinguishes "dsh not installed" from "`DSH_HOME` pointing somewhere empty".

## Storage format

```
sessions/--<slugified-cwd>--/<session-id>/
  session.jsonl.zstd     default (compression: zstd)
  session.jsonl          when compression: none
```

Both variants are read; a session directory never holds both. The log is append-only JSONL whose first line is the session header:

```jsonc
{ "type": "session", "version": 0, "id": "...", "createdAt": 1783352050748,
  "cwd": "/home/u/proj", "parentSession": "...", "seedLength": 3, "delegationDepth": 0 }
```

`cwd` becomes `projectPath` / `workingDirectory` (git-repo attribution) and its last segment the project name.

Every later line is one event `{ type, seq, time, data }`. The parser reads:

| Event | Used for |
|---|---|
| `turn/start` | current turn number |
| `user/message` | the turn's preview, when `data.source.kind === 'user'` |
| `request/header` | `data.header.config.model` — the model for steps that follow |
| `assistant/chunk` with `chunk.type === 'usage'` | streamed usage sample for `(turn, step)` |
| `assistant/message` | final usage for `(turn, step)`, plus `data.message.source.model` |
| `tool/call` | tool names, bash commands, skill names |

One parsed call per `(turn, step)` — one model call and the tools it requested. Dedup key: `dsh:<sessionId>:<turn>:<step>`.

`.zstd` logs are a concatenation of **independent** zstd frames, one per write batch, so they are decoded frame by frame behind a structural frame scan ported from `@deepseek-ai/dsh-session-persistence-jsonl`. Needs Node 22.15+ for `zlib.zstdDecompressSync`; below that dsh is skipped with a notice instead of counted as $0.

## Caching

None at the provider level; the log file is the cached source path and the normal parser/cache layers apply. Cache invalidates on `DSH_HOME` (`PROVIDER_ENV_VARS`) and on parser changes (`PROVIDER_PARSE_VERSIONS`).

## Quirks

- **DSH is a developer preview.** `SESSION_FORMAT_VERSION` is pinned at `0` with "no compatibility implied" upstream, and breaking changes are expected. The parser reads version `0` only and skips a log stamped with anything else, with a notice — reading a bumped format under today's assumptions would report confident wrong numbers. **A version bump upstream means this parser needs updating, not just relaxing the check.**
- **The JSONL backend only.** DSH also ships an opt-in SQLite persistence backend (`@deepseek-ai/dsh-session-persistence-sqlite`); it is not the default and is not read.
- **DSH records tokens, never dollars.** `usage` is `{ inputTokens, outputTokens, cacheReadTokens?, cacheWriteTokens?, reasoningTokens? }` with no cost field, so every call is priced from the shared tables. Reasoning bills at the output rate (same as Gemini and Hermes): `outputTokens + reasoningTokens` goes into `calculateCost`, while the two stay separate on the emitted call. Tokens are the provider's own exact counts, so `costIsEstimated` stays false.
- **`assistant/message` usage wins over the `assistant/chunk` sample** for the same `(turn, step)` — the two are adjacent reports of one API call, not two calls. A late chunk never overwrites a final report, so the two are never summed.
- **The model comes from the message, not the request.** `data.message.source.model` is what actually served the step; `request/header` only describes the request DSH was about to make, and is the fallback when a message names no model. The `provider` field there (`deepseek-official`) is the upstream LLM route, not the tool — the codeburn provider name is always `dsh`.
- **A forked session's log replays its parent's events.** The header's `parentSession` + `seedLength` mark that prefix; codeburn parses the parent's own log as its own session, so events with `seq < seedLength` are skipped to avoid billing the same calls twice.
- **`user/message` also carries agent-injected context** (runtime snapshots, skill bodies, file-change notices) under `source.kind: 'plugin'`. Only `kind: 'user'` messages become the preview.
- **Delta chunks are packed.** Runs of streamed deltas are stored as `text-chunks` / `reasoning-chunks` / `tool-call-chunks` storage rows rather than one event per line. They carry no usage and no tool identity the `tool/call` event lacks, so they are ignored — as is any event type the parser does not know.
- **A torn final zstd frame is ignored.** A crashed writer leaves an incomplete trailing frame; the complete frames before it parse normally. A structurally corrupt file is skipped whole with a notice rather than throwing.

## When fixing a bug here

1. Reproduce with a minimal session dir: `sessions/--proj--/<id>/session.jsonl` (uncompressed is easiest to hand-write).
2. `tests/fixtures/dsh/bash-tool-turn.jsonl` is the upstream `examples/acp-agent/tests/snapshots/bash-tool-turn/session.jsonl` snapshot with its template placeholders filled in — refresh it from the DSH repo when the format moves.
3. Run `tests/providers/dsh.test.ts`.
4. `.zstd` fixtures must compress **each batch separately**; one `zstdCompressSync` over the whole file is a single-frame layout DSH never writes.
