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
            [SquadTarget.Cursor] = SquadHookBlockFormat.Cursor,
            [SquadTarget.Codex] = SquadHookBlockFormat.Codex,
            [SquadTarget.Factory] = SquadHookBlockFormat.Factory,
            [SquadTarget.Devin] = SquadHookBlockFormat.Devin,
            [SquadTarget.Antigravity] = SquadHookBlockFormat.Antigravity,
        };

    private readonly string _root;

    internal ArbiterUninstaller(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    /// <inheritdoc />
    public void Uninstall(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (!SquadTargetCatalog.TryResolve(target, out SquadTarget parsed))
            throw new ArgumentException($"Unknown target '{target}'.", nameof(target));

        RemoveOwnedFiles(parsed);
        RemoveOwnedBlocks(parsed);
    }

    /// <summary>
    /// Whether any arbiter-owned artifact for <paramref name="target"/> exists beneath
    /// <paramref name="root"/>: an owned file on disk, or a shared hook file still carrying an
    /// owned block entry. An unparsable hook file counts as present so it fails honestly in
    /// <see cref="Uninstall"/> instead of reading as "nothing to uninstall".
    /// </summary>
    internal static bool HasDeployment(string root, SquadTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string full = Path.GetFullPath(root);
        if (OwnedFiles.TryGetValue(target, out string[]? files)
            && files.Any(relative => File.Exists(Path.Combine(full, relative))))
        {
            return true;
        }

        if (!OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat format))
            return false;

        string path = Path.Combine(full, SquadHookJsonBlock.RelativePath(format));
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

    private void RemoveOwnedFiles(SquadTarget target)
    {
        if (!OwnedFiles.TryGetValue(target, out string[]? files))
            return;

        foreach (string relative in files)
        {
            string path = Path.Combine(_root, relative);
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private void RemoveOwnedBlocks(SquadTarget target)
    {
        if (!OwnedBlocks.TryGetValue(target, out SquadHookBlockFormat format))
            return;

        string path = Path.Combine(_root, SquadHookJsonBlock.RelativePath(format));
        if (File.Exists(path))
            SquadHookJsonBlock.RemoveFile(format, path);
    }
}
