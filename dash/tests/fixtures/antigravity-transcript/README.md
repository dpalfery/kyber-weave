# Antigravity transcript fixtures (synthetic)

Every value in this directory is **synthetic**. The paths, tool names, commands,
queries, file contents, timestamps and conversation id are invented for the tests.
No real user content, transcript, workspace or conversation is stored here, and none
may be added.

## Layout

The tree mirrors the on-disk shape the reader expects:

- `gen-metadata.json`: three `gen_metadata` rows, one per model call. Each row is the
  protobuf bytes of a `chatModel` (field 1) carrying `usage`, `chatStartMetadata.created_at`
  and a `last_step_index` attribute under field 20. The `last_step_index` (lsi) is the last
  transcript step the model saw as input, so the model's own `PLANNER_RESPONSE` sits at
  `lsi + 1`. The rows carry lsi 2, 5 and 8.
- `brain/fixture-segments/.system_generated/logs/transcript.jsonl`: the sibling transcript.

## What the segments encode

Call N owns the transcript steps in `(lsi[N], lsi[N+1]]`; the last call's range is open
above. With the rows above:

| Call (lsi) | Range       | Planner step | Tool call        | Result step |
|------------|-------------|--------------|------------------|-------------|
| 2          | (2, 5]      | 3            | `run_command`    | 4 (GENERIC) |
| 5          | (5, 8]      | 6            | `view_file`      | 7 (GENERIC) |
| 8          | (8, +inf)   | 9            | `grep_search`    | 10 (GENERIC)|

Each tool name differs, so an off-by-one attachment (a call receiving its predecessor's
segment) is visible in every assertion.

Step 1 is a `PLANNER_RESPONSE` with no `tool_calls`; a reader must skip it. Steps 2, 5
and 8 are absent: the transcript is not contiguous, and nothing may be inferred for them.

## Argument encoding

Every `args` value is a JSON-encoded string that itself holds a JSON-encoded value, so
it must be decoded twice to reach the plain value. For example `"12"` in the file
decodes once to the string `"12"` and twice to the number `12`.

## Variants built inside the tests

The tests derive other shapes from these files rather than storing more fixtures:
a transcript that starts at step 6, a segment with two tool calls, and a transcript
with an unparseable line. Those variants are also synthetic.
