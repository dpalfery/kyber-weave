using KyberWeave.Cli.Rendering;
using KyberWeave.Core.Agents.Model;
using KyberWeave.Core.Agents.Parsing;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Agents;

public sealed class AgentCatalogCommand : Command<AgentCatalogSettings>
{
    public override int Execute(CommandContext context, AgentCatalogSettings settings)
    {
        DiagnosticReport report = new DiagnosticReport();
        if (!CommandHelpers.TryLoadConfig(settings.Path, settings.Config, report, out KyberWeaveConfig? config))
        {
            ReportRenderer.Render(report, OutputFormat.Table, "agent catalog", "Config");
            return 1;
        }

        AgentSet agentSet = AgentLoader.LoadAll(settings.Path);
        IReadOnlyDictionary<string, Dictionary<HarnessKind, AgentModel>> matrix = agentSet.GetRoleHarnessMatrix();
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
            List<string> row = new List<string> { $"[bold]{Markup.Escape(role)}[/]" };

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

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\nTotal agent roles: [bold]{matrix.Count}[/]");
        return 0;
    }
}
