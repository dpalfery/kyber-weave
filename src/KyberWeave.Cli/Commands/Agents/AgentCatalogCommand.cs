using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

public sealed class AgentCatalogCommand(IAgentGovernanceService agents, IKyberWeaveConfigRepository configs, IAnsiConsole console) : Command<AgentCatalogSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, AgentCatalogSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new DiagnosticReport();
        if (!CommandHelpers.TryLoadConfig(configs, settings.Path, settings.Config, report, out KyberWeaveConfig config))
        {
            ReportRenderer.Render(console, report, OutputFormat.Table, "agent catalog", "Config");
            return 1;
        }

        IReadOnlyDictionary<string, Dictionary<HarnessKind, AgentModel>> matrix = agents.RoleMatrix(settings.Path);
        IReadOnlyDictionary<HarnessKind, HarnessCapabilityProfile> profiles = config.Harness.Profiles;

        Table table = new Table();
        table.Title("[bold blue]Agent Harness Parity Matrix[/]");
        table.AddColumn("[bold]Role Name[/]");

        foreach ((HarnessKind harnessKind, HarnessCapabilityProfile _) in profiles)
        {
            table.AddColumn(new TableColumn($"[bold]{harnessKind}[/]").Centered());
        }

        foreach ((string role, Dictionary<HarnessKind, AgentModel> harnessMap) in matrix.OrderBy(m => m.Key))
        {
            List<string> row = [$"[bold]{Markup.Escape(role)}[/]"];

            foreach ((HarnessKind harnessKind, HarnessCapabilityProfile profile) in profiles)
            {
                if (harnessMap.ContainsKey(harnessKind))
                {
                    row.Add("[green]✔ Native[/]");
                }
                else if (profile.MappedRoleSkillOverrides.ContainsKey(role))
                {
                    row.Add("[cyan]⚡ Skill[/]");
                }
                else
                {
                    row.Add("[red]✘ Missing[/]");
                }
            }

            table.AddRow(row.ToArray());
        }

        console.Write(table);
        console.MarkupLine($"\nTotal agent roles: [bold]{matrix.Count}[/]");
        return 0;
    }
}
