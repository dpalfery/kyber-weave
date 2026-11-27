using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

public sealed class AgentSyncCheckCommand(IAgentGovernanceService agents, IKyberWeaveConfigRepository configs, IAnsiConsole console) : Command<AnalysisSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, AnalysisSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new DiagnosticReport();
        if (!CommandHelpers.TryLoadConfig(configs, settings.Path, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(console, report, settings, "agent sync-check", "Agent");
            return 1;
        }

        report.AddRange(agents.SyncCheck(settings.Path, config.Harness).Items);

        CommandHelpers.Finish(console, report, settings, "agent sync-check", "Agent");
        return report.HasErrors ? 1 : 0;
    }
}
