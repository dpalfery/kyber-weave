using System.Security.Cryptography;
using System.Text;
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

        SquadReceipt? receipt;
        try
        {
            receipt = stateStore.ReadReceipt(targetRoot, scope);
        }
        catch (Exception ex) when (ex is SquadDeploymentConflictException or InvalidDataException)
        {
            AnsiConsole.MarkupLine($"[red]kyber-weave squad: error: {Markup.Escape(ex.Message)}[/]");
            return 1;
        }

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

        if (scope == SquadDeploymentScope.Global && IsReadOnlyLegacyReceiptAcrossRoots(stateStore, targetRoot, receipt))
        {
            AnsiConsole.MarkupLine(
                "[yellow]legacy global partition:[/] status is read-only here because this receipt " +
                "was recovered from a different original root. The mounted state is still valid for inspection.");
            foreach (SquadOwnedFile file in receipt.Files)
            {
                AnsiConsole.MarkupLine(StatusLine("green", "ok", file, scope));
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]All deployed files match the recorded receipt.[/]");
            return 0;
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
                fullPath = SquadDeploymentPlan.ResolveOwnedFilePath(
                    receipt,
                    targetRoot,
                    globalRoots,
                    file,
                    _userPaths ?? SquadUserPaths.Instance);
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

    private static bool IsReadOnlyLegacyReceiptAcrossRoots(
        SquadStateStore stateStore,
        string targetRoot,
        SquadReceipt receipt)
    {
        if (receipt.Scope != SquadDeploymentScope.Global ||
            SquadDeploymentPlan.IsLegacySingleRootReceipt(receipt))
        {
            return false;
        }

        string globalStateDirectory = stateStore.ResolveStateDirectory(targetRoot, SquadDeploymentScope.Global);
        if (File.Exists(Path.Combine(globalStateDirectory, "squad.receipt.json")))
        {
            return false;
        }

        string rootsDirectory = Path.Combine(
            Path.GetDirectoryName(globalStateDirectory) ?? throw new InvalidOperationException(
                "Could not resolve the global Squad state parent."),
            "roots");
        if (!Directory.Exists(rootsDirectory))
        {
            return false;
        }

        string[] bindingIds = Directory.EnumerateDirectories(rootsDirectory)
            .Select(path => Path.GetFileName(path))
            .Where(id => !string.IsNullOrWhiteSpace(id) && id.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (bindingIds.Length != 1)
        {
            return false;
        }

        string bindingId = bindingIds[0];
        if (string.Equals(ComputePhysicalRootKey(targetRoot), bindingId, StringComparison.Ordinal))
        {
            return false;
        }

        string receiptPath = Path.Combine(rootsDirectory, bindingId, "squad.receipt.json");
        if (!File.Exists(receiptPath))
        {
            return false;
        }

        try
        {
            SquadReceipt legacyReceipt = stateStore.DeserializeReceipt(File.ReadAllText(receiptPath, Encoding.UTF8));
            return !SquadDeploymentPlan.IsLegacySingleRootReceipt(legacyReceipt);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renders one status line. Global scope deploys the same relative path to several
    /// targets' roots, so the target token is part of the identity shown to the operator;
    /// project scope already encodes the target in the path itself.
    /// </summary>
    private static string ComputePhysicalRootKey(string targetRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        string fullPath = Path.GetFullPath(targetRoot);
        string physicalPath = Path.TrimEndingDirectorySeparator(fullPath)
            .Normalize(NormalizationForm.FormC);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(physicalPath)));
    }

    private static string StatusLine(string color, string state, SquadOwnedFile file, SquadDeploymentScope scope)
    {
        string suffix = scope == SquadDeploymentScope.Global ? $" ({file.Target})" : string.Empty;
        return $"  [{color}]{state.PadRight(7)}[/] {Markup.Escape(file.RelativePath)}{suffix}";
    }

    public int Execute(CommandContext context, SquadStatusSettings settings) => Execute(context, settings, CancellationToken.None);
}
