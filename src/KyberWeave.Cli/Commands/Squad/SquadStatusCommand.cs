using System.Security.Cryptography;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
///     Verifies the integrity of deployed Kyber-Squad files against the recorded ownership receipt.
/// </summary>
public sealed class SquadStatusCommand : Command<SquadStatusSettings>
{
    private readonly ISquadGlobalRootResolver? _globalRoots;
    private readonly SquadStateStore? _stateStore;
    private readonly ISquadUserPaths? _userPaths;

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
    public override int Execute(CommandContext context, SquadStatusSettings settings,
        CancellationToken cancellationToken)
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

        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);

        // State is migrated before the receipt is read, and the anchor is chosen after the migration. A --global
        // status reads the one home-level receipt, so it is anchored at the home directory whatever the working
        // directory is, or at the recorded root of a legacy single-root receipt the migration has just copied.
        string projectRoot = SquadCommandComposition.ResolveTargetRoot(effectivePath);
        SquadCommandComposition.WriteMigrationNotes(SquadCommandComposition.EnsureStateMigrated(
            stateStore,
            Environment.CurrentDirectory,
            scope == SquadDeploymentScope.Project ? projectRoot : null,
            false));
        string targetRoot = scope == SquadDeploymentScope.Global
            ? SquadCommandComposition.ResolveGlobalAnchorRoot(stateStore, effectivePath, false)
            : projectRoot;
        ISquadGlobalRootResolver globalRoots = _globalRoots ?? SquadCommandComposition.ResolveGlobalRoots();

        SquadReceipt? receipt = stateStore.ReadReceipt(targetRoot, scope);
        IReadOnlyList<(string TargetPreset, string Version)> instances = scope == SquadDeploymentScope.Global
            ? GetDeployedInstances(stateStore)
            : [];

        if (receipt is null)
        {
            if (scope == SquadDeploymentScope.Global && instances.Count > 0)
            {
                AnsiConsole.MarkupLine(
                    $"Kyber-Squad deployment at [bold]{Markup.Escape(targetRoot)}[/] ([grey]global[/]):");
                AnsiConsole.MarkupLine("  [grey]none[/]");

                WriteInstances(instances);
                bool registryFailed = !WriteRegisteredProjects(stateStore);
                if (registryFailed)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[red]The Kyber-Squad project registry is unreadable.[/]");
                    return 1;
                }

                return 0;
            }

            AnsiConsole.MarkupLine($"[red]No Kyber-Squad deployment found at [bold]{Markup.Escape(targetRoot)}[/].[/]");
            AnsiConsole.MarkupLine("Run [bold]kyber-weave squad install[/] to deploy agents and skills.");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"Kyber-Squad deployment at [bold]{Markup.Escape(targetRoot)}[/] ([grey]{(scope == SquadDeploymentScope.Global ? "global" : "project")}[/]):");

        if (SquadDeploymentPlan.IsLegacySingleRootReceipt(receipt))
            AnsiConsole.MarkupLine(
                $"[yellow]legacy layout:[/] this receipt predates issue #91 and every entry " +
                $"resolves beneath the recorded root [bold]{Markup.Escape(targetRoot)}[/] rather " +
                "than each target's own global root. Recover with [bold]kyber-weave squad uninstall --global[/] " +
                "then [bold]kyber-weave squad install --global[/].");

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
                AnsiConsole.MarkupLine(
                    $"  [red]invalid[/] {Markup.Escape(file.RelativePath)} (outside the deployment root)");
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

        bool hasMissingPreset = WriteModelPresets(stateStore, targetRoot, scope);

        if (scope == SquadDeploymentScope.Global) WriteInstances(instances);

        bool registryUnreadable = scope == SquadDeploymentScope.Global && !WriteRegisteredProjects(stateStore);

        if (hasIssues || hasMissingPreset)
        {
            AnsiConsole.WriteLine();
            string summary = hasMissing
                ? "Drift or missing files detected in Kyber-Squad deployment."
                : hasMissingPreset
                    ? "Missing model preset detected in Kyber-Squad deployment."
                    : "Drift detected in Kyber-Squad deployment.";
            AnsiConsole.MarkupLine($"[red]{summary}[/]");
            return 1;
        }

        if (registryUnreadable)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[red]The Kyber-Squad project registry is unreadable.[/]");
            return 1;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]All deployed files match the recorded receipt.[/]");
        return 0;
    }

    /// <summary>
    ///     Inspects and renders the active model presets recorded in the deployment lock.
    /// </summary>
    /// <returns><see langword="true" /> when a recorded preset file is missing; otherwise <see langword="false" />.</returns>
    private static bool WriteModelPresets(SquadStateStore stateStore, string targetRoot, SquadDeploymentScope scope)
    {
        SquadLock? squadLock = null;
        try
        {
            squadLock = stateStore.ReadLock(targetRoot, scope);
        }
        catch (Exception)
        {
            // If the lock file is missing or unreadable, treat as having no recorded presets.
        }

        AnsiConsole.WriteLine();
        if (squadLock?.ModelPresets is null || squadLock.ModelPresets.Count == 0)
        {
            AnsiConsole.MarkupLine("Model presets: [grey]none[/]");
            return false;
        }

        AnsiConsole.MarkupLine("Model presets:");
        SquadModelPresetStore presetStore = new(stateStore.UserPaths);
        bool hasMissingPreset = false;

        foreach (KeyValuePair<SquadTarget, SquadAppliedPreset> entry in squadLock.ModelPresets.OrderBy(
                     static kvp => SquadTargetCatalog.GetToken(kvp.Key), StringComparer.Ordinal))
        {
            string targetToken = SquadTargetCatalog.GetToken(entry.Key);
            string presetName = entry.Value.Name;
            NormalizePresetFileEncoding(stateStore.UserPaths, presetName);
            SquadModelPreset? loadedPreset = null;
            try
            {
                loadedPreset = presetStore.TryLoad(presetName);
            }
            catch (Exception)
            {
                loadedPreset = null;
            }

            if (loadedPreset is null)
            {
                AnsiConsole.MarkupLine(
                    $"  [red]{Markup.Escape(targetToken)}: {Markup.Escape(presetName)} (missing)[/]");
                hasMissingPreset = true;
            }
            else if (!string.Equals(loadedPreset.Digest, entry.Value.Digest, StringComparison.Ordinal))
            {
                AnsiConsole.MarkupLine(
                    $"  [yellow]{Markup.Escape(targetToken)}: {Markup.Escape(presetName)} (changed since applied)[/]");
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"  [green]{Markup.Escape(targetToken)}: {Markup.Escape(presetName)}[/]");
            }
        }

        return hasMissingPreset;
    }

    /// <summary>
    ///     Strips a UTF-8 byte order mark (BOM) from a model preset file if present.
    ///     Presets authored with standard .NET APIs may carry a 3-byte preamble that
    ///     YamlDotNet and SHA-256 digest comparisons treat as content unless normalized.
    /// </summary>
    private static void NormalizePresetFileEncoding(ISquadUserPaths userPaths, string presetName)
    {
        try
        {
            string presetsDir = Path.Combine(userPaths.HomeDirectory, ".kyber-weave", "squad", "presets");
            string filePath = Path.Combine(presetsDir, $"{presetName}.yml");
            if (!File.Exists(filePath)) return;

            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                File.WriteAllBytes(filePath, bytes.AsSpan(3).ToArray());
        }
        catch (Exception)
        {
            // Ignore filesystem errors during best-effort encoding normalization.
        }
    }

    /// <summary>
    ///     Lists the projects registered with the home-level registry, flagging each stale one with its reason.
    ///     A global deployment is the home's to report, and the registry is the list of projects that depend on
    ///     it, so the operator sees both in one place.
    /// </summary>
    /// <remarks>
    ///     Each project is one unfolded line, so its path can be copied straight from the output. An unreadable registry
    ///     is named the way doctor names it, and the result is false so the run fails. Status does not repair a corrupt
    ///     file. The registry is rebuildable from per-project state, so removing an unreadable file lets the next listing
    ///     rebuild it.
    /// </remarks>
    /// <returns><see langword="false" /> when the registry could not be read; otherwise <see langword="true" />.</returns>
    private static bool WriteRegisteredProjects(SquadStateStore stateStore)
    {
        IReadOnlyList<SquadProjectRegistryEntry> projects;
        try
        {
            projects = stateStore.ListProjects();
        }
        catch (InvalidDataException ex)
        {
            AnsiConsole.WriteLine();
            SquadCommandComposition.WriteLine(
                $"  [red]fail[/] Squad project registry is unreadable: {Markup.Escape(ex.Message)}");
            return false;
        }

        AnsiConsole.WriteLine();
        if (projects.Count == 0)
        {
            SquadCommandComposition.WriteLine("[grey]No projects are registered.[/]");
            return true;
        }

        SquadCommandComposition.WriteLine("Registered projects:");
        foreach (SquadProjectRegistryEntry project in projects)
        {
            string line = project.Stale
                ? $"  [yellow]stale[/] {Markup.Escape(project.Path)} (stale: {Markup.Escape(project.StaleReason ?? "unknown")})"
                : $"  [green]ok[/]    {Markup.Escape(project.Path)}";
            SquadCommandComposition.WriteLine(line);
        }

        return true;
    }

    /// <summary>
    ///     Inspects and returns the deployed harness instances recorded in the instance state directory.
    /// </summary>
    private static List<(string TargetPreset, string Version)> GetDeployedInstances(SquadStateStore stateStore)
    {
        string home = stateStore.UserPaths.HomeDirectory;
        string instanceStateBase = Path.Combine(home, ".kyber-weave", "squad", "instance-state");

        List<(string TargetPreset, string Version)> instances = [];

        if (!Directory.Exists(instanceStateBase)) return instances;

        DirectoryInfo baseDir = new(instanceStateBase);
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        };

        foreach (FileSystemInfo targetEntry in baseDir.EnumerateFileSystemInfos("*", options))
        {
            if (targetEntry is not DirectoryInfo targetDir || targetEntry.LinkTarget is not null) continue;

            string targetToken = targetDir.Name;

            foreach (FileSystemInfo presetEntry in targetDir.EnumerateFileSystemInfos("*", options))
            {
                if (presetEntry is not DirectoryInfo presetDir || presetEntry.LinkTarget is not null) continue;

                string receiptPath = Path.Combine(presetDir.FullName, "squad.receipt.json");
                if (!File.Exists(receiptPath)) continue;

                string version = "unknown";
                string lockPath = Path.Combine(presetDir.FullName, "squad.lock.yml");
                if (File.Exists(lockPath))
                    try
                    {
                        SquadLock? squadLock = stateStore.DeserializeLock(File.ReadAllText(lockPath));
                        if (!string.IsNullOrWhiteSpace(squadLock?.SquadVersion))
                            version = squadLock.SquadVersion;
                        else if (!string.IsNullOrWhiteSpace(squadLock?.CliVersion)) version = squadLock.CliVersion;
                    }
                    catch (Exception)
                    {
                        // Treat unreadable lock as unknown version
                    }

                instances.Add(($"{targetToken}/{presetDir.Name}", version));
            }
        }

        return instances;
    }

    /// <summary>
    ///     Inspects and renders the deployed harness instances recorded in the instance state directory.
    /// </summary>
    private static void WriteInstances(IReadOnlyList<(string TargetPreset, string Version)> instances)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("Instances:");

        if (instances.Count == 0)
        {
            AnsiConsole.MarkupLine("  [grey]none[/]");
            return;
        }

        foreach ((string targetPreset, string version) in instances.OrderBy(static i => i.TargetPreset,
                     StringComparer.Ordinal))
            AnsiConsole.MarkupLine($"  {Markup.Escape(targetPreset)}  {Markup.Escape(version)}");
    }

    /// <summary>
    ///     Renders one status line. Global scope deploys the same relative path to several
    ///     targets' roots, so the target token is part of the identity shown to the operator;
    ///     project scope already encodes the target in the path itself.
    /// </summary>
    private static string StatusLine(string color, string state, SquadOwnedFile file, SquadDeploymentScope scope)
    {
        string suffix = scope == SquadDeploymentScope.Global ? $" ({file.Target})" : string.Empty;
        return $"  [{color}]{state.PadRight(7)}[/] {Markup.Escape(file.RelativePath)}{suffix}";
    }

    public int Execute(CommandContext context, SquadStatusSettings settings)
    {
        return Execute(context, settings, CancellationToken.None);
    }
}
