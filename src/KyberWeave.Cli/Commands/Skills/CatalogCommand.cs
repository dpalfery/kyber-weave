using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using KyberWeave.Core.Skills.Parsing;
using KyberWeave.Core.Skills.Validation;
using KyberWeave.Cli.Commands;

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
        var set = SkillLoader.LoadSet(settings.Path);
        if (set.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No skills found under '{Markup.Escape(settings.Path)}'.[/]");
            return 0;
        }

        var rows = set.Skills.Select(s =>
        {
            var meta = s.Frontmatter.Metadata;
            return new
            {
                Name = s.Frontmatter.Name ?? s.DirectoryName,
                Version = meta is not null && meta.TryGetValue("version", out var v) ? v : "—",
                Author = meta is not null && meta.TryGetValue("author", out var a) ? a : "—",
                Score = DescriptionScorer.Score(s).Total,
                Tokens = s.ApproximateBodyTokens,
                Resources = s.Resources.Count
            };
        }).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();

        if (settings.Json)
        {
            var arr = new System.Text.Json.Nodes.JsonArray(
                rows.Select(r => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = r.Name,
                    ["version"] = r.Version,
                    ["author"] = r.Author,
                    ["descriptionScore"] = r.Score,
                    ["bodyTokens"] = r.Tokens,
                    ["resources"] = r.Resources
                }).ToArray());
            Console.WriteLine(arr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Skill"); table.AddColumn("Version"); table.AddColumn("Author");
        table.AddColumn("Desc score"); table.AddColumn("~Tokens"); table.AddColumn("Files");
        foreach (var r in rows)
        {
            var scoreColor = r.Score >= 70 ? "green" : r.Score >= 50 ? "yellow" : "red";
            table.AddRow(Markup.Escape(r.Name), Markup.Escape(r.Version), Markup.Escape(r.Author),
                $"[{scoreColor}]{r.Score}[/]", r.Tokens.ToString(), r.Resources.ToString());
        }
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]{set.Count} skill(s) catalogued.[/]");
        return 0;
    }
}
