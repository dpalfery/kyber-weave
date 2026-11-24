---
id: reference/glossary
title: Glossary
doc-type: reference
status: current
owner: dpalfery
last-reviewed: 2026-11-24
component: ContextHygiene
---

# Glossary

## KW skill

| Sense ID | Status | Definition | Scope | Aliases |
|---|---|---|---|---|
| kw-skill-5d0a9b13 | approved | A Kyber-Weave skill loaded on demand: a folder `<docs-root>/standards/skills/<name>/SKILL.md` in the repository pool (or the shipped pool), assigned to a technology or role by a `<scope>.skills.yml` list of names, listed by name and description in an agent's `<kw-skills>` prompt part, and read by calling the kw-docs `load_skill` tool. It is never deployed to a harness skills folder. Not the same as a harness skill, and not the `.apm/skills` skills that [ADR 0049](adr/0049-kyber-squad-ships-the-kyber-weave-skills.md) calls "Kyber-Weave skills" (those are harness skills Kyber-Squad deploys). See [ADR 0093](adr/0093-kw-skills-load-on-demand-through-kw-docs.md). | component:KyberSquad | KW skills |
