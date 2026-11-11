using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the <c>worktree-index-hook</c> repository setting: on when absent, off when false, and a refusal naming the key
/// for any other value.
/// </summary>
/// <remarks>
/// The setting is strict on purpose. A value outside the two booleans fails config loading with the key and both
/// accepted values in the message, so an owner who writes <c>maybe</c> learns what to write instead of getting a hook
/// that silently picked a side.
/// </remarks>
public sealed class WorktreeIndexHookConfigTests : IDisposable
{
    private readonly TempDirectory _root = new();

    public void Dispose() => _root.Dispose();

    /// <summary>A repository that has never mentioned the key gets the index hook, so a new worktree is indexed by default.</summary>
    [Fact]
    public void AnAbsentWorktreeIndexHookKeyMeansOn()
    {
        WriteConfig("stop-on-tool-failure: true\n");

        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(_root.Path);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Config);
        Assert.True(result.Config.WorktreeIndexHook);
    }

    [Fact]
    public void WorktreeIndexHookFalseTurnsTheHookOff()
    {
        WriteConfig("stop-on-tool-failure: true\nworktree-index-hook: false\n");

        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(_root.Path);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Config);
        Assert.False(result.Config.WorktreeIndexHook);
    }

    /// <summary>
    /// A misspelt or unexpected value fails loading. The message names the key and both accepted values, so the operator
    /// can fix the file without reading the source.
    /// </summary>
    [Fact]
    public void WorktreeIndexHookMaybeFailsLoadingWithAMessageNamingTheKeyAndBothValues()
    {
        WriteConfig("worktree-index-hook: maybe\n");

        KyberWeaveConfigLoadResult result = KyberWeaveConfigLoader.TryLoad(_root.Path);

        Assert.False(result.Success);
        string error = result.Error ?? string.Empty;
        Assert.Contains("worktree-index-hook", error, StringComparison.Ordinal);
        Assert.Contains("true", error, StringComparison.Ordinal);
        Assert.Contains("false", error, StringComparison.Ordinal);
    }

    private void WriteConfig(string text)
    {
        Directory.CreateDirectory(Path.Combine(_root.Path, ".kyber-weave"));
        File.WriteAllText(Path.Combine(_root.Path, ".kyber-weave", "kyber-weave.yml"), text);
    }
}
