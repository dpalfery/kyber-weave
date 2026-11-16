using KyberWeave.Contracts.Services;
using KyberWeave.Core.Diagnostics;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

public sealed class AgentValidateCommand(IAgentGovernanceService agents) : Command<AgentCommandSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, AgentCommandSettings settings, CancellationToken cancellationToken)
    {
        AgentAnalysis analysis = agents.Validate(settings.Path, settings.Harness);
        DiagnosticReport report = analysis.Report;

        CommandHelpers.Finish(report, settings, "agent validate", "Agent");
        return analysis.FilterRefused || report.HasErrors ? 1 : 0;
    }
}
