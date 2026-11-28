using KyberWeave.Core.FileSystem;
using System.Security.Cryptography;
using System.Text.Json;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace KyberWeave.Cli.Commands.Squad;

/// <summary>
///     Verifies the integrity of deployed Kyber-Squad files against the recorded ownership receipt.
/// </summary>
internal sealed class SquadStatusCommand : Command<SquadStatusSettings>
{
    private readonly ISquadStateStore _stateStore;
    private readonly ISquadStateMigration _migration;
    private readonly ISquadGlobalRootResolver _globalRoots;
    private readonly ISquadModelPresetStore _presetStore;
    private readonly IFileSystemPathSemantics _pathSemantics;
    private readonly SquadConsole _output;
    private readonly IFileSystem _files;
    private readonly IProcessEnvironment _environment;
    private readonly SquadCommandRoots _roots;

    /// <summary>Creates the status command over the Squad services the host registers.</summary>
    /// <param name="stateStore">Reads the deployment state.</param>
    /// <param name="migration">Moves legacy state into the home layout before status reads it.</param>
    /// <param name="globalRoots">Resolves each target's global root.</param>
    /// <param name="presetStore">Loads the presets the lock names.</param>
    /// <param name="pathSemantics">The filesystem's containment and canonical spelling rules.</param>
    /// <param name="output">Where the command writes.</param>
    /// <param name="files">Reads the deployed files, the shared hook files and the instance state.</param>
    /// <param name="environment">Names the working directory the migration reports.</param>
    /// <param name="roots">Resolves the target root, migrates legacy state and anchors a global run.</param>
    public SquadStatusCommand(
        ISquadStateStore stateStore,
        ISquadStateMigration migration,
        ISquadGlobalRootResolver globalRoots,
        ISquadModelPresetStore presetStore,
        IFileSystemPathSemantics pathSemantics,
        SquadConsole output,
        IFileSystem files,
        IProcessEnvironment environment,
        SquadCommandRoots roots)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(migration);
        ArgumentNullException.ThrowIfNull(globalRoots);
        ArgumentNullException.ThrowIfNull(presetStore);
        ArgumentNullException.ThrowIfNull(pathSemantics);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(roots);
        _stateStore = stateStore;
        _migration = migration;
        _globalRoots = globalRoots;
        _presetStore = presetStore;
        _pathSemantics = pathSemantics;
        _output = output;
        _files = files;
        _environment = environment;
        _roots = roots;
    }

    /// <inheritdoc />
    public override int Execute(CommandContext context, SquadStatusSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ISquadStateStore stateStore = _stateStore;

        // Coalesce the positional path with --path; invalid client input returns exit
        // code 2 before any resolution or work
        string? effectivePath;
        try
        {
            effectivePath = SquadCommandComposition.CoalesceTargetPath(settings.Path, settings.PathOption);
        }
        catch (ArgumentException ex)
        {
            _output.WriteClientInputError(ex.Message);
            return 2;
        }

        SquadDeploymentScope scope = SquadCommandComposition.ResolveScope(settings.Global);

        // State is migrated before the receipt is read, and the anchor is chosen after the migration. A --global
        // status reads the one home-level receipt, so it is anchored at the home directory whatever the working
        // directory is, or at the recorded root of a legacy single-root receipt the migration has just copied.
        string projectRoot = _roots.ResolveTargetRoot(effectivePath);
        _output.WriteMigrationNotes(_roots.EnsureStateMigrated(
            _migration,
            _environment.CurrentDirectory,
            scope == SquadDeploymentScope.Project ? projectRoot : null,
            false));
        string targetRoot = scope == SquadDeploymentScope.Global
            ? _roots.ResolveGlobalAnchorRoot(stateStore, effectivePath, false)
            : projectRoot;
        ISquadGlobalRootResolver globalRoots = _globalRoots;

        SquadReceipt? receipt = stateStore.ReadReceipt(targetRoot, scope);
        IReadOnlyList<(string TargetPreset, string Version)> instances = scope == SquadDeploymentScope.Global
            ? GetDeployedInstances(stateStore)
            : [];

        if (receipt is null)
        {
            if (scope == SquadDeploymentScope.Global && instances.Count > 0)
            {
                _output.Ansi.MarkupLine(
                    $"Kyber-Squad deployment at [bold]{Markup.Escape(targetRoot)}[/] ([grey]global[/]):");
                _output.Ansi.MarkupLine("  [grey]none[/]");

                WriteInstances(instances);
                bool registryFailed = !WriteRegisteredProjects(stateStore);
                if (registryFailed)
                {
                    _output.Ansi.WriteLine();
                    _output.Ansi.MarkupLine("[red]The Kyber-Squad project registry is unreadable.[/]");
                    return 1;
                }

                return 0;
            }

            _output.Ansi.MarkupLine($"[red]No Kyber-Squad deployment found at [bold]{Markup.Escape(targetRoot)}[/].[/]");
            _output.Ansi.MarkupLine("Run [bold]kyber-weave squad install[/] to deploy agents and skills.");
            return 1;
        }

        _output.Ansi.MarkupLine(
            $"Kyber-Squad deployment at [bold]{Markup.Escape(targetRoot)}[/] ([grey]{(scope == SquadDeploymentScope.Global ? "global" : "project")}[/]):");

        if (SquadDeploymentPlanRules.IsLegacySingleRootReceipt(receipt))
            _output.Ansi.MarkupLine(
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
                fullPath = SquadDeploymentPlanRules.ResolveOwnedFilePath(receipt, targetRoot, globalRoots, file, _pathSemantics);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                _output.Ansi.MarkupLine(
                    $"  [red]invalid[/] {Markup.Escape(file.RelativePath)} (outside the deployment root)");
                hasIssues = true;
                continue;
            }

            if (!_files.FileExists(fullPath))
            {
                _output.Ansi.MarkupLine(StatusLine("red", "missing", file, scope));
                hasIssues = true;
                hasMissing = true;
                continue;
            }

            byte[] bytes = _files.ReadAllBytes(fullPath);
            string actualSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actualSha256, file.Sha256, StringComparison.Ordinal))
            {
                // The receipt holds only the digest of the bytes Squad shipped, so a mismatch cannot
                // say whether an update kept an earlier edit or someone edited the file afterwards.
                // Both are reported the same way, naming the flag that ends the difference.
                _output.Ansi.MarkupLine(
                    StatusLine("yellow", "modified locally", file, scope)
                    + " (kept at update; run [bold]squad update --replace-managed[/] to take the shipped file)");
                hasIssues = true;
                continue;
            }

            _output.Ansi.MarkupLine(StatusLine("green", "ok", file, scope));
        }

        foreach (SquadOwnedBlock block in receipt.Blocks)
        {
            ReportOwnedBlock(block, receipt, targetRoot, globalRoots, scope, ref hasIssues, ref hasMissing);
        }

        (bool hasMissingPreset, bool hasLockError) = WriteModelPresets(stateStore, targetRoot, scope);

        if (scope == SquadDeploymentScope.Global) WriteInstances(instances);

        bool registryUnreadable = scope == SquadDeploymentScope.Global && !WriteRegisteredProjects(stateStore);

        if (hasIssues || hasMissingPreset || hasLockError || registryUnreadable)
        {
            _output.Ansi.WriteLine();
            string summary = registryUnreadable
                ? "The Kyber-Squad project registry is unreadable."
                : hasLockError
                    ? "Error detected in Kyber-Squad deployment lock."
                    : hasMissing
                        ? "Drift or missing files detected in Kyber-Squad deployment."
                        : hasMissingPreset
                            ? "Missing model preset detected in Kyber-Squad deployment."
                            : "Drift detected in Kyber-Squad deployment.";
            _output.Ansi.MarkupLine($"[red]{summary}[/]");
            return 1;
        }

        _output.Ansi.WriteLine();
        _output.Ansi.MarkupLine("[green]All deployed files match the recorded receipt.[/]");
        return 0;
    }

    /// <summary>
    /// Reports one receipt-owned block: its file with its entry count when healthy, or one
    /// drift line per owned entry naming the file and the container (Req 8.4) when the file
    /// is missing, unparsable, or hand-edited.
    /// </summary>
    /// <remarks>
    /// Blocks own entries, never files, so a missing or unparsable file drifts every entry
    /// it should carry rather than reading as a whole-file miss. Both the file's path and its
    /// shape are read from the receipt, so reporting never depends on a render that may no
    /// longer exist and never needs a scope of its own to say where a global block lives.
    /// </remarks>
    private void ReportOwnedBlock(
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
            // Blocks resolve through the same receipt-aware helper as owned files, so a
            // global block lands beneath its target's own root.
            fullPath = SquadDeploymentPlanRules.ResolveOwnedFilePath(
                receipt,
                targetRoot,
                globalRoots,
                new SquadOwnedFile(block.RelativePath, new string('0', 64), block.Target, false), _pathSemantics);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            _output.Ansi.MarkupLine($"  [red]invalid[/] {Markup.Escape(block.RelativePath)} (outside the deployment root)");
            hasIssues = true;
            return;
        }

        if (!SquadHookJsonBlock.TryResolveFormat(block, out SquadHookBlockFormat format))
        {
            _output.Ansi.MarkupLine($"  [red]invalid[/] {fileLabel} (unknown shared hook file)");
            hasIssues = true;
            return;
        }

        if (!_files.FileExists(fullPath))
        {
            foreach (SquadOwnedBlockEntry entry in block.Entries)
            {
                _output.Ansi.MarkupLine($"  [red]drift  [/] {fileLabel} {Markup.Escape(entry.Container)} (missing)");
            }

            if (block.Entries.Count == 0)
            {
                _output.Ansi.MarkupLine($"  [red]drift  [/] {fileLabel} (missing)");
            }

            hasIssues = true;
            hasMissing = true;
            return;
        }

        string currentJson;
        try
        {
            currentJson = _files.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _output.Ansi.MarkupLine(
                $"  [red]drift  [/] {fileLabel} (unreadable: {Markup.Escape(ex.Message)})");
            hasIssues = true;
            return;
        }

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
                _output.Ansi.MarkupLine($"  [yellow]drift  [/] {fileLabel} {Markup.Escape(entry.Container)} (unparsable)");
            }

            hasIssues = true;
            return;
        }

        if (drifts.Count == 0)
        {
            _output.Ansi.MarkupLine($"  [green]ok     [/] {fileLabel} {block.Entries.Count} {entryWord}");
            return;
        }

        foreach (SquadHookDrift drift in drifts)
        {
            _output.Ansi.MarkupLine($"  [yellow]drift  [/] {fileLabel} {Markup.Escape(drift.Location)} ({Markup.Escape(drift.Reason)})");
        }

        hasIssues = true;
        if (drifts.Any(drift => string.Equals(drift.Reason, "missing", StringComparison.Ordinal)))
        {
            hasMissing = true;
        }
    }

    /// <summary>
    ///     Inspects and renders the active model presets recorded in the deployment lock.
    /// </summary>
    /// <returns><see langword="true" /> when a recorded preset file is missing; otherwise <see langword="false" />.</returns>
    private (bool HasMissingPreset, bool HasLockError) WriteModelPresets(ISquadStateStore stateStore,
        string targetRoot, SquadDeploymentScope scope)
    {
        SquadLock? squadLock;
        try
        {
            squadLock = stateStore.ReadLock(targetRoot, scope);
        }
        catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException)
        {
            _output.Ansi.WriteLine();
            _output.Ansi.MarkupLine($"[red]Error reading Squad lock file: {Markup.Escape(ex.Message)}[/]");
            return (false, true);
        }

        _output.Ansi.WriteLine();
        if (squadLock?.ModelPresets is null || squadLock.ModelPresets.Count == 0)
        {
            _output.Ansi.MarkupLine("Model presets: [grey]none[/]");
            return (false, false);
        }

        _output.Ansi.MarkupLine("Model presets:");
        ISquadModelPresetStore presetStore = _presetStore;
        bool hasMissingPreset = false;

        foreach (KeyValuePair<SquadTarget, SquadAppliedPreset> entry in squadLock.ModelPresets.OrderBy(
                     static kvp => SquadTargetCatalog.GetToken(kvp.Key), StringComparer.Ordinal))
        {
            string targetToken = SquadTargetCatalog.GetToken(entry.Key);
            string presetName = entry.Value.Name;
            SquadModelPreset? loadedPreset = null;
            try
            {
                loadedPreset = presetStore.TryLoad(presetName);
            }
            catch (Exception ex) when (ex is ArgumentException or SquadSourceValidationException
                                           or IOException or UnauthorizedAccessException)
            {
                loadedPreset = null;
            }

            if (loadedPreset is null)
            {
                _output.Ansi.MarkupLine(
                    $"  [red]{Markup.Escape(targetToken)}: {Markup.Escape(presetName)} (missing)[/]");
                hasMissingPreset = true;
            }
            else if (!string.Equals(loadedPreset.Digest, entry.Value.Digest, StringComparison.Ordinal))
            {
                _output.Ansi.MarkupLine(
                    $"  [yellow]{Markup.Escape(targetToken)}: {Markup.Escape(presetName)} (changed since applied)[/]");
            }
            else
            {
                _output.Ansi.MarkupLine(
                    $"  [green]{Markup.Escape(targetToken)}: {Markup.Escape(presetName)}[/]");
            }
        }

        return (hasMissingPreset, false);
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
    private bool WriteRegisteredProjects(ISquadStateStore stateStore)
    {
        IReadOnlyList<SquadProjectRegistryEntry> projects;
        try
        {
            projects = stateStore.ListProjects();
        }
        catch (InvalidDataException ex)
        {
            _output.Ansi.WriteLine();
            _output.WriteLine(
                $"  [red]fail[/] Squad project registry is unreadable: {Markup.Escape(ex.Message)}");
            return false;
        }

        _output.Ansi.WriteLine();
        if (projects.Count == 0)
        {
            _output.WriteLine("[grey]No projects are registered.[/]");
            return true;
        }

        _output.WriteLine("Registered projects:");
        foreach (SquadProjectRegistryEntry project in projects)
        {
            string line = project.Stale
                ? $"  [yellow]stale[/] {Markup.Escape(project.Path)} (stale: {Markup.Escape(project.StaleReason ?? "unknown")})"
                : $"  [green]ok[/]    {Markup.Escape(project.Path)}";
            _output.WriteLine(line);
        }

        return true;
    }

    /// <summary>
    ///     Inspects and returns the deployed harness instances recorded in the instance state directory.
    /// </summary>
    private List<(string TargetPreset, string Version)> GetDeployedInstances(ISquadStateStore stateStore)
    {
        string home = stateStore.UserPaths.HomeDirectory;
        string instanceStateBase = UserStatePaths.SquadInstanceStateDirectory(home);

        List<(string TargetPreset, string Version)> instances = [];

        foreach (FileSystemEntry targetEntry in _files.EnumerateEntries(instanceStateBase))
        {
            if (!targetEntry.IsDirectory || targetEntry.IsLink) continue;

            string targetToken = Path.GetFileName(targetEntry.Path);

            foreach (FileSystemEntry presetEntry in _files.EnumerateEntries(targetEntry.Path))
            {
                if (!presetEntry.IsDirectory || presetEntry.IsLink) continue;

                string receiptPath = Path.Combine(presetEntry.Path, "squad.receipt.json");
                if (!_files.FileExists(receiptPath)) continue;

                string version = "unknown";
                string lockPath = Path.Combine(presetEntry.Path, "squad.lock.yml");
                if (_files.FileExists(lockPath))
                    try
                    {
                        SquadLock? squadLock = stateStore.DeserializeLock(_files.ReadAllText(lockPath));
                        if (!string.IsNullOrWhiteSpace(squadLock?.SquadVersion))
                            version = squadLock.SquadVersion;
                        else if (!string.IsNullOrWhiteSpace(squadLock?.CliVersion)) version = squadLock.CliVersion;
                    }
                    catch (Exception ex) when (FileFailure.Is(ex) || ex is InvalidDataException)
                    {
                        // Treat unreadable lock as unknown version
                    }

                instances.Add(($"{targetToken}/{Path.GetFileName(presetEntry.Path)}", version));
            }
        }

        return instances;
    }

    /// <summary>
    ///     Inspects and renders the deployed harness instances recorded in the instance state directory.
    /// </summary>
    private void WriteInstances(IReadOnlyList<(string TargetPreset, string Version)> instances)
    {
        _output.Ansi.WriteLine();
        _output.Ansi.MarkupLine("Instances:");

        if (instances.Count == 0)
        {
            _output.Ansi.MarkupLine("  [grey]none[/]");
            return;
        }

        foreach ((string targetPreset, string version) in instances.OrderBy(static i => i.TargetPreset,
                     StringComparer.Ordinal))
            _output.Ansi.MarkupLine($"  {Markup.Escape(targetPreset)}  {Markup.Escape(version)}");
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
