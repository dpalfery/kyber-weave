using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Diagnostics;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

public sealed class AgentSyncCheckCommand : Command<AnalysisSettings>
{
    public override int Execute(CommandContext context, AnalysisSettings settings)
    {
        var report = new DiagnosticReport();
        if (!CommandHelpers.TryLoadConfig(settings.Path, settings.Config, report, out var config))
        {
            CommandHelpers.Finish(report, settings, "agent sync-check", "Agent");
            return 1;
        }

        var agentSet = AgentLoader.LoadAll(settings.Path);
        var r = AgentSyncLinter.LintSet(agentSet, settings.Path, config.Harness);
        report.AddRange(r.Items);

        CommandHelpers.Finish(report, settings, "agent sync-check", "Agent");
        return report.HasErrors ? 1 : 0;
    }
}
