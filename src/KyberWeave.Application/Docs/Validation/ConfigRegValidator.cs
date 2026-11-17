using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Checks the configuration registry: that every path-valued property resolves to something on
/// disk, and that the copy rendered into the repository root <c>AGENTS.md</c> still says what
/// configuration says.
/// </summary>
/// <remarks>
/// The registry is the one artifact a portable skill is allowed to depend on, which makes a
/// broken entry expensive in a way an ordinary broken link is not: the skill fails in a host
/// repository, where nobody can see the configuration that caused it. Both checks are errors
/// for that reason, and both are fixed by the same command that generated the registry. The
/// one exception is a host still in the standards warning period: its entry points at the
/// prefixed standard that a folder still holding <c>README.md</c> has not gained yet, and that
/// is a warning naming the migration command so the host's runs are not blocked.
/// </remarks>
public sealed class ConfigRegValidator
{
    /// <summary>A registry property naming a path that does not exist.</summary>
    public const string UnresolvedPath = "KW-CONFIG-REG-001";

    /// <summary>The rendered <c>AGENTS.md</c> block is missing or no longer matches configuration.</summary>
    public const string StaleRendering = "KW-CONFIG-REG-002";

    private const string AgentsFilePath = "AGENTS.md";
    private const string Subject = "config-reg";

    private readonly string _repoRoot;
    private readonly IDocsEngineConfig _config;
    private readonly IRepositoryFileStore _files;

    public ConfigRegValidator(string repoRoot, IDocsEngineConfig config, IRepositoryFileStore files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
        _repoRoot = Path.GetFullPath(repoRoot);
        _config = config;
    }

    /// <summary>
    /// Checks the registry, or reports nothing at all in a repository that has not adopted
    /// one.
    /// </summary>
    /// <remarks>
    /// Adoption is what makes these findings meaningful. Every corpus predating the registry
    /// would otherwise fail the moment its CLI was upgraded, with an error per property, for
    /// a structure it never asked for. A repository has adopted the registry when its
    /// <c>AGENTS.md</c> carries the generated block, or when it configured something the
    /// registry publishes — an addition, or a setting behind a mode property — since each of
    /// those is a deliberate act, and after any of them a broken entry is a real defect.
    /// </remarks>
    public DiagnosticReport Validate()
    {
        DiagnosticReport report = new();
        string agentsFile = Path.Combine(_repoRoot, AgentsFilePath);
        bool adopted = _config.ConfigReg.Additions.Count > 0
                       || _config.ConfigReg.Autonomy is not null
                       || !string.IsNullOrWhiteSpace(_config.ConfigReg.Onboarding)
                       || _config.ConfigReg.StopOnToolFailure is not null
                       || _config.ConfigReg.RunBaseBranch is not null
                       || _config.Ontology.Intake
                       || (_files.FileExists(agentsFile) && ConfigRegMarkdown.ContainsBlock(_files.ReadAllText(agentsFile)));

        if (!adopted)
            return report;

        IReadOnlyList<ConfigRegEntry> entries = _config.ConfigReg.Resolve(_config.Ontology);

        foreach (ConfigRegEntry entry in entries)
        {
            // A mode entry carries a setting, not a path: there is no 'enabled' file for this
            // check to find, and reporting one would fail an opted-in repository for adopting
            // the feature. The exemption is a property of the entry kind, not a way out for a
            // path entry — those are still checked, below and without exception.
            if (entry.Kind is ConfigRegEntryKind.Mode)
                continue;

            string absolute = Path.Combine(_repoRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (_files.DirectoryExists(absolute) || _files.FileExists(absolute))
                continue;

            if (LegacyStandardScope(entry.Path) is { } scope)
            {
                report.Add(new Diagnostic(
                    UnresolvedPath, Severity.Warning,
                    $"<{entry.Name}> points at '{entry.Path}', which does not exist: the " +
                    $"'{scope}' folder still holds its standard as README.md.",
                    Subject, entry.Path,
                    $"Run '{DocsLayout.MigrateStandardsCommand}' to rename it to " +
                    $"'{scope}{DocsLayout.StandardsSuffix}' and reshape it under the six headings."));
                continue;
            }

            report.Add(new Diagnostic(
                UnresolvedPath, Severity.Error,
                $"<{entry.Name}> points at '{entry.Path}', which does not exist.",
                Subject, entry.Path,
                "Run 'kyber-weave docs init' to scaffold what it creates, or correct the entry " +
                "in .kyber-weave/kyber-weave.yml."));
        }

        ValidateRendering(entries, report);
        return report;
    }

    /// <summary>
    /// The scope whose legacy <c>README.md</c> standard still stands where this registry path
    /// points, or null when the path is not a standard's or the legacy file is absent.
    /// </summary>
    private string? LegacyStandardScope(string relativePath)
    {
        OntologyConfig ontology = _config.Ontology;
        IEnumerable<string> scopes = ontology.Technologies.Concat(ontology.Roles)
            .Distinct(StringComparer.Ordinal);

        foreach (string docsRoot in ontology.DocsRoots)
        {
            foreach (string scope in scopes)
            {
                if (!string.Equals(
                        relativePath,
                        DocsLayout.StandardFile(docsRoot, scope),
                        StringComparison.Ordinal))
                {
                    continue;
                }

                string legacy = $"{docsRoot}/{DocsLayout.Standards}/{scope}/README.md";
                string absolute = Path.Combine(
                    _repoRoot, legacy.Replace('/', Path.DirectorySeparatorChar));
                return _files.FileExists(absolute) ? scope : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Compares the generated region of <c>AGENTS.md</c> to what the current configuration
    /// renders. An absent file, an absent block and a stale block are one finding with one
    /// fix, so they share an id and differ only in what the message says happened.
    /// </summary>
    private void ValidateRendering(IReadOnlyList<ConfigRegEntry> entries, DiagnosticReport report)
    {
        string absolute = Path.Combine(_repoRoot, AgentsFilePath);
        if (!_files.FileExists(absolute))
        {
            report.Add(new Diagnostic(
                StaleRendering, Severity.Error,
                $"{AgentsFilePath} does not exist, so the configuration registry reaches no agent.",
                Subject, AgentsFilePath,
                "Run 'kyber-weave docs init', which creates it."));
            return;
        }

        string existing = _files.ReadAllText(absolute);
        string expected;
        try
        {
            expected = ConfigRegMarkdown.Splice(existing,
                ConfigRegMarkdown.Render(entries, _config.ConfigReg.Autonomy));
        }
        catch (InvalidDataException ex)
        {
            report.Add(new Diagnostic(
                StaleRendering, Severity.Error, ex.Message, Subject, AgentsFilePath));
            return;
        }

        if (string.Equals(existing, expected, StringComparison.Ordinal))
            return;

        report.Add(new Diagnostic(
            StaleRendering, Severity.Error,
            $"The Config Reg block in {AgentsFilePath} does not match .kyber-weave/kyber-weave.yml.",
            Subject, AgentsFilePath,
            "Run 'kyber-weave docs init'. The block is generated; edit the configuration instead."));
    }
}
