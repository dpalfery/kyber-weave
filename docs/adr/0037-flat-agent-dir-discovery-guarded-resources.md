---
id: adr/0037-flat-agent-dir-discovery-guarded-resources
title: Flat Agent-Directory Discovery Guard for OpenCode and Kilo
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-19
---

# ADR 0037: Flat Agent-Directory Discovery Guard for OpenCode and Kilo

## Status

Accepted, 2026-10-19. Records the OpenCode and Kilo agent-resource rendering decisions
delivered with the co-located extension guard. Generalizes the "authored links resolve
verbatim" exception rule (ADR 0021, Copilot-global) into a property binding every future
renderer whose agent discovery registers nested markdown. Does not change how any other
target projects resources.

## Context

On the `opencode` and `kilo` targets, an agent's declared resource closure was projected
verbatim beside its principal — ten agent-reference `.md` files under
`.opencode/agents/{architect,conductor,task-reviewer}/references/` and the same ten under
`.kilo/agents/…`. Both harnesses registered every one of those nested files as a phantom
agent: the human report was that "the reference files are being displaded / deployed as
agents and not reference files".

The facts below were verified against harness source on 2026-10-19, not against
documentation alone:

**OpenCode v2** — [`anomalyco/opencode`](https://github.com/anomalyco/opencode)
`dev` branch, `packages/core/src/config/plugin/agent.ts`:

```ts
const legacySources = [
  { pattern: "{agent,agents}/**/*.md", primary: false },
  { pattern: "{mode,modes}/*.md", primary: true },
] as const
```

`discover()` scans each config root with that glob — recursive into subdirectories,
hidden files included, `.md` files only — and `decode()` derives the agent name from the
file's path relative to the config root, so a nested
`architect/references/intake-assessment.md` becomes an agent named
`architect/references/intake-assessment`. An unrestricted `Schema.String` name makes the
slashes legal, which is the phantom the human sees. `packages/schema/src/config/agent.ts`
defines a native `disabled: Schema.Boolean` whose registry loop hard-removes disabled
entries.

**Kilo (and the OpenCode v1 line)** — [`Kilo-Org/kilocode`](https://github.com/Kilo-Org/kilocode)
`main`, which carries the OpenCode v1 engine at `packages/opencode` (root
`.opencode-version` pins **v1.18.26**), `packages/opencode/src/config/agent.ts`:

```ts
for (const item of await Glob.scan("{agent,agents}/**/*.md", {
  cwd: dir, absolute: true, dot: true, symlink: true,
}))
```

The glob line carries no `kilocode_change` marker: it is inherited verbatim from OpenCode
v1.18.26, so the v1 line and Kilo recurse exactly like v2. `packages/opencode/src/agent/agent.ts`
hard-removes disabled entries from the registry under the v1 key `disable`.

Three consequences follow. First, the recursion premise holds on all three lines, so
nested references are not safe as plain `.md`. Second, a frontmatter opt-out exists on
both lines but is version-keyed (`disable` on v1/Kilo, `disabled` on v2). Third, both
globs match **only `*.md`**, with `dot: true` — so a `.markdown` extension is invisible
to discovery on both harnesses with zero content mutation, while dot-prefixing is not an
escape. Readability is unaffected either way: agents exercise the `read` tool at
predictable relative paths beside their principal, and nothing in either runtime
restricts reads inside the agents tree — the defect is misregistration, not reachability.

The human's design constraint (2026-10-19, verbatim; spelling as written) rules out the
relocation geometry:

> "i would really prefer to keep the reference files wholy owned by the agents they belong
> too. putting them in a skills folder weather visible or not makes them hard to find, no.
> is there no mechanisumm for agents to read other files?"

Co-location with the owning principal is therefore required; no skills-tree relocation.
The closing question is answered by the evidence above: agents can already read sibling
files.

This is the third recurrence of the symptom class. ZCode relocated agent closures under
`.zcode/skills/<owner>/…` with rewritten links (ADR 0021), because its agents *and*
commands roots are scanned recursively. Copilot-global kept an absolute-path form for
links read from outside any workspace (recorded as an ADR waiver: a narrow,
target-specific refinement of the ADR 0021 pattern). Now OpenCode and Kilo complete the
class — which is why the narrow-waiver precedent cuts the other way here (decision 6).

## Decision

1. **Co-location is retained; the guard is an extension, not a relocation.** Agent-owned
   resources stay exactly where `SquadResourceProjection` puts them today — beside the
   principal, under `{agents}/<owner>/…` — but every agent-owned `.md` resource deploys
   with a `.markdown` extension. The scanner globs above never match it, so the agents
   directories register only principals, while each file stays beside — and wholly owned
   by — its agent, with no content mutation and no registry participation.

2. **Authored links are rewritten closure-driven, `.md` → `.markdown`.** Only a Markdown
   link target that exactly equals a declared resource's relative path is changed, mapping
   the in-closure `.md` path to the same path with a `.markdown` extension; non-`.md`
   in-closure paths (e.g. `.txt` assets) pass through untouched. `#fragment`/`?query`
   suffixes, titles, and angle-form destinations are preserved; fenced and inline code is
   left alone. An authored `.markdown` link is already correct and its file projects
   verbatim.

3. **The deviation is recorded, not implied.** Each resource-owning agent records one
   `resource-links-rewritten` degradation (Target `opencode`/`kilo`, identities = agent
   name, digest = `BodyDigest`), quoting the harness glob and the extension guard —
   including the "no authored link required rewriting" variant — so the one place the
   projection is not verbatim is visible in the receipt.

4. **The extension map fails closed on aliasing.** A closure holding both `x.md` and
   `x.markdown` would deploy two files onto one path, so the render throws
   `SquadRenderValidationException` rather than merging them. Skill-owned resources keep
   verbatim beside-principal projection: both skill scanners admit only `*/SKILL.md`, one
   level deep, so nothing phantom appears there.

5. **Rejected alternative: frontmatter `disable`/`disabled` injection.** Exists on both
   harness lines and hard-removes the entry from the registry — but it mutates every
   deployed reference file with target- and version-specific frontmatter, and the wrong
   line's key risks schema-warning noise. The extension guard achieves the same invisibility
   with zero content mutation.

6. **Rejected alternative: ZCode-style skills-tree relocation.** The revision-1
   recommendation, rejected by the human on 2026-10-19: moving references under a skills
   folder makes them hard to find and breaks the wholly-owned-by-the-agent constraint.
   Amending ADR 0021 in place was rejected for the same reason its Context is a
   ZCode-source verification record and its decision is specifically about relocation; a
   waiver under-states a rule change of this reach. Hence a new ADR.

7. **The rule is stated for future targets.** A target whose agent discovery registers
   nested markdown must deploy agent-owned markdown resources in a form its scanner
   ignores, keeping them co-located with their principal, with the deviation recorded as
   a `resource-links-rewritten` degradation. The other flat-file agent targets (Claude,
   Cursor, Copilot project scope, Codex, Pi, Factory, Devin, Antigravity) are out of scope
   by human decision, with no deferred-work record: per-target misdiscovery is unverified
   and the right mechanism is source-dependent (extension guard, frontmatter opt-out,
   relocation — the §Context inventory above), so each future target applies this rule
   only once its own scanner is source-verified.

## Consequences

- Deployed reference files carry a `.markdown` extension on these two targets while
  canonical source keeps `.md`. Editors, GitHub, and the `read` tool all handle
  `.markdown`; the degradation record and this ADR document the intent.
- The agents directories now register only principals. A harness release that widens its
  glob beyond `*.md` would resurface the phantom — the exact globs and files to re-check
  are quoted in Context above (ADR 0021 precedent: the renderer's remarks name them).
- The "authored relative links resolve verbatim in the deployed tree" rule now carries a
  property-based exception instead of a two-item list: ZCode relocation, OpenCode/Kilo
  extension guard, Copilot-global absolute form.
- The shared `SquadResourceLinkRewriter` gains its third and fourth users (via the new
  `SquadAgentResourceDiscoveryGuard` extension-map wrapper); `SquadResourceProjection`
  and the ZCode/Copilot private implementations are unchanged. Converging them is
  explicitly out of scope.
- Receipts on the update path carry extension-mapped owned-file lists, so `squad update`
  replaces the old `.md` files rather than orphaning them.
- If a future operator prefers frontmatter `disable`/`disabled` injection (content
  mutation, versioned keys), decision 5's rejected option remains implementable behind
  the same tests.

## Related

- [ADR 0021](0021-zcode-command-lowering-and-resource-relocation.md) — the precedent this
  record follows in mechanism (closure-driven rewrite + recorded degradation) while
  departing from in destination (co-location retained)
- [Kyber-Squad architecture](../kyber-squad/architecture.md) — §8 rendering,
  resource-projection rule
- [The OpenCode/Kilo agent resource plan](../plans/2026-10-19-opencode-kilo-agent-resource-relocation.md) —
  the verified source-of-truth table behind these decisions
