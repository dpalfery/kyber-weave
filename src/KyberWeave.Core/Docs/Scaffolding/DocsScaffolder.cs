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
    IReadOnlyList<ScaffoldedFile> Files);

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

    /// <summary>The one file the configuration registry is rendered into.</summary>
    private const string AgentsFilePath = "AGENTS.md";

    /// <summary>Roots checked, in order, when no docs root is supplied.</summary>
    private static readonly string[] ConventionalRoots = ["docs", "6-Docs", "doc", "documentation"];

    public static ScaffoldResult Scaffold(
        string repoRoot,
        string? docsRoot = null,
        string owner = "unassigned",
        bool force = false,
        bool kyberStandards = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string root = Path.GetFullPath(repoRoot);

        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        string resolvedOwner = owner.Trim();
        RequireCatalogValue(resolvedOwner, nameof(owner));

        KyberWeaveConfigLoadResult loadedConfig = RequireLoadableHostConfig(root);
        (string DocsRoot, DocsRootSource Source) resolution = string.IsNullOrWhiteSpace(docsRoot)
            ? ResolveDocsRoot(root, loadedConfig)
            : (docsRoot.Trim().Replace('\\', '/').TrimEnd('/'), DocsRootSource.Explicit);
        string resolvedDocsRoot = resolution.DocsRoot;
        resolvedDocsRoot = RequireEmittableValue(resolvedDocsRoot, nameof(docsRoot));

        // Checked before the first write, not only inside Write. The host config resolves
        // inside the root and would otherwise be created successfully and left behind,
        // pointing at a docs root that the very next write then rejects.
        RequireContained(root, resolvedDocsRoot, nameof(docsRoot));

        // The registry publishes paths under the root being scaffolded into, which is not
        // necessarily the one the loaded configuration names — an operator moving the corpus
        // with --docs-root would otherwise get a registry describing where it used to be.
        OntologyConfig ontology = (loadedConfig.Config ?? KyberWeaveConfig.ProductDefaults)
            .Ontology.WithDocsRoot(resolvedDocsRoot);

        if (kyberStandards)
        {
            IReadOnlyList<string> mergedTechnologies = ontology.Technologies
                .Union(KyberStandardsTemplates.All, StringComparer.Ordinal)
                .ToList();
            ontology = ontology.Clone(technologies: mergedTechnologies);
        }

        List<ScaffoldedFile> files =
        [
            WriteHostConfig(root, resolvedDocsRoot, kyberStandards),
            WriteAnalysisCacheIgnore(root),
            Write(root, DocsLayout.Ontology(resolvedDocsRoot),
                OntologyReference(resolvedDocsRoot, resolvedOwner), force),
            Write(root, $"{resolvedDocsRoot}/catalog.md",
                Catalog(resolvedOwner), force),
            Write(root, DocsLayout.Index(resolvedDocsRoot),
                DocumentationIndex(resolvedDocsRoot, resolvedOwner), force)
        ];

        foreach (string folder in DocsLayout.Folders)
        {
            files.Add(Write(root, DocsLayout.FolderIndex(resolvedDocsRoot, folder),
                FolderIndex(folder, resolvedOwner), force));
        }

        foreach (string technology in ontology.Technologies)
        {
            string standardContent = kyberStandards && KyberStandardsTemplates.TryRender(technology, resolvedOwner, Today, out string? rendered)
                ? rendered
                : TechnologyStandard(technology, resolvedOwner);

            files.Add(Write(root, DocsLayout.TechnologyStandard(resolvedDocsRoot, technology),
                standardContent, force));
        }

        files.Add(WriteConfigReg(
            root,
            resolvedDocsRoot,
            (loadedConfig.Config ?? KyberWeaveConfig.ProductDefaults).ConfigReg.Resolve(ontology)));

        return new ScaffoldResult(resolvedDocsRoot, resolution.Source, files);
    }

    /// <summary>
    /// The first conventional documentation directory that already exists, else "docs".
    /// Adopting an existing tree is the common case; creating one is not.
    /// </summary>
    private static string DetectDocsRoot(string repoRoot)
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
    private static (string DocsRoot, DocsRootSource Source) ResolveDocsRoot(
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
    private static ScaffoldedFile WriteHostConfig(string repoRoot, string docsRoot, bool kyberStandards)
    {
        string? existingPath = KyberWeaveConfigLoader.FindConfigPath(repoRoot);
        if (existingPath is null)
        {
            return Write(
                repoRoot,
                $"{KyberWeaveYamlParser.DefaultDirectoryName}/{KyberWeaveYamlParser.DefaultFileName}",
                HostConfig(docsRoot, kyberStandards),
                force: false);
        }

        string relativePath = Path.GetRelativePath(repoRoot, existingPath).Replace('\\', '/');
        string existing = File.ReadAllText(existingPath);
        string updated = HostConfigYaml.WithDocsRoot(existing, docsRoot);
        if (kyberStandards)
        {
            updated = HostConfigYaml.WithTechnologies(updated, KyberStandardsTemplates.All);
        }

        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return new ScaffoldedFile(
                relativePath,
                ScaffoldOutcome.Preserved,
                "your configuration, kept as-is; --force does not overwrite it");
        }

        File.WriteAllText(existingPath, updated);
        return new ScaffoldedFile(
            relativePath,
            ScaffoldOutcome.Updated,
            kyberStandards
                ? "docs-root and technologies; the rest of the file is untouched"
                : "docs-root only; the rest of the file is untouched");
    }

    /// <summary>
    /// Renders the configuration registry into the repository root <c>AGENTS.md</c>, creating
    /// that file when the repository has none.
    /// </summary>
    /// <remarks>
    /// A repository with no agent instructions file is the one that gains most from a
    /// registry, so the absent case is created rather than skipped. The generated region is
    /// rewritten whether or not <c>force</c> was passed — <c>force</c> governs hand-authored
    /// content, and this block is neither hand-authored nor safe to leave stale.
    /// </remarks>
    private static ScaffoldedFile WriteConfigReg(
        string repoRoot,
        string docsRoot,
        IReadOnlyList<ConfigRegEntry> entries)
    {
        string absolute = RequireContained(repoRoot, AgentsFilePath, nameof(repoRoot));
        string block = ConfigRegMarkdown.Render(entries);

        if (!File.Exists(absolute))
        {
            File.WriteAllText(absolute, ConfigRegMarkdown.NewAgentsFile(block, docsRoot));
            return new ScaffoldedFile(
                AgentsFilePath, ScaffoldOutcome.Created, "Config Reg, and a file to hold it");
        }

        string existing = File.ReadAllText(absolute);
        string updated = ConfigRegMarkdown.Splice(existing, block);
        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return new ScaffoldedFile(
                AgentsFilePath, ScaffoldOutcome.Preserved, "Config Reg already current");
        }

        File.WriteAllText(absolute, updated);
        return new ScaffoldedFile(
            AgentsFilePath,
            ScaffoldOutcome.Updated,
            "Config Reg block only; everything outside the markers is untouched");
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

#pragma warning disable CA1847 // Single-char Contains with StringComparison requires string overload to satisfy CA1307
        if (!pattern.Contains("/", StringComparison.Ordinal))
#pragma warning restore CA1847
        {
            return FileSystemName.MatchesSimpleExpression(
                pattern,
                Path.GetFileName(databaseRelativePath),
                ignoreCase: OperatingSystem.IsWindows());
        }

#pragma warning disable CA1847 // Single-char Contains with StringComparison requires string overload to satisfy CA1307
        return pattern.Contains("[", StringComparison.Ordinal)
            ? pattern.StartsWith("cache/", StringComparison.Ordinal)
            : FileSystemName.MatchesSimpleExpression(
                pattern.Replace("**", "*", StringComparison.Ordinal),
                databaseRelativePath,
                ignoreCase: OperatingSystem.IsWindows());
#pragma warning restore CA1847
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

    private static string HostConfig(string docsRoot, bool kyberStandards = false)
    {
        string yamlDocsRoot = HostConfigYaml.QuoteScalar(docsRoot);
        string technologiesBlock;
        if (kyberStandards)
        {
            technologiesBlock = "technologies:\n" + string.Join(
                "\n",
                KyberStandardsTemplates.All.Select(t => $"    - {t}"));
        }
        else
        {
            technologiesBlock = "technologies: []";
        }

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

        # Additions to the configuration registry rendered into AGENTS.md. Everything
        # 'docs init' creates is already published; name only what is yours.
        # config-reg:
        #   auth-design: {docsRoot}/reference/auth-design.md

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
            + "resolve it through the configuration registry rather than carrying their own."),
        "plans" => ("Plans",
            "Sequenced implementation work: what will be done, in what order, and how it will be\n"
            + "verified. A plan is a record of intent rather than current guidance — retrieval\n"
            + "demotes it accordingly, and it is archived once closed."),
        "specs" => ("Specifications",
            "Upfront specification work for a greenfield project or a large feature, written when\n"
            + "requirements and architecture still need defining before a plan can exist."),
        "todo" => ("Todos",
            "Work identified but not done now — a finding, a deferred fix, a declined suggestion.\n"
            + "Capturing it here is what stops it evaporating between sessions."),
        "adr" => ("Architecture decision records",
            "One record per architectural decision: what was decided, the alternatives, and why.\n"
            + "An ADR is never edited to say something else — it is superseded."),
        "rules" => ("Rules",
            "Repository-wide rules that govern how the system is built, independent of any one\n"
            + "technology. A rule about one language belongs in that technology's coding standard."),
        "reference" => ("Reference",
            "Reference material with no other home: environment variables, naming standards, the\n"
            + "external systems this repository depends on."),
        _ => throw new InvalidOperationException($"No identity defined for scaffolded folder '{folder}'.")
    };

    private static string FolderIndex(string folder, string owner)
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
        last-reviewed: {Today}
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
    private static string StandardsRegistry() =>
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

    private static string TechnologyStandard(string technology, string owner)
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
        last-reviewed: {Today}
        ---

        # {technology} coding standard

        How {technology} code is written in this repository. Agents and skills resolve this
        document as `<{technology}{ConfigRegConfig.CodingStandardSuffix}>` in the repository
        root `AGENTS.md`.

        ## Authority & status

        When this standard is in `status: current`, what it says here outranks whatever defaults a
        portable agent shipped with. While in `status: draft`, it serves as a non-authoritative
        template/proposal and does NOT override portable agent defaults until reviewed and
        promoted to `current`.

        Replace this file with the rules that actually apply, and promote `status` to
        `current`. A standard that restates a language's own documentation is noise; write the
        decisions this repository has made and would otherwise have to re-argue.

        ## Structure

        ## Naming

        ## Error handling

        ## Testing

        """;
    }

    private static string DocumentationIndex(string docsRoot, string owner)
    {
        string yamlOwner = HostConfigYaml.QuoteScalar(owner);
        return $"""
        ---
        id: documentation-index
        title: Documentation
        doc-type: index
        status: draft
        owner: {yamlOwner}
        last-reviewed: {Today}
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
