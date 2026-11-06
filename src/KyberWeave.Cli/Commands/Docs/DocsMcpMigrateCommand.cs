using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Renames the MCP server keys existing installs were written under to the names this version
/// ships, in place, and refuses every entry it cannot prove is Kyber-Weave's.
/// </summary>
/// <remarks>
/// <para>
/// This is the migration half of the rename, and it is a separate verb from <c>docs mcp-wire</c> on
/// purpose: wiring adds what a declaration asks for, while migrating changes the name an existing
/// entry already has. Doing the second by adding the new name and removing the old one is what this
/// verb exists to avoid — that leaves a window in which one file holds one server under two names,
/// and it loses the entry's other properties.
/// </para>
/// <para>
/// With no <c>--target</c> it is a preview that writes nothing at all, not even the receipt. A named
/// target is the consent to write that file, exactly as it is for the wire verb.
/// </para>
/// <para>
/// A user-scope file — under the operator's home directory — is never read or written. The report
/// names those files so the operator can rename a key there by hand.
/// </para>
/// </remarks>
public sealed class DocsMcpMigrateCommand : Command<DocsMcpMigrateSettings>
{
    /// <summary>The removable, writable targets, spelled once for the usage message.</summary>
    private static string TargetList =>
        string.Join(
            ", ",
            HarnessMcpCatalog.Rows
                .Where(row => row.Fixability == McpWiringFixability.Fixable && row.Shape == McpConfigurationShape.Json)
                .Select(row => row.Harness)
                .Append(McpServerRenamer.DeclarationTarget));

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        DocsMcpMigrateSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        if (!DocsMcpWireCommand.TryRoot(settings.Path, report, out string root))
        {
            DocsMcpWireCommand.PrintDiagnostics(report);
            return 1;
        }

        IReadOnlyList<string> targets = DocsMcpWireCommand.Targets(settings.Target);
        bool preview = targets.Count == 0;
        IReadOnlyList<McpRenameChange> changes = preview
            ? McpServerRenamer.Scan(root)
            : McpServerRenamer.Migrate(root, targets, settings.DryRun);

        CommandHelpers.WriteUnfolded(() =>
        {
            PrintHeader(root, preview, settings.DryRun);

            foreach (McpRenameChange change in changes)
                PrintChange(change, settings.DryRun);

            if (changes.Count == 0)
                AnsiConsole.MarkupLine("  [green]nothing to do[/] no retired MCP server key was found");

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(preview
                ? $"[grey]Nothing was written. Name the targets to migrate: {Markup.Escape(McpServerRenamer.MigrateCommand)} "
                    + $"<{Markup.Escape(TargetList)}>. Add --dry-run to see what a write would change.[/]"
                : "[grey]A user-scope file is still yours to edit: Kyber-Weave reads and writes repository-scope files only.[/]");
        });

        return preview || !changes.Any(change => change.Outcome == McpRenameOutcome.Refused) ? 0 : 1;
    }

    /// <summary>The two-argument overload the rest of this branch's commands carry.</summary>
    public int Execute(CommandContext context, DocsMcpMigrateSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    /// <summary>
    /// Prints the rename table and the user-scope pointer every run carries, so an operator reading a
    /// preview learns both what the old keys are and which files this command will never touch.
    /// </summary>
    private static void PrintHeader(string root, bool preview, bool dryRun)
    {
        string run = preview ? "no --target named, so nothing will be written" : dryRun ? "dry run, so nothing will be written" : "writing the named targets";

        AnsiConsole.MarkupLine($"[bold]MCP server key migration[/] for {Markup.Escape(root)}: {Markup.Escape(run)}.");
        AnsiConsole.WriteLine();

        foreach (McpRenameRule rule in McpServerRenamer.Rules)
        {
            string launch = rule.NextArgument is null ? rule.Binary : $"{rule.Binary} {rule.NextArgument}";
            AnsiConsole.MarkupLine(
                $"  [bold]{Markup.Escape(rule.OldKey)}[/] [grey]→[/] [bold]{Markup.Escape(rule.NewKey)}[/] "
                + $"[grey](a launch of {Markup.Escape(launch)} proves the entry ours)[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[grey]User-scope files are never read or written: ~/.claude.json, ~/.cursor/mcp.json, ~/.codex/config.toml, "
            + "~/.config/opencode/opencode.json, ~/.factory/mcp.json, ~/.copilot/mcp-config.json, and Kilo's and ZCode's "
            + "user tiers. Rename a key there by hand.[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>Reports one change — or one refusal — as the operator reads it.</summary>
    /// <remarks>
    /// Every value that came out of a file goes through <see cref="TerminalText"/> before the terminal
    /// sees it, and the markup escaping is a second, separate concern: one stops a bracket being read
    /// as a tag, the other stops a control character moving the operator's cursor.
    /// </remarks>
    private static void PrintChange(McpRenameChange change, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(change);

        string target = change.Path is null
            ? Markup.Escape(TerminalText.Line(change.Harness))
            : $"{Markup.Escape(TerminalText.Line(change.Harness))} [grey]({Markup.Escape(TerminalText.Line(change.Path))})[/]";

        if (change.Outcome == McpRenameOutcome.NothingToDo)
        {
            AnsiConsole.MarkupLine($"  [green]nothing to do[/] {target}: no retired key was found");
            return;
        }

        string code = Markup.Escape(change.Code ?? McpServerRenamer.RenameCode);

        if (change.Outcome == McpRenameOutcome.Refused)
        {
            AnsiConsole.MarkupLine($"  [red]{code}[/] {target}");
            PrintDetail(change.Message);
            PrintHint(change.Hint);
            PrintSnippet(change.Snippet);
            return;
        }

        string style = change.Outcome == McpRenameOutcome.Reported ? "grey" : "yellow";
        string note = dryRun && change.Outcome != McpRenameOutcome.Reported
            ? " [grey](dry run; nothing written)[/]"
            : string.Empty;

        AnsiConsole.MarkupLine($"  [{style}]{code}[/] {target}: {Markup.Escape(TerminalText.Line(change.Message))}{note}");
        PrintHint(change.Hint);
        PrintSnippet(change.Snippet);
    }

    private static void PrintDetail(string? message)
    {
        if (!string.IsNullOrEmpty(message))
            AnsiConsole.MarkupLine($"    {Markup.Escape(TerminalText.Line(message))}");
    }

    private static void PrintHint(string? hint)
    {
        if (!string.IsNullOrEmpty(hint))
            AnsiConsole.MarkupLine($"    [grey]→ {Markup.Escape(TerminalText.Line(hint))}[/]");
    }

    private static void PrintSnippet(string? snippet)
    {
        if (string.IsNullOrEmpty(snippet))
            return;

        AnsiConsole.MarkupLine("    [grey]Paste this into that file instead:[/]");

        // Written raw: a snippet is TOML or JSON, and Spectre markup would read its brackets as tags
        // and drop them. The control characters go before the brackets are allowed through.
        AnsiConsole.WriteLine(TerminalText.Block(snippet));
    }
}
