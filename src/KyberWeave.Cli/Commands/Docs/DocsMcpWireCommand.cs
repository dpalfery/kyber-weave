using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Writes the MCP servers a repository declares into the harnesses an operator names, and reports
/// the ones it will not write.
/// </summary>
/// <remarks>
/// <para>
/// This is the remedy half of the wiring check, and it is a separate verb from <c>docs init</c> on
/// purpose: diagnosis and repair are different operations, and running the repair is the consent
/// to write (5.1). With no <c>--target</c> it is a preview that writes nothing at all, not even the
/// receipt (5.3, 5.10).
/// </para>
/// <para>
/// The command is thin by design. What to write, how to splice it, and what to refuse are
/// <see cref="McpWiringRepairer"/>'s. What it adds is the two decisions the repair cannot make for
/// itself. The first is whether a named harness has anything to write at all: a file that already
/// holds every declared server, launched exactly as declared, is reported and skipped rather than
/// refused, because otherwise a second run of this command would fail on its own earlier success.
/// The second is what the operator reads: every refusal names its file, its rule id, its remedy
/// and, where the harness is snippet-only, the exact text to paste instead.
/// </para>
/// </remarks>
public sealed class DocsMcpWireCommand : Command<DocsMcpWireSettings>
{
    /// <summary>The remedy command, spelled once so the check and the repair agree on it.</summary>
    private const string WireCommand = "kyber-weave docs mcp-wire --target";

    /// <summary>
    /// How a message writes "the slugs you would pass here". A literal angle bracket cannot appear
    /// in Spectre markup unescaped — it reads as the start of a tag — so the placeholder is spelled
    /// once and escaped at every use.
    /// </summary>
    internal const string TargetPlaceholder = "<harness>[,<harness>]";

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        DocsMcpWireSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        DiagnosticReport report = new();
        if (!TryRoot(settings.Path, report, out string root) ||
            !CommandHelpers.TryLoadConfig(root, configPath: null, report, out KyberWeaveConfig config))
        {
            PrintDiagnostics(report);
            return 1;
        }

        if (config.Mcp.RequiredServers.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No MCP servers are declared[/], so there is nothing to wire.");
            AnsiConsole.MarkupLine(
                "[grey]Declare them under mcp.required-servers in .kyber-weave/kyber-weave.yml, then run this "
                + "command again.[/]");
            return 0;
        }

        LaunchCommandResolver resolver = LaunchCommandResolver.FromEnvironment();
        IReadOnlyList<string> targets = Targets(settings.Target);

        return targets.Count == 0
            ? Preview(root, config.Mcp, resolver)
            : Wire(root, config.Mcp, resolver, targets, settings.DryRun);
    }

    /// <summary>The two-argument overload the rest of this branch's commands carry.</summary>
    public int Execute(CommandContext context, DocsMcpWireSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    /// <summary>
    /// Resolves the repository root, reporting an unresolvable path through the configuration
    /// failure code rather than letting <see cref="Path.GetFullPath(string)"/> escape.
    /// </summary>
    internal static bool TryRoot(string path, DiagnosticReport report, out string root)
    {
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            root = Path.GetFullPath(path);
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException
            or System.Security.SecurityException)
        {
            root = string.Empty;
            report.Add(new Diagnostic(
                KyberWeaveConfigLoader.ConfigLoadErrorCode,
                Severity.Error,
                $"Cannot resolve path '{path}': {exception.Message}",
                "kyber-weave.yml",
                path,
                Hint: "Pass a repository root this process can resolve, or run the command from the repository."));
            return false;
        }
    }

    /// <summary>
    /// The harness slugs a <c>--target</c> value names, deduplicated in the order written.
    /// </summary>
    /// <remarks>
    /// Deduplicated because naming one harness twice is one write, not two: the repair works on the
    /// live tree, so a second pass would meet its own first entry and refuse it as a name collision.
    /// </remarks>
    internal static IReadOnlyList<string> Targets(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return [];

        List<string> targets = [];
        foreach (string candidate in target.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!targets.Contains(candidate, StringComparer.Ordinal))
                targets.Add(candidate);
        }

        return targets;
    }

    /// <summary>
    /// Runs <paramref name="write"/> with console folding lifted, then restores the width.
    /// </summary>
    /// <remarks>
    /// A redirected console folds at 80 columns, which splits the repository-relative path or the
    /// remedy command the operator is meant to copy onto a second line. The same unfolded-write
    /// pattern lives in <c>DocsInitCommand</c> and <c>SquadCommandComposition</c>; it is repeated
    /// here so the two MCP verbs share one copy and neither depends on another branch.
    /// </remarks>
    internal static void Unfolded(Action write)
    {
        ArgumentNullException.ThrowIfNull(write);

        int originalWidth = AnsiConsole.Profile.Width;
        AnsiConsole.Profile.Width = int.MaxValue;
        try
        {
            write();
        }
        finally
        {
            AnsiConsole.Profile.Width = originalWidth > 0 ? originalWidth : 80;
        }
    }

    /// <summary>
    /// Prints the diagnostics a configuration or path failure produced, rule id first.
    /// </summary>
    /// <remarks>
    /// These are rendered as lines rather than through the findings table the analysis verbs use.
    /// This command takes no <c>--format</c>, and a table squeezed into a redirected 80-column
    /// console wraps the rule id across several lines — which is the one token the operator has to
    /// search the corpus for.
    /// </remarks>
    internal static void PrintDiagnostics(DiagnosticReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Unfolded(() =>
        {
            foreach (Diagnostic diagnostic in report.Items)
            {
                AnsiConsole.MarkupLine(
                    $"[red]{Markup.Escape(diagnostic.Code)}[/] {Markup.Escape(diagnostic.Message)}");

                if (!string.IsNullOrEmpty(diagnostic.Hint))
                    AnsiConsole.MarkupLine($"  [grey]→ {Markup.Escape(diagnostic.Hint)}[/]");
            }
        });
    }

    /// <summary>Reports one change — or one refusal — as the operator reads it.</summary>
    internal static void PrintChange(McpWiringFileChange change, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(change);

        string harness = Markup.Escape(change.Harness);
        string file = change.Path is null ? "(no repository-scope file)" : Markup.Escape(change.Path);

        if (change.Outcome == McpWiringChangeOutcome.Refused)
        {
            AnsiConsole.MarkupLine($"  [red]{Markup.Escape(change.Code ?? "refused")}[/] [bold]{harness}[/] {file}");

            if (!string.IsNullOrEmpty(change.Message))
                AnsiConsole.MarkupLine($"    {Markup.Escape(change.Message)}");

            if (!string.IsNullOrEmpty(change.Hint))
                AnsiConsole.MarkupLine($"    [grey]→ {Markup.Escape(change.Hint)}[/]");

            if (!string.IsNullOrEmpty(change.Snippet))
            {
                AnsiConsole.MarkupLine("    [grey]Paste this into that file instead:[/]");

                // Written raw: a snippet is TOML or JSON, and Spectre markup would read its
                // brackets as tags and drop them.
                AnsiConsole.WriteLine(change.Snippet);
            }

            return;
        }

        AnsiConsole.MarkupLine($"  {Verb(change.Outcome, dryRun)} [bold]{file}[/] [grey]({harness})[/]");

        if (!string.IsNullOrEmpty(change.Message))
            AnsiConsole.MarkupLine($"    [grey]{Markup.Escape(change.Message)}[/]");
    }

    /// <summary>
    /// The no-target run: the harnesses in play, the gaps found, the writable harnesses no marker
    /// can detect, and not one byte written (5.3).
    /// </summary>
    private static int Preview(string root, McpConfig config, LaunchCommandResolver resolver)
    {
        McpWiringReport inspection = McpWiringInspector.Inspect(root, config, resolver);

        // A writable harness with neither a marker nor its configuration file is invisible to
        // detection, so naming it in the preview is the only way it can ever be written: the
        // operator has to learn the slug somewhere (5.3).
        List<HarnessMcpEntry> undetectable =
        [
            .. HarnessMcpCatalog.Rows.Where(row =>
                row.Fixability == McpWiringFixability.Fixable && !InPlay(root, row))
        ];

        Unfolded(() =>
        {
            AnsiConsole.MarkupLine(
                $"[bold]MCP wiring preview[/] for {Markup.Escape(root)}: no --target named, so nothing will be written.");
            AnsiConsole.WriteLine();

            if (inspection.Findings.Count == 0)
            {
                AnsiConsole.MarkupLine("[green]Every harness in play holds the declared servers.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[bold]Harnesses in play, and the gaps found:[/]");
                foreach (McpWiringFinding finding in inspection.Findings)
                    PrintFinding(finding);
            }

            AnsiConsole.WriteLine();

            if (undetectable.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    "[bold]Writable harnesses no marker can detect in this tree:[/] naming one is the only way to write it.");
                foreach (HarnessMcpEntry row in undetectable)
                {
                    AnsiConsole.MarkupLine(
                        $"  [bold]{Markup.Escape(row.Harness)}[/] [grey](nothing named '{Markup.Escape(row.InPlaySignal)}' was found)[/]: {Markup.Escape(WireCommand)} {Markup.Escape(row.Harness)}");
                }

                AnsiConsole.WriteLine();
            }

            AnsiConsole.MarkupLine(
                $"[grey]Nothing was written. Wire a harness with: {Markup.Escape(WireCommand + " " + TargetPlaceholder)}. Add --dry-run to preview the write.[/]");
        });

        return 0;
    }

    /// <summary>
    /// The targeted run: skip what is already right, write what the operator named, and exit 1 when
    /// any requested target was refused.
    /// </summary>
    private static int Wire(
        string root,
        McpConfig config,
        LaunchCommandResolver resolver,
        IReadOnlyList<string> targets,
        bool dryRun)
    {
        McpWiringReport inspection = McpWiringInspector.Inspect(root, config, resolver);

        List<string> satisfied = [];
        List<string> toRepair = [];
        foreach (string target in targets)
        {
            if (Satisfied(root, inspection, target))
                satisfied.Add(target);
            else
                toRepair.Add(target);
        }

        List<McpWiringFileChange> changes = toRepair.Count == 0
            ? []
            : [.. McpWiringRepairer.Repair(root, config, resolver, toRepair, dryRun).Files];

        Unfolded(() =>
        {
            foreach (McpWiringFileChange change in changes)
                PrintChange(change, dryRun);

            foreach (string harness in satisfied)
                PrintSatisfied(harness, config);
        });

        // A refusal is an error outcome of the operation the operator asked for, so it decides the
        // exit code in a dry run as well: holding back the bytes must not soften the verdict (5.10).
        return changes.Any(change => change.Outcome == McpWiringChangeOutcome.Refused) ? 1 : 0;
    }

    /// <summary>
    /// Whether a named harness already holds every declared server, launched exactly as declared,
    /// so the request has nothing to write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the one decision the repair cannot make for itself. The repair refuses a file that
    /// already holds the server's name (5.5), which is right for a name somebody else owns and
    /// wrong for the entry this command wrote itself; without this distinction a second run of the
    /// same command would fail on the success of the first.
    /// </para>
    /// <para>
    /// The check reports no finding for a server that is wired with the declared launch command,
    /// which is exactly the "already satisfied" condition, so the inspection is the discriminator
    /// rather than a second copy of its comparison. The converse matters just as much: an unknown
    /// slug, a harness this tool never writes, a harness whose file is absent, and a name present
    /// with a different launch command are all left to the repair, which carries the message each
    /// of them needs — a typo and a snippet-only harness need an explanation, not a skip.
    /// </para>
    /// </remarks>
    private static bool Satisfied(string root, McpWiringReport inspection, string harness)
    {
        if (HarnessMcpCatalog.Find(harness) is not { Fixability: McpWiringFixability.Fixable } row)
            return false;

        // File absent is the "not in play" condition for Claude Code and opencode, and creation is
        // the answer there (R5.4, D19), never "nothing to do".
        if (!row.Paths.Any(candidate => File.Exists(FullPath(root, candidate))))
            return false;

        foreach (McpWiringFinding finding in inspection.Findings)
        {
            if (!string.Equals(finding.Harness, harness, StringComparison.Ordinal))
                continue;

            if (finding.Kind is McpWiringKind.NotWired
                or McpWiringKind.CommandDiffers
                or McpWiringKind.BinaryAbsent)
            {
                return false;
            }
        }

        return true;
    }

    private static void PrintSatisfied(string harness, McpConfig config)
    {
        HarnessMcpEntry? row = HarnessMcpCatalog.Find(harness);
        string file = row is not null && row.Paths.Count > 0 ? row.Paths[0] : "its configuration file";

        AnsiConsole.MarkupLine(
            $"  [green]nothing to write[/] [bold]{Markup.Escape(harness)}[/] [grey]({Markup.Escape(file)} already holds {Markup.Escape(Declared(config))})[/]");
    }

    private static void PrintFinding(McpWiringFinding finding)
    {
        string style = finding.Severity switch
        {
            Severity.Critical or Severity.Error => "red",
            Severity.Warning => "yellow",
            _ => "grey"
        };

        string about = string.IsNullOrEmpty(finding.Server)
            ? Markup.Escape(finding.Harness)
            : $"{Markup.Escape(finding.Harness)} [grey]({Markup.Escape(finding.Server)})[/]";

        AnsiConsole.MarkupLine(
            $"  [grey]{Markup.Escape(finding.Code)}[/] {about}: {Markup.Escape(finding.Message)}");
        AnsiConsole.MarkupLine($"    [{style}]→ {Markup.Escape(finding.Hint)}[/]");
    }

    private static string Verb(McpWiringChangeOutcome outcome, bool dryRun) => outcome switch
    {
        McpWiringChangeOutcome.Created => dryRun ? "[green]would create[/]" : "[green]created[/]",
        McpWiringChangeOutcome.Inserted => dryRun ? "[green]would insert into[/]" : "[green]inserted into[/]",
        McpWiringChangeOutcome.Restored => "[green]restored[/]",
        McpWiringChangeOutcome.Deleted => "[green]deleted[/]",
        _ => "[grey]examined[/]"
    };

    /// <summary>
    /// Whether a harness is in play in this tree: a marker directory exists, or one of the files
    /// the catalog names does. Absence of both is what the preview has to speak for.
    /// </summary>
    private static bool InPlay(string root, HarnessMcpEntry row) =>
        row.MarkerDirectories.Any(marker => Directory.Exists(Path.Combine(root, marker))) ||
        row.Paths.Any(path => File.Exists(FullPath(root, path)));

    /// <summary>The declared server names, in the order findings and receipts name them.</summary>
    private static string Declared(McpConfig config) =>
        string.Join(", ", config.RequiredServers.Keys.OrderBy(name => name, StringComparer.Ordinal));

    private static string FullPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
