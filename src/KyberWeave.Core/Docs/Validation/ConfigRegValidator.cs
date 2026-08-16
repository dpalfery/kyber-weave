using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Scaffolding;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// Checks the configuration registry: that every property resolves to something on disk, and
/// that the copy rendered into the repository root <c>AGENTS.md</c> still says what
/// configuration says.
/// </summary>
/// <remarks>
/// The registry is the one artifact a portable skill is allowed to depend on, which makes a
/// broken entry expensive in a way an ordinary broken link is not: the skill fails in a host
/// repository, where nobody can see the configuration that caused it. Both checks are errors
/// for that reason, and both are fixed by the same command that generated the registry.
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
    private readonly KyberWeaveConfig _config;

    public ConfigRegValidator(string repoRoot, KyberWeaveConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        ArgumentNullException.ThrowIfNull(config);
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
    /// <c>AGENTS.md</c> carries the generated block, or when it declared additions of its own
    /// — both are deliberate acts, and after either one a broken entry is a real defect.
    /// </remarks>
    public DiagnosticReport Validate()
    {
        DiagnosticReport report = new DiagnosticReport();
        string agentsFile = Path.Combine(_repoRoot, AgentsFilePath);
        bool adopted = _config.ConfigReg.Additions.Count > 0
            || (File.Exists(agentsFile) && ConfigRegMarkdown.ContainsBlock(File.ReadAllText(agentsFile)));

        if (!adopted)
            return report;

        IReadOnlyList<ConfigRegEntry> entries = _config.ConfigReg.Resolve(_config.Ontology);

        foreach (ConfigRegEntry entry in entries)
        {
            string absolute = Path.Combine(_repoRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(absolute) || File.Exists(absolute))
                continue;

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
    /// Compares the generated region of <c>AGENTS.md</c> to what the current configuration
    /// renders. An absent file, an absent block and a stale block are one finding with one
    /// fix, so they share an id and differ only in what the message says happened.
    /// </summary>
    private void ValidateRendering(IReadOnlyList<ConfigRegEntry> entries, DiagnosticReport report)
    {
        string absolute = Path.Combine(_repoRoot, AgentsFilePath);
        if (!File.Exists(absolute))
        {
            report.Add(new Diagnostic(
                StaleRendering, Severity.Error,
                $"{AgentsFilePath} does not exist, so the configuration registry reaches no agent.",
                Subject, AgentsFilePath,
                "Run 'kyber-weave docs init', which creates it."));
            return;
        }

        string existing = File.ReadAllText(absolute);
        string expected;
        try
        {
            expected = ConfigRegMarkdown.Splice(existing, ConfigRegMarkdown.Render(entries));
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
