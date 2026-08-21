using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Skills.Model;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Validation;
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

public sealed class CatalogCommand : Command<CatalogSettings>
{
    public override int Execute(CommandContext context, CatalogSettings settings)
    {
        SkillSet set = SkillLoader.LoadSet(settings.Path);
        if (set.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No skills found under '{Markup.Escape(settings.Path)}'.[/]");
            return 0;
        }

        List<(string Name, string Version, string Author, int Score, int Tokens, int Resources)> rows = set.Skills.Select(s =>
        {
            Dictionary<string, string>? meta = s.Frontmatter.Metadata;
            return (
                Name: s.Frontmatter.Name ?? s.DirectoryName,
                Version: meta is not null && meta.TryGetValue("version", out string? v) ? v : "—",
                Author: meta is not null && meta.TryGetValue("author", out string? a) ? a : "—",
                Score: DescriptionScorer.Score(s).Total,
                Tokens: s.ApproximateBodyTokens,
                Resources: s.Resources.Count
            );
        }).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();

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
            Console.WriteLine(arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Table table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Skill"); table.AddColumn("Version"); table.AddColumn("Author");
        table.AddColumn("Desc score"); table.AddColumn("~Tokens"); table.AddColumn("Files");
        foreach ((string Name, string Version, string Author, int Score, int Tokens, int Resources) r in rows)
        {
            string scoreColor = r.Score >= 70 ? "green" : r.Score >= 50 ? "yellow" : "red";
            table.AddRow(Markup.Escape(r.Name), Markup.Escape(r.Version), Markup.Escape(r.Author),
                $"[{scoreColor}]{r.Score}[/]", r.Tokens.ToString(), r.Resources.ToString());
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{set.Count} skill(s) catalogued.[/]");
        return 0;
    }
}
