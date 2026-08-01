using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>One file the scaffolder considered writing.</summary>
/// <param name="RelativePath">Repository-relative path, forward-slashed.</param>
/// <param name="Written">False when the file already existed and was left alone.</param>
public sealed record ScaffoldedFile(string RelativePath, bool Written);

/// <summary>What <see cref="DocsScaffolder"/> did.</summary>
/// <param name="DocsRoot">The documentation root the corpus was scaffolded into.</param>
/// <param name="DocsRootDetected">True when the root was inferred rather than supplied.</param>
/// <param name="Files">Every file considered, written or skipped.</param>
public sealed record ScaffoldResult(
    string DocsRoot,
    bool DocsRootDetected,
    IReadOnlyList<ScaffoldedFile> Files)
{
    public bool WroteAnything => Files.Any(f => f.Written);
}

/// <summary>
/// Bootstraps a host repository into a governable documentation corpus: host config, the
/// catalog that supplies the component and owner vocabularies, and the ontology reference.
/// </summary>
/// <remarks>
/// <para>
/// The ontology reference is emitted rather than merely documented upstream because every
/// <c>KW-DOC-SPEC-001</c> diagnostic tells the author to "add a frontmatter block per
/// <c>&lt;docs-root&gt;/documentation-ontology.md</c>". Until this command existed, that
/// hint pointed at a file the tool never produced.
/// </para>
/// <para>
/// Existing files are never overwritten unless <c>force</c> is set. Re-running is
/// therefore safe, and a partially adopted repository can be topped up.
/// </para>
/// </remarks>
public static class DocsScaffolder
{
    /// <summary>Roots checked, in order, when no docs root is supplied.</summary>
    private static readonly string[] ConventionalRoots = ["docs", "6-Docs", "doc", "documentation"];

    public static ScaffoldResult Scaffold(
        string repoRoot,
        string? docsRoot = null,
        string owner = "unassigned",
        bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var root = Path.GetFullPath(repoRoot);

        RequireEmittableValue(owner, nameof(owner));

        var detected = string.IsNullOrWhiteSpace(docsRoot);
        var resolvedDocsRoot = detected
            ? DetectDocsRoot(root)
            : RequireEmittableValue(docsRoot!.Trim().Replace('\\', '/').TrimEnd('/'), nameof(docsRoot));

        // Checked before the first write, not only inside Write. The host config resolves
        // inside the root and would otherwise be created successfully and left behind,
        // pointing at a docs root that the very next write then rejects.
        RequireContained(root, resolvedDocsRoot, nameof(docsRoot));

        var files = new List<ScaffoldedFile>
        {
            Write(root, $"{KyberWeaveYamlParser.DefaultDirectoryName}/{KyberWeaveYamlParser.DefaultFileName}",
                HostConfig(resolvedDocsRoot), force),
            Write(root, $"{resolvedDocsRoot}/documentation-ontology.md",
                OntologyReference(resolvedDocsRoot, owner), force),
            Write(root, $"{resolvedDocsRoot}/catalog.md",
                Catalog(owner), force)
        };

        return new ScaffoldResult(resolvedDocsRoot, detected, files);
    }

    /// <summary>
    /// The first conventional documentation directory that already exists, else "docs".
    /// Adopting an existing tree is the common case; creating one is not.
    /// </summary>
    internal static string DetectDocsRoot(string repoRoot)
    {
        foreach (var candidate in ConventionalRoots)
        {
            if (Directory.Exists(Path.Combine(repoRoot, candidate)))
                return candidate;
        }

        return "docs";
    }

    /// <summary>
    /// Rejects values that would change the structure of what they are emitted into.
    /// </summary>
    /// <remarks>
    /// These reach two formats: YAML frontmatter, where a newline adds a key, and the
    /// pipe-delimited catalog row, where a <c>|</c> shifts the columns the component and
    /// owner vocabularies are read from. Rejecting beats escaping — two formats would need
    /// two escapes, and a scaffolder that silently rewrites what the operator typed is
    /// worse than one that stops and says why.
    /// </remarks>
    private static string RequireEmittableValue(string value, string parameterName)
    {
        if (value.Any(c => char.IsControl(c) || c is '|' or '"'))
        {
            throw new ArgumentException(
                $"'{parameterName}' may not contain control characters, '|' or '\"'. " +
                "These values are written into YAML frontmatter and a pipe-delimited catalog row.",
                parameterName);
        }

        return value;
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> under <paramref name="repoRoot"/>, refusing
    /// anything that lands outside it.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.Combine(string, string)"/> returns its second argument outright when
    /// that argument is rooted, and <c>..</c> segments walk upward, so an unchecked
    /// operator-supplied docs root places files anywhere the process can reach.
    /// </remarks>
    private static string RequireContained(string repoRoot, string relativePath, string parameterName)
    {
        var absolute = Path.GetFullPath(
            Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var boundary = repoRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!absolute.StartsWith(boundary, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Refusing to write outside the repository root: '{relativePath}' resolves to '{absolute}'.",
                parameterName);
        }

        return absolute;
    }

    private static ScaffoldedFile Write(string repoRoot, string relativePath, string content, bool force)
    {
        // Enforced per write as well as up front, so the invariant holds for every path
        // this type will ever emit, not only the ones routed through the docs root.
        var absolute = RequireContained(repoRoot, relativePath, nameof(relativePath));

        if (File.Exists(absolute) && !force)
            return new ScaffoldedFile(relativePath, false);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        return new ScaffoldedFile(relativePath, true);
    }

    private static string HostConfig(string docsRoot) =>
        $"""
        # Kyber-Weave host configuration. Every ontology default is overridable here.
        # Reference: {docsRoot}/documentation-ontology.md
        ontology:
          docs-root: {docsRoot}

          # Product defaults exclude five DevOps paths from the repository the ontology
          # was first built for. An empty list clears them.
          excluded-files: []

        """;

    private static string Catalog(string owner) =>
        $"""
        ---
        id: catalog
        title: Component and owner catalog
        doc-type: reference
        status: draft
        owner: {owner}
        last-reviewed: {Today}
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
        | Example | Service | `src/Example` | Replace this row. | — | {owner} | {Today} | draft |

        ## How the columns are read

        Only **Component** (index 1) and **Owner** (index 6) are parsed, counting the empty
        cell produced by the leading pipe. The other columns are for human readers and may
        be reworded freely. Moving either parsed column requires a matching
        `ontology.catalog` override in `.kyber-weave/kyber-weave.yml`.

        """;

    private static string Today =>
        DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string OntologyReference(string docsRoot, string owner) =>
        $"""
        ---
        id: documentation-ontology
        title: The documentation ontology
        doc-type: reference
        status: draft
        owner: {owner}
        last-reviewed: {Today}
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
        last-reviewed: {Today}
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
        | `owner` | Who answers for it. Must exist in `catalog.md`. |
        | `last-reviewed` | ISO `yyyy-MM-dd`. Any other format is an error. |
        | `code-refs` | Symbols this document formally claims. Resolved against the code graph. |
        | `api-endpoints` | Exact route strings, e.g. `GET /api/me/usage`. |
        | `decided-by` | Ids of the ADRs that decided this document's content. |
        | `supersedes` | Ids of documents this one replaces. |

        ## Closed vocabularies

        **doc-type** — `architecture`, `onboarding`, `requirements`, `adr`, `plan`, `spec`,
        `runbook`, `reference`, `rule`, `governance`, `index`

        **status** — `current`, `draft`, `needs-review`, `superseded`

        A value outside these sets is an error. An open vocabulary is not a vocabulary — it
        is a text field that drifts until two documents of the same kind carry different
        labels and neither is findable by the other's name. If nothing fits, use
        `reference`; widen the set in `.kyber-weave/kyber-weave.yml` deliberately or not at
        all.

        ## Required keys

        **Every document**: `id`, `title`, `owner`, `last-reviewed`, `doc-type`, `status`

        | Doc type | Additionally required |
        |---|---|
        | `architecture`, `requirements`, `runbook`, `plan`, `spec` | `component` |
        | `onboarding` | `component`, `source-root` |
        | `adr`, `reference`, `rule`, `governance`, `index` | — |

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
        reviewed. The `kyber-weave-docs` skill covers the whole procedure.

        """;
}
