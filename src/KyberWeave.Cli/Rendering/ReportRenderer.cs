using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;

namespace KyberWeave.Cli.Rendering;

public enum OutputFormat { Table, Json, Sarif, Markdown }

/// <summary>Renders a <see cref="DiagnosticReport"/> in the requested format.</summary>
public static class ReportRenderer
{
    /// <summary>
    /// Renders a report. <paramref name="subjectLabel"/> names what the findings are
    /// about — "Skill", "Agent", "Document" — because the diagnostic model is shared
    /// across artifact classes and only the caller knows which one it is running over.
    /// </summary>
    public static void Render(DiagnosticReport report, OutputFormat format, string command, string subjectLabel)
    {
        switch (format)
        {
            case OutputFormat.Json: Console.WriteLine(ToJson(report)); break;
            case OutputFormat.Sarif: Console.WriteLine(ToSarif(report)); break;
            case OutputFormat.Markdown: Console.WriteLine(ToMarkdown(report, command, subjectLabel)); break;
            default: RenderTable(report, subjectLabel); break;
        }
    }

    private static Color ColorFor(Severity s) => s switch
    {
        Severity.Critical => Color.Red,
        Severity.Error => Color.Red3_1,
        Severity.Warning => Color.Yellow,
        _ => Color.Grey
    };

    private static string Glyph(Severity s) => s switch
    {
        Severity.Critical => "✖",
        Severity.Error => "✖",
        Severity.Warning => "▲",
        _ => "ℹ"
    };

    private static void RenderTable(DiagnosticReport report, string subjectLabel)
    {
        if (report.Items.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No findings.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Severity");
        table.AddColumn("Code");
        table.AddColumn(subjectLabel);
        table.AddColumn("Message");

        foreach (var d in report.Items.OrderByDescending(i => i.Severity))
        {
            var color = ColorFor(d.Severity);
            table.AddRow(
                new Markup($"[{color.ToMarkup()}]{Glyph(d.Severity)} {d.Severity}[/]"),
                new Markup(Markup.Escape(d.Code)),
                new Markup(Markup.Escape(d.Subject)),
                new Markup(Markup.Escape(d.Message) + (d.Hint is null ? "" : $"\n[grey]→ {Markup.Escape(d.Hint)}[/]")));
        }

        AnsiConsole.Write(table);
    }

    public static void RenderSummary(DiagnosticReport report)
    {
        AnsiConsole.MarkupLine(
            $"[red]{report.Count(Severity.Critical)} critical[/], " +
            $"[red3_1]{report.Count(Severity.Error)} error[/], " +
            $"[yellow]{report.Warnings} warning[/], " +
            $"[grey]{report.Infos} info[/].");
    }

    private static string ToJson(DiagnosticReport report)
    {
        var arr = new JsonArray();
        foreach (var d in report.Items)
            arr.Add(new JsonObject
            {
                ["code"] = d.Code,
                ["severity"] = d.Severity.ToString().ToLowerInvariant(),
                ["subject"] = d.Subject,
                ["message"] = d.Message,
                ["file"] = d.FilePath,
                ["hint"] = d.Hint
            });
        var root = new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["critical"] = report.Count(Severity.Critical),
                ["error"] = report.Count(Severity.Error),
                ["warning"] = report.Warnings,
                ["info"] = report.Infos
            },
            ["findings"] = arr
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ToMarkdown(DiagnosticReport report, string command, string subjectLabel)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Kyber-Weave `{command}` results");
        sb.AppendLine();
        sb.AppendLine($"**{report.Count(Severity.Critical)} critical · {report.Count(Severity.Error)} error · {report.Warnings} warning · {report.Infos} info**");
        sb.AppendLine();
        if (report.Items.Count == 0) { sb.AppendLine("_No findings._"); return sb.ToString(); }
        sb.AppendLine($"| Severity | Code | {subjectLabel} | Message |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var d in report.Items.OrderByDescending(i => i.Severity))
            sb.AppendLine($"| {d.Severity} | {d.Code} | {d.Subject} | {d.Message.Replace("|", "\\|")} |");
        return sb.ToString();
    }

    private static string ToSarif(DiagnosticReport report)
    {
        string Level(Severity s) => s switch
        {
            Severity.Critical or Severity.Error => "error",
            Severity.Warning => "warning",
            _ => "note"
        };

        var rules = report.Items
            .Select(i => i.Code).Distinct()
            .Select(code => new JsonObject { ["id"] = code })
            .Aggregate(new JsonArray(), (a, r) => { a.Add(r); return a; });

        var results = new JsonArray();
        foreach (var d in report.Items)
        {
            var result = new JsonObject
            {
                ["ruleId"] = d.Code,
                ["level"] = Level(d.Severity),
                ["message"] = new JsonObject { ["text"] = d.Message }
            };
            if (!string.IsNullOrEmpty(d.FilePath))
            {
                result["locations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["physicalLocation"] = new JsonObject
                        {
                            ["artifactLocation"] = new JsonObject { ["uri"] = d.FilePath }
                        }
                    }
                };
            }
            results.Add(result);
        }

        var sarif = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray
            {
                new JsonObject
                {
                    ["tool"] = new JsonObject
                    {
                        ["driver"] = new JsonObject
                        {
                            ["name"] = "Kyber-Weave",
                            ["version"] = "0.1.0",
                            ["rules"] = rules
                        }
                    },
                    ["results"] = results
                }
            }
        };
        return sarif.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
