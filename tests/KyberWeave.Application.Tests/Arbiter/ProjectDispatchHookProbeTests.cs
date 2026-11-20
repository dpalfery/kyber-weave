using KyberWeave.Core.Arbiter;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Which project-scope hook a project actually declares for one harness. The user-level hook asks this
/// before deferring a dispatch: a project with no hook of its own must not be deferred to, or the worker
/// starts with only the header lines. Only Antigravity and OpenCode are read; every other harness keeps
/// today's deferral.
/// </summary>
public sealed class ProjectDispatchHookProbeTests
{
    private const string AntigravityHookPath = ".agents/hooks.json";
    private const string OpenCodeShimPath = ".opencode/plugins/kyber-arbiter.ts";

    private readonly InMemoryFileSystem _files = new();
    private readonly ProjectDispatchHookProbe _probe;

    public ProjectDispatchHookProbeTests() => _probe = new ProjectDispatchHookProbe(_files);

    private string Root => Path.Combine(_files.Root, "repo");

    [Fact]
    public void Antigravity_WhenAProjectGroupCarriesTheDispatchHook_Covers()
    {
        Write(
            AntigravityHookPath,
            """
            {
              "kyber-arbiter": {
                "PreToolUse": [
                  {
                    "matcher": "^invoke_subagent$",
                    "hooks": [
                      { "type": "command", "command": "kyber-weave-arbiter hook --harness antigravity" }
                    ]
                  }
                ]
              }
            }
            """);

        Assert.True(_probe.CoversHarness(Root, "antigravity"));
    }

    [Fact]
    public void Antigravity_WhenTheOnlyGroupIsNotADispatchHook_DoesNotCover()
    {
        Write(
            AntigravityHookPath,
            """
            {
              "codegraph-index": {
                "SessionStart": [
                  {
                    "hooks": [
                      { "type": "command", "command": "codegraph index" }
                    ]
                  }
                ]
              }
            }
            """);

        Assert.False(_probe.CoversHarness(Root, "antigravity"));
    }

    [Fact]
    public void Antigravity_WhenTheCommandIsTheUserLevelHook_DoesNotCover()
    {
        Write(
            AntigravityHookPath,
            """
            {
              "kyber-arbiter": {
                "PreToolUse": [
                  {
                    "matcher": "^invoke_subagent$",
                    "hooks": [
                      { "type": "command", "command": "kyber-weave-arbiter hook --harness antigravity --scope global" }
                    ]
                  }
                ]
              }
            }
            """);

        Assert.False(_probe.CoversHarness(Root, "antigravity"));
    }

    [Fact]
    public void Antigravity_WhenTheFileIsAbsent_DoesNotCover() =>
        Assert.False(_probe.CoversHarness(Root, "antigravity"));

    [Fact]
    public void Antigravity_WhenTheFileIsNotJson_DoesNotCover()
    {
        Write(AntigravityHookPath, "not json");

        Assert.False(_probe.CoversHarness(Root, "antigravity"));
    }

    [Fact]
    public void OpenCode_WhenTheShimCarriesTheProjectHook_Covers()
    {
        Write(
            OpenCodeShimPath,
            """
            const command = "kyber-weave-arbiter";
            export const KyberArbiter = async () => command;
            """);

        Assert.True(_probe.CoversHarness(Root, "opencode"));
    }

    [Fact]
    public void OpenCode_WhenTheShimIsTheUserLevelHook_DoesNotCover()
    {
        Write(
            OpenCodeShimPath,
            """
            const command = ["kyber-weave-arbiter", "--scope", "global"];
            export const KyberArbiter = async () => command;
            """);

        Assert.False(_probe.CoversHarness(Root, "opencode"));
    }

    [Fact]
    public void OpenCode_WhenTheShimIsAbsent_DoesNotCover() =>
        Assert.False(_probe.CoversHarness(Root, "opencode"));

    [Theory]
    [InlineData("cursor")]
    [InlineData("claude")]
    public void AnotherHarness_KeepsTheDeferral(string harness) =>
        Assert.True(_probe.CoversHarness(Root, harness));

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath);
        string directory = Path.GetDirectoryName(path)!;
        _files.CreateDirectory(directory);
        _files.WriteAllText(path, content);
    }
}
