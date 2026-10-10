using System.Security.Cryptography;
using System.Text.Json;
using KyberWeave.Core.Squad.Deployment;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
/// Verifies the integrity of deployed Kyber-Squad files against the recorded ownership receipt.
/// </summary>
public sealed class SquadStatusCommand : Command<SquadStatusSettings>
{
    private readonly ISquadUserPaths? _userPaths;
    private readonly SquadStateStore? _stateStore;
    private readonly ISquadGlobalRootResolver? _globalRoots;

    /// <summary>Creates a new status command using default system paths.</summary>
    public SquadStatusCommand()
    {
    }

    /// <summary>Creates a new status command using injectable dependencies.</summary>
    internal SquadStatusCommand(
        ISquadUserPaths? userPaths = null,
        SquadStateStore? stateStore = null,
        ISquadGlobalRootResolver? globalRoots = null)
    {
        _userPaths = userPaths;
        _stateStore = stateStore;
        _globalRoots = globalRoots;
    }

    /// <inheritdoc />
    protected override int Execute(CommandContext context, SquadStatusSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        SquadStateStore stateStore = _stateStore ?? SquadCommandComposition.ResolveStateStore(_userPaths);

        // Coalesce the positional path with --path; invalid client input returns exit
        // code 2 before any resolution or work
        string? effectivePath;
        try
        {
            effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        }
        catch (ArgumentException ex)
        {
            SquadCommandComposition.WriteClientInputError(ex.Message);
            return 2;
        }

        string targetRoot = SquadCommandComposition.ResolveTargetRoot(effectivePath);
        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);
        ISquadGlobalRootResolver globalRoots = _globalRoots ?? SquadCommandComposition.ResolveGlobalRoots();

        SquadReceipt? receipt = stateStore.ReadReceipt(targetRoot, scope);
        if (receipt is null)
        {
            AnsiConsole.MarkupLine($"[red]No Kyber-Squad deployment found at [bold]{Markup.Escape(targetRoot)}[/].[/]");
            AnsiConsole.MarkupLine("Run [bold]kyber-weave squad install[/] to deploy agents and skills.");
            return 1;
        }

        AnsiConsole.MarkupLine($"Kyber-Squad deployment at [bold]{Markup.Escape(targetRoot)}[/] ([grey]{(scope == SquadDeploymentScope.Global ? "global" : "project")}[/]):");

        if (SquadDeploymentPlan.IsLegacySingleRootReceipt(receipt))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]legacy layout:[/] this receipt predates issue #91 and every entry " +
                $"resolves beneath the recorded root [bold]{Markup.Escape(targetRoot)}[/] rather " +
                "than each target's own global root. Recover with [bold]kyber-weave squad uninstall --global[/] " +
                "then [bold]kyber-weave squad install --global[/].");
        }

        bool hasIssues = false;
        bool hasMissing = false;
        foreach (SquadOwnedFile file in receipt.Files)
        {
            string fullPath;
            try
            {
                // A receipt's relative paths are target-relative: beneath the deployment root
                // for project scope, beneath each target's own global root otherwise (or
                // beneath the recorded root for a legacy single-root receipt — see
                // ResolveOwnedFilePath). Joining targetRoot directly would check a global
                // deployment against the project directory and report every file missing.
                fullPath = SquadDeploymentPlan.ResolveOwnedFilePath(receipt, targetRoot, globalRoots, file);
            }
            catch (Exception)
            {
                AnsiConsole.MarkupLine($"  [red]invalid[/] {Markup.Escape(file.RelativePath)} (outside the deployment root)");
                hasIssues = true;
                continue;
            }

            if (!File.Exists(fullPath))
            {
                AnsiConsole.MarkupLine(StatusLine("red", "missing", file, scope));
                hasIssues = true;
                hasMissing = true;
                continue;
            }

            byte[] bytes = File.ReadAllBytes(fullPath);
            string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actualSha256, file.Sha256, StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine(StatusLine("yellow", "drift", file, scope) + " (modified)");
                hasIssues = true;
                continue;
            }

            AnsiConsole.MarkupLine(StatusLine("green", "ok", file, scope));
        }

        foreach (SquadOwnedBlock block in receipt.Blocks)
        {
            ReportOwnedBlock(block, receipt, targetRoot, globalRoots, scope, ref hasIssues, ref hasMissing);
        }

        if (hasIssues)
        {
            AnsiConsole.WriteLine();
            string summary = hasMissing
                ? "Drift or missing files detected in Kyber-Squad deployment."
                : "Drift detected in Kyber-Squad deployment.";
            AnsiConsole.MarkupLine($"[red]{summary}[/]");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]All deployed files match the recorded receipt.[/]");
        return 0;
    }

    /// <summary>
    /// Reports one receipt-owned block: its file with its entry count when healthy, or one
    /// drift line per owned entry naming the file and the container (Req 8.4) when the file
    /// is missing, unparsable, or hand-edited.
    /// </summary>
    /// <remarks>
    /// Blocks own entries, never files, so a missing or unparsable file drifts every entry
    /// it should carry rather than reading as a whole-file miss. The format is resolved from
    /// the recorded relative path the same way uninstall resolves it, so reporting never
    /// depends on a render that may no longer exist.
    /// </remarks>
    private static void ReportOwnedBlock(
        SquadOwnedBlock block,
        SquadReceipt receipt,
        string targetRoot,
        ISquadGlobalRootResolver? globalRoots,
        SquadDeploymentScope scope,
        ref bool hasIssues,
        ref bool hasMissing)
    {
        ArgumentNullException.ThrowIfNull(block);
        string suffix = scope == SquadDeploymentScope.Global ? $" ({block.Target})" : string.Empty;
        string fileLabel = $"{Markup.Escape(block.RelativePath)}{suffix}";
        string entryWord = block.Entries.Count == 1 ? "entry" : "entries";

        string fullPath;
        try
        {
            // Blocks exist on project scope only, but resolve through the same
            // receipt-aware helper as owned files so a future scope never drifts apart.
            fullPath = SquadDeploymentPlan.ResolveOwnedFilePath(
                receipt,
                targetRoot,
                globalRoots,
                new SquadOwnedFile(block.RelativePath, new string('0', 64), block.Target, false));
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine($"  [red]invalid[/] {Markup.Escape(block.RelativePath)} (outside the deployment root)");
            hasIssues = true;
            return;
        }

        if (!TryResolveBlockFormat(block, out SquadHookBlockFormat format))
        {
            AnsiConsole.MarkupLine($"  [red]invalid[/] {fileLabel} (unknown shared hook file)");
            hasIssues = true;
            return;
        }

        if (!File.Exists(fullPath))
        {
            foreach (SquadOwnedBlockEntry entry in block.Entries)
            {
                AnsiConsole.MarkupLine($"  [red]drift  [/] {fileLabel} {Markup.Escape(entry.Container)} (missing)");
            }

            if (block.Entries.Count == 0)
            {
                AnsiConsole.MarkupLine($"  [red]drift  [/] {fileLabel} (missing)");
            }

            hasIssues = true;
            hasMissing = true;
            return;
        }

        string currentJson = File.ReadAllText(fullPath);
        Dictionary<string, string> expected = new(StringComparer.Ordinal);
        foreach (SquadOwnedBlockEntry entry in block.Entries)
        {
            expected[entry.Container] = entry.Sha256;
        }

        IReadOnlyList<SquadHookDrift> drifts;
        try
        {
            drifts = SquadHookJsonBlock.FindDrift(format, currentJson, expected);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            foreach (SquadOwnedBlockEntry entry in block.Entries)
            {
                AnsiConsole.MarkupLine($"  [yellow]drift  [/] {fileLabel} {Markup.Escape(entry.Container)} (unparsable)");
            }

            hasIssues = true;
            return;
        }

        if (drifts.Count == 0)
        {
            AnsiConsole.MarkupLine($"  [green]ok     [/] {fileLabel} {block.Entries.Count} {entryWord}");
            return;
        }

        foreach (SquadHookDrift drift in drifts)
        {
            AnsiConsole.MarkupLine($"  [yellow]drift  [/] {fileLabel} {Markup.Escape(drift.Location)} ({Markup.Escape(drift.Reason)})");
        }

        hasIssues = true;
        if (drifts.Any(drift => string.Equals(drift.Reason, "missing", StringComparison.Ordinal)))
        {
            hasMissing = true;
        }
    }

    /// <summary>
    /// Resolves the hook-file shape for a receipt-owned block from its recorded relative
    /// path, so reporting never depends on a render that may no longer exist.
    /// </summary>
    private static bool TryResolveBlockFormat(SquadOwnedBlock block, out SquadHookBlockFormat format)
    {
        foreach (SquadHookBlockFormat candidate in Enum.GetValues<SquadHookBlockFormat>())
        {
            if (string.Equals(
                    SquadHookJsonBlock.RelativePath(candidate),
                    block.RelativePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                format = candidate;
                return true;
            }
        }

        format = default;
        return false;
    }

    /// <summary>
    /// Renders one status line. Global scope deploys the same relative path to several
    /// targets' roots, so the target token is part of the identity shown to the operator;
    /// project scope already encodes the target in the path itself.
    /// </summary>
    private static string StatusLine(string color, string state, SquadOwnedFile file, SquadDeploymentScope scope)
    {
        string suffix = scope == SquadDeploymentScope.Global ? $" ({file.Target})" : string.Empty;
        return $"  [{color}]{state.PadRight(7)}[/] {Markup.Escape(file.RelativePath)}{suffix}";
    }

    public int Execute(CommandContext context, SquadStatusSettings settings) => Execute(context, settings, CancellationToken.None);
}
