using KyberWeave.Core.Agents.Model;
using System.Threading;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

public sealed class AgentSyncCheckCommand : Command<AnalysisSettings>
{
    protected override int Execute(CommandContext context, AnalysisSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new DiagnosticReport();
        if (!CommandHelpers.TryLoadConfig(settings.Path, settings.Config, report, out KyberWeaveConfig config))
        {
            CommandHelpers.Finish(report, settings, "agent sync-check", "Agent");
            return 1;
        }

        AgentSet agentSet = AgentLoader.LoadAll(settings.Path);
        DiagnosticReport r = AgentSyncLinter.LintSet(agentSet, settings.Path, config.Harness);
        report.AddRange(r.Items);

        CommandHelpers.Finish(report, settings, "agent sync-check", "Agent");
        return report.HasErrors ? 1 : 0;
    }

    public int Execute(CommandContext context, AnalysisSettings settings) => Execute(context, settings, CancellationToken.None);
}
