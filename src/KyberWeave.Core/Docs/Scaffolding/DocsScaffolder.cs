using System.IO.Enumeration;
using System.Text;
using KyberWeave.Core.Configuration;

namespace KyberWeave.Core.Docs.Scaffolding;

/// <summary>What the scaffolder did to one file.</summary>
public enum ScaffoldOutcome
{
    /// <summary>The file did not exist and was written whole.</summary>
    Created,

    /// <summary>The file existed and was edited in place, keeping the rest of its content.</summary>
    Updated,

    /// <summary>The file existed and was left alone; <c>force</c> would have overwritten it.</summary>
    Skipped,

    /// <summary>
    /// The file existed and was left alone because it is operator state rather than
    /// scaffolding. <c>force</c> does not reach it.
    /// </summary>
    Preserved
}

/// <summary>Where the documentation root used by <c>docs init</c> came from.</summary>
public enum DocsRootSource
{
    /// <summary>The operator supplied <c>--docs-root</c>.</summary>
    Explicit,

    /// <summary>An existing host configuration supplied the root.</summary>
    Configuration,

    /// <summary>The root was selected from the conventional directory names.</summary>
    Convention
}

/// <summary>One file the scaffolder considered writing.</summary>
/// <param name="RelativePath">Repository-relative path, forward-slashed.</param>
/// <param name="Outcome">What happened to it.</param>
/// <param name="Note">
/// Why, when the outcome alone would understate it — an edit that touched one key, or a
/// file held back from <c>force</c>. Plain text; the caller escapes it for its own output.
/// </param>
public sealed record ScaffoldedFile(string RelativePath, ScaffoldOutcome Outcome, string? Note = null)
{
    /// <summary>True when the file's content changed.</summary>
    public bool Written => Outcome is ScaffoldOutcome.Created or ScaffoldOutcome.Updated;
}

/// <summary>What <see cref="DocsScaffolder"/> did.</summary>
/// <param name="DocsRoot">The documentation root the corpus was scaffolded into.</param>
/// <param name="DocsRootSource">Where the resolved documentation root came from.</param>
/// <param name="Files">Every file considered, written or skipped.</param>
public sealed record ScaffoldResult(
    string DocsRoot,
    DocsRootSource DocsRootSource,
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
/// therefore safe, and a partially adopted repository can be topped up. The host config is
/// the exception in the other direction: it is operator state, so <c>force</c> does not
/// reach it and only its docs root is ever rewritten.
/// </para>
/// </remarks>
public static class DocsScaffolder
{
    private const string AnalysisCacheIgnorePath = ".kyber-weave/.gitignore";
    private const string AnalysisCacheIgnoreEntry = "cache/";

    /// <summary>Roots checked, in order, when no docs root is supplied.</summary>
    private static readonly string[] ConventionalRoots = ["docs", "6-Docs", "doc", "documentation"];

    public static ScaffoldResult Scaffold(
        string repoRoot,
        string? docsRoot = null,
        string owner = "unassigned",
        bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string root = Path.GetFullPath(repoRoot);

        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        string resolvedOwner = owner.Trim();
        RequireCatalogValue(resolvedOwner, nameof(owner));

        KyberWeaveConfigLoadResult loadedConfig = RequireLoadableHostConfig(root);
        (string DocsRoot, DocsRootSource Source) resolution = string.IsNullOrWhiteSpace(docsRoot)
            ? ResolveDocsRoot(root, loadedConfig)
            : (docsRoot!.Trim().Replace('\\', '/').TrimEnd('/'), DocsRootSource.Explicit);
        string resolvedDocsRoot = resolution.DocsRoot;
        resolvedDocsRoot = RequireEmittableValue(resolvedDocsRoot, nameof(docsRoot));

        // Checked before the first write, not only inside Write. The host config resolves
        // inside the root and would otherwise be created successfully and left behind,
        // pointing at a docs root that the very next write then rejects.
        RequireContained(root, resolvedDocsRoot, nameof(docsRoot));

        List<ScaffoldedFile> files = new List<ScaffoldedFile>
        {
            WriteHostConfig(root, resolvedDocsRoot),
            WriteAnalysisCacheIgnore(root),
            Write(root, $"{resolvedDocsRoot}/documentation-ontology.md",
                OntologyReference(resolvedDocsRoot, resolvedOwner), force),
            Write(root, $"{resolvedDocsRoot}/catalog.md",
                Catalog(resolvedOwner), force)
        };

        return new ScaffoldResult(resolvedDocsRoot, resolution.Source, files);
    }

    /// <summary>
    /// The first conventional documentation directory that already exists, else "docs".
    /// Adopting an existing tree is the common case; creating one is not.
    /// </summary>
    internal static string DetectDocsRoot(string repoRoot)
    {
        foreach (string candidate in ConventionalRoots)
        {
            if (Directory.Exists(Path.Combine(repoRoot, candidate)))
                return candidate;
        }

        return "docs";
    }

    /// <summary>
    /// The docs root to scaffold into when the operator supplied none. An existing host
    /// config wins over convention: every other docs command resolves <c>docs-root</c>
    /// from <c>.kyber-weave/kyber-weave.yml</c> (configured value, else product default),
    /// so a re-run of <c>docs init</c> that re-detected by convention could land
    /// <c>catalog.md</c> and <c>documentation-ontology.md</c> in a different tree than
    /// <c>docs validate</c> then reads. Only a missing config falls back to
    /// <see cref="DetectDocsRoot"/>; an invalid or unreadable one must stop scaffolding.
    /// </summary>
    internal static (string DocsRoot, DocsRootSource Source) ResolveDocsRoot(
        string repoRoot,
        KyberWeaveConfigLoadResult loaded)
    {
        if (loaded.ConfigPath is not null && loaded.Config is not null)
            return (loaded.Config.Ontology.DocsRoot, DocsRootSource.Configuration);

        return (DetectDocsRoot(repoRoot), DocsRootSource.Convention);
    }

    /// <summary>
    /// Loads host configuration before the first scaffold write. An existing file that
    /// cannot be read is operator state requiring repair, not permission to infer a
    /// different root and write a second corpus.
    /// </summary>
    private static KyberWeaveConfigLoadResult RequireLoadableHostConfig(string repoRoot)
    {
        KyberWeaveConfigLoadResult loaded = KyberWeaveConfigLoader.TryLoad(repoRoot);
        if (loaded.Success)
            return loaded;

        string path = loaded.ConfigPath ?? KyberWeaveYamlParser.DefaultFileName;
        throw new InvalidDataException(
            $"{KyberWeaveConfigLoader.ConfigLoadErrorCode}: Failed to load '{path}': " +
            (loaded.Error ?? "The configuration could not be read."));
    }

    /// <summary>
    /// Rejects values that cannot remain on one generated line.
    /// </summary>
    /// <remarks>
    /// YAML punctuation is quoted at the emission site. Control characters still change
    /// the generated document's line structure and are therefore rejected before any write.
    /// </remarks>
    private static string RequireEmittableValue(string value, string parameterName)
    {
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"'{parameterName}' may not contain control characters. " +
                "Generated values must remain on one line.",
                parameterName);
        }

        return value;
    }

    /// <summary>Rejects values that would add a column to the pipe-delimited catalog row.</summary>
    private static void RequireCatalogValue(string value, string parameterName)
    {
        RequireEmittableValue(value, parameterName);
        if (value.Contains('|', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{parameterName}' may not contain '|'. " +
                "The value is written into a pipe-delimited catalog row.",
                parameterName);
        }
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
        string absolute = Path.GetFullPath(
            Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        string boundary = repoRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!IsWithinRepositoryBoundary(absolute, boundary, OperatingSystem.IsWindows()))
        {
            throw new ArgumentException(
                $"Refusing to write outside the repository root: '{relativePath}' resolves to '{absolute}'.",
                parameterName);
        }

        return absolute;
    }

    /// <summary>
    /// Applies the path-comparison semantics of the target platform to a normalized path
    /// and repository boundary. Kept separate so Windows behavior is testable on every CI host.
    /// </summary>
    internal static bool IsWithinRepositoryBoundary(string absolutePath, string boundary, bool isWindows) =>
        absolutePath.StartsWith(
            boundary,
            isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Creates the host config when the repository has none; otherwise sets only its
    /// <c>ontology.docs-root</c>, in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host config is operator state, not scaffolding, so <c>force</c> deliberately
    /// does not reach it. The template emitted here carries two keys; a host file may carry
    /// harness profiles, catalog column overrides, closed vocabularies and a required-key
    /// matrix. Regenerating it would discard every one of them in order to restate a docs
    /// root the file already had — a silent loss, since the only thing an operator asked
    /// for was fresh scaffolding.
    /// </para>
    /// <para>
    /// The one key <c>docs init</c> owns is the docs root, and it is rewritten only when it
    /// disagrees with the root being scaffolded into — which, since an unsupplied root is
    /// read back out of this same file, means only when the operator moved it with
    /// <c>--docs-root</c>. Leaving it stale instead would point <c>docs validate</c> at a
    /// different tree than the one the catalog was just written to.
    /// </para>
    /// </remarks>
    private static ScaffoldedFile WriteHostConfig(string repoRoot, string docsRoot)
    {
        string? existingPath = KyberWeaveConfigLoader.FindConfigPath(repoRoot);
        if (existingPath is null)
        {
            return Write(
                repoRoot,
                $"{KyberWeaveYamlParser.DefaultDirectoryName}/{KyberWeaveYamlParser.DefaultFileName}",
                HostConfig(docsRoot),
                force: false);
        }

        string relativePath = Path.GetRelativePath(repoRoot, existingPath).Replace('\\', '/');
        string existing = File.ReadAllText(existingPath);
        string updated = HostConfigYaml.WithDocsRoot(existing, docsRoot);

        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return new ScaffoldedFile(
                relativePath,
                ScaffoldOutcome.Preserved,
                "your configuration, kept as-is; --force does not overwrite it");
        }

        File.WriteAllText(existingPath, updated);
        return new ScaffoldedFile(
            relativePath, ScaffoldOutcome.Updated, "docs-root only; the rest of the file is untouched");
    }

    /// <summary>
    /// Establishes the narrow repository-local cache exclusion without regenerating the
    /// operator's other ignore rules.
    /// </summary>
    /// <remarks>
    /// An exact entry earlier in the file is not enough when a later negation exposes the
    /// analysis database again. Appending the same narrow entry after that negation restores
    /// protection while preserving every existing byte. The existing newline style is used
    /// for the appended boundary so a merge does not introduce mixed line endings.
    /// </remarks>
    private static ScaffoldedFile WriteAnalysisCacheIgnore(string repoRoot)
    {
        string absolute = RequireContained(repoRoot, AnalysisCacheIgnorePath, nameof(repoRoot));
        if (!File.Exists(absolute))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, AnalysisCacheIgnoreEntry + "\n");
            return new ScaffoldedFile(AnalysisCacheIgnorePath, ScaffoldOutcome.Created);
        }

        byte[] existingBytes = File.ReadAllBytes(absolute);
        (Encoding? encoding, int preambleLength) = DetectEncoding(existingBytes);
        string existing = encoding.GetString(
            existingBytes,
            preambleLength,
            existingBytes.Length - preambleLength);
        if (HasEffectiveAnalysisCacheIgnore(existing))
        {
            return new ScaffoldedFile(
                AnalysisCacheIgnorePath,
                ScaffoldOutcome.Preserved,
                "your local-state ignore rules, kept as-is");
        }

        string newline = ExistingNewline(existing);
        string boundary = existing.Length == 0 || EndsWithNewline(existing) ? string.Empty : newline;
        byte[] appendedBytes = encoding.GetBytes(boundary + AnalysisCacheIgnoreEntry + newline);
        using (FileStream stream = File.Open(absolute, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.Write(appendedBytes);
        }

        return new ScaffoldedFile(
            AnalysisCacheIgnorePath,
            ScaffoldOutcome.Updated,
            "added cache/ only; existing ignore rules are untouched");
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] content) =>
        content switch
        {
            [0x00, 0x00, 0xFE, 0xFF, ..] =>
                (new UTF32Encoding(bigEndian: true, byteOrderMark: true), 4),
            [0xFF, 0xFE, 0x00, 0x00, ..] => (Encoding.UTF32, 4),
            [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), 3),
            [0xFE, 0xFF, ..] => (Encoding.BigEndianUnicode, 2),
            [0xFF, 0xFE, ..] => (Encoding.Unicode, 2),
            _ => (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0)
        };

    private static bool HasEffectiveAnalysisCacheIgnore(string content)
    {
        bool protectedByExactEntry = false;
        using StringReader reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (StringComparer.Ordinal.Equals(line, AnalysisCacheIgnoreEntry))
            {
                protectedByExactEntry = true;
                continue;
            }

            if (protectedByExactEntry
                && line.StartsWith('!')
                && NegatesAnalysisCacheProtection(line[1..]))
            {
                protectedByExactEntry = false;
            }
        }

        return protectedByExactEntry;
    }

    private static bool NegatesAnalysisCacheProtection(string pattern)
    {
        if (pattern.StartsWith('/')) pattern = pattern[1..];
        if (pattern.Length == 0 || pattern.StartsWith('#')) return false;

        const string databaseRelativePath = "cache/docs-analysis.sqlite3";
        if (pattern.EndsWith('/'))
            return databaseRelativePath.StartsWith(pattern, StringComparison.Ordinal);

        if (!pattern.Contains('/'))
        {
            return FileSystemName.MatchesSimpleExpression(
                pattern,
                Path.GetFileName(databaseRelativePath),
                ignoreCase: OperatingSystem.IsWindows());
        }

        return pattern.Contains('[')
            ? pattern.StartsWith("cache/", StringComparison.Ordinal)
            : FileSystemName.MatchesSimpleExpression(
                pattern.Replace("**", "*", StringComparison.Ordinal),
                databaseRelativePath,
                ignoreCase: OperatingSystem.IsWindows());
    }

    private static string ExistingNewline(string content)
    {
        int lineFeed = content.IndexOf('\n', StringComparison.Ordinal);
        if (lineFeed >= 0)
            return lineFeed > 0 && content[lineFeed - 1] == '\r' ? "\r\n" : "\n";

        return content.Contains('\r', StringComparison.Ordinal) ? "\r" : "\n";
    }

    private static bool EndsWithNewline(string content) =>
        content.EndsWith('\n') || content.EndsWith('\r');

    private static ScaffoldedFile Write(string repoRoot, string relativePath, string content, bool force)
    {
        // Enforced per write as well as up front, so the invariant holds for every path
        // this type will ever emit, not only the ones routed through the docs root.
        string absolute = RequireContained(repoRoot, relativePath, nameof(relativePath));

        bool existed = File.Exists(absolute);
        if (existed && !force)
            return new ScaffoldedFile(relativePath, ScaffoldOutcome.Skipped);

        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
        return new ScaffoldedFile(
            relativePath, existed ? ScaffoldOutcome.Updated : ScaffoldOutcome.Created);
    }

    private static string HostConfig(string docsRoot)
    {
        string yamlDocsRoot = HostConfigYaml.QuoteScalar(docsRoot);
        return $"""
        # Kyber-Weave host configuration. Every ontology default is overridable here.
        # Reference: {docsRoot}/documentation-ontology.md
        ontology:
          docs-root: {yamlDocsRoot}

          # Product defaults exclude five DevOps paths from the repository the ontology
          # was first built for. An empty list clears them.
          excluded-files: []

        """;
    }

    private static string Catalog(string owner)
    {
        RequireCatalogValue(owner, nameof(owner));
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        return $"""
        ---
        id: catalog
        title: Component and owner catalog
        doc-type: reference
        status: draft
        owner: {yamlOwner}
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
    }

    private static string Today =>
        DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string OntologyReference(string docsRoot, string owner)
    {
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        return $"""
        ---
        id: documentation-ontology
        title: The documentation ontology
        doc-type: reference
        status: draft
        owner: {yamlOwner}
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
}
