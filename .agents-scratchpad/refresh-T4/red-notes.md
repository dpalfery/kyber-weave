# T4 RED notes

Focused tests added before implementation:

- `dash/kyber/synth/synth.test.ts` — envelope provenance, stable/in-place ids, Gemini exclusion
- `dash/kyber/synth/dedup.test.ts` — no position-only join, native-id join, OTLP counters, no guessed client
- `dash/kyber/synth/provider.test.ts` — source-unit parse errors, Gemini skip, claude-cli reader

Implementation must not start until these fail for the missing T4 seams.
