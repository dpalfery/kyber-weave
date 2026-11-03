# antigravity skills captures

Listing: one real `llm.invoke` span attribute set (`gen_ai.skills` is a single string `Active Skills: a, b, ...`; `gen_ai.usage.skill_tokens` is the harness-reported total). Invocation: no activation record exists in the local store; only the listing attributes were found.

The real Antigravity record also carries the duplicate attribute `github.copilot.tool.parameters.skill_name`, identical to `gen_ai.skills` and observed on every record. Nothing reads it as a signal; the skills tests use it as a negative guard, so it must never count as an invocation.
