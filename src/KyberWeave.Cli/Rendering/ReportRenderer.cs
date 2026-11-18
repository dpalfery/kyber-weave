using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KyberWeave.Core.Diagnostics;
using Spectre.Console;

namespace KyberWeave.Cli.Rendering;

/// <summary>The ways a report is written.</summary>
/// <remarks><c>Lines</c> is the human format for a reader that is not a terminal: one finding per line, never wrapped.</remarks>
public enum OutputFormat { Table, Json, Sarif, Markdown, Lines }

/// <summary>Renders a <see cref="DiagnosticReport"/> in the requested format.</summary>
public static class ReportRenderer
{
    private const string NoFindings = "No findings.";

    /// <summary>
    /// The directory a rooted finding path is shown relative to, so the Location column stays short. Empty leaves a
    /// rooted path as it is. The composition root sets it from the process environment's current directory.
    /// </summary>
    public static string DisplayRoot { get; set; } = string.Empty;

    /// <summary>
    /// Renders a report to <paramref name="console"/>. <paramref name="subjectLabel"/> names what the findings are
    /// about — "Skill", "Agent", "Document" — because the diagnostic model is shared
    /// across artifact classes and only the caller knows which one it is running over.
    /// </summary>
    /// <param name="console">The console the report is written to.</param>
    /// <param name="report">The report to render.</param>
    /// <param name="format">The requested format.</param>
    /// <param name="command">The command name a document format records.</param>
    /// <param name="subjectLabel">Names what the findings are about.</param>
    public static void Render(
        IAnsiConsole console,
        DiagnosticReport report,
        OutputFormat format,
        string command,
        string subjectLabel)
    {
        ArgumentNullException.ThrowIfNull(console);

        switch (format)
        {
            case OutputFormat.Json: WriteLine(console, ToJson(report)); break;
            case OutputFormat.Sarif: WriteLine(console, ToSarif(report)); break;
            case OutputFormat.Markdown: WriteLine(console, ToMarkdown(report, command, subjectLabel)); break;
            case OutputFormat.Lines: RenderLines(console, report); break;
            default: RenderTable(console, report, subjectLabel); break;
        }
    }

    /// <summary>
    /// Writes one line of plain text, byte for byte as <see cref="System.Console"/> would: no markup parsing and no
    /// folding, because a document format or a finding line is read by a program that greps it.
    /// </summary>
    private static void WriteLine(IAnsiConsole console, string text) => console.Profile.Out.Writer.WriteLine(text);

    /// <summary>
    /// Whether output goes to a terminal that can lay a table out. A redirected stream, or a console that cannot
    /// measure itself, is read by a program or an agent that greps it, and a table there wraps a rule id or a message
    /// across several lines.
    /// </summary>
    /// <param name="console">The console the output goes to.</param>
    public static bool IsInteractive(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        return console.Profile.Out.IsTerminal && console.Profile.Width > 0;
    }

    /// <summary>Whether the format is the one meant for a person or an agent to read, rather than a document for a program.</summary>
    public static bool IsHuman(OutputFormat format) => format is OutputFormat.Table or OutputFormat.Lines;

    /// <summary>
    /// One finding per line, <c>&lt;severity&gt; &lt;rule-id&gt; &lt;location&gt;: &lt;message&gt; → &lt;fix&gt;</c>, written without
    /// folding so each line can be grepped whole. Informational findings are listed only when nothing actionable exists,
    /// as in the table.
    /// </summary>
    private static void RenderLines(IAnsiConsole console, DiagnosticReport report)
    {
        if (report.Items.Count == 0)
        {
            WriteLine(console, NoFindings);
            WriteMetricLines(console, report);
            return;
        }

        List<Diagnostic> actionable = report.Items.Where(item => item.Severity >= Severity.Warning).ToList();
        IEnumerable<Diagnostic> rows = actionable.Count > 0 ? actionable : report.Items;
        int omitted = report.Items.Count - rows.Count();
        if (omitted > 0)
        {
            WriteLine(
                console,
                $"{omitted.ToString(CultureInfo.InvariantCulture)} informational findings omitted. Use --format json to list them.");
        }

        foreach (Diagnostic d in rows.OrderByDescending(item => item.Severity))
        {
            WriteLine(console, FormatLine(d));
        }

        WriteMetricLines(console, report);
    }

    /// <summary>One finding as a single line.</summary>
    internal static string FormatLine(Diagnostic d)
    {
        string where = FormatLocation(d);
        if (where.Length == 0)
        {
            where = d.Subject;
        }

        StringBuilder line = new StringBuilder();
        line.Append(d.Severity.ToString().ToUpperInvariant()).Append(' ').Append(d.Code);
        if (where.Length > 0)
        {
            line.Append(' ').Append(OneLine(where)).Append(':');
        }
        else
        {
            line.Append(':');
        }

        line.Append(' ').Append(OneLine(d.Message));
        if (!string.IsNullOrWhiteSpace(d.Hint))
        {
            line.Append(" → ").Append(OneLine(d.Hint));
        }

        return line.ToString();
    }

    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static void WriteMetricLines(IAnsiConsole console, DiagnosticReport report)
    {
        foreach (KeyValuePair<string, object?> metric in report.Metrics)
        {
            WriteLine(console, $"metric {metric.Key}: {OneLine(FormatMetric(metric.Value))}");
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

    private static void RenderTable(IAnsiConsole console, DiagnosticReport report, string subjectLabel)
    {
        if (report.Items.Count == 0)
        {
            console.MarkupLine("[green]No findings.[/]");
            RenderMetricsTable(console, report);
            return;
        }

        // A finding the reader has to scroll past hundreds of Info rows to reach is not
        // reported. When anything actionable exists, Info is counted rather than listed;
        // --format json remains the complete record.
        List<Diagnostic> actionable = report.Items
            .Where(item => item.Severity >= Severity.Warning)
            .OrderByDescending(item => item.Severity)
            .ToList();
        List<Diagnostic> rows = actionable.Count > 0
            ? actionable
            : report.Items.OrderByDescending(item => item.Severity).ToList();
        int omitted = report.Items.Count - rows.Count;
        if (omitted > 0)
        {
            console.MarkupLine(
                $"[grey]{omitted.ToString(CultureInfo.InvariantCulture)} informational findings omitted from the table. Use --format json to list them.[/]");
        }

        Table table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn("Severity");
        table.AddColumn("Code");
        table.AddColumn(subjectLabel);
        table.AddColumn(new TableColumn("Location").NoWrap());
        table.AddColumn("Message");

        foreach (Diagnostic d in rows)
        {
            Color color = ColorFor(d.Severity);
            table.AddRow(
                new Markup($"[{color.ToMarkup()}]{Glyph(d.Severity)} {d.Severity}[/]"),
                new Markup(Markup.Escape(d.Code)),
                new Markup(Markup.Escape(d.Subject)),
                new Markup(Markup.Escape(FormatLocation(d))),
                new Markup(Markup.Escape(d.Message) + (d.Hint is null ? "" : $"\n[grey]→ {Markup.Escape(d.Hint)}[/]")));
        }

        console.Write(table);
        RenderMetricsTable(console, report);
    }

    /// <summary>Renders the one-line severity summary to <paramref name="console"/>.</summary>
    /// <param name="console">The console the summary is written to.</param>
    /// <param name="report">The report the counts are read from.</param>
    public static void RenderSummary(IAnsiConsole console, DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(console);

        console.MarkupLine(
            $"[red]{report.Count(Severity.Critical)} critical[/], " +
            $"[red3_1]{report.Count(Severity.Error)} error[/], " +
            $"[yellow]{report.Warnings} warning[/], " +
            $"[grey]{report.Infos} info[/].");
    }

    private static string ToJson(DiagnosticReport report)
    {
        JsonArray arr = new JsonArray();
        foreach (Diagnostic d in report.Items)
        {
            JsonObject finding = new JsonObject
            {
                ["code"] = d.Code,
                ["severity"] = d.Severity.ToString().ToUpperInvariant(),
                ["subject"] = d.Subject,
                ["message"] = d.Message,
                ["file"] = d.FilePath,
                ["hint"] = d.Hint
            };
            AddRange(finding, d.StartLine, d.EndLine);
            if (d.RelatedLocations is { Count: > 0 })
            {
                JsonArray relatedLocations = new JsonArray();
                foreach (DiagnosticLocation related in d.RelatedLocations)
                {
                    JsonObject location = new JsonObject
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

        JsonObject root = new JsonObject
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
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"### Kyber-Weave `{command}` results");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**{report.Count(Severity.Critical)} critical · {report.Count(Severity.Error)} error · {report.Warnings} warning · {report.Infos} info**");
        sb.AppendLine();
        if (report.Items.Count == 0)
        {
            sb.AppendLine("_No findings._");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| Severity | Code | {subjectLabel} | Location | Message |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (Diagnostic d in report.Items.OrderByDescending(i => i.Severity))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {d.Severity} | {EscapeMarkdown(d.Code)} | {EscapeMarkdown(d.Subject)} | {EscapeMarkdown(FormatLocation(d, includeRelatedCount: false))} | {EscapeMarkdown(d.Message)} |");
                if (d.RelatedLocations is not { Count: > 0 })
                {
                    continue;
                }

                foreach (DiagnosticLocation related in d.RelatedLocations)
                {
                    string message = related.Message ?? "Related location";
                    sb.AppendLine(CultureInfo.InvariantCulture, $"|  |  |  | {EscapeMarkdown(FormatLocation(related))} | {EscapeMarkdown(message)} |");
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

        JsonArray rules = report.Items
            .Select(i => i.Code).Distinct()
            .Select(code => new JsonObject { ["id"] = code })
            .Aggregate(new JsonArray(), (a, r) => { a.Add(r); return a; });

        JsonArray results = new JsonArray();
        foreach (Diagnostic d in report.Items)
        {
            JsonObject result = new JsonObject
            {
                ["ruleId"] = d.Code,
                ["level"] = Level(d.Severity),
                ["message"] = new JsonObject { ["text"] = d.Message }
            };
            if (!string.IsNullOrEmpty(d.FilePath))
            {
                JsonObject physicalLocation = CreateSarifPhysicalLocation(d.FilePath, d.StartLine, d.EndLine);
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
                JsonArray relatedLocations = new JsonArray();
                foreach (DiagnosticLocation related in d.RelatedLocations)
                {
                    JsonObject location = new JsonObject
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

        JsonObject run = new JsonObject
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
            JsonObject properties = new JsonObject();
            AddMetrics(properties, report);
            run["properties"] = properties;
        }

        JsonObject sarif = new JsonObject
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

    private static void RenderMetricsTable(IAnsiConsole console, DiagnosticReport report)
    {
        if (report.Metrics.Count == 0)
        {
            return;
        }

        Table table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Metric");
        table.AddColumn("Value");
        foreach (KeyValuePair<string, object?> metric in report.Metrics)
        {
            table.AddRow(
                new Markup(Markup.Escape(metric.Key)),
                new Markup(Markup.Escape(FormatMetric(metric.Value))));
        }

        console.Write(table);
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
        foreach (KeyValuePair<string, object?> metric in report.Metrics)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {EscapeMarkdown(metric.Key)} | {EscapeMarkdown(FormatMetric(metric.Value))} |");
        }
    }

    private static void AddMetrics(JsonObject parent, DiagnosticReport report)
    {
        if (report.Metrics.Count == 0)
        {
            return;
        }

        JsonObject metrics = new JsonObject();
        foreach (KeyValuePair<string, object?> metric in report.Metrics)
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
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty)
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
        JsonObject physicalLocation = new JsonObject
        {
            ["artifactLocation"] = new JsonObject { ["uri"] = filePath }
        };
        if (startLine is not null)
        {
            JsonObject region = new JsonObject { ["startLine"] = startLine.Value };
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
        string formatted = FormatLocation(diagnostic.FilePath, diagnostic.StartLine, diagnostic.EndLine);
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

        string displayPath = DisplayPath(filePath);
        if (startLine is null)
        {
            return displayPath;
        }

        string start = startLine.Value.ToString(CultureInfo.InvariantCulture);
        return endLine is null || endLine == startLine
            ? $"{displayPath}:{start}"
            : $"{displayPath}:{start}-{endLine.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Absolute paths under the display root become relative so the Location column does not consume the row.
    /// JSON and SARIF keep the original path.
    /// </summary>
    private static string DisplayPath(string filePath)
    {
        if (DisplayRoot.Length == 0 || !Path.IsPathRooted(filePath))
        {
            return filePath;
        }

        try
        {
            string relative = Path.GetRelativePath(DisplayRoot, filePath);
            if (string.IsNullOrEmpty(relative)
                || relative.StartsWith("..", StringComparison.Ordinal))
            {
                return filePath;
            }

            return relative.Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return filePath;
        }
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
