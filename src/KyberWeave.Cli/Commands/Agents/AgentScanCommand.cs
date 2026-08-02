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
    public override int Execute(CommandContext context, AgentScanSettings settings)
    {
        var report = new DiagnosticReport();

        if (!AgentLoader.TryParseHarnessFilter(settings.Harness, out var harnessFilter, out var error))
        {
            report.Add(new Diagnostic("KW-PARSE-000", Severity.Error, error!, "agent", settings.Path));
            CommandHelpers.Finish(report, settings, "agent scan", "Agent");
            return 1;
        }

        var agentSet = AgentLoader.LoadAll(settings.Path, harnessFilter);

        foreach (var agent in agentSet.Agents)
            report.AddRange(AgentPromptScanner.Scan(agent).Items);

        CommandHelpers.Finish(report, settings, "agent scan", "Agent");

        return settings.FailOn.ToLowerInvariant() switch
        {
            "warning" => report.Warnings > 0 || report.HasErrors ? 1 : 0,
            "error" => report.HasErrors ? 1 : 0,
            _ => report.HasCritical ? 1 : 0
        };
    }
}
