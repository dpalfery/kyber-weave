using System.IO.Enumeration;
using System.Text;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review.Catalogue;

namespace KyberWeave.Core.Docs.Scaffolding;

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
public sealed class DocsScaffolder : IDocsScaffoldingService
{
    private readonly IRepositoryFileStore _files;
    private readonly IKyberWeaveConfigRepository _config;
    private readonly TimeProvider _time;
    private readonly ReviewBlockWriter _reviewBlock;

    private const string AnalysisCacheIgnorePath = ".kyber-weave/.gitignore";
    private const string AnalysisCacheIgnoreEntry = "cache/";

    /// <summary>
    /// The wiring receipts directory, ignored beside the analysis cache. A receipt is a
    /// machine-local record of what this tool wrote into a harness configuration; committed, it
    /// would read as a decision the repository never made.
    /// </summary>
    private const string McpReceiptsIgnoreEntry = "receipts/";

    /// <summary>The one file the configuration registry is rendered into.</summary>
    private const string AgentsFilePath = "AGENTS.md";

    /// <summary>The note a skipped standard carries while its folder still holds README.md.</summary>
    private const string LegacyStandardNote =
        "the folder still holds its standard as README.md; run '" +
        DocsLayout.MigrateStandardsCommand + "' to rename it";

    /// <summary>Roots checked, in order, when no docs root is supplied.</summary>
    private readonly string[] ConventionalRoots = ["docs", "6-Docs", "doc", "documentation"];

    /// <summary>Creates the scaffolder over the ports it reads and writes through.</summary>
    /// <param name="files">Reads and writes the repository's files.</param>
    /// <param name="config">Loads and locates the host configuration.</param>
    /// <param name="lister">Lists the repository's files, for project-root detection.</param>
    /// <param name="tree">Answers the path questions the review configuration parser asks.</param>
    /// <param name="time">Supplies the date every generated document carries.</param>
    public DocsScaffolder(
        IRepositoryFileStore files,
        IKyberWeaveConfigRepository config,
        IRepositoryFileLister lister,
        IRepositoryTreeProbe tree,
        TimeProvider time)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _reviewBlock = new ReviewBlockWriter(files, lister, tree);
    }

    /// <inheritdoc />
    public ScaffoldResult Scaffold(
        string repoRoot,
        string? docsRoot = null,
        string owner = "unassigned",
        bool force = false,
        bool kyberStandards = false,
        bool reviewGates = true,
        IReadOnlyList<string>? standards = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        string root = Path.GetFullPath(repoRoot);

        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        string resolvedOwner = owner.Trim();
        ScaffoldValues.RequireCatalogValue(resolvedOwner, nameof(owner));

        KyberWeaveConfigLoadResult loadedConfig = RequireLoadableHostConfig(root);
        (string DocsRoot, DocsRootSource Source) resolution = string.IsNullOrWhiteSpace(docsRoot)
            ? ResolveDocsRoot(root, loadedConfig)
            : (docsRoot.Trim().Replace('\\', '/').TrimEnd('/'), DocsRootSource.Explicit);
        string resolvedDocsRoot = resolution.DocsRoot;
        resolvedDocsRoot = ScaffoldValues.RequireEmittableValue(resolvedDocsRoot, nameof(docsRoot));

        // Checked before the first write, not only inside Write. The host config resolves
        // inside the root and would otherwise be created successfully and left behind,
        // pointing at a docs root that the very next write then rejects.
        RequireContained(root, resolvedDocsRoot, nameof(docsRoot));

        // The registry publishes paths under the root being scaffolded into, which is not
        // necessarily the one the loaded configuration names — an operator moving the corpus
        // with --docs-root would otherwise get a registry describing where it used to be.
        OntologyConfig ontology = (loadedConfig.Config ?? KyberWeaveConfigDefaults.Product)
            .Ontology.WithDocsRoot(resolvedDocsRoot);

        // The two choices add to each other rather than replace each other: an operator may ask
        // for every shipped technology and one role folder in the same run. Only the technology
        // folders this run was asked for are remembered: naming one folder is not a request to
        // restyle a technology the host already declared, and that folder keeps the stub it would
        // have been given without either flag. A delivery platform ships a template and is stored
        // as a technology, so "every folder except a role" is the set --kyber-standards names.
        HashSet<string> templatedTechnologies = new(StringComparer.Ordinal);
        if (kyberStandards)
        {
            IReadOnlyList<string> mergedTechnologies = ontology.Technologies
                .Union(KyberStandardsTemplates.All, StringComparer.Ordinal)
                .ToList();
            ontology = ontology.Clone(technologies: mergedTechnologies);
            templatedTechnologies.UnionWith(KyberStandardsTemplates.AllFolders.Where(
                name => !KyberStandardsTemplates.Roles.Contains(name, StringComparer.Ordinal)));
        }

        bool writesShippedTemplates = kyberStandards;
        if (standards is { Count: > 0 })
        {
            ontology = WithChosenStandards(ontology, standards);
            templatedTechnologies.UnionWith(standards.Where(
                name => !KyberStandardsTemplates.Roles.Contains(name, StringComparer.Ordinal)));
            writesShippedTemplates = true;
        }

        // Planned before the first host-config write so a newly created file carries the
        // block at creation, and so the declared technologies are the merged ones when
        // --kyber-standards is passed. Whether the file already had a review section is read
        // up front too: the section is host-owned from then on and never rewritten.
        ReviewGatesPlan? reviewPlan = reviewGates ? _reviewBlock.Plan(root, ontology.Technologies) : null;
        string? preexistingConfigPath = _config.FindConfigPath(root);
        bool hadReviewSection = preexistingConfigPath is not null
            && ReviewBlockWriter.HasReviewSection(_files.ReadAllText(preexistingConfigPath));

        List<ScaffoldedFile> files =
        [
            WriteHostConfig(root, resolvedDocsRoot, ontology, ReviewBlockFor(reviewPlan)),
            WriteLocalStateIgnore(root),
            Write(root, DocsLayout.Ontology(resolvedDocsRoot),
                DocsScaffoldTemplates.OntologyReference(resolvedDocsRoot, resolvedOwner, Today), force),
            Write(root, $"{resolvedDocsRoot}/catalog.md",
                DocsScaffoldTemplates.Catalog(resolvedOwner, Today), force),
            Write(root, DocsLayout.Index(resolvedDocsRoot),
                DocsScaffoldTemplates.DocumentationIndex(resolvedDocsRoot, resolvedOwner, Today), force)
        ];

        foreach (string folder in DocsLayout.Folders)
        {
            files.Add(Write(root, DocsLayout.FolderIndex(resolvedDocsRoot, folder),
                DocsScaffoldTemplates.FolderIndex(folder, resolvedOwner, Today), force));
        }

        foreach (string technology in ontology.Technologies)
        {
            // A technology folder holds four files. A shipped template supplies all four; a
            // declared technology with no template gets four stubs, so the folder check has
            // the same shape to grade either way.
            foreach (KyberStandardsTemplates.Part part in Enum.GetValues<KyberStandardsTemplates.Part>())
            {
                string path =
                    $"{resolvedDocsRoot}/{DocsLayout.Standards}/{technology}/{KyberStandardsTemplates.FileName(technology, part)}";
                if (part == KyberStandardsTemplates.Part.Standard
                    && HasLegacyStandard(root, resolvedDocsRoot, technology, path))
                {
                    files.Add(new ScaffoldedFile(
                        path, ScaffoldOutcome.Skipped, LegacyStandardNote));
                    continue;
                }

                string content =
                    templatedTechnologies.Contains(technology)
                    && KyberStandardsTemplates.TryRenderPart(
                        technology, part, resolvedOwner, Today, out string? rendered)
                        ? rendered
                        : TechnologyStub(technology, part, resolvedOwner);

                files.Add(Write(root, path, content, force));
            }
        }

        foreach (string role in ontology.Roles)
        {
            foreach (KyberStandardsTemplates.Part part in Enum.GetValues<KyberStandardsTemplates.Part>())
            {
                string path =
                    $"{resolvedDocsRoot}/{DocsLayout.Standards}/{role}/{KyberStandardsTemplates.FileName(role, part)}";
                if (part == KyberStandardsTemplates.Part.Standard
                    && HasLegacyStandard(root, resolvedDocsRoot, role, path))
                {
                    files.Add(new ScaffoldedFile(
                        path, ScaffoldOutcome.Skipped, LegacyStandardNote));
                    continue;
                }

                string content =
                    KyberStandardsTemplates.TryRenderPart(role, part, resolvedOwner, Today, out string? rendered)
                        ? rendered
                        : RoleStub(role, part, resolvedOwner);

                files.Add(Write(root, path, content, force));
            }
        }

        if ((kyberStandards || standards is { Count: > 0 })
            && KyberStandardsTemplates.TryRenderSharedRules(resolvedOwner, Today, out string? sharedRules))
        {
            files.Add(Write(
                root,
                $"{resolvedDocsRoot}/{DocsLayout.Standards}/shared-rules.md",
                sharedRules,
                force));
        }

        ConfigRegConfig configReg = (loadedConfig.Config ?? KyberWeaveConfigDefaults.Product).ConfigReg;
        files.Add(WriteConfigReg(root, resolvedDocsRoot, configReg.Resolve(ontology), configReg.Autonomy));

        return new ScaffoldResult(resolvedDocsRoot, resolution.Source, files)
        {
            ReviewGates = BuildReviewOutcome(root, reviewPlan, hadReviewSection, loadedConfig.Config?.Review)
        };
    }

    /// <summary>
    /// True when a scope folder still holds its standard as <c>README.md</c> and the prefixed
    /// standard does not exist yet. Scaffolding a stub beside it would give the folder two
    /// standards carrying the same id; the migration command is what names the standard.
    /// </summary>
    private bool HasLegacyStandard(
        string repoRoot,
        string docsRoot,
        string scope,
        string standardPath)
    {
        if (_files.FileExists(RequireContained(repoRoot, standardPath, nameof(standardPath))))
            return false;

        string legacy = $"{docsRoot}/{DocsLayout.Standards}/{scope}/README.md";
        return _files.FileExists(RequireContained(repoRoot, legacy, nameof(scope)));
    }

    /// <summary>The stub written for a role part a shipped template does not supply.</summary>
    private string RoleStub(
        string role,
        KyberStandardsTemplates.Part part,
        string owner) => part switch
        {
            KyberStandardsTemplates.Part.Standard =>
                DocsScaffoldTemplates.RoleStandard(role, owner, Today),
            KyberStandardsTemplates.Part.Rules =>
                DocsScaffoldTemplates.RoleRules(role, owner, Today),
            KyberStandardsTemplates.Part.Skills =>
                DocsScaffoldTemplates.RoleSkills(role, owner, Today),
            KyberStandardsTemplates.Part.ArbiterRules =>
                DocsScaffoldTemplates.TechnologyArbiterRules(role),
            _ => throw new ArgumentOutOfRangeException(nameof(part))
        };

    /// <summary>
    /// Declares exactly the folders the operator named: a technology or platform name joins
    /// <c>ontology.technologies</c>, a role name joins <c>ontology.roles</c>. An unknown name
    /// is refused with the nearest known one rather than creating a folder nothing scopes.
    /// </summary>
    private static OntologyConfig WithChosenStandards(
        OntologyConfig ontology,
        IReadOnlyList<string> chosen)
    {
        List<string> technologies = [.. ontology.Technologies];
        List<string> roles = [.. ontology.Roles];

        foreach (string name in chosen.Distinct(StringComparer.Ordinal))
        {
            if (!KyberStandardsTemplates.AllFolders.Contains(name, StringComparer.Ordinal))
            {
                string? nearest = KyberWeave.Core.Text.StringDistance.Nearest(
                    name, KyberStandardsTemplates.AllFolders);
                throw new ArgumentException(
                    nearest is null
                        ? $"Unknown standards folder '{name}'. Name a technology, role or platform template."
                        : $"Unknown standards folder '{name}'. Did you mean '{nearest}'?",
                    nameof(chosen));
            }

            if (KyberStandardsTemplates.Roles.Contains(name, StringComparer.Ordinal))
            {
                if (!roles.Contains(name, StringComparer.Ordinal)) roles.Add(name);
            }
            else if (!technologies.Contains(name, StringComparer.Ordinal))
            {
                technologies.Add(name);
            }
        }

        return ontology.Clone(technologies: technologies, roles: roles);
    }

    /// <summary>The stub written for a folder part a shipped template does not supply.</summary>
    private string TechnologyStub(string technology,
        KyberStandardsTemplates.Part part,
        string owner) => part switch
        {
            KyberStandardsTemplates.Part.Standard =>
                DocsScaffoldTemplates.TechnologyStandard(technology, owner, Today),
            KyberStandardsTemplates.Part.Rules =>
                DocsScaffoldTemplates.TechnologyRules(technology, owner, Today),
            KyberStandardsTemplates.Part.Skills =>
                DocsScaffoldTemplates.TechnologySkills(technology, owner, Today),
            KyberStandardsTemplates.Part.ArbiterRules =>
                DocsScaffoldTemplates.TechnologyArbiterRules(technology),
            _ => throw new ArgumentOutOfRangeException(nameof(part))
        };

    /// <summary>The rendered block to write, or null when there is nothing to write.</summary>
    private RenderedReviewBlock? ReviewBlockFor(ReviewGatesPlan? reviewPlan) =>
        reviewPlan is { ShouldWrite: true } ? reviewPlan.Rendered : null;

    /// <summary>
    /// Accounts for the review section after the host config was written: what was appended,
    /// what was already host-owned, or that nothing was declared to write.
    /// </summary>
    private ReviewGatesOutcome BuildReviewOutcome(
        string repoRoot,
        ReviewGatesPlan? reviewPlan,
        bool hadReviewSection,
        ReviewConfig? hostReview)
    {
        if (reviewPlan is null)
        {
            return new ReviewGatesOutcome(
                OptedOut: true,
                AlreadyPresent: false,
                Written: false,
                WrittenGates: [],
                CommentedOutGates: [],
                NoFamily: [],
                MissingFromExisting: []);
        }

        // WriteHostConfig always ensures a host config, so this resolves; the guard names the
        // invariant rather than letting a null flow into the file read below.
        string configPath = _config.FindConfigPath(repoRoot)
            ?? throw new InvalidOperationException(
                $"The scaffolded host configuration for '{repoRoot}' could not be found.");
        bool hasReviewSection = ReviewBlockWriter.HasReviewSection(_files.ReadAllText(configPath));

        if (hasReviewSection && !hadReviewSection)
        {
            return new ReviewGatesOutcome(
                OptedOut: false,
                AlreadyPresent: false,
                Written: true,
                WrittenGates: reviewPlan.Rendered.Written,
                CommentedOutGates: reviewPlan.Rendered.CommentedOut,
                NoFamily: reviewPlan.Rendered.NoFamily,
                MissingFromExisting: []);
        }

        if (hasReviewSection)
        {
            return new ReviewGatesOutcome(
                OptedOut: false,
                AlreadyPresent: true,
                Written: false,
                WrittenGates: [],
                CommentedOutGates: [],
                NoFamily: reviewPlan.Rendered.NoFamily,
                MissingFromExisting: ReviewBlockWriter.MissingGateIds(hostReview, reviewPlan.Rendered));
        }

        return new ReviewGatesOutcome(
            OptedOut: false,
            AlreadyPresent: false,
            Written: false,
            WrittenGates: [],
            CommentedOutGates: [],
            NoFamily: reviewPlan.Rendered.NoFamily,
            MissingFromExisting: []);
    }

    /// <summary>
    /// The first conventional documentation directory that already exists, else "docs".
    /// Adopting an existing tree is the common case; creating one is not.
    /// </summary>
    private string DetectDocsRoot(string repoRoot)
    {
        foreach (string candidate in ConventionalRoots)
        {
            if (_files.DirectoryExists(Path.Combine(repoRoot, candidate)))
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
    private (string DocsRoot, DocsRootSource Source) ResolveDocsRoot(
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
    private KyberWeaveConfigLoadResult RequireLoadableHostConfig(string repoRoot)
    {
        KyberWeaveConfigLoadResult loaded = _config.TryLoad(repoRoot);
        if (loaded.Success)
            return loaded;

        string path = loaded.ConfigPath ?? KyberWeaveYamlParser.DefaultFileName;
        throw new InvalidDataException(
            $"{IDocsHostConfiguration.ConfigLoadErrorCode}: Failed to load '{path}': " +
            (loaded.Error ?? "The configuration could not be read."));
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
    private string RequireContained(string repoRoot, string relativePath, string parameterName)
    {
        string absolute = Path.GetFullPath(
            Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        string boundary = RepositoryBoundary.For(repoRoot, Path.DirectorySeparatorChar);
        if (!RepositoryBoundary.IsWithin(absolute, boundary, OperatingSystem.IsWindows()))
        {
            throw new ArgumentException(
                $"Refusing to write outside the repository root: '{relativePath}' resolves to '{absolute}'.",
                parameterName);
        }

        return absolute;
    }

    /// <summary>
    /// Creates the host config when the repository has none — with the <c>mcp:</c> declaration
    /// and any rendered review block already in it — and otherwise rewrites only its
    /// <c>ontology.docs-root</c> while appending the sections it lacks.
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
    /// <para>
    /// The <c>mcp:</c> declaration and the review gates are append-only: written when the file
    /// has none, never rewritten once the host has one. Both are host-owned from the moment
    /// they exist, so a re-run that regenerated either would replace the repository's own
    /// claims with the tool's guess, showing up in review as changes nobody made.
    /// </para>
    /// </remarks>
    private ScaffoldedFile WriteHostConfig(
        string repoRoot,
        string docsRoot,
        OntologyConfig ontology,
        RenderedReviewBlock? reviewBlock)
    {
        string? existingPath = _config.FindConfigPath(repoRoot);
        if (existingPath is null)
        {
            string created = DocsScaffoldTemplates.HostConfig(
                docsRoot,
                technologies: ontology.Technologies,
                roles: ontology.Roles);
            if (reviewBlock is not null)
            {
                string finalText = created.TrimEnd('\r', '\n') + "\n\n" + ReviewBlockWriter.KeyFirstYaml(reviewBlock);
                _reviewBlock.ValidateFinalText(finalText, repoRoot, reviewBlock);
                created = finalText;
            }

            return Write(
                repoRoot,
                $"{KyberWeaveYamlParser.DefaultDirectoryName}/{KyberWeaveYamlParser.DefaultFileName}",
                created,
                force: false);
        }

        string relativePath = Path.GetRelativePath(repoRoot, existingPath).Replace('\\', '/');
        string existing = _files.ReadAllText(existingPath);
        string updated = HostConfigYaml.WithDocsRoot(existing, docsRoot);
        if (ontology.Technologies.Count > 0)
        {
            updated = HostConfigYaml.WithTechnologies(updated, ontology.Technologies);
        }

        if (ontology.Roles.Count > 0)
        {
            updated = HostConfigYaml.WithRoles(updated, ontology.Roles);
        }

        bool mergeChanged = !string.Equals(existing, updated, StringComparison.Ordinal);

        // A host-owned mcp declaration is never rewritten, with or without --force, for the
        // same reason as the review block below: the declaration is host-owned from the
        // moment it exists and belongs entirely to the host repository.
        bool mcpAppended = false;
        if (!McpDeclarationBlockWriter.HasMcpSection(updated))
        {
            updated = McpDeclarationBlockWriter.AppendBlock(updated);
            mcpAppended = true;
        }

        // A host-owned review section is never rewritten, with or without --force.
        bool reviewAppended = false;
        if (reviewBlock is not null && !ReviewBlockWriter.HasReviewSection(updated))
        {
            updated = _reviewBlock.AppendBlock(updated, reviewBlock, repoRoot);
            reviewAppended = true;
        }

        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return new ScaffoldedFile(
                relativePath,
                ScaffoldOutcome.Preserved,
                "your configuration, kept as-is; --force does not overwrite it");
        }

        List<string> changes = [];
        if (mergeChanged)
        {
            changes.Add("docs-root and ontology lists merged");
        }

        if (mcpAppended)
        {
            changes.Add("the mcp declaration appended");
        }

        if (reviewAppended)
        {
            changes.Add("the review gates appended");
        }

        string note = $"{string.Join(", ", changes)}; the rest of the file is untouched";

        _files.WriteAllText(existingPath, updated);
        return new ScaffoldedFile(
            relativePath,
            ScaffoldOutcome.Updated,
            note);
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
    private ScaffoldedFile WriteConfigReg(
        string repoRoot,
        string docsRoot,
        IReadOnlyList<ConfigRegEntry> entries,
        string? autonomy)
    {
        string absolute = RequireContained(repoRoot, AgentsFilePath, nameof(repoRoot));
        string block = ConfigRegMarkdown.Render(entries, autonomy);

        if (!_files.FileExists(absolute))
        {
            _files.WriteAllText(absolute, ConfigRegMarkdown.NewAgentsFile(block, docsRoot));
            return new ScaffoldedFile(
                AgentsFilePath, ScaffoldOutcome.Created, "Config Reg, and a file to hold it");
        }

        string existing = _files.ReadAllText(absolute);
        string updated = ConfigRegMarkdown.Splice(existing, block);
        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return new ScaffoldedFile(
                AgentsFilePath, ScaffoldOutcome.Preserved, "Config Reg already current");
        }

        _files.WriteAllText(absolute, updated);
        return new ScaffoldedFile(
            AgentsFilePath,
            ScaffoldOutcome.Updated,
            "Config Reg block only; everything outside the markers is untouched");
    }

    /// <summary>
    /// Establishes the narrow repository-local state exclusions — the analysis cache and the
    /// wiring receipts — without regenerating the operator's other ignore rules.
    /// </summary>
    /// <remarks>
    /// An exact entry earlier in the file is not enough when a later negation exposes the
    /// analysis database again. Appending the same narrow entry after that negation restores
    /// protection while preserving every existing byte. The existing newline style is used
    /// for the appended boundary so a merge does not introduce mixed line endings.
    /// The two entries are decided independently: a host that already ignores the cache still
    /// needs the receipts directory, which an older scaffold never wrote.
    /// </remarks>
    private ScaffoldedFile WriteLocalStateIgnore(string repoRoot)
    {
        string absolute = RequireContained(repoRoot, AnalysisCacheIgnorePath, nameof(repoRoot));
        if (!_files.FileExists(absolute))
        {
            _files.CreateDirectory(Path.GetDirectoryName(absolute)!);
            _files.WriteAllText(
                absolute,
                AnalysisCacheIgnoreEntry + "\n" + McpReceiptsIgnoreEntry + "\n");
            return new ScaffoldedFile(AnalysisCacheIgnorePath, ScaffoldOutcome.Created);
        }

        byte[] existingBytes = _files.ReadAllBytes(absolute);
        (Encoding? encoding, int preambleLength) = DetectEncoding(existingBytes);
        string existing = encoding.GetString(
            existingBytes,
            preambleLength,
            existingBytes.Length - preambleLength);

        List<string> missing = [];
        if (!HasEffectiveAnalysisCacheIgnore(existing))
        {
            missing.Add(AnalysisCacheIgnoreEntry);
        }

        if (!HasIgnoreEntry(existing, McpReceiptsIgnoreEntry))
        {
            missing.Add(McpReceiptsIgnoreEntry);
        }

        if (missing.Count == 0)
        {
            return new ScaffoldedFile(
                AnalysisCacheIgnorePath,
                ScaffoldOutcome.Preserved,
                "your local-state ignore rules, kept as-is");
        }

        string newline = ExistingNewline(existing);
        string boundary = existing.Length == 0 || EndsWithNewline(existing) ? string.Empty : newline;
        byte[] appendedBytes = encoding.GetBytes(boundary + string.Join(newline, missing) + newline);
        using (Stream stream = _files.OpenAppend(absolute))
        {
            stream.Write(appendedBytes);
        }

        return new ScaffoldedFile(
            AnalysisCacheIgnorePath,
            ScaffoldOutcome.Updated,
            $"added {string.Join(" and ", missing)}; existing ignore rules are untouched");
    }

    /// <summary>True when <paramref name="content"/> carries <paramref name="entry"/> as a whole line.</summary>
    /// <remarks>
    /// Exact equality rather than a pattern match: the receipts directory is a plain directory
    /// rule with no negation semantics of its own, and a line that merely starts with the entry
    /// (<c>receipts/archive/</c>) does not ignore the directory the wiring writes into.
    /// </remarks>
    private bool HasIgnoreEntry(string content, string entry)
    {
        using StringReader reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (StringComparer.Ordinal.Equals(line, entry))
                return true;
        }

        return false;
    }

    private (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] content) =>
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

    private bool HasEffectiveAnalysisCacheIgnore(string content)
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

    private bool NegatesAnalysisCacheProtection(string pattern)
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

    private string ExistingNewline(string content)
    {
        int lineFeed = content.IndexOf('\n', StringComparison.Ordinal);
        if (lineFeed >= 0)
            return lineFeed > 0 && content[lineFeed - 1] == '\r' ? "\r\n" : "\n";

        return content.Contains('\r', StringComparison.Ordinal) ? "\r" : "\n";
    }

    private bool EndsWithNewline(string content) =>
        content.EndsWith('\n') || content.EndsWith('\r');

    private ScaffoldedFile Write(string repoRoot, string relativePath, string content, bool force)
    {
        // Enforced per write as well as up front, so the invariant holds for every path
        // this type will ever emit, not only the ones routed through the docs root.
        string absolute = RequireContained(repoRoot, relativePath, nameof(relativePath));

        bool existed = _files.FileExists(absolute);
        if (existed && !force)
            return new ScaffoldedFile(relativePath, ScaffoldOutcome.Skipped);

        _files.CreateDirectory(Path.GetDirectoryName(absolute)!);
        _files.WriteAllText(absolute, content);
        return new ScaffoldedFile(
            relativePath, existed ? ScaffoldOutcome.Updated : ScaffoldOutcome.Created);
    }

    private string Today =>
        _time.GetUtcNow().UtcDateTime.ToString(
            "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
