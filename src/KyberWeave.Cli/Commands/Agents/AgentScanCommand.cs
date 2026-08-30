using KyberWeave.Core.Agents.Model;
using System.Threading;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Agents.Security;
using KyberWeave.Core.Diagnostics;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

/// <summary>
/// Scans harness agent definitions as a trust surface (secrets, safety-bypass directives).
/// </summary>
public sealed class AgentScanCommand : Command<AgentScanSettings>
{
    protected override int Execute(CommandContext context, AgentScanSettings settings, CancellationToken cancellationToken)
    {
        DiagnosticReport report = new DiagnosticReport();

        if (!AgentLoader.TryParseHarnessFilter(settings.Harness, out HarnessKind? harnessFilter, out string? error))
        {
            report.Add(new Diagnostic("KW-PARSE-000", Severity.Error, error!, "agent", settings.Path));
            CommandHelpers.Finish(report, settings, "agent scan", "Agent");
            return 1;
        }

        AgentSet agentSet = AgentLoader.LoadAll(settings.Path, harnessFilter);

        foreach (AgentModel agent in agentSet.Agents)
            report.AddRange(AgentPromptScanner.Scan(agent).Items);

        CommandHelpers.Finish(report, settings, "agent scan", "Agent");

        return settings.FailOn.ToUpperInvariant() switch
        {
            "warning" => report.Warnings > 0 || report.HasErrors ? 1 : 0,
            "error" => report.HasErrors ? 1 : 0,
            _ => report.HasCritical ? 1 : 0
        };
    }

    public int Execute(CommandContext context, AgentScanSettings settings) => Execute(context, settings, CancellationToken.None);
}
