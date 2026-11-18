using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Security;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Application.Agents;

/// <summary>Runs the agent governance library over a project root and aggregates what it finds.</summary>
public sealed class AgentGovernanceService(IFileSystem files, IPathResolver paths, IProcessEnvironment environment) : IAgentGovernanceService
{
    private const string FilterRefusedCode = "KW-PARSE-000";

    private readonly AgentFileReader _reader = new(files, paths, environment);

    /// <inheritdoc />
    public AgentAnalysis Validate(string path, string? harness)
    {
        return Run(path, harness, agent => AgentSpecValidator.Validate(agent).Items);
    }

    /// <inheritdoc />
    public AgentAnalysis Scan(string path, string? harness)
    {
        return Run(path, harness, agent => AgentPromptScanner.Scan(agent).Items);
    }

    /// <inheritdoc />
    public DiagnosticReport SyncCheck(string path, HarnessProfileConfig profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        DiagnosticReport report = new();
        AgentSet agentSet = _reader.LoadAll(path);
        DiagnosticReport found = AgentSyncLinter.LintSet(agentSet, path, profiles, files.FileExists);
        report.AddRange(found.Items);
        return report;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, Dictionary<HarnessKind, AgentModel>> RoleMatrix(string path)
    {
        return _reader.LoadAll(path).GetRoleHarnessMatrix();
    }

    private AgentAnalysis Run(string path, string? harness, Func<AgentModel, IEnumerable<Diagnostic>> check)
    {
        DiagnosticReport report = new();

        if (!AgentHarnessFilter.TryParse(harness, out HarnessKind? harnessFilter, out string? error))
        {
            report.Add(new Diagnostic(FilterRefusedCode, Severity.Error, error!, "agent", path));
            return new AgentAnalysis(report, FilterRefused: true);
        }

        AgentSet agentSet = _reader.LoadAll(path, harnessFilter);
        foreach (AgentModel agent in agentSet.Agents)
            report.AddRange(check(agent));

        return new AgentAnalysis(report, FilterRefused: false);
    }
}
