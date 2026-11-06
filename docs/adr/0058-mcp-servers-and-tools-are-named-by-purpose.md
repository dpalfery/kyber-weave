---
id: adr/0058-mcp-servers-and-tools-are-named-by-purpose
title: MCP servers and tools are named by purpose
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-05
decided-by: []
supersedes: []
---

# ADR 0058: MCP servers and tools are named by purpose

## Status

Accepted, 2026-11-05. Records the naming rule for the three first-party MCP servers and for every
tool they expose, and retires the short labels both carried before it.

## Context

Three MCP servers ship with Kyber-Weave, and each was named after the product it grew out of
rather than after what it answers: the documentation server, the dashboard analytics server, and
the arbiter. Their tool names were abbreviated to what fit comfortably after a server prefix —
`docs_explore`, `dash_query`, `arbiter_rules` — on the assumption that the description would carry
the meaning.

It does not. A tool name is the routing metadata a model reads *before* it decides whether to read
the description at all, and it is what a human reads in a rendered agent's `tools` allow-list, in a
Kilo `permission` map, and in a `tools/list` dump. `docs_status` and `dash_status` name neither the
question nor the object; `arbiter_rules` reads as a command about rules rather than a list of the
effective ones. Two servers abbreviated to `docs_` and `dash_` are also one letter apart in a
grant, and the product names share the `kyber` prefix with the binaries that launch them, so a
wildcard, a grep for the product, and a reference to an executable were easy to confuse.

A second force is mechanical: a tool name is not free. Clients bound the name, the qualified
`mcp__<server>__<tool>` string they build from it, and the server and tool together, and the
tightest of those bounds is smaller than the loose one. Whatever the names say, they have to fit.

## Decision

1. **A server configuration key is `kw-<area>`.** The documentation server's key is `kw-docs`, the
   dashboard analytics server's is `kw-dash`, and the arbiter's is `kw-arbiter`. The `kw-` prefix
   marks a first-party server in a file that also holds third-party ones, and `<area>` is the
   component the server answers for. The key is the whole of what changes here: the executables and
   their arguments are not renamed (decision 3).

2. **A tool name is `<area>_<verb>_<object…>`, and it names what the tool does.** The area repeats
   the server's area, so a name read out of context still says which surface answers it, and the
   verb-and-object tail states the operation and what it returns. The rule is what the name is
   *for*, not a word limit: `docs_answer_question_from_repo_documentation`,
   `docs_find_documents_citing_code_symbol`, `dash_list_available_analytics_operations`,
   `dash_query_session_cost_tokens_and_duration`, and
   `arbiter_check_configuration_and_key_readiness` each state the question a caller has.
   `docs_explore`, `dash_status`, and `arbiter_status` do not.

3. **Nothing outside the two surfaces above is renamed.** The executables and their arguments, the
   CLI commands, the product name, the `.kyber-weave/` paths, `kyber-weave.yml`, the environment
   variables, the GitHub repository, the keychain service, and the published rule and doctor check
   ids keep their names. The `kw-docs` server still launches `kyber-weave-mcp`, `kw-dash` still
   launches `kyberdash mcp`, and `kw-arbiter` still launches `kyber-weave-arbiter serve`. Renaming a
   binary is a distribution change and is explicitly out of scope of this decision.

4. **There are no aliases.** An old tool name is an unknown tool: it is not registered, it is not
   forwarded, and it does not resolve through a compatibility shim. Every first-party caller — the
   canonical agent files and their `copilot-tools` and `pi-tools` entries, the capability profiles,
   the renderers' emitted grants, the shipped skills, the checked-in MCP configurations, and the
   runbook's recipes — changes in the same change as the rename.

5. **The names are held inside the tightest limit any client reports, and a test pins it.** A tool
   name matches `^[a-zA-Z0-9_-]{1,64}$` and carries no `__`; the qualified
   `mcp__<server>__<tool>` string is at most 64 characters; the server and tool together are at
   most 60. These are the tightest figures anyone reports, not three vendor-verified figures — two
   of the three are not vendor documentation at all — so they are treated as a budget rather than a
   remark, and the roster is chosen against the smallest of them. A contract test
   (`McpToolNameContractTests`) pins all three bounds over the live tool roster, so a future tool
   cannot exceed one silently; the figures, the sources, and the current maxima are tabulated under
   "Name limits, and why the names are long" in the
   [DocGraph MCP runbook](../docgraph/mcp-runbook.md#name-limits-and-why-the-names-are-long).

   | Server | Old tool name | New tool name |
   |---|---|---|
   | `kw-docs` | `docs_explore` | `docs_answer_question_from_repo_documentation` |
   | `kw-docs` | `docs_for_symbol` | `docs_find_documents_citing_code_symbol` |
   | `kw-docs` | `docs_glossary` | `docs_look_up_glossary_term_definition` |
   | `kw-docs` | `docs_analysis_candidates` | `docs_find_duplicates_conflicts_ambiguous_terms` |
   | `kw-docs` | `docs_validate` | `docs_validate_governed_document_schema` |
   | `kw-docs` | `docs_drift` | `docs_detect_drift_between_documents_and_code` |
   | `kw-docs` | `docs_status` | `docs_get_source_repository_and_revision` |
   | `kw-docs` | `agent_coding_standards` | `docs_get_coding_standard_for_technology` |
   | `kw-dash` | `dash_status` | `dash_list_available_analytics_operations` |
   | `kw-dash` | `dash_catalog` | `dash_list_queryable_metrics_dimensions_limits` |
   | `kw-dash` | `dash_query` | `dash_query_session_cost_tokens_and_duration` |
   | `kw-dash` | `dash_search_sessions` | `dash_search_sessions_by_text_and_scope` |
   | `kw-dash` | `dash_inspect` | `dash_read_captured_run_session_or_turn_context` |
   | `kw-dash` | `dash_findings` | `dash_list_detected_context_and_tool_problems` |
   | `kw-dash` | `dash_compare` | `dash_compare_metrics_between_two_scopes` |
   | `kw-dash` | `dash_outcomes` | `dash_get_run_success_or_failure_outcomes` |
   | `kw-arbiter` | `arbiter_evaluate` | `arbiter_evaluate_agent_dispatch_or_return` |
   | `kw-arbiter` | `arbiter_rules` | `arbiter_list_effective_rules_by_trigger` |
   | `kw-arbiter` | `arbiter_status` | `arbiter_check_configuration_and_key_readiness` |

   The server keys move with the tools: `kyber-weave` becomes `kw-docs`, `kyberdash` becomes
   `kw-dash`, and `kyber-weave-arbiter` becomes `kw-arbiter`, in every checked-in configuration, in
   the declarable `required-servers` list, and in every grant built from one — the Claude
   `mcp__kw-docs__*` wildcard, the enumerated `mcp__kw-docs__<tool>` names on ZCode, Devin, and
   Command Code, the Copilot `kw-docs/*` entry, the Kilo and OpenCode `kw-docs_*` permission key,
   the Factory and Antigravity `mcpServers` entry, and Pi's adapter-mapped `mcp__kw_docs__<tool>`
   overlay.

## Alternatives Considered

- **Keep the short labels and let the description carry the meaning.** Rejected. The description is
  read after the name has already been chosen or passed over, and it is not read at all in the
  places a name is published: a rendered allow-list, a permission map, a `tools/list`. The short
  labels also collide by design — `docs_status`, `dash_status`, and `arbiter_status` were three
  different answers behind one word, and a model choosing between them chose on the server prefix
  alone.
- **An alias layer: keep the old names registered beside the new ones.** Rejected. It freezes the
  short labels as a permanent public surface, doubles the roster every client enumerates, and makes
  every future rename additive. A rename with an alias is two names to document and two names to
  teach, and the old one keeps winning because it is the name the corpus and the model's own
  history mention most. An unknown tool fails loudly on the first call, which is cheaper than a
  deprecation that never ends.
- **Rename the executables and CLI commands to match the keys.** Rejected, and out of scope. It
  changes the distribution — install paths, package contents, the release assets, the keychain
  service, and every script and hook an operator already has. The naming rule is about the MCP
  surfaces a model reads; a binary name is a human interface with a different pressure on it, and
  conflating the two would have made this change a migration of everything.

## Consequences

- **The names are long by design.** `docs_answer_question_from_repo_documentation` is 46
  characters, and the longest qualified name is `mcp__kw-arbiter__` plus
  `arbiter_check_configuration_and_key_readiness` at 62. That is the cost of the rule, and it is
  paid in the qualified form, which is why decision 5 is a budget with a test behind it rather than
  a preference.
- **The limit table is the reference, not a claim of vendor verification.** It lives in the
  [DocGraph MCP runbook](../docgraph/mcp-runbook.md#name-limits-and-why-the-names-are-long) with
  each bound's source, so a future name is argued against figures a reader can check.
- **A rename is atomic or it is broken.** Because there are no aliases (decision 4), a first-party
  caller left on an old name does not degrade — it calls an unknown tool. The repository guard
  (`McpRenameRepoGuardTests`) scans the tracked tree for the retired forms, so a stale name on a
  shipped surface fails the build instead of a user's run.
- **An existing install has to be migrated once.** An operator's configuration still names the old
  keys and the old tools, and the migration is mechanical: `docs mcp-migrate` renames the server
  key in the repository-scope configurations it is allowed to touch, and the
  [install guide](../install.md) carries the old-to-new table for everything it cannot reach. A
  user-scope configuration is the operator's own, as
  [ADR 0052](0052-docs-branch-writes-repository-scope-mcp-configuration.md) requires.
- **Names are now a review surface.** A new tool is judged on whether its name states its question
  before its description is read, and a name that does not fit the limit budget is a redesign
  rather than a truncation.

## Related

- [DocGraph MCP runbook](../docgraph/mcp-runbook.md#name-limits-and-why-the-names-are-long) — the limit table, the sources, and the per-client wiring recipes
- [ADR 0052](0052-docs-branch-writes-repository-scope-mcp-configuration.md) — the writers that may touch repository-scope MCP configuration, including `docs mcp-migrate`
- [ADR 0043](0043-kyberdash-analysis-mcp-read-only-boundary.md) — the dashboard analytics surface whose eight tools are renamed here
- [ADR 0046](0046-docs-explore-section-answer.md) — the section-answer contract the documentation server's first tool answers under its new name
- [ADR 0050](0050-universal-kyber-weave-mcp-grant.md) — the per-target grant forms, now spelled with the `kw-` keys
