using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Docs.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Removes the MCP entries a previous <c>docs mcp-wire</c> wrote, restoring each file to the bytes
/// the repair found there.
/// </summary>
/// <remarks>
/// <para>
/// Removal proves its case before it touches anything: it works from the receipt the writer left
/// behind, and refuses on a missing record or on a file whose digest no longer matches the one
/// recorded, rather than restoring over someone else's edit (5.9). Overwriting a foreign change is
/// the failure this verb exists to prevent, so drift is an error outcome with exit 1, not a
/// warning.
/// </para>
/// <para>
/// The declaration is deliberately not read here. Removal restores bytes; it does not need to know
/// which servers the repository means to declare, and requiring a loadable <c>mcp:</c> section
/// would block the unwind of exactly the repository whose section is malformed — which is the
/// state an operator most often wants out of.
/// </para>
/// </remarks>
public sealed class DocsMcpUnwireCommand : Command<DocsMcpUnwireSettings>
{
    /// <summary>The remedy command, spelled once so the listing and the usage agree on it.</summary>
    private const string UnwireCommand = "kyber-weave docs mcp-unwire --target";

    /// <inheritdoc />
    public override int Execute(
        CommandContext context,
        DocsMcpUnwireSettings settings,
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
        if (targets.Count == 0)
        {
            // Nothing was named, so nothing was asked for: the run reports what can be removed and
            // writes nothing. Removal without a name would be the same unwritten consent the wire
            // verb refuses (5.3).
            PrintRecord(root);
            return 0;
        }

        McpWiringRepairReport removal = McpWiringRepairer.Unwire(root, targets);

        CommandHelpers.WriteUnfolded(() =>
        {
            foreach (McpWiringFileChange change in removal.Files)
                DocsMcpWireCommand.PrintChange(change, dryRun: false);
        });

        return removal.Files.Any(change => change.Outcome == McpWiringChangeOutcome.Refused) ? 1 : 0;
    }

    /// <summary>The two-argument overload the rest of this branch's commands carry.</summary>
    public int Execute(CommandContext context, DocsMcpUnwireSettings settings) =>
        Execute(context, settings, CancellationToken.None);

    /// <summary>
    /// Lists the files the receipt records, so an operator without a target learns which harness
    /// slugs are removable before naming one.
    /// </summary>
    private static void PrintRecord(string root)
    {
        McpWiringReceipt receipt = McpWiringReceipt.Load(root);

        CommandHelpers.WriteUnfolded(() =>
        {
            if (receipt.Files.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]No receipt record[/] at {Markup.Escape(McpWiringReceipt.RelativePath)}: this repository "
                    + "holds nothing the repair wrote, so there is nothing to remove.");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[bold]Recorded writes[/] ({Markup.Escape(McpWiringReceipt.RelativePath)}):");

                foreach (McpWiringReceiptRecord record in receipt.Files)
                {
                    AnsiConsole.MarkupLine(
                        $"  [bold]{Markup.Escape(record.Path)}[/] [grey]({Markup.Escape(string.Join(", ", record.Servers))})[/]");
                }
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[grey]Nothing was removed. Name the harnesses to restore: {Markup.Escape(UnwireCommand + " " + DocsMcpWireCommand.TargetPlaceholder)}.[/]");
        });
    }
}
