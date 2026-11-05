using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        if (TryResolveGlobalSettingsPath(parsed, out string settingsPath, _root))
        {
            RemoveOwnedGlobalBlock(parsed, settingsPath);
            return;
        }

        SquadReceipt? receipt = _stateStore.ReadReceipt(_root, SquadDeploymentScope.Project);
        RemoveOwnedFiles(parsed, receipt);
        RemoveOwnedBlocks(parsed, receipt);
    }

    internal static bool IsGlobalRoot(string root)
    {
        return string.Equals(Path.GetFileName(root), ".claude", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".codex", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".cursor", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".copilot", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".pi", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".config", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".kilo", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".factory", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".warp", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".zcode", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".devin", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".commandcode", StringComparison.Ordinal)
            || string.Equals(Path.GetFileName(root), ".gemini", StringComparison.Ordinal);
    }

    private static bool TryResolveGlobalSettingsPath(SquadTarget target, out string settingsPath, string? root = null)
    {
        settingsPath = string.Empty;
        if (target != SquadTarget.Claude)
            return false;

        string candidateRoot = Path.GetFullPath(root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (IsGlobalRoot(candidateRoot))
        {
            settingsPath = Path.Combine(candidateRoot, "settings.json");
            return true;
        }

        string claudeRoot = Path.Combine(candidateRoot, ".claude");
        if (Directory.Exists(claudeRoot))
        {
            settingsPath = Path.Combine(claudeRoot, "settings.json");
            return true;
        }

        return false;
    }

    private static void RemoveOwnedGlobalBlock(SquadTarget target, string settingsPath)
    {
        if (target != SquadTarget.Claude || !File.Exists(settingsPath))
            return;

        string before = File.ReadAllText(settingsPath);
        JsonNode? parsed = JsonNode.Parse(before);
        if (parsed is not JsonObject root)
            throw new InvalidOperationException($"Claude settings file '{settingsPath}' does not parse as a JSON object.");

        if (!root.ContainsKey("kyber-arbiter"))
            return;

        root.Remove("kyber-arbiter");
        string rendered = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(settingsPath, rendered);
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

        if (TryResolveGlobalSettingsPath(target, out string settingsPath, root))
        {
            if (!File.Exists(settingsPath))
                return false;

            try
            {
                JsonNode? parsed = JsonNode.Parse(File.ReadAllText(settingsPath));
                return parsed is JsonObject obj && obj.ContainsKey("kyber-arbiter");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return true;
            }
        }

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
