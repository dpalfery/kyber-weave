# OpenCode V2 fixtures: provenance

Ground truth for the OpenCode V2 plugin and its wire contract version "3". Every file here was
captured from a live OpenCode run, or derived from one, on 2026-10-30.

## Capture

- OpenCode version: **2.2.1** (V2 plugin API). Desktop 2.2.2 shares the same background service but was not the capture target.
- Capture date: 2026-10-30 (probe times 05:15Z to 05:17Z for run A; run C at 05:28Z).
- Model used: `muse-spark-1.3-contributor` via provider `opencode-go`. No other model was called.
- Session under test: `ses_e74c398c7ffeVajzhRWZNgfR2n` (run A, 3 model steps, 2 tool calls, one prompt).
- Foreign session in the event log: `ses_e74c858a4ffevuH3zgPbKTQ0xc` (a separate session in the same service, not owned by the probe). Its events appear only in `runA.events.jsonl`, lines 63 to 93 of the probe log.
- Source: the capture folder for this investigation (`runA/`, `runC/`, `opencode-diagnosis.md`, the OpenCode 2.2.1 type packages). `runB/` was reviewed but no fixture is taken from it.

## Files

| File | Source | How it was made |
|---|---|---|
| `runA.events.jsonl` | `runA/probe-log.jsonl`, lines 1 to 62, plus 63 to 93 | Verbatim lines. Removed: `http.request`, `http.response`, `permission.hook` and every `*.delta` event (reasoning and text). Headers dropped and paths sanitized. 73 lines kept. |
| `runA.context.json` | `runA/probe-log.jsonl` lines 8, 27, 47 (`session.hook:context`), and `runA/context-system-1793337354085.txt` | System parts 0 and 2 read in full from the text dump, because the probe keeps only 2000 characters of them. Parts 1 and 3 copied from the probe. Tools object copied from the probe. The three steps were checked to carry identical values. |
| `runC.prompt-skills.jsonl` | `runC/probe-log.jsonl` line 4 (`session.hook:prompt` with `prompt.skills`) | Verbatim line, sanitized. |
| `runA.opencode-db.json` | `runA/probe-log.jsonl`: `session.step.ended` (lines 26, 46, 60) and `session.usage.updated` (line 61) | Derived. The `session_v2` table was not dumped into the capture folder, so its values are taken from the bus events it is written from. The step values sum to the totals; both match the figures recorded for this capture. |
| `runA.kyberdash-api-session.v0.json` | `runA/runA-api-session.json` | Verbatim API response, sanitized. This is today's shape, before the V3 contract. |
| `runA.kyberdash-api-session.v3-expected.json` | Not captured. Written by the live-gate test author | Expected API document for the rebuilt pipeline, one prompt (runA). Sources: `runA.opencode-db.json` (tokens, cost, steps), `runA.context.json` (tools offered, system text), `runA.events.jsonl` (prompt, title, tool calls). Its own `fixture_notes` field labels the hand-built estimates, such as the system-prompt bucket. |
| `otlp-v3-runA.consent-on.json` | Authored from the V3 contract, using the values above | Golden OTLP JSON body with content keys present. |
| `otlp-v3-runA.consent-off.json` | Same as above | Golden body with every content key absent. Metadata is identical to the consent-on body. |
| `fixtures.sanitized.test.ts` | Authored | Guard test. |

## Sanitizing applied

- The capture scratch folder prefix (the temporary-directory path that holds the session scratch) becomes `/HOME/scratch`, so the scratch project folder `oc-probe` reads `/HOME/scratch/oc-probe`. Any other temporary-directory prefix becomes `/HOME/tmp`.
- The developer's home-directory prefix becomes `/HOME`.
- Every JSON object key named `headers` is dropped (model-request hook headers).
- Nothing else in a payload was edited.
- Every byte count is computed on the sanitized text, the same text committed in `runA.events.jsonl`, not on the raw capture. The skill tool's `opencode.tool.result_bytes` is therefore 448. The raw text was 550 bytes; the scratch path that sanitizing shortens accounts for the 102-byte gap. The read tool's 56 is unaffected. Input bytes are also computed on sanitized values.

## Probe truncation (known gaps)

- The probe keeps only the first 2000 characters of any long string. It writes the envelope `{"$str": <first 2000 characters>, "$len": <original length>}`. The envelope is kept as captured in `runA.events.jsonl` and `runA.context.json`.
- **Why the probe truncates:** the probe serializer caps every string at 2000 characters when it writes the capture. The cut happens at capture time, before any sanitizing, so it is a property of the source and not of this fixture set.
- **`tools.subagent.description`** is truncated: 2000 of 8704 characters were captured. The full text does not exist anywhere in the capture folder. The probe's own summary (`extra.toolsJsonChars` in `runA.events.jsonl`) reports the full tools object at 20,353 characters; the captured object is shorter. The golden bodies use the 2000-character text for this one description. Replacing it needs a re-capture with the probe limit lifted for that field.
- `$len` counts characters before sanitizing, so a sanitized `$str` can be a few characters shorter than its 2000-character limit.

## Derived values and rules the packet left open

- `opencode.tool.input_bytes`: UTF-8 bytes of `JSON.stringify(input)`.
- `opencode.tool.result_bytes`: UTF-8 bytes of the concatenated `text` parts of `session.tool.success` `data.content`, counted on the sanitized text (see Sanitizing applied). This is the result the model receives.
- `opencode.context.skills`: parsed by the repository's own `parseAvailableSkills` (`skills-block.ts`) from the sanitized system text (37 ids).
- `kyber.skills.invoked` appears on the step where the skill tool was called, not on later steps. It is an OTLP string array.
- `opencode.model.context_limit` is absent from both goldens. No V2 capture carries a context window: the context hook's `options.maxTokens` (131072) is an output limit, not the window.
- Span order in each body follows emission: a tool at its `session.tool.success`, a step at its `session.step.ended`, the request at its terminal execution event.
- Wire encoding follows the current sender: resource `service.name` `kyberdash-opencode-plugin`, scope `kyberdash.opencode-plugin` version `1.0.0`, kind `SPAN_KIND_INTERNAL`. The request span also carries `status.code` `STATUS_CODE_OK`.
