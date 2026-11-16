using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;

namespace KyberWeave.Contracts.Services;

/// <summary>The findings of an agent definition run, or the reason the harness filter was refused.</summary>
/// <param name="Report">Every finding, including the refusal.</param>
/// <param name="FilterRefused">Whether the <c>--harness</c> value named no harness, so nothing was loaded.</param>
public sealed record AgentAnalysis(DiagnosticReport Report, bool FilterRefused);

/// <summary>What the <c>agent</c> commands ask of the agent governance library: validate, scan, sync-check and inventory.</summary>
public interface IAgentGovernanceService
{
    /// <summary>Validates every agent definition under <paramref name="path"/> against its harness's format.</summary>
    /// <param name="path">The project root.</param>
    /// <param name="harness">The <c>--harness</c> filter, or null for every harness.</param>
    AgentAnalysis Validate(string path, string? harness);

    /// <summary>Scans every agent definition under <paramref name="path"/> as a trust surface.</summary>
    /// <param name="path">The project root.</param>
    /// <param name="harness">The <c>--harness</c> filter, or null for every harness.</param>
    AgentAnalysis Scan(string path, string? harness);

    /// <summary>Checks that every harness carries the roles the others do, under the configured profiles.</summary>
    /// <param name="path">The project root.</param>
    /// <param name="profiles">The harness profiles of the loaded configuration.</param>
    DiagnosticReport SyncCheck(string path, HarnessProfileConfig profiles);

    /// <summary>Groups every agent definition under <paramref name="path"/> by role and harness.</summary>
    /// <param name="path">The project root.</param>
    IReadOnlyDictionary<string, Dictionary<HarnessKind, AgentModel>> RoleMatrix(string path);
}
