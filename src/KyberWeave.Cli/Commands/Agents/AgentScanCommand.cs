using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Authoring;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

/// <summary>
/// Scans harness agent definitions as a trust surface (secrets, safety-bypass directives).
/// </summary>
public sealed class AgentScanCommand(IAgentGovernanceService agents, IAnsiConsole console) : Command<AgentScanSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, AgentScanSettings settings, CancellationToken cancellationToken)
    {
        AgentAnalysis analysis = agents.Scan(settings.Path, settings.Harness);
        DiagnosticReport report = analysis.Report;

        CommandHelpers.Finish(console, report, settings, "agent scan", "Agent");
        if (analysis.FilterRefused)
            return 1;

        return FailOnPolicy.ExitCode(report, settings.FailOn);
    }
}
