using System.ComponentModel;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Core.Docs.Model;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Review;
using KyberWeave.Core.FileSystem;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Shared schema-validation sequence for the CLI and the docs MCP server so the two
/// surfaces cannot diverge on which validators run or in what order.
/// </summary>
/// <param name="files">Reads the registers and glossary the validators check.</param>
/// <param name="changes">Reads the files the working tree changed, for the archive-closeout gate.</param>
/// <param name="severityOverrides">Validates the review guidance's severity overrides.</param>
/// <param name="timeProvider">Dates new glossary rows.</param>
/// <param name="paths">Resolves the repository root the caller names.</param>
/// <param name="environment">Names the directory a relative repository root is resolved against.</param>
/// <param name="skills">Builds the KW skill catalog the skills-list name check reads. Null skips that one check.</param>
public sealed class DocsSchemaGate(
    IRepositoryFileStore files,
    IGitChangeReader changes,
    ISeverityOverrideSource severityOverrides,
    TimeProvider timeProvider,
    IPathResolver paths,
    IProcessEnvironment environment,
    IKwSkillCatalogFactory? skills = null) : IDocsSchemaGate
{
    /// <summary>
    /// Runs every frontmatter and inventory validator, the review guidance's severity overrides, the managed glossary gate, and — only
    /// when <paramref name="mergeReady"/> is set — the open-work and archive-closeout gates.
    /// </summary>
    public DiagnosticReport Validate(
        string repoRoot,
        KyberWeaveConfig config,
        DocumentSet corpus,
        Func<IDocumentSource> closeoutLoaderFactory,
        bool mergeReady,
        string? gitBase = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(closeoutLoaderFactory);

        // One checkout for the whole run: every validator below reads the same root, so a caller that passes a
        // relative spelling cannot have half the gate check one tree and half another.
        string root = paths.GetFullPath(repoRoot, environment.CurrentDirectory);
        DiagnosticReport report = new();
        OntologyConfig ontology = config.Ontology;

        IKwSkillCatalog? catalog = skills?.ForRepository(root, config);
        report.AddRange(new DocSpecValidator(root, ontology, files, paths, environment, catalog).Validate(corpus).Items);
        report.AddRange(new ConfigRegValidator(root, config, files, paths, environment).Validate().Items);
        report.AddRange(severityOverrides.Validate(root, config.ConfigReg).Items);
        report.AddRange(new PlanInventoryValidator(config).Validate(corpus).Items);
        report.AddRange(new TodoInventoryValidator(config).Validate(corpus).Items);
        report.AddRange(new PlanTaskFileValidator(root, config, files).Validate(corpus).Items);

        try
        {
            report.AddRange(new ManagedGlossary(
                root,
                config,
                timeProvider,
                files,
                paths,
                environment).Validate().Items);
        }
        catch (Exception exception) when (IsOperationalGlossaryFailure(exception))
        {
            report.Add(new Diagnostic(
                ManagedGlossary.ValidationRuleCode,
                Severity.Error,
                exception.Message,
                "glossary",
                Hint: "Fix the managed glossary path or contents, then re-run docs validate."));
        }

        if (mergeReady)
        {
            report.AddRange(new OpenWorkValidator(config, files, root).Validate(corpus).Items);
            report.AddRange(ValidateCloseout(root, config, corpus, closeoutLoaderFactory, gitBase).Items);
        }

        return report;
    }

    private static bool IsOperationalGlossaryFailure(Exception exception) => exception is
        IOException or UnauthorizedAccessException or InvalidDataException or
        InvalidOperationException or ArgumentException;

    /// <summary>
    /// Runs the archive-closeout gate over the change the working tree carries. Scope that
    /// cannot resolve fails closed instead of reporting a clean run.
    /// </summary>
    private DiagnosticReport ValidateCloseout(
        string repoRoot,
        KyberWeaveConfig config,
        DocumentSet set,
        Func<IDocumentSource> closeoutLoaderFactory,
        string? gitBase)
    {
        DiagnosticReport report = new();

        if (!string.IsNullOrWhiteSpace(gitBase))
        {
            string? refusal = GitChangeParsing.ValidateBase(gitBase);
            if (refusal is not null)
            {
                report.Add(new Diagnostic(
                    ArchiveCloseoutValidator.MissingCloseoutEvidence,
                    Severity.Error,
                    $"Archive closeout scope is unknown (C0): {refusal}",
                    "archive",
                    Hint: "Name a git ref such as HEAD or origin/main."));
                return report;
            }
        }

        string baseRef = string.IsNullOrWhiteSpace(gitBase) ? "origin/HEAD" : gitBase;
        IReadOnlyList<ChangedPath> changed;
        try
        {
            changed = changes.ResolveSinceMergeBase(repoRoot, baseRef);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidOperationException or Win32Exception or TimeoutException)
        {
            report.Add(new Diagnostic(
                ArchiveCloseoutValidator.MissingCloseoutEvidence,
                Severity.Error,
                $"Archive closeout scope is unknown (C0): {ex.Message}",
                "archive",
                Hint: "Fetch the full history ('git fetch --unshallow' or 'git fetch origin') and re-run."));
            return report;
        }

        report.AddRange(new ArchiveCloseoutValidator(
            repoRoot,
            config,
            closeoutLoaderFactory,
            files,
            paths,
            environment).Validate(set, changed).Items);
        return report;
    }
}
