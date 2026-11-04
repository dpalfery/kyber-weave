using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Cli.Commands.Arbiter;

/// <summary>
/// Default arbiter uninstaller: deletes the renderers' arbiter-exclusive files and scrubs their
/// owned hook blocks beneath one deployment root. Shared files are never deleted, only scrubbed;
/// agent frontmatter, MCP grants, and every non-arbiter entry stay byte-identical.
/// </summary>
/// <remarks>
/// The static tables are the arbiter-exclusive subset of a deployment. When the project has a
/// Squad receipt, that receipt is authoritative: an artifact is touched only when the receipt
/// records it for the target, so an operator file at the same path survives. A root without a
/// receipt keeps the historical path-only behavior, but an unreadable receipt is an error rather
/// than an absent one: falling back to a path-only delete would remove an operator's file at the
/// same path on the strength of a receipt the tool could not read.
/// </remarks>
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
            [SquadTarget.Cursor] = SquadHookBlockFormat.Cursor,
            [SquadTarget.Codex] = SquadHookBlockFormat.Codex,
            [SquadTarget.Factory] = SquadHookBlockFormat.Factory,
            [SquadTarget.Devin] = SquadHookBlockFormat.Devin,
            [SquadTarget.Antigravity] = SquadHookBlockFormat.Antigravity,
        };

    private readonly string _root;
    private readonly SquadStateStore _stateStore;

    internal ArbiterUninstaller(string root)
        : this(root, SquadCommandComposition.ResolveStateStore(null, Environment.GetEnvironmentVariable))
    {
    }

    internal ArbiterUninstaller(string root, SquadStateStore stateStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(stateStore);
        _root = Path.GetFullPath(root);
        _stateStore = stateStore;
    }

    /// <inheritdoc />
    public void Uninstall(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!SquadTargetCatalog.TryResolve(target, out SquadTarget parsed))
            throw new ArgumentException($"Unknown target '{target}'.", nameof(target));

        SquadReceipt? receipt = _stateStore.ReadReceipt(_root, SquadDeploymentScope.Project);
        RemoveOwnedFiles(parsed, receipt);
        RemoveOwnedBlocks(parsed, receipt);
    }

    /// <summary>
    /// Whether any arbiter-owned artifact for <paramref name="target"/> exists beneath
    /// <paramref name="root"/>: a receipt-owned file on disk, or a shared hook file still carrying
    /// a receipt-owned block entry. An unparsable hook file counts as present so it fails honestly
    /// in <see cref="Uninstall"/> instead of reading as "nothing to uninstall". An unreadable
    /// receipt propagates for the same reason rather than reading as absent.
    /// </summary>
    internal static bool HasDeployment(string root, SquadTarget target, SquadStateStore stateStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(stateStore);
        string full = Path.GetFullPath(root);
        string token = SquadTargetCatalog.GetToken(target);
        SquadReceipt? receipt = stateStore.ReadReceipt(full, SquadDeploymentScope.Project);

        if (OwnedFiles.TryGetValue(target, out string[]? files)
            && files.Any(relative =>
                File.Exists(Path.Combine(full, relative))
                && (receipt is null || ReceiptOwnsFile(receipt, token, relative))))
        {
            return true;
        }

        if (!OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat format))
            return false;

        string relativePath = SquadHookJsonBlock.RelativePath(format);
        if (receipt is not null && !ReceiptOwnsBlock(receipt, token, relativePath))
            return false;

        string path = Path.Combine(full, relativePath);
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
