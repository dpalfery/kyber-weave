# Skills in your ALM: a governance playbook

The source article ([Modern Agents Have Skills Now](https://microsoft.github.io/mcscatblog/posts/modern-mcs-agent-skills/)) shows makers how Skills work inside Copilot Studio. This doc covers the part that matters once you operate skills at enterprise scale: treating them as governed software artifacts.

## 1. A skill is a reviewable artifact

A `SKILL.md` is plain text, which means it belongs in source control and in pull requests like any other code. Two consequences:

- **It can be diffed and reviewed.** A description change is a routing change — reviewers should see it.
- **It can be malicious.** A skill shapes agent behavior and can bundle scripts, so an untrusted or AI-generated skill is a trust surface. Review it the way you'd review a third-party dependency. Kyber-Weave's `skill scan` is the automated first pass; it does not replace human review.

## 2. The PR gate

Run four checks on every PR that touches a skill (see `.github/workflows/ci.yml`):

1. `validate` — spec conformance. Catches the silent killers, especially **name ≠ folder name**, which makes runtimes fail to load a skill with no error.
2. `lint --min-desc-score` — routing readiness. A description is routing metadata; a weak one makes the wrong skill fire or none at all. The gate keeps descriptions routable and catches **collisions/overlap** between skills.
3. `scan --format sarif` — trust surface. SARIF uploads land in the GitHub Security tab.
4. `route --eval --min-accuracy` — routing regression. Author a set of `{prompt → expected skill}` cases (including negatives that should fire nothing) and assert an accuracy floor, so a description edit that breaks routing fails the build.

## 3. Versioning and provenance

Put an owner and a version on every skill via front-matter `metadata`, and declare a `license`:

```yaml
metadata:
  author: it-platform-team
  version: 1.2.0
license: MIT
```

`scan` flags missing provenance; `catalog` reports version/owner/score across your whole tree so you can audit what's deployed.

## 4. Copilot Studio specifics (preview — verify before relying on this)

- A skill is uploaded as a single `SKILL.md` or a `.zip` bundling `SKILL.md` + `scripts/`/`references/`/`assets/`. `kyber-weave skill pack` produces that `.zip`.
- A skill is **scoped to its agent** and travels with it through Power Platform **solutions** and ALM — not a cross-product catalog. Microsoft has described a catalog-like sharing model as planned but not yet shipped; confirm the current state on Microsoft Learn before depending on it.
- Skills can **soft-point** at the agent's existing tools (actions, flows, connectors, MCP servers). The pointer is a reference, not a binding or a grant — if the agent lacks the tool, the instruction can't be fulfilled.

## 5. A skill, or a new agent?

Reach for a separate agent when the capability would **stand on its own** (different audience, different security boundary) or when one agent has accumulated so many tools/skills that accuracy degrades. Otherwise, another skill on the existing agent is the cheaper unit of modularity. Evaluate accuracy for your own agent rather than assuming.

---

*This playbook pairs with the Kyber-Weave CLI. Everything here is enforceable: each section maps to a command you can put in CI today.*
