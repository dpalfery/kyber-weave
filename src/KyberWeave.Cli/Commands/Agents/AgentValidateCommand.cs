using KyberWeave.Core.Agents.Model;
using System.Threading;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Validation;
using KyberWeave.Core.Diagnostics;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

public sealed class AgentValidateCommand : Command<AgentCommandSettings>
{
    protected override int Execute(CommandContext context, AgentCommandSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new DiagnosticReport();

        if (!AgentLoader.TryParseHarnessFilter(settings.Harness, out HarnessKind? harnessFilter, out string? error))
        {
            report.Add(new Diagnostic("KW-PARSE-000", Severity.Error, error!, "agent", settings.Path));
            CommandHelpers.Finish(report, settings, "agent validate", "Agent");
            return 1;
        }

        AgentSet agentSet = AgentLoader.LoadAll(settings.Path, harnessFilter);

        foreach (AgentModel agent in agentSet.Agents)
            report.AddRange(AgentSpecValidator.Validate(agent).Items);

        CommandHelpers.Finish(report, settings, "agent validate", "Agent");
        return report.HasErrors ? 1 : 0;
    }

    public int Execute(CommandContext context, AgentCommandSettings settings) => Execute(context, settings, CancellationToken.None);
}
