using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// Default arbiter uninstaller: deletes the renderers' arbiter-exclusive files and scrubs their
/// owned hook blocks beneath one deployment root. Shared files are never deleted, only scrubbed;
/// agent frontmatter, MCP grants, and every non-arbiter entry stay byte-identical.
/// </summary>
internal sealed class ArbiterUninstaller : IArbiterUninstaller
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
            [SquadTarget.Claude] = SquadHookBlockFormat.Cursor,
            [SquadTarget.Cursor] = SquadHookBlockFormat.Cursor,
            [SquadTarget.Codex] = SquadHookBlockFormat.Codex,
            [SquadTarget.Factory] = SquadHookBlockFormat.Factory,
            [SquadTarget.Devin] = SquadHookBlockFormat.Devin,
            [SquadTarget.Antigravity] = SquadHookBlockFormat.Antigravity,
        };

    private readonly string _root;
    private readonly SquadDeploymentScope _scope;
    private readonly SquadStateStore _stateStore;
    private readonly ISquadGlobalRootResolver _globalRoots;

    internal ArbiterUninstaller(string root)
        : this(root, SquadDeploymentScope.Project, SquadCommandComposition.ResolveStateStore(null, ArbiterCommandComposition.GetEnvironmentVariable))
    {
    }

    internal ArbiterUninstaller(string root, SquadStateStore stateStore)
        : this(root, SquadDeploymentScope.Project, stateStore)
    {
    }

    internal ArbiterUninstaller(
        string root,
        SquadDeploymentScope scope,
        SquadStateStore stateStore,
        ISquadGlobalRootResolver? globalRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(stateStore);
        _root = Path.GetFullPath(root);
        _scope = scope;
        _stateStore = stateStore;
        _globalRoots = globalRoots ?? SquadCommandComposition.ResolveGlobalRoots(ArbiterCommandComposition.GetEnvironmentVariable);
    }

    /// <inheritdoc />
    public void Uninstall(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!SquadTargetCatalog.TryResolve(target, out SquadTarget parsed))
            throw new ArgumentException($"Unknown target '{target}'.", nameof(target));

        string targetToken = SquadTargetCatalog.GetToken(parsed);

        if (_scope == SquadDeploymentScope.Global)
        {
            UninstallGlobal(parsed, targetToken);
            return;
        }

        UninstallProject(parsed, targetToken);
    }

    private void UninstallGlobal(SquadTarget target, string targetToken)
    {
        SquadReceipt? receipt = _stateStore.ReadReceipt(_root, SquadDeploymentScope.Global);
        if (receipt is not null)
        {
            foreach (SquadOwnedFile file in receipt.Files)
            {
                if (!string.Equals(file.Target, targetToken, StringComparison.Ordinal))
                    continue;

                string path = ResolveGlobalFilePath(target, file.RelativePath);
                if (File.Exists(path))
                    File.Delete(path);
            }

            foreach (SquadOwnedBlock block in receipt.Blocks)
            {
                if (!string.Equals(block.Target, targetToken, StringComparison.Ordinal))
                    continue;

                string path = ResolveGlobalBlockPath(target, block.RelativePath);
                if (File.Exists(path))
                {
                    SquadHookBlockFormat blockFormat = target == SquadTarget.Claude
                        ? SquadHookBlockFormat.Cursor
                        : (OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat fmt) ? fmt : SquadHookBlockFormat.Cursor);
                    SquadHookJsonBlock.RemoveFile(blockFormat, path);
                }
            }

            List<SquadOwnedFile> remainingFiles = receipt.Files
                .Where(file => !string.Equals(file.Target, targetToken, StringComparison.Ordinal))
                .ToList();
            List<SquadOwnedBlock> remainingBlocks = receipt.Blocks
                .Where(block => !string.Equals(block.Target, targetToken, StringComparison.Ordinal))
                .ToList();

            string receiptPath = _stateStore.ResolveReceiptPath(_root, SquadDeploymentScope.Global);
            if (remainingFiles.Count == 0 && remainingBlocks.Count == 0)
            {
                if (File.Exists(receiptPath))
                    File.Delete(receiptPath);
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
                File.WriteAllText(receiptPath, _stateStore.SerializeReceipt(updated));
            }

            return;
        }

        // Unreceipted fallback:
        if (target == SquadTarget.Claude)
        {
            string settingsPath = ResolveGlobalBlockPath(SquadTarget.Claude, "settings.json");
            if (File.Exists(settingsPath))
                SquadHookJsonBlock.RemoveFile(SquadHookBlockFormat.Cursor, settingsPath);
            return;
        }

        if (OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat format))
        {
            string blockPath = ResolveGlobalBlockPath(target, SquadHookJsonBlock.RelativePath(format));
            if (File.Exists(blockPath))
                SquadHookJsonBlock.RemoveFile(format, blockPath);
            return;
        }

        if (OwnedFiles.TryGetValue(target, out string[]? files))
        {
            foreach (string rel in files)
            {
                string filePath = ResolveGlobalFilePath(target, rel);
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
        }
    }

    private void UninstallProject(SquadTarget target, string targetToken)
    {
        SquadReceipt? receipt = _stateStore.ReadReceipt(_root, SquadDeploymentScope.Project);
        RemoveOwnedFiles(target, receipt);
        RemoveOwnedBlocks(target, receipt);

        if (receipt is not null)
        {
            List<SquadOwnedFile> remainingFiles = receipt.Files
                .Where(file => !string.Equals(file.Target, targetToken, StringComparison.Ordinal))
                .ToList();
            List<SquadOwnedBlock> remainingBlocks = receipt.Blocks
                .Where(block => !string.Equals(block.Target, targetToken, StringComparison.Ordinal))
                .ToList();

            string receiptPath = _stateStore.ResolveReceiptPath(_root, SquadDeploymentScope.Project);
            if (remainingFiles.Count == 0 && remainingBlocks.Count == 0)
            {
                if (File.Exists(receiptPath))
                    File.Delete(receiptPath);
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
                File.WriteAllText(receiptPath, _stateStore.SerializeReceipt(updated));
            }
        }
    }

    private string ResolveGlobalFilePath(SquadTarget target, string relativePath)
    {
        string targetRoot;
        try
        {
            targetRoot = _globalRoots.ResolveGlobalRoot(target);
        }
        catch (ArgumentException)
        {
            targetRoot = _root;
        }

        string candidate1 = Path.Combine(targetRoot, relativePath);
        if (File.Exists(candidate1))
            return candidate1;

        string candidate2 = Path.Combine(_root, relativePath);
        if (File.Exists(candidate2))
            return candidate2;

        return candidate1;
    }

    private string ResolveGlobalBlockPath(SquadTarget target, string relativePath)
    {
        string targetRoot;
        try
        {
            targetRoot = _globalRoots.ResolveGlobalRoot(target);
        }
        catch (ArgumentException)
        {
            targetRoot = _root;
        }

        string candidate1 = Path.Combine(targetRoot, relativePath);
        if (File.Exists(candidate1))
            return candidate1;

        string candidate2 = Path.Combine(_root, relativePath);
        if (File.Exists(candidate2))
            return candidate2;

        string token = SquadTargetCatalog.GetToken(target);
        if (relativePath.StartsWith("." + token + "/", StringComparison.OrdinalIgnoreCase))
        {
            string trimmedRel = relativePath[(token.Length + 2)..];
            string candidate3 = Path.Combine(targetRoot, trimmedRel);
            if (File.Exists(candidate3))
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
    internal static bool HasDeployment(
        string root,
        SquadTarget target,
        SquadStateStore stateStore,
        SquadDeploymentScope scope = SquadDeploymentScope.Global,
        ISquadGlobalRootResolver? globalRoots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(stateStore);
        string full = Path.GetFullPath(root);
        string token = SquadTargetCatalog.GetToken(target);
        globalRoots ??= SquadCommandComposition.ResolveGlobalRoots(ArbiterCommandComposition.GetEnvironmentVariable);

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
                if (!File.Exists(settingsPath))
                    return false;

                try
                {
                    string before = File.ReadAllText(settingsPath);
                    return !string.Equals(
                        SquadHookJsonBlock.NormalizeContent(before),
                        SquadHookJsonBlock.RemoveContent(SquadHookBlockFormat.Cursor, before),
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
                string path = File.Exists(candidate1) ? candidate1 : candidate2;
                if (!File.Exists(path) && relativePath.StartsWith("." + token + "/", StringComparison.OrdinalIgnoreCase))
                {
                    string candidate3 = Path.Combine(targetRoot, relativePath[(token.Length + 2)..]);
                    if (File.Exists(candidate3))
                        path = candidate3;
                }

                if (!File.Exists(path))
                    return false;

                try
                {
                    string before = File.ReadAllText(path);
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
                    File.Exists(Path.Combine(targetRoot, rel)) || File.Exists(Path.Combine(full, rel)));
            }

            return false;
        }

        // Project scope:
        SquadReceipt? receipt = stateStore.ReadReceipt(full, SquadDeploymentScope.Project);

        if (OwnedFiles.TryGetValue(target, out string[]? files)
            && files.Any(relative =>
                File.Exists(Path.Combine(full, relative))
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
        if (!File.Exists(projPath))
            return false;

        try
        {
            string before = File.ReadAllText(projPath);
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

    private void RemoveOwnedFiles(SquadTarget target, SquadReceipt? receipt)
    {
        if (!OwnedFiles.TryGetValue(target, out string[]? files))
            return;

        string token = SquadTargetCatalog.GetToken(target);
        foreach (string relative in files)
        {
            if (receipt is not null && !ReceiptOwnsFile(receipt, token, relative))
                continue;

            string path = Path.Combine(_root, relative);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private void RemoveOwnedBlocks(SquadTarget target, SquadReceipt? receipt)
    {
        if (!OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat format))
            return;

        string relative = SquadHookJsonBlock.RelativePath(format);
        if (receipt is not null && !ReceiptOwnsBlock(receipt, SquadTargetCatalog.GetToken(target), relative))
            return;

        string path = Path.Combine(_root, relative);
        if (File.Exists(path))
            SquadHookJsonBlock.RemoveFile(format, path);
    }
}
