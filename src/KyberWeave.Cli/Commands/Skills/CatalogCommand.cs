using System.Globalization;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Contracts.Host;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Skills.Authoring;
using KyberWeave.Core.Skills.Model;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Skills;

public sealed class CatalogSettings : CommandSettings
{
    [CommandArgument(0, "[path]")]
    [Description("Root path to inventory. Default: current directory.")]
    public string Path { get; set; } = ".";

    [CommandOption("--json")]
    [Description("Emit JSON instead of a table.")]
    public bool Json { get; set; }
}

public sealed class CatalogCommand(ISkillGovernanceService skills, IAnsiConsole ansi, IConsole standard) : Command<CatalogSettings>
{
    /// <inheritdoc />
    public override int Execute(CommandContext context, CatalogSettings settings, CancellationToken cancellationToken)
    {
        SkillSet set = skills.LoadSet(settings.Path);
        if (set.Count == 0)
        {
            ansi.MarkupLine($"[yellow]No skills found under '{Markup.Escape(settings.Path)}'.[/]");
            return 0;
        }

        IReadOnlyList<SkillCatalogRow> rows = SkillCatalogRow.Of(set);

        if (settings.Json)
        {
            JsonArray arr = new JsonArray(
                rows.Select(r => (JsonNode)new JsonObject
                {
                    ["name"] = r.Name,
                    ["version"] = r.Version,
                    ["author"] = r.Author,
                    ["descriptionScore"] = r.Score,
                    ["bodyTokens"] = r.Tokens,
                    ["resources"] = r.Resources
                }).ToArray());
            standard.Out.WriteLine(arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Table table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Skill"); table.AddColumn("Version"); table.AddColumn("Author");
        table.AddColumn("Desc score"); table.AddColumn("~Tokens"); table.AddColumn("Files");
        foreach (SkillCatalogRow r in rows)
        {
            string scoreColor = r.Score >= 70 ? "green" : r.Score >= 50 ? "yellow" : "red";
            table.AddRow(Markup.Escape(r.Name), Markup.Escape(r.Version), Markup.Escape(r.Author),
                $"[{scoreColor}]{r.Score}[/]", r.Tokens.ToString(CultureInfo.InvariantCulture), r.Resources.ToString(CultureInfo.InvariantCulture));
        }
        ansi.Write(table);
        ansi.MarkupLine($"[grey]{set.Count} skill(s) catalogued.[/]");
        return 0;
    }
}
