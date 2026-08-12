using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
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
            RenderMetricsTable(report);
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Severity");
        table.AddColumn("Code");
        table.AddColumn(subjectLabel);
        table.AddColumn(new TableColumn("Location").NoWrap());
        table.AddColumn("Message");

        foreach (var d in report.Items.OrderByDescending(i => i.Severity))
        {
            var color = ColorFor(d.Severity);
            table.AddRow(
                new Markup($"[{color.ToMarkup()}]{Glyph(d.Severity)} {d.Severity}[/]"),
                new Markup(Markup.Escape(d.Code)),
                new Markup(Markup.Escape(d.Subject)),
                new Markup(Markup.Escape(FormatLocation(d))),
                new Markup(Markup.Escape(d.Message) + (d.Hint is null ? "" : $"\n[grey]→ {Markup.Escape(d.Hint)}[/]")));
        }

        AnsiConsole.Write(table);
        RenderMetricsTable(report);
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
        {
            var finding = new JsonObject
            {
                ["code"] = d.Code,
                ["severity"] = d.Severity.ToString().ToLowerInvariant(),
                ["subject"] = d.Subject,
                ["message"] = d.Message,
                ["file"] = d.FilePath,
                ["hint"] = d.Hint
            };
            AddRange(finding, d.StartLine, d.EndLine);
            if (d.RelatedLocations is { Count: > 0 })
            {
                var relatedLocations = new JsonArray();
                foreach (var related in d.RelatedLocations)
                {
                    var location = new JsonObject
                    {
                        ["file"] = related.FilePath,
                        ["message"] = related.Message
                    };
                    AddRange(location, related.StartLine, related.EndLine);
                    relatedLocations.Add(location);
                }

                finding["relatedLocations"] = relatedLocations;
            }

            arr.Add(finding);
        }

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
        AddMetrics(root, report);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string ToMarkdown(DiagnosticReport report, string command, string subjectLabel)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### Kyber-Weave `{command}` results");
        sb.AppendLine();
        sb.AppendLine($"**{report.Count(Severity.Critical)} critical · {report.Count(Severity.Error)} error · {report.Warnings} warning · {report.Infos} info**");
        sb.AppendLine();
        if (report.Items.Count == 0)
        {
            sb.AppendLine("_No findings._");
        }
        else
        {
            sb.AppendLine($"| Severity | Code | {subjectLabel} | Location | Message |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var d in report.Items.OrderByDescending(i => i.Severity))
            {
                sb.AppendLine($"| {d.Severity} | {EscapeMarkdown(d.Code)} | {EscapeMarkdown(d.Subject)} | {EscapeMarkdown(FormatLocation(d, includeRelatedCount: false))} | {EscapeMarkdown(d.Message)} |");
                if (d.RelatedLocations is not { Count: > 0 })
                {
                    continue;
                }

                foreach (var related in d.RelatedLocations)
                {
                    var message = related.Message is null ? "Related location" : related.Message;
                    sb.AppendLine($"|  |  |  | {EscapeMarkdown(FormatLocation(related))} | {EscapeMarkdown(message)} |");
                }
            }
        }

        AppendMarkdownMetrics(sb, report);
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
                var physicalLocation = CreateSarifPhysicalLocation(d.FilePath, d.StartLine, d.EndLine);
                result["locations"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["physicalLocation"] = physicalLocation
                    }
                };
            }

            if (d.RelatedLocations is { Count: > 0 })
            {
                var relatedLocations = new JsonArray();
                foreach (var related in d.RelatedLocations)
                {
                    var location = new JsonObject
                    {
                        ["physicalLocation"] = CreateSarifPhysicalLocation(
                            related.FilePath,
                            related.StartLine,
                            related.EndLine)
                    };
                    if (related.Message is not null)
                    {
                        location["message"] = new JsonObject { ["text"] = related.Message };
                    }

                    relatedLocations.Add(location);
                }

                result["relatedLocations"] = relatedLocations;
            }

            results.Add(result);
        }

        var run = new JsonObject
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
        };
        if (report.Metrics.Count > 0)
        {
            var properties = new JsonObject();
            AddMetrics(properties, report);
            run["properties"] = properties;
        }

        var sarif = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray
            {
                run
            }
        };
        return sarif.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void RenderMetricsTable(DiagnosticReport report)
    {
        if (report.Metrics.Count == 0)
        {
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Metric");
        table.AddColumn("Value");
        foreach (var metric in report.Metrics)
        {
            table.AddRow(
                new Markup(Markup.Escape(metric.Key)),
                new Markup(Markup.Escape(FormatMetric(metric.Value))));
        }

        AnsiConsole.Write(table);
    }

    private static void AppendMarkdownMetrics(StringBuilder sb, DiagnosticReport report)
    {
        if (report.Metrics.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("#### Metrics");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        foreach (var metric in report.Metrics)
        {
            sb.AppendLine($"| {EscapeMarkdown(metric.Key)} | {EscapeMarkdown(FormatMetric(metric.Value))} |");
        }
    }

    private static void AddMetrics(JsonObject parent, DiagnosticReport report)
    {
        if (report.Metrics.Count == 0)
        {
            return;
        }

        var metrics = new JsonObject();
        foreach (var metric in report.Metrics)
        {
            metrics[metric.Key] = ToJsonScalar(metric.Value);
        }

        parent["metrics"] = metrics;
    }

    private static JsonNode? ToJsonScalar(object? value) => value switch
    {
        null => null,
        string item => JsonValue.Create(item),
        bool item => JsonValue.Create(item),
        byte item => JsonValue.Create(item),
        sbyte item => JsonValue.Create(item),
        short item => JsonValue.Create(item),
        ushort item => JsonValue.Create(item),
        int item => JsonValue.Create(item),
        uint item => JsonValue.Create(item),
        long item => JsonValue.Create(item),
        ulong item => JsonValue.Create(item),
        float item => JsonValue.Create(item),
        double item => JsonValue.Create(item),
        decimal item => JsonValue.Create(item),
        _ => throw new InvalidOperationException("Diagnostic metrics must be JSON scalar values.")
    };

    private static void AddRange(JsonObject target, int? startLine, int? endLine)
    {
        if (startLine is not null)
        {
            target["startLine"] = startLine.Value;
        }

        if (endLine is not null)
        {
            target["endLine"] = endLine.Value;
        }
    }

    private static JsonObject CreateSarifPhysicalLocation(string filePath, int? startLine, int? endLine)
    {
        var physicalLocation = new JsonObject
        {
            ["artifactLocation"] = new JsonObject { ["uri"] = filePath }
        };
        if (startLine is not null)
        {
            var region = new JsonObject { ["startLine"] = startLine.Value };
            if (endLine is not null)
            {
                region["endLine"] = endLine.Value;
            }

            physicalLocation["region"] = region;
        }

        return physicalLocation;
    }

    private static string FormatLocation(Diagnostic diagnostic, bool includeRelatedCount = true)
    {
        var formatted = FormatLocation(diagnostic.FilePath, diagnostic.StartLine, diagnostic.EndLine);
        if (includeRelatedCount && diagnostic.RelatedLocations is { Count: > 0 })
        {
            formatted += $" (+{diagnostic.RelatedLocations.Count.ToString(CultureInfo.InvariantCulture)} related)";
        }

        return formatted;
    }

    private static string FormatLocation(DiagnosticLocation location) =>
        FormatLocation(location.FilePath, location.StartLine, location.EndLine);

    private static string FormatLocation(string? filePath, int? startLine, int? endLine)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return string.Empty;
        }

        if (startLine is null)
        {
            return filePath;
        }

        var start = startLine.Value.ToString(CultureInfo.InvariantCulture);
        return endLine is null || endLine == startLine
            ? $"{filePath}:{start}"
            : $"{filePath}:{start}-{endLine.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string FormatMetric(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string EscapeMarkdown(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal);
}
