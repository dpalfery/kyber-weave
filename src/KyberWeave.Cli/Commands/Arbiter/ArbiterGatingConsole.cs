using KyberWeave.Core.Arbiter;
using Spectre.Console;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>Formats the harness gating rows for <c>arbiter status</c> and <c>arbiter doctor</c>.</summary>
internal static class ArbiterGatingConsole
{
    /// <summary>Prints one line per row, the label first and the fix command after it.</summary>
    /// <param name="console">The console the rows are written to: the command's own, so a report is never split.</param>
    /// <param name="report">The rows to print.</param>
    /// <param name="displayName">Turns a harness token into the name the operator knows it by.</param>
    internal static void WriteRows(IAnsiConsole console, HarnessGatingReport report, Func<string, string> displayName)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(displayName);

        if (report.Rows.Count == 0)
        {
            console.MarkupLine("  [grey]no harness installed for this project.[/]");
            return;
        }

        foreach (HarnessGatingRow row in report.Rows)
        {
            string fix = row.FixCommand is null ? string.Empty : $"; run {row.FixCommand}";
            console.MarkupLine(
                $"  {Markup.Escape(displayName(row.Token))}: {Markup.Escape(row.State)}{Markup.Escape(fix)}");
        }
    }
}
