using KyberWeave.Core.Arbiter;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Which project-scope hook a project actually declares for one harness. The user-level hook asks this
/// before deferring a dispatch: a project with no hook of its own must not be deferred to, or the worker
/// starts with only the header lines. Antigravity, Factory, Cursor, OpenCode and Pi are read; every other
/// harness keeps today's deferral.
/// </summary>
public sealed class ProjectDispatchHookProbeTests
{
    private const string AntigravityHookPath = ".agents/hooks.json";
    private const string FactoryHookPath = ".factory/hooks.json";
    private const string FactorySettingsPath = ".factory/settings.json";
    private const string CursorHookPath = ".cursor/hooks.json";
    private const string OpenCodeShimPath = ".opencode/plugins/kyber-arbiter.ts";
    private const string PiShimPath = ".pi/extensions/kyber-arbiter.ts";

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
    public void Antigravity_WhenTheFileCannotBeRead_SurfacesTheFailure()
    {
        Write(AntigravityHookPath, "{}");
        _files.FailWhen(
            operation => operation.Name == nameof(InMemoryFileSystem.ReadAllText),
            new UnauthorizedAccessException("denied"));

        UnauthorizedAccessException ex = Assert.Throws<UnauthorizedAccessException>(
            () => _probe.CoversHarness(Root, "antigravity"));
        Assert.Equal("denied", ex.Message);
    }

    [Fact]
    public void Antigravity_WhenTheFileIsNotJson_DoesNotCover()
    {
        Write(AntigravityHookPath, "not json");

        Assert.False(_probe.CoversHarness(Root, "antigravity"));
    }

    [Fact]
    public void Antigravity_WhenTheFileIsJsonc_Covers()
    {
        Write(
            AntigravityHookPath,
            """
            {
              // A comment must not hide the hook.
              "kyber-arbiter": {
                "PreToolUse": [
                  {
                    "matcher": "^invoke_subagent$",
                    "hooks": [
                      { "type": "command", "command": "kyber-weave-arbiter hook --harness antigravity" },
                    ],
                  },
                ],
              },
            }
            """);

        Assert.True(_probe.CoversHarness(Root, "antigravity"));
    }

    [Fact]
    public void Factory_WhenAPreToolUseGroupCarriesTheDispatchHook_Covers()
    {
        Write(FactoryHookPath, FactoryHooks(FactoryProjectHook));

        Assert.True(_probe.CoversHarness(Root, "factory"));
    }

    [Fact]
    public void Factory_WhenTheMatcherDoesNotMatchTheDispatchTool_DoesNotCover()
    {
        Write(FactoryHookPath, FactoryHooks(FactoryProjectHook, matcher: "^Bash$"));

        Assert.False(_probe.CoversHarness(Root, "factory"));
    }

    [Fact]
    public void Factory_WhenTheCommandIsTheUserLevelHook_DoesNotCover()
    {
        Write(FactoryHookPath, FactoryHooks(FactoryProjectHook + " --scope global"));

        Assert.False(_probe.CoversHarness(Root, "factory"));
    }

    [Fact]
    public void Factory_WhenTheFileIsAbsent_DoesNotCover() =>
        Assert.False(_probe.CoversHarness(Root, "factory"));

    [Fact]
    public void Factory_WhenTheFileIsNotJson_DoesNotCover()
    {
        Write(FactoryHookPath, "not json");

        Assert.False(_probe.CoversHarness(Root, "factory"));
    }

    [Fact]
    public void Factory_WhenTheFileIsJsonc_Covers()
    {
        Write(
            FactoryHookPath,
            """
            {
              // Factory tolerates JSONC, so a comment must not hide the hook.
              "PreToolUse": [
                {
                  "matcher": "Task",
                  "hooks": [
                    { "type": "command", "command": "kyber-weave-arbiter hook --harness factory" },
                  ],
                },
              ],
            }
            """);

        Assert.True(_probe.CoversHarness(Root, "factory"));
    }

    [Fact]
    public void Factory_WhenHooksJsonIsAbsent_ReadsTheSettingsHooksKey()
    {
        Write(FactorySettingsPath, $$"""{"hooks": {{FactoryHooks(FactoryProjectHook)}}}""");

        Assert.True(_probe.CoversHarness(Root, "factory"));
    }

    [Fact]
    public void Factory_WhenHooksJsonAndSettingsBothExist_HooksJsonWins()
    {
        // The settings file carries a hook, but Factory reads hooks.json and ignores the key,
        // so a hooks.json with no dispatch hook must not be covered.
        Write(FactoryHookPath, FactoryHooks(FactoryProjectHook, matcher: "^Bash$"));
        Write(FactorySettingsPath, $$"""{"hooks": {{FactoryHooks(FactoryProjectHook)}}}""");

        Assert.False(_probe.CoversHarness(Root, "factory"));
    }

    [Fact]
    public void Cursor_WhenAPreToolUseEntryCarriesTheDispatchHook_Covers()
    {
        Write(
            CursorHookPath,
            """
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  {
                    "command": "kyber-weave-arbiter hook --harness cursor",
                    "matcher": "Task"
                  }
                ]
              }
            }
            """);

        Assert.True(_probe.CoversHarness(Root, "cursor"));
    }

    [Fact]
    public void Cursor_WhenTheEntryHasNoMatcher_Covers()
    {
        Write(
            CursorHookPath,
            """
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  { "command": "kyber-weave-arbiter hook --harness cursor" }
                ]
              }
            }
            """);

        Assert.True(_probe.CoversHarness(Root, "cursor"));
    }

    [Fact]
    public void Cursor_WhenTheCommandIsTheUserLevelHook_DoesNotCover()
    {
        Write(
            CursorHookPath,
            """
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  {
                    "command": "kyber-weave-arbiter hook --harness cursor --scope global",
                    "matcher": "Task"
                  }
                ]
              }
            }
            """);

        Assert.False(_probe.CoversHarness(Root, "cursor"));
    }

    [Fact]
    public void Cursor_WhenTheMatcherDoesNotMatchTheDispatchTool_DoesNotCover()
    {
        Write(
            CursorHookPath,
            """
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  {
                    "command": "kyber-weave-arbiter hook --harness cursor",
                    "matcher": "^Bash$"
                  }
                ]
              }
            }
            """);

        Assert.False(_probe.CoversHarness(Root, "cursor"));
    }

    [Fact]
    public void Cursor_WhenOnlyASessionStartEntryExists_DoesNotCover()
    {
        Write(
            CursorHookPath,
            """
            {
              "version": 1,
              "hooks": {
                "sessionStart": [
                  { "command": "kyber-weave-arbiter hook --harness cursor" }
                ]
              }
            }
            """);

        Assert.False(_probe.CoversHarness(Root, "cursor"));
    }

    [Fact]
    public void Cursor_WhenTheFileIsAbsent_DoesNotCover() =>
        Assert.False(_probe.CoversHarness(Root, "cursor"));

    [Fact]
    public void Cursor_WhenTheFileIsNotJson_DoesNotCover()
    {
        Write(CursorHookPath, "not json");

        Assert.False(_probe.CoversHarness(Root, "cursor"));
    }

    [Fact]
    public void Cursor_WhenTheFileIsJsonc_Covers()
    {
        Write(
            CursorHookPath,
            """
            {
              // A comment must not hide the hook.
              "version": 1,
              "hooks": {
                "preToolUse": [
                  { "command": "kyber-weave-arbiter hook --harness cursor" },
                ],
              },
            }
            """);

        Assert.True(_probe.CoversHarness(Root, "cursor"));
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

    [Fact]
    public void Pi_WhenTheExtensionCarriesTheProjectHook_Covers()
    {
        Write(
            PiShimPath,
            """
            const command = "kyber-weave-arbiter";
            export const KyberArbiter = async () => command;
            """);

        Assert.True(_probe.CoversHarness(Root, "pi"));
    }

    [Fact]
    public void Pi_WhenTheExtensionIsTheUserLevelHook_DoesNotCover()
    {
        Write(
            PiShimPath,
            """
            const command = ["kyber-weave-arbiter", "--scope", "global"];
            export const KyberArbiter = async () => command;
            """);

        Assert.False(_probe.CoversHarness(Root, "pi"));
    }

    [Fact]
    public void Pi_WhenTheExtensionIsAbsent_DoesNotCover() =>
        Assert.False(_probe.CoversHarness(Root, "pi"));

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("devin")]
    public void AnotherHarness_KeepsTheDeferral(string harness) =>
        Assert.True(_probe.CoversHarness(Root, harness));

    private const string FactoryProjectHook = "kyber-weave-arbiter hook --harness factory";

    private static string FactoryHooks(string command, string matcher = "Task") =>
        $$"""
        {
          "PreToolUse": [
            {
              "matcher": "{{matcher}}",
              "hooks": [
                { "type": "command", "command": "{{command}}" }
              ]
            }
          ]
        }
        """;

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath);
        string directory = Path.GetDirectoryName(path)!;
        _files.CreateDirectory(directory);
        _files.WriteAllText(path, content);
    }
}
