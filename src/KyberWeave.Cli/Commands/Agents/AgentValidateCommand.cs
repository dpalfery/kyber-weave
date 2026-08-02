using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Diagnostics;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

public sealed class AgentValidateCommand : Command<AgentCommandSettings>
{
    public override int Execute(CommandContext context, AgentCommandSettings settings)
    {
        var report = new DiagnosticReport();

        if (!AgentLoader.TryParseHarnessFilter(settings.Harness, out var harnessFilter, out var error))
        {
            report.Add(new Diagnostic("KW-PARSE-000", Severity.Error, error!, "agent", settings.Path));
            CommandHelpers.Finish(report, settings, "agent validate", "Agent");
            return 1;
        }

        var agentSet = AgentLoader.LoadAll(settings.Path, harnessFilter);

        foreach (var agent in agentSet.Agents)
            report.AddRange(AgentSpecValidator.Validate(agent).Items);

        CommandHelpers.Finish(report, settings, "agent validate", "Agent");
        return report.HasErrors ? 1 : 0;
    }
}
