using System.Text.Json;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// Default arbiter uninstaller: deletes the renderers' arbiter-exclusive files and scrubs their
/// owned hook blocks beneath one deployment root. Shared files are never deleted, only scrubbed;
/// agent frontmatter, MCP grants, and every non-arbiter entry stay byte-identical.
/// </summary>
public sealed class ArbiterUninstaller : IArbiterUninstaller
{
    private static readonly IReadOnlyDictionary<SquadTarget, string[]> OwnedFiles =
        new Dictionary<SquadTarget, string[]>
        {
            [SquadTarget.Kilo] = [".kilo/plugin/kyber-arbiter.ts"],
            [SquadTarget.OpenCode] = [".opencode/plugins/kyber-arbiter.ts"],
            [SquadTarget.Pi] = [".pi/extensions/kyber-arbiter.ts"],
            [SquadTarget.Copilot] = [".github/hooks/kyber-arbiter.json"],
        };

    private static readonly IReadOnlyDictionary<SquadTarget, SquadHookBlockFormat> OwnedBlocks =
        new Dictionary<SquadTarget, SquadHookBlockFormat>
        {
            [SquadTarget.Claude] = SquadHookBlockFormat.Claude,
            [SquadTarget.Cursor] = SquadHookBlockFormat.Cursor,
            [SquadTarget.Codex] = SquadHookBlockFormat.Codex,
            [SquadTarget.Factory] = SquadHookBlockFormat.Factory,
            [SquadTarget.Devin] = SquadHookBlockFormat.Devin,
            [SquadTarget.Antigravity] = SquadHookBlockFormat.Antigravity,
        };

    private readonly ISquadStateStore _stateStore;
    private readonly ISquadGlobalRootResolver _globalRoots;
    private readonly IWritableFileSystem _files;
    private readonly IUserHostConfigStore _userConfig;
    private readonly IProcessEnvironment _environment;
    private readonly IPathResolver _paths;

    /// <summary>Creates the uninstaller over the registered Squad state, global roots, user config and file system.</summary>
    /// <param name="stateStore">Reads and writes the deployment receipts.</param>
    /// <param name="globalRoots">Resolves each harness's global root.</param>
    /// <param name="files">Probes, reads, writes and deletes the owned files.</param>
    /// <param name="userConfig">Reads the global targets the user config lists.</param>
    /// <param name="environment">Supplies the home directory.</param>
    /// <param name="paths">Turns the caller's root into a full path.</param>
    public ArbiterUninstaller(
        ISquadStateStore stateStore,
        ISquadGlobalRootResolver globalRoots,
        IWritableFileSystem files,
        IUserHostConfigStore userConfig,
        IProcessEnvironment environment,
        IPathResolver paths)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(globalRoots);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(userConfig);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(paths);
        _stateStore = stateStore;
        _globalRoots = globalRoots;
        _files = files;
        _userConfig = userConfig;
        _environment = environment;
        _paths = paths;
    }

    /// <inheritdoc />
    public void Uninstall(string root, SquadDeploymentScope scope, string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        string fullRoot = _paths.GetFullPath(root, _environment.CurrentDirectory);
        if (!SquadTargetCatalog.TryResolve(target, out SquadTarget parsed))
            throw new ArgumentException($"Unknown target '{target}'.", nameof(target));

        string targetToken = SquadTargetCatalog.GetToken(parsed);

        if (scope == SquadDeploymentScope.Global)
        {
            UninstallGlobal(fullRoot, parsed, targetToken);
            return;
        }

        UninstallProject(fullRoot, parsed, targetToken);
    }

    private void UninstallGlobal(string root, SquadTarget target, string targetToken)
    {
        SquadReceipt? receipt = _stateStore.ReadReceipt(root, SquadDeploymentScope.Global);
        if (receipt is not null)
        {
            foreach (SquadOwnedFile file in receipt.Files)
            {
                if (!string.Equals(file.Target, targetToken, StringComparison.Ordinal))
                    continue;

                string path = ResolveGlobalFilePath(root, target, file.RelativePath);
                if (_files.FileExists(path))
                    _files.DeleteFile(path);
            }

            foreach (SquadOwnedBlock block in receipt.Blocks)
            {
                if (!string.Equals(block.Target, targetToken, StringComparison.Ordinal))
                    continue;

                string path = ResolveGlobalBlockPath(root, target, block.RelativePath);
                if (_files.FileExists(path))
                {
                    SquadHookBlockFormat blockFormat = OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat fmt)
                        ? fmt
                        : SquadHookBlockFormat.Claude;
                    RemoveHookBlocks(blockFormat, path);
                }
            }

            List<SquadOwnedFile> remainingFiles = receipt.Files
                .Where(file => !string.Equals(file.Target, targetToken, StringComparison.Ordinal))
                .ToList();
            List<SquadOwnedBlock> remainingBlocks = receipt.Blocks
                .Where(block => !string.Equals(block.Target, targetToken, StringComparison.Ordinal))
                .ToList();

            string receiptPath = _stateStore.ResolveReceiptPath(root, SquadDeploymentScope.Global);
            if (remainingFiles.Count == 0 && remainingBlocks.Count == 0)
            {
                if (_files.FileExists(receiptPath))
                    _files.DeleteFile(receiptPath);
            }
            else
            {
                SquadReceipt updated = new(
                    receipt.Schema,
                    receipt.Scope,
                    receipt.TargetRoot,
                    receipt.InstalledAtUtc,
                    receipt.Degradations,
                    remainingFiles)
                {
                    Layout = receipt.Layout,
                    Blocks = remainingBlocks,
                    HomeFiles = receipt.HomeFiles
                };
                _files.WriteAllText(receiptPath, _stateStore.SerializeReceipt(updated));
            }

            return;
        }

        // Unreceipted fallback:
        if (target == SquadTarget.Claude)
        {
            string settingsPath = ResolveGlobalBlockPath(root, SquadTarget.Claude, "settings.json");
            if (_files.FileExists(settingsPath))
                RemoveHookBlocks(SquadHookBlockFormat.Claude, settingsPath);
            return;
        }

        if (OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat format))
        {
            string blockPath = ResolveGlobalBlockPath(root, target, SquadHookJsonBlock.RelativePath(format));
            if (_files.FileExists(blockPath))
                RemoveHookBlocks(format, blockPath);
            return;
        }

        if (OwnedFiles.TryGetValue(target, out string[]? files))
        {
            foreach (string rel in files)
            {
                string filePath = ResolveGlobalFilePath(root, target, rel);
                if (_files.FileExists(filePath))
                    _files.DeleteFile(filePath);
            }
        }
    }

    private void UninstallProject(string root, SquadTarget target, string targetToken)
    {
        SquadReceipt? receipt = _stateStore.ReadReceipt(root, SquadDeploymentScope.Project);
        RemoveOwnedFiles(root, target, receipt);
        RemoveOwnedBlocks(root, target, receipt);

        if (receipt is not null)
        {
            List<SquadOwnedFile> remainingFiles = receipt.Files
                .Where(file => !string.Equals(file.Target, targetToken, StringComparison.Ordinal))
                .ToList();
            List<SquadOwnedBlock> remainingBlocks = receipt.Blocks
                .Where(block => !string.Equals(block.Target, targetToken, StringComparison.Ordinal))
                .ToList();

            string receiptPath = _stateStore.ResolveReceiptPath(root, SquadDeploymentScope.Project);
            if (remainingFiles.Count == 0 && remainingBlocks.Count == 0)
            {
                if (_files.FileExists(receiptPath))
                    _files.DeleteFile(receiptPath);
            }
            else
            {
                SquadReceipt updated = new(
                    receipt.Schema,
                    receipt.Scope,
                    receipt.TargetRoot,
                    receipt.InstalledAtUtc,
                    receipt.Degradations,
                    remainingFiles)
                {
                    Layout = receipt.Layout,
                    Blocks = remainingBlocks,
                    HomeFiles = receipt.HomeFiles
                };
                _files.WriteAllText(receiptPath, _stateStore.SerializeReceipt(updated));
            }
        }
    }

    private string ResolveGlobalFilePath(string root, SquadTarget target, string relativePath)
    {
        string targetRoot;
        try
        {
            targetRoot = _globalRoots.ResolveGlobalRoot(target);
        }
        catch (ArgumentException)
        {
            targetRoot = root;
        }

        string token = SquadTargetCatalog.GetToken(target);
        string normalizedRel = relativePath.StartsWith("." + token + "/", StringComparison.OrdinalIgnoreCase)
            ? relativePath[(token.Length + 2)..]
            : relativePath;

        string candidate1 = Path.Combine(targetRoot, normalizedRel);
        if (_files.FileExists(candidate1))
            return candidate1;

        string candidate2 = Path.Combine(root, relativePath);
        if (_files.FileExists(candidate2))
            return candidate2;

        return candidate1;
    }

    private string ResolveGlobalBlockPath(string root, SquadTarget target, string relativePath)
    {
        string targetRoot;
        try
        {
            targetRoot = _globalRoots.ResolveGlobalRoot(target);
        }
        catch (ArgumentException)
        {
            targetRoot = root;
        }

        string candidate1 = Path.Combine(targetRoot, relativePath);
        if (_files.FileExists(candidate1))
            return candidate1;

        string candidate2 = Path.Combine(root, relativePath);
        if (_files.FileExists(candidate2))
            return candidate2;

        string token = SquadTargetCatalog.GetToken(target);
        if (relativePath.StartsWith("." + token + "/", StringComparison.OrdinalIgnoreCase))
        {
            string trimmedRel = relativePath[(token.Length + 2)..];
            string candidate3 = Path.Combine(targetRoot, trimmedRel);
            if (_files.FileExists(candidate3))
                return candidate3;
        }

        return candidate1;
    }

    /// <summary>
    /// Whether any arbiter-owned artifact for <paramref name="target"/> exists beneath
    /// <paramref name="root"/>: a receipt-owned file on disk, or a shared hook file still carrying
    /// a receipt-owned block entry. An unparsable hook file counts as present so it fails honestly
    /// in <see cref="Uninstall"/> instead of reading as "nothing to uninstall". An unreadable
    /// receipt propagates for the same reason rather than reading as absent.
    /// </summary>
    /// <inheritdoc cref="IArbiterUninstaller.HasDeployment" />
    public bool HasDeployment(string root, SquadTarget target, SquadDeploymentScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ISquadStateStore stateStore = _stateStore;
        ISquadGlobalRootResolver globalRoots = _globalRoots;
        string full = _paths.GetFullPath(root, _environment.CurrentDirectory);
        string token = SquadTargetCatalog.GetToken(target);

        if (scope == SquadDeploymentScope.Global)
        {
            SquadReceipt? globalReceipt = stateStore.ReadReceipt(full, SquadDeploymentScope.Global);
            if (globalReceipt is not null)
            {
                return globalReceipt.Files.Any(file => string.Equals(file.Target, token, StringComparison.Ordinal))
                       || globalReceipt.Blocks.Any(block => string.Equals(block.Target, token, StringComparison.Ordinal));
            }

            if (target == SquadTarget.Claude)
            {
                string claudeRoot;
                try
                {
                    claudeRoot = globalRoots.ResolveGlobalRoot(SquadTarget.Claude);
                }
                catch (ArgumentException)
                {
                    claudeRoot = Path.Combine(full, ".claude");
                }

                string settingsPath = Path.Combine(claudeRoot, "settings.json");
                if (!_files.FileExists(settingsPath))
                    return false;

                try
                {
                    string before = _files.ReadAllText(settingsPath);
                    return !string.Equals(
                        SquadHookJsonBlock.NormalizeContent(before),
                        SquadHookJsonBlock.RemoveContent(SquadHookBlockFormat.Claude, before),
                        StringComparison.Ordinal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    return true;
                }
            }

            if (OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat format))
            {
                string targetRoot;
                try
                {
                    targetRoot = globalRoots.ResolveGlobalRoot(target);
                }
                catch (ArgumentException)
                {
                    targetRoot = full;
                }

                string relativePath = SquadHookJsonBlock.RelativePath(format);
                string candidate1 = Path.Combine(targetRoot, relativePath);
                string candidate2 = Path.Combine(full, relativePath);
                string path = _files.FileExists(candidate1) ? candidate1 : candidate2;
                if (!_files.FileExists(path) && relativePath.StartsWith("." + token + "/", StringComparison.OrdinalIgnoreCase))
                {
                    string candidate3 = Path.Combine(targetRoot, relativePath[(token.Length + 2)..]);
                    if (_files.FileExists(candidate3))
                        path = candidate3;
                }

                if (!_files.FileExists(path))
                    return false;

                try
                {
                    string before = _files.ReadAllText(path);
                    return !string.Equals(
                        SquadHookJsonBlock.NormalizeContent(before),
                        SquadHookJsonBlock.RemoveContent(format, before),
                        StringComparison.Ordinal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    return true;
                }
            }

            if (OwnedFiles.TryGetValue(target, out string[]? globalFiles))
            {
                string targetRoot;
                try
                {
                    targetRoot = globalRoots.ResolveGlobalRoot(target);
                }
                catch (ArgumentException)
                {
                    targetRoot = full;
                }

                return globalFiles.Any(rel =>
                    _files.FileExists(Path.Combine(targetRoot, rel)) || _files.FileExists(Path.Combine(full, rel)));
            }

            return false;
        }

        // Project scope:
        SquadReceipt? receipt = stateStore.ReadReceipt(full, SquadDeploymentScope.Project);

        if (OwnedFiles.TryGetValue(target, out string[]? files)
            && files.Any(relative =>
                _files.FileExists(Path.Combine(full, relative))
                && (receipt is null || ReceiptOwnsFile(receipt, token, relative))))
        {
            return true;
        }

        if (!OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat projFormat))
            return false;

        string projRelativePath = SquadHookJsonBlock.RelativePath(projFormat);
        if (receipt is not null && !ReceiptOwnsBlock(receipt, token, projRelativePath))
            return false;

        string projPath = Path.Combine(full, projRelativePath);
        if (!_files.FileExists(projPath))
            return false;

        try
        {
            string before = _files.ReadAllText(projPath);
            return !string.Equals(
                SquadHookJsonBlock.NormalizeContent(before),
                SquadHookJsonBlock.RemoveContent(projFormat, before),
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or InvalidOperationException)
        {
            return true;
        }
    }

    private static bool ReceiptOwnsFile(SquadReceipt receipt, string targetToken, string relativePath)
    {
        return receipt.Files.Any(file =>
            string.Equals(file.RelativePath, relativePath, StringComparison.Ordinal)
            && string.Equals(file.Target, targetToken, StringComparison.Ordinal));
    }

    private static bool ReceiptOwnsBlock(SquadReceipt receipt, string targetToken, string relativePath)
    {
        return receipt.Blocks.Any(block =>
            string.Equals(block.RelativePath, relativePath, StringComparison.Ordinal)
            && string.Equals(block.Target, targetToken, StringComparison.Ordinal));
    }

    private void RemoveOwnedFiles(string root, SquadTarget target, SquadReceipt? receipt)
    {
        if (!OwnedFiles.TryGetValue(target, out string[]? files))
            return;

        string token = SquadTargetCatalog.GetToken(target);
        foreach (string relative in files)
        {
            if (receipt is not null && !ReceiptOwnsFile(receipt, token, relative))
                continue;

            string path = Path.Combine(root, relative);
            if (_files.FileExists(path))
                _files.DeleteFile(path);
        }
    }

    private void RemoveOwnedBlocks(string root, SquadTarget target, SquadReceipt? receipt)
    {
        if (!OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat format))
            return;

        string relative = SquadHookJsonBlock.RelativePath(format);
        if (receipt is not null && !ReceiptOwnsBlock(receipt, SquadTargetCatalog.GetToken(target), relative))
            return;

        string path = Path.Combine(root, relative);
        if (_files.FileExists(path))
            RemoveHookBlocks(format, path);
    }

    /// <inheritdoc />
    public bool AnotherTargetIsDeployed(string root, SquadTarget target)
    {
        string token = SquadTargetCatalog.GetToken(target);
        string home = _environment.HomeDirectory;
        ISquadStateStore stateStore = _stateStore;

        try
        {
            IReadOnlyList<string> globalTargets = _userConfig.ReadGlobalTargets(home);
            if (globalTargets.Any(t => !string.Equals(t, token, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            SquadReceipt? globalReceipt = stateStore.ReadReceipt(home, SquadDeploymentScope.Global);
            if (globalReceipt is not null &&
                (globalReceipt.Files.Any(file => !string.Equals(file.Target, token, StringComparison.Ordinal))
                 || globalReceipt.Blocks.Any(block => !string.Equals(block.Target, token, StringComparison.Ordinal))))
            {
                return true;
            }

            SquadReceipt? currentProjectReceipt = stateStore.ReadReceipt(root, SquadDeploymentScope.Project);
            if (currentProjectReceipt is not null &&
                (currentProjectReceipt.Files.Any(file => !string.Equals(file.Target, token, StringComparison.Ordinal))
                 || currentProjectReceipt.Blocks.Any(block => !string.Equals(block.Target, token, StringComparison.Ordinal))))
            {
                return true;
            }

            IReadOnlyList<SquadProjectRegistryEntry> projects = stateStore.ListProjects();
            foreach (SquadProjectRegistryEntry project in projects)
            {
                SquadReceipt? projReceipt = stateStore.ReadReceipt(project.Path, SquadDeploymentScope.Project);
                if (projReceipt is null)
                {
                    continue;
                }

                bool isCurrent = string.Equals(
                    _paths.GetFullPath(project.Path, _environment.CurrentDirectory),
                    _paths.GetFullPath(root, _environment.CurrentDirectory),
                    StringComparison.OrdinalIgnoreCase);

                if (isCurrent)
                {
                    if (projReceipt.Files.Any(f => !string.Equals(f.Target, token, StringComparison.Ordinal))
                        || projReceipt.Blocks.Any(b => !string.Equals(b.Target, token, StringComparison.Ordinal)))
                    {
                        return true;
                    }
                }
                else
                {
                    if (projReceipt.Files.Count > 0 || projReceipt.Blocks.Count > 0)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or InvalidDataException
                                       or InvalidOperationException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Scrubs the Arbiter's owned hook entries from a shared hook file. A missing file needs no work; an unparsable file fails and is left untouched.</summary>
    private void RemoveHookBlocks(SquadHookBlockFormat format, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!_files.FileExists(filePath))
        {
            return;
        }

        string existing = _files.ReadAllText(filePath);
        string content;
        try
        {
            content = SquadHookJsonBlock.RemoveContent(format, existing);
        }
        catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Squad hook removal failed: '{filePath}' does not parse as a hook file for {SquadHookJsonBlock.Harness(format)}.",
                ex);
        }

        _files.WriteAllText(filePath, content);
    }
}
