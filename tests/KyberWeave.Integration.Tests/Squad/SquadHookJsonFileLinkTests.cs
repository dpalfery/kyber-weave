using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Testing.Squad;
using Xunit;

namespace KyberWeave.Integration.Tests.Squad;

/// <summary>
/// Proves on the real disk that a hook file the user keeps behind a symbolic link is rewritten through the link: the
/// edit is a plain overwrite, so the link survives and the file it names is the one that changes.
/// </summary>
public sealed class SquadHookJsonFileLinkTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static JsonNode[] Entry() =>
    [
        new JsonObject
        {
            ["matcher"] = "m",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = "kyber-weave-arbiter hook --harness codex",
                ["timeout"] = 5,
            }),
        },
    ];

    [Fact]
    public void ASplicedHookFileBehindALinkStaysALinkAndItsTargetChanges()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Symbolic links need developer mode on Windows.");
        }

        string target = Path.Combine(_temp.Path, "dotfiles", "hooks.json");
        string link = Path.Combine(_temp.Path, "repo", ".codex", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.WriteAllText(target, "{}\n");
        File.CreateSymbolicLink(link, target);

        RealSquadServices.HookJsonFile().SpliceFile(SquadHookBlockFormat.Codex, link, Entry(), []);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Contains("kyber-weave-arbiter", File.ReadAllText(target), StringComparison.Ordinal);

        RealSquadServices.HookJsonFile().RemoveFile(SquadHookBlockFormat.Codex, link);

        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.DoesNotContain("kyber-weave-arbiter", File.ReadAllText(target), StringComparison.Ordinal);
    }

    [Fact]
    public void AHookFileOnTheRealDiskIsCreatedSplicedAndRestored()
    {
        string path = Path.Combine(_temp.Path, "fresh", ".codex", "hooks.json");

        RealSquadServices.HookJsonFile().SpliceFile(SquadHookBlockFormat.Codex, path, Entry(), []);
        Assert.Contains("kyber-weave-arbiter", File.ReadAllText(path), StringComparison.Ordinal);

        RealSquadServices.HookJsonFile().RemoveFile(SquadHookBlockFormat.Codex, path);
        Assert.DoesNotContain("kyber-weave-arbiter", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal([path], Directory.EnumerateFiles(Path.Combine(_temp.Path, "fresh"), "*", SearchOption.AllDirectories).ToArray());
    }
}
