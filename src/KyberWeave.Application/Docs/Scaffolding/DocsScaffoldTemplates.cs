using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>The text of every file <c>docs init</c> scaffolds. Pure: the date it stamps comes from the caller.</summary>
public static class DocsScaffoldTemplates
{
    public static string HostConfig(
        string docsRoot,
        bool kyberStandards = false,
        IReadOnlyList<string>? technologies = null,
        IReadOnlyList<string>? roles = null)
    {
        string yamlDocsRoot = HostConfigYaml.QuoteScalar(docsRoot);
        IReadOnlyList<string> declared = technologies
            ?? (kyberStandards ? KyberStandardsTemplates.All : []);
        string technologiesBlock = declared.Count == 0
            ? "technologies: []"
            : "technologies:\n" + string.Join("\n", declared.Select(t => $"    - {t}"));
        string rolesBlock = roles is { Count: > 0 }
            ? "\n  # Roles this repository declares a folder for. A role scopes its standard,\n" +
              "  # rules and skills instead of a technology.\n  roles:\n" +
              string.Join("\n", roles.Select(r => $"    - {r}"))
            : string.Empty;

        return $"""
        # Kyber-Weave host configuration. Every ontology default is overridable here.
        # Reference: {docsRoot}/documentation-ontology.md
        ontology:
          docs-root: {yamlDocsRoot}

          # Product defaults exclude five DevOps paths from the repository the ontology
          # was first built for. An empty list clears them.
          excluded-files: []

          # Technologies this repository declares a coding standard for. Adding one and
          # re-running 'docs init' creates {docsRoot}/standards/<technology>/, publishes its
          # registry property, and legalizes that value in the standard's 'technology' key.
          {technologiesBlock}
        {rolesBlock}

        {McpDeclarationBlockWriter.DeclarationYaml}

        # Additions to the configuration registry rendered into AGENTS.md. Everything
        # 'docs init' creates is already published; name only what is yours.
        # config-reg:
        #   auth-design: {docsRoot}/reference/auth-design.md

        """;
    }

    public static string Catalog(string owner, string today)
    {
        ScaffoldValues.RequireCatalogValue(owner, nameof(owner));
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        return $"""
        ---
        id: catalog
        title: Component and owner catalog
        doc-type: reference
        status: draft
        owner: {yamlOwner}
        last-reviewed: {today}
        ---

        # Component and owner catalog

        This table is the **authoritative vocabulary** for the `component` and `owner`
        frontmatter keys. A document naming a component with no row here fails
        `KW-DOC-SPEC-004`. The check exists so that components cannot be invented one
        document at a time until nobody can say how many there are.

        Replace the example row below with the real units of your system. One row per
        component that genuinely exists — a catalog with forty rows is a list of files.

        | Component | Type | Source root | Overview | Detailed documentation | Owner | Last reviewed | Status |
        |---|---|---|---|---|---|---|---|
        | Example | Service | `src/Example` | Replace this row. | — | {owner} | {today} | draft |

        ## How the columns are read

        Only **Component** (index 1) and **Owner** (index 6) are parsed, counting the empty
        cell produced by the leading pipe. The other columns are for human readers and may
        be reworded freely. Moving either parsed column requires a matching
        `ontology.catalog` override in `.kyber-weave/kyber-weave.yml`.

        """;
    }

    /// <summary>
    /// What each scaffolded folder is for, as the paragraph its README opens with. Wrapped
    /// here rather than left to the editor, so a generated document matches the width of the
    /// hand-authored ones beside it.
    /// </summary>
    private static (string Title, string Purpose) FolderIdentity(string folder) => folder switch
    {
        DocsLayout.Standards => ("Coding standards",
            "How code is written in this repository, one directory per technology. A standard\n"
            + "is project-specific; the agents and skills that read it are not, which is why they\n"
            + "resolve it through the configuration registry rather than carrying their own. Only\n"
            + "the agent working in a technology reads its standard, so per-technology detail belongs\n"
            + "here, not in a rule (see the documentation ontology's \"Rules and standards\")."),
        "plans" => ("Plans",
            "Sequenced implementation work: what will be done, in what order, and how it will be\n"
            + "verified. A plan is a record of intent rather than current guidance — retrieval\n"
            + "demotes it accordingly. While open it is linked from this index; once closed it is\n"
            + "archived."),
        "specs" => ("Specifications",
            "Upfront specification work for a greenfield project or a large feature, written when\n"
            + "requirements and architecture still need defining before a plan can exist."),
        "todo" => ("Todos",
            "Work identified but not done now — a finding, a deferred fix, a declined suggestion.\n"
            + "Capturing it here is what stops it evaporating between sessions."),
        "adr" => ("Architecture decision records",
            "One record per architectural decision: what was decided, the alternatives, and why.\n"
            + "An ADR is edited in place to state only the current decision; outdated content is\n"
            + "removed, because git holds the history."),
        "rules" => ("Rules",
            "Repository-wide rules that govern how the system is built, independent of any one\n"
            + "technology. Every agent reads them on every task, so a rule applies to every task and\n"
            + "breaking it is never acceptable; anything else, including how one language is\n"
            + "written, belongs in a coding standard (see the documentation ontology's \"Rules and\n"
            + "standards\"). Keep each rule short."),
        "reference" => ("Reference",
            "Reference material with no other home: environment variables, naming standards, the\n"
            + "external systems this repository depends on."),
        _ => throw new InvalidOperationException($"No identity defined for scaffolded folder '{folder}'.")
    };

    public static string FolderIndex(string folder, string owner, string today)
    {
        (string title, string purpose) = FolderIdentity(folder);
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        string body = folder == DocsLayout.Standards
            ? StandardsRegistry()
#pragma warning disable CA1308 // Lowercase is intentional for stable IDs/hashing; changing to Upper would invalidate persisted hashes
            : $"Add one document per {title.TrimEnd('s').ToLowerInvariant()} and list it here.\n";
#pragma warning restore CA1308

        return $"""
        ---
        id: {folder}/index
        title: {title}
        doc-type: index
        status: draft
        owner: {yamlOwner}
        last-reviewed: {today}
        ---

        # {title}

        {purpose}

        {body}
        """;
    }

    /// <summary>
    /// The standards folder's README: how a technology comes to have a standard here.
    /// </summary>
    /// <remarks>
    /// Deliberately not a table of the declared technologies. This file is scaffolding, which
    /// means it is written once and never regenerated, so a list inside it would be wrong the
    /// first time a technology was added. The registry in <c>AGENTS.md</c> is regenerated on
    /// every run and is therefore the one place that list can be trusted.
    /// </remarks>
    public static string StandardsRegistry() =>
        """
        ## Declaring a technology

        Add it to `ontology.technologies` in `.kyber-weave/kyber-weave.yml` and re-run
        `kyber-weave docs init`. That one list creates the technology's folder, publishes its
        `<name-coding-standard>` property in the Config Reg block of the repository root
        `AGENTS.md`, and legalizes the `technology` value in the standard's frontmatter — so
        the three cannot disagree.

        A technology name is a slug: lowercase letters, digits and single hyphens.

        The declared technologies are listed in that registry, which is regenerated on every
        run. This file is not.

        """;

    /// <summary>
    /// Builds the draft coding-standard stub <c>docs init</c> writes for a declared technology.
    /// </summary>
    public static string TechnologyStandard(string technology, string owner, string today)
    {
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        return $"""
        ---
        id: standards/{technology}
        title: {technology} coding standard
        doc-type: coding-standard
        status: draft
        technology: {technology}
        owner: {yamlOwner}
        last-reviewed: {today}
        ---

        # {technology} coding standard

        How {technology} code is written in this repository. Agents and skills resolve this
        document as `<{technology}{ConfigRegConfig.CodingStandardSuffix}>` in the repository
        root `AGENTS.md`.

        When this standard is in `status: current`, it is how this technology is written in this
        repository and what reviewers grade against. Portable agents ship no built-in default to fall back on. While it is in
        `status: draft` it is a proposal, but a worker never stops for it: it uses the draft as
        the best available guidance and names the draft standard it relied on in one line of its
        report.

        Replace this file with the decisions that actually apply, and promote `status` to
        `current`. A standard that restates a language's own documentation is noise; write the
        decisions this repository has made and would otherwise have to re-argue. Only the agent
        working in {technology} reads this file; a statement that applies to every task and is
        never OK to break is a rule, and belongs in the root `AGENTS.md` or `rules/` instead.

        ## Naming and layout

        ## Coding standards

        ## Testing

        ## Commands

        ## Security and data handling

        ## Done checklist

        """;
    }

    /// <summary>Builds the prose rules stub for a declared technology with no shipped template.</summary>
    public static string TechnologyRules(string technology, string owner, string today)
    {
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        return $"""
        ---
        id: standards/{technology}/rules
        title: "{technology} rules"
        doc-type: rule
        status: draft
        technology: {technology}
        owner: {yamlOwner}
        last-reviewed: {today}
        ---

        # {technology} rules

        Rules that apply to every task written in {technology}. Technology rules beat parent rules,
        parent rules beat shared rules, and rules beat skills.
        """;
    }

    /// <summary>
    /// Builds the skills list for a declared scope with no shipped template: an empty YAML list, because a
    /// list names skills and a scope that has none lists none.
    /// </summary>
    public static string SkillsList() => "[]\n";

    /// <summary>Builds the empty Arbiter rules file for a declared technology.</summary>
    public static string TechnologyArbiterRules(string technology) =>
        $"# Machine rules the Arbiter evaluates for the '{technology}' folder.\n" +
        "# An empty list is valid: most folders declare none.\n" +
        "rules: []\n";

    /// <summary>Builds the standard stub for a declared role with no shipped template.</summary>
    public static string RoleStandard(string role, string owner, string today)
    {
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        return $"""
        ---
        id: standards/{role}
        title: "{role} standard"
        doc-type: coding-standard
        status: draft
        role: {role}
        owner: {yamlOwner}
        last-reviewed: {today}
        ---

        # {role} standard

        How the {role} role does its work in this repository. Agents resolve this document as
        `<{role}-coding-standard>`.

        ## Naming and layout

        ## Coding standards

        ## Testing

        ## Commands

        ## Security and data handling

        ## Done checklist

        """;
    }

    /// <summary>Builds the prose rules stub for a declared role.</summary>
    public static string RoleRules(string role, string owner, string today)
    {
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        return $"""
        ---
        id: standards/{role}/rules
        title: "{role} rules"
        doc-type: rule
        status: draft
        role: {role}
        owner: {yamlOwner}
        last-reviewed: {today}
        ---

        # {role} rules

        Rules that apply wherever the {role} role works.
        """;
    }

    public static string DocumentationIndex(string docsRoot, string owner, string today)
    {
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        return $"""
        ---
        id: documentation-index
        title: Documentation
        doc-type: index
        status: draft
        owner: {yamlOwner}
        last-reviewed: {today}
        ---

        # Documentation

        The governed documentation corpus for this repository. Every document under
        `{docsRoot}/` conforms to [the documentation ontology](documentation-ontology.md) and is
        checked by `kyber-weave docs validate` and `kyber-weave docs drift`.

        | Directory | Holds |
        |---|---|
        | [standards/](standards/README.md) | Coding standards, one per technology |
        | [plans/](plans/README.md) | Sequenced implementation work |
        | [specs/](specs/README.md) | Upfront specification work |
        | [todo/](todo/README.md) | Work identified but not done now |
        | [adr/](adr/README.md) | Architecture decision records |
        | [rules/](rules/README.md) | Repository-wide rules |
        | [reference/](reference/README.md) | Reference material |

        [`catalog.md`](catalog.md) is the authoritative vocabulary for the `component` and
        `owner` keys. Start there when adding a document for something new.

        """;
    }

    public static string OntologyReference(string docsRoot, string owner, string today)
    {
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        return $"""
        ---
        id: documentation-ontology
        title: The documentation ontology
        doc-type: reference
        status: draft
        owner: {yamlOwner}
        last-reviewed: {today}
        ---

        # The documentation ontology

        Every governed document in `{docsRoot}/` conforms to this schema. `kyber-weave docs
        validate` enforces it; `kyber-weave docs drift` checks that what documents claim
        about code is still true.

        ## Frontmatter

        Keys are hyphenated. Unknown keys are ignored, so your own metadata never breaks
        parsing.

        ```yaml
        ---
        id: payments/architecture
        title: Payments service
        doc-type: architecture
        status: current
        component: Payments
        source-root: src/Payments
        owner: payments-team
        last-reviewed: {today}
        code-refs:
          - PaymentProcessor
        ---
        ```

        | Key | Meaning |
        |---|---|
        | `id` | Permanent, unique slug. Other documents reference this, never the file path. |
        | `title` | Human title. |
        | `doc-type` | One of the closed set below. Decides which other keys are required. |
        | `status` | Currency of the document, from the closed set below. |
        | `component` | The unit of the system this covers. Must exist in `catalog.md`. |
        | `source-root` | Repository-relative path to that component's source. Must exist. |
        | `technology` | The stack a coding standard governs. Declared in configuration; matches its folder. |
        | `owner` | Who answers for it. Must exist in `catalog.md`. |
        | `last-reviewed` | ISO `yyyy-MM-dd`. Any other format is an error. |
        | `code-refs` | Symbols this document formally claims. Resolved against the code graph. |
        | `api-endpoints` | Exact route strings, e.g. `GET /api/me/usage`. |
        | `decided-by` | Ids of the ADRs that decided this document's content. |
        | `supersedes` | Ids of documents this one replaces. |

        ## Closed vocabularies

        **doc-type** — `architecture`, `onboarding`, `requirements`, `adr`, `plan`, `spec`,
        `todo`, `runbook`, `reference`, `rule`, `governance`, `index`, `coding-standard`

        **status** — `current`, `draft`, `needs-review`, `superseded`

        **technology** — whatever `ontology.technologies` declares. Empty until this
        repository says which stacks it writes code in, which is also what creates each
        standard's folder and its registry property.

        A value outside these sets is an error. An open vocabulary is not a vocabulary — it
        is a text field that drifts until two documents of the same kind carry different
        labels and neither is findable by the other's name. If nothing fits, use
        `reference`; widen the set in `.kyber-weave/kyber-weave.yml` deliberately or not at
        all.

        ## Required keys

        **Every document**: `id`, `title`, `owner`, `last-reviewed`, `doc-type`, `status`

        | Doc type | Additionally required |
        |---|---|
        | `architecture`, `requirements`, `runbook`, `plan`, `spec`, `todo` | `component` |
        | `onboarding` | `component`, `source-root` |
        | `coding-standard` | `technology` |
        | `adr`, `reference`, `rule`, `governance`, `index` | — |

        A standard takes no `component`: a language's standard governs code in every component
        the catalog lists, so naming one of them would be a false claim about its reach.

        ## Rules and standards

        Every instruction this repository gives its agents is a rule or part of a standard.

        - **Rules** apply to every task, whatever the technology, and breaking one is never
          acceptable: secrets, what may be committed or pushed, architecture boundaries, scope,
          what needs human approval. Every agent reads them on every dispatch, so they are few and
          short, and ideally checkable by a gate. They live in the root `AGENTS.md` and `rules/`.
        - **Standards** are per technology, under `standards/<technology>/`. Only the agent
          working in that technology fetches one, through `docs_get_coding_standard_for_technology`.
          They can be long, with examples; reviewers grade against them.

        **The placement test:** does the statement apply to every task, and is breaking it never
        OK? Yes means a rule; anything else goes in a standard. Each statement has one home: a
        rule may point to a standard for detail, never restate it. Keep everything always loaded
        (the root `AGENTS.md`, the files it tells every agent to read, harness instruction files)
        to about 3 KB; a rule set larger than that contains standards.

        ## The pairing invariant

        For `architecture` and `runbook`, `source-root` and `code-refs` travel together. A
        source root without symbols claims coverage the document does not have; symbols
        without a root leave nothing to check them against. Both halves are what make drift
        detection possible.

        ## code-refs are claims, not mentions

        Listing a symbol asserts the document is answerable for it. A document that merely
        discusses `PaymentProcessor` in prose has not claimed it; one listing it in
        `code-refs` has, and will fail `KW-DOC-DRIFT-001` when the symbol is renamed.

        That distinction is the point of the ontology. After a rename, prose still reads
        correctly — no linter, reviewer, or test notices. Only resolution does.

        ## Ranking consequences

        Retrieval weights declared identity above prose, and scales by how far a document
        counts as current guidance: `plan` and `spec` are demoted to 0.55, `superseded` to
        0.4, `draft` and `needs-review` to 0.85, `adr` to 0.9.

        So `doc-type` and `status` are not cosmetic. Labelling a standard as a `plan` buries
        it; labelling a closed plan as `reference` promotes a work artifact into guidance an
        agent will act on.

        ## Adopting an existing tree

        Fill the mechanical keys in bulk with `status: draft`, get `docs validate` clean,
        then add `code-refs` selectively and promote to `current` as each component is
        reviewed. The `kyber-docs-authoring` skill covers the whole procedure.

        """;
    }
}
