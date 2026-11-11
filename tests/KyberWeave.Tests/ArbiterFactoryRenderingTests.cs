using System.Text.Json.Nodes;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.Fixtures;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the Arbiter Factory owned block (task 16.6): when enabled at project scope the
/// <see cref="FactoryRenderer"/> returns a <c>factory</c> <see cref="SquadRenderedBlock"/>
/// for <c>.factory/hooks.json</c> holding <c>PreToolUse</c> and <c>PostToolUse</c> matcher
/// groups with matcher <c>Task</c> and one command hook carrying the wiring timeout; the
/// groups sit at the top level, with no <c>hooks</c> wrapper [F11]. When
/// <c>.factory/settings.json</c> keeps a <c>hooks</c> key and <c>.factory/hooks.json</c> is
/// absent, the lifecycle drops the Factory block and records <c>arbiter-not-enforced</c>
/// with <c>settings-hooks-shadowed</c>, naming the fix (R18). Nothing renders when the
/// Arbiter is disabled or the scope is Global (Req 6.3, 8.1, 8.2, 22.2).
/// </summary>
public sealed class ArbiterFactoryRenderingTests : IDisposable
{
    private const string BlockPath = ".factory/hooks.json";
    private const string ExpectedCommand = "kyber-weave-arbiter hook --harness factory";
    private const int HookTimeoutSeconds = 5;

    private readonly ArbiterSquadFixture _fixture = ArbiterSquadFixture.Create();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_ReturnsFactoryBlock()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.Equal("factory", block.Target);
        Assert.Equal(BlockPath, block.RelativePath);
        Assert.Equal(SquadHookBlockFormat.Factory, block.Format);
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_HoldsTopLevelPreAndPostTaskGroupsAndAStopGroup()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);

        // F11: the event names are the top-level keys of .factory/hooks.json (no `hooks`
        // wrapper), so each block entry is one matcher group keyed by its event container.
        Assert.Equal(
            ["PreToolUse", "PostToolUse", "SubagentStop"],
            block.Entries.Select(entry => entry.Container).ToArray());

        foreach (SquadRenderedBlockEntry entry in block.Entries)
        {
            JsonObject group = Assert.IsType<JsonObject>(entry.Entry);

            // SubagentStop is the one group with no matcher: Factory fires it on every stop, not
            // on a named tool. The key is omitted, not set to an empty string.
            bool isStop = entry.Container == "SubagentStop";
            string[] expectedKeys = isStop ? ["hooks"] : ["matcher", "hooks"];
            Assert.Equal(expectedKeys, group.Select(property => property.Key).ToArray());
            if (!isStop)
            {
                Assert.Equal("Task", group["matcher"]!.GetValue<string>());
            }

            JsonArray hooks = Assert.IsType<JsonArray>(group["hooks"]);
            JsonObject hook = Assert.IsType<JsonObject>(Assert.Single(hooks));
            Assert.Equal("command", hook["type"]!.GetValue<string>());
            Assert.Equal(ExpectedCommand, hook["command"]!.GetValue<string>());
            Assert.Equal(HookTimeoutSeconds, hook["timeout"]!.GetValue<int>());
        }
    }

    [Fact]
    public void SpliceContent_Factory_RegistersSubagentStopGroupWithNoMatcher()
    {
        (string content, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Factory,
            null,
            [SquadGroup("Task")],
            [SquadGroup("Task")],
            [SquadGroup(matcher: null)]);

        JsonObject root = Assert.IsType<JsonObject>(JsonNode.Parse(content));
        Assert.Equal(["PreToolUse", "PostToolUse", "SubagentStop"], root.Select(property => property.Key).ToArray());

        JsonObject stop = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(root["SubagentStop"])));
        Assert.Equal(["hooks"], stop.Select(property => property.Key).ToArray());
        JsonObject hook = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(stop["hooks"])));
        Assert.Equal(ExpectedCommand, hook["command"]!.GetValue<string>());
        Assert.Equal(HookTimeoutSeconds, hook["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void SpliceContent_Factory_ReinstallReplacesSquadStopEntryWithoutDuplicating()
    {
        (string first, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Factory, null, [SquadGroup("Task")], [SquadGroup("Task")], [SquadGroup(null)]);

        (string second, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Factory, first, [SquadGroup("Task")], [SquadGroup("Task")], [SquadGroup(null)]);

        Assert.Equal(first, second);
        Assert.Equal(1, SquadEntryCount(second, "PreToolUse"));
        Assert.Equal(1, SquadEntryCount(second, "PostToolUse"));
        Assert.Equal(1, SquadEntryCount(second, "SubagentStop"));
    }

    [Fact]
    public void SpliceContent_Factory_UpgradesTwoContainerFileAndKeepsTheUsersEntries()
    {
        // The shape an older Squad wrote: PreToolUse and PostToolUse only, the user's own Pre hook
        // beside Squad's. Upgrading adds the SubagentStop container and keeps every user entry.
        const string olderVersion = """
            {
              "PreToolUse": [
                { "matcher": "Read", "hooks": [ { "type": "command", "command": "user-own-linter" } ] },
                { "matcher": "Task", "hooks": [ { "type": "command", "command": "kyber-weave-arbiter hook --harness factory", "timeout": 5 } ] }
              ],
              "PostToolUse": [
                { "matcher": "Task", "hooks": [ { "type": "command", "command": "kyber-weave-arbiter hook --harness factory", "timeout": 5 } ] }
              ]
            }
            """;

        (string upgraded, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Factory,
            olderVersion,
            [SquadGroup("Task")],
            [SquadGroup("Task")],
            [SquadGroup(null)]);

        Assert.Contains("user-own-linter", upgraded, StringComparison.Ordinal);
        Assert.Equal(1, SquadEntryCount(upgraded, "PreToolUse"));
        Assert.Equal(1, SquadEntryCount(upgraded, "PostToolUse"));
        Assert.Equal(1, SquadEntryCount(upgraded, "SubagentStop"));
    }

    [Fact]
    public void SpliceContent_Factory_LeavesTheUsersOwnSubagentStopEntryAlone()
    {
        const string userStop = """
            {
              "SubagentStop": [
                { "hooks": [ { "type": "command", "command": "user-own-stop-notifier" } ] }
              ]
            }
            """;

        (string spliced, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Factory,
            userStop,
            [SquadGroup("Task")],
            [SquadGroup("Task")],
            [SquadGroup(null)]);

        JsonArray stop = Assert.IsType<JsonArray>(JsonNode.Parse(spliced)!["SubagentStop"]);
        Assert.Equal(2, stop.Count);
        Assert.Contains("user-own-stop-notifier", stop.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal(1, SquadEntryCount(spliced, "SubagentStop"));
    }

    [Fact]
    public void RemoveContent_Factory_RemovesSquadStopEntryAndKeepsTheUsersOwn()
    {
        const string userStop = """
            {
              "SubagentStop": [
                { "hooks": [ { "type": "command", "command": "user-own-stop-notifier" } ] }
              ]
            }
            """;
        (string installed, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Factory,
            userStop,
            [SquadGroup("Task")],
            [SquadGroup("Task")],
            [SquadGroup(null)]);

        string removed = SquadHookJsonBlock.RemoveContent(SquadHookBlockFormat.Factory, installed);

        Assert.DoesNotContain(ExpectedCommand, removed, StringComparison.Ordinal);
        Assert.Contains("user-own-stop-notifier", removed, StringComparison.Ordinal);
        Assert.Equal(0, SquadEntryCount(removed, "SubagentStop"));
        Assert.Equal(0, SquadEntryCount(removed, "PreToolUse"));
    }

    [Theory]
    [InlineData(SquadHookBlockFormat.Devin)]
    [InlineData(SquadHookBlockFormat.Cursor)]
    [InlineData(SquadHookBlockFormat.Codex)]
    [InlineData(SquadHookBlockFormat.Claude)]
    [InlineData(SquadHookBlockFormat.Antigravity)]
    public void SpliceContent_NonFactoryFormat_RefusesASubagentStopEntry(SquadHookBlockFormat format)
    {
        // Only Factory's file has this container. Any other shape receiving one is a caller bug,
        // refused rather than written under a key that harness never reads.
        Assert.Throws<InvalidOperationException>(() => SquadHookJsonBlock.SpliceContent(
            format,
            null,
            [SquadGroup("Task")],
            [SquadGroup("Task")],
            [SquadGroup(null)]));
    }

    [Fact]
    public void SpliceContent_Devin_OutputStaysByteIdenticalWithTwoContainers()
    {
        // Devin shares Factory's root-array handling. Its file must not gain a SubagentStop
        // container or change a byte: this literal is the pre-existing Devin output.
        (string content, _) = SquadHookJsonBlock.SpliceContent(
            SquadHookBlockFormat.Devin,
            null,
            [SquadGroup("^run_subagent$", "kyber-weave-arbiter hook --harness devin", 5)],
            [SquadGroup("^run_subagent$", "kyber-weave-arbiter hook --harness devin", 5)]);

        const string expected = """
            {
              "PreToolUse": [
                {
                  "matcher": "^run_subagent$",
                  "hooks": [
                    {
                      "type": "command",
                      "command": "kyber-weave-arbiter hook --harness devin",
                      "timeout": 5
                    }
                  ]
                }
              ],
              "PostToolUse": [
                {
                  "matcher": "^run_subagent$",
                  "hooks": [
                    {
                      "type": "command",
                      "command": "kyber-weave-arbiter hook --harness devin",
                      "timeout": 5
                    }
                  ]
                }
              ]
            }

            """;

        Assert.Equal(expected, content, StringComparer.Ordinal);
    }

    private static JsonObject SquadGroup(string? matcher, string command = ExpectedCommand, int timeout = HookTimeoutSeconds)
    {
        JsonObject group = new();
        if (matcher is not null)
        {
            group["matcher"] = matcher;
        }

        group["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = timeout,
        });
        return group;
    }

    private static int SquadEntryCount(string content, string container) =>
        JsonNode.Parse(content)![container] is JsonArray array
            ? array.Count(node => SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Factory, node))
            : 0;

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_EntriesCarryTheWiringTimeout()
    {
        const int timeoutSeconds = 17;
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: timeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.All(block.Entries, entry =>
        {
            JsonObject group = Assert.IsType<JsonObject>(entry.Entry);
            JsonArray hooks = Assert.IsType<JsonArray>(group["hooks"]);
            JsonObject hook = Assert.IsType<JsonObject>(Assert.Single(hooks));
            Assert.Equal(timeoutSeconds, hook["timeout"]!.GetValue<int>());
        });
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_EntriesAreRecognizedAsSquadOwned()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);
        Assert.All(block.Entries, entry =>
            Assert.True(
                SquadHookJsonBlock.IsSquadEntry(SquadHookBlockFormat.Factory, entry.Entry),
                $"Entry in container '{entry.Container}' is not recognized as Squad-owned."));
    }

    [Fact]
    public async Task RenderAsync_EnabledAtProjectScope_EmitsNoWholeFile()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        Assert.NotNull(result.Blocks);
        Assert.NotEmpty(result.Blocks);
        Assert.DoesNotContain(result.Files, f => f.RelativePath == BlockPath);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("disabled")]
    public async Task RenderAsync_WhenNotRendered_EmitsNoBlock(string mode)
    {
        SquadArbiterWiring? wiring = mode switch
        {
            "disabled" => new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: HookTimeoutSeconds),
            _ => null,
        };
        SquadDeploymentScope scope = SquadDeploymentScope.Project;

        SquadRenderResult result = await RenderAsync(wiring, scope);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.True(result.Blocks is null || result.Blocks.Count == 0, "Expected no owned blocks when the Arbiter block is not rendered.");
        Assert.DoesNotContain(result.Files, f => f.RelativePath == BlockPath);
    }

    [Fact]
    public async Task RenderAsync_EnabledArbiter_LeavesFilesByteForByte()
    {
        SquadRenderResult omitted = await RenderAsync(null, SquadDeploymentScope.Project);
        SquadRenderResult enabled = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        Assert.Equal(
            omitted.Files.Select(f => (f.Target, f.RelativePath)).Order().ToList(),
            enabled.Files.Select(f => (f.Target, f.RelativePath)).Order().ToList());
        foreach ((SquadDeploymentFile expected, SquadDeploymentFile actual) in omitted.Files
            .OrderBy(f => f.Target, StringComparer.Ordinal)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .Zip(enabled.Files
                .OrderBy(f => f.Target, StringComparer.Ordinal)
                .ThenBy(f => f.RelativePath, StringComparer.Ordinal)))
        {
            Assert.True(
                expected.Content.Span.SequenceEqual(actual.Content.Span),
                $"Rendered bytes differ for {actual.Target}/{actual.RelativePath} when the Arbiter block is added.");
        }
    }

    /// <summary>
    /// The absence of a block has to stay distinguishable from an empty one.
    /// <c>FactoryHooksShadowing</c> reads null as "nothing was rendered" and an empty
    /// list as "a block was rendered and it was empty", so the render that is byte
    /// identical to omitting the wiring must also report null, as Antigravity and Devin
    /// already do.
    /// </summary>
    [Fact]
    public async Task RenderAsync_WithoutArbiterWiring_CarriesNoBlockAndLeavesFilesUnchanged()
    {
        // The renderer is exercised directly: SquadRendererRegistry merges fragments and
        // reports one list for the whole request, so the per-renderer distinction the
        // shadowing check consumes only exists at this seam.
        SquadRenderResult omitted = await RenderFromRendererAsync(null);
        SquadRenderResult disabled = await RenderFromRendererAsync(
            new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: HookTimeoutSeconds));

        Assert.Null(omitted.Blocks);
        Assert.Null(disabled.Blocks);

        foreach ((SquadDeploymentFile expected, SquadDeploymentFile actual) in omitted.Files
            .OrderBy(f => f.Target, StringComparer.Ordinal)
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .Zip(disabled.Files
                .OrderBy(f => f.Target, StringComparer.Ordinal)
                .ThenBy(f => f.RelativePath, StringComparer.Ordinal)))
        {
            Assert.True(
                expected.Content.Span.SequenceEqual(actual.Content.Span),
                $"Rendered bytes differ for {actual.Target}/{actual.RelativePath} between no wiring and a disabled wiring.");
        }
    }

    private async Task<SquadRenderResult> RenderFromRendererAsync(SquadArbiterWiring? arbiter)
    {
        SquadRenderResult result = await new FactoryRenderer().RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Factory],
            Scope: SquadDeploymentScope.Project,
            Arbiter: arbiter));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }

    private async Task<SquadRenderResult> RenderAsync(SquadArbiterWiring? arbiter, SquadDeploymentScope scope)
    {
        SquadRendererRegistry registry = new([new FactoryRenderer()]);
        SquadRenderResult result = await registry.RenderAsync(new SquadRenderRequest(
            SourceDirectory: _fixture.Path,
            Targets: [SquadTarget.Factory],
            Scope: scope,
            Arbiter: arbiter));
        Assert.True(result.Success, string.Join("; ", result.Errors));
        return result;
    }

    // ----------------------------------------------------------------------------------
    // Shadowing: settings.json hooks key + absent hooks.json drops the block (§10.8, R18)
    // ----------------------------------------------------------------------------------

    [Fact]
    public void IsShadowed_WhenSettingsJsonHasHooksKeyAndHooksJsonIsAbsent_IsTrue()
    {
        using TempDirectory targetRoot = new();
        WriteFile(Path.Combine(targetRoot.Path, ".factory/settings.json"), """
            {"model": "droid",
             "hooks": {"post_tool_use": [{"command": "user-own-linter"}]}}
            """);

        Assert.True(FactoryHooksShadowing.IsShadowed(targetRoot.Path));
    }

    [Fact]
    public void IsShadowed_WhenHooksJsonExists_IsFalse()
    {
        using TempDirectory targetRoot = new();
        WriteFile(Path.Combine(targetRoot.Path, ".factory/settings.json"), "{\"hooks\": {}}");
        WriteFile(Path.Combine(targetRoot.Path, ".factory/hooks.json"), "{}");

        Assert.False(FactoryHooksShadowing.IsShadowed(targetRoot.Path));
    }

    [Fact]
    public void IsShadowed_WhenSettingsJsonHasNoHooksKey_IsFalse()
    {
        using TempDirectory targetRoot = new();
        WriteFile(Path.Combine(targetRoot.Path, ".factory/settings.json"), "{\"model\": \"droid\"}");

        Assert.False(FactoryHooksShadowing.IsShadowed(targetRoot.Path));
    }

    [Fact]
    public void IsShadowed_WhenSettingsJsonIsAbsent_IsFalse()
    {
        using TempDirectory targetRoot = new();

        Assert.False(FactoryHooksShadowing.IsShadowed(targetRoot.Path));
    }

    [Fact]
    public void Degradation_RecordsSettingsHooksShadowedWithTheNamedFix()
    {
        SquadDegradationRecord record = FactoryHooksShadowing.Degradation();

        Assert.Equal("factory", record.Target);
        Assert.Equal("arbiter", record.CanonicalIdentity);
        Assert.Equal(FactoryHooksShadowing.DegradationCode, record.Code);
        Assert.Equal("arbiter-not-enforced", record.Code);
        Assert.StartsWith(FactoryHooksShadowing.Reason, record.Details, StringComparison.Ordinal);
        Assert.Equal("settings-hooks-shadowed", FactoryHooksShadowing.Reason);
        Assert.Contains(".factory/hooks.json", record.Details, StringComparison.Ordinal);
        Assert.Contains("squad update", record.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_SettingsHooksShadowFactoryHooks_DropsTheBlockAndRecordsTheDegradation()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(targetRoot);
        WriteFile(Path.Combine(targetRoot, ".factory/settings.json"), "{\"hooks\": {\"post_tool_use\": []}}");

        SquadLifecycleResult result = await InstallAsync(targetRoot, fixture.Path);

        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));
        Assert.NotNull(result.Receipt);
        Assert.True(
            result.Receipt.Blocks is null || result.Receipt.Blocks.Count == 0,
            "The shadowed Factory block must not reach the receipt.");
        Assert.False(
            File.Exists(Path.Combine(targetRoot, ".factory/hooks.json")),
            "Creating .factory/hooks.json would silently disable the user's settings.json hooks.");
        Assert.Contains(
            result.Degradations ?? [],
            d => d.Target == "factory" && d.Subject == "arbiter" && d.Code == "arbiter-not-enforced");
    }

    [Fact]
    public async Task Install_WhenHooksJsonAlreadyExists_KeepsTheBlockBesideTheUsersHooks()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(targetRoot);
        // The user already migrated: settings.json keeps its (inert) hooks key, and the
        // hooks.json Factory actually reads holds their entries. The block must splice.
        WriteFile(Path.Combine(targetRoot, ".factory/settings.json"), "{\"hooks\": {}}");
        WriteFile(Path.Combine(targetRoot, ".factory/hooks.json"), """
            {"PreToolUse": [{"matcher": "Read", "hooks": [{"type": "command", "command": "user-own-linter"}]}]}
            """);

        SquadLifecycleResult result = await InstallAsync(targetRoot, fixture.Path);

        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));
        SquadOwnedBlock block = Assert.Single(result.Receipt?.Blocks ?? []);
        Assert.Equal("factory", block.Target);
        Assert.Equal(BlockPath, block.RelativePath);

        string hooksJson = await File.ReadAllTextAsync(Path.Combine(targetRoot, ".factory/hooks.json"));
        Assert.Contains("user-own-linter", hooksJson, StringComparison.Ordinal);
        Assert.Contains(ExpectedCommand, hooksJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnparsableSettingsJson_DropsTheBlockAndNamesTheFile()
    {
        // Factory may tolerate JSONC, so an unparsable settings.json cannot be read as "no
        // hooks key". Creating hooks.json blindly could disable the user's hooks: the block
        // is dropped and the record names the file (review 20.1, e).
        using TempDirectory targetRoot = new();
        WriteFile(Path.Combine(targetRoot.Path, ".factory/settings.json"), "{ // jsonc comment\n \"hooks\": {} }");
        SquadRenderedBlock block = new("factory", BlockPath, SquadHookBlockFormat.Factory, []);

        FactoryHooksShadowingOutcome outcome = FactoryHooksShadowing.Resolve(
            targetRoot.Path,
            [SquadTarget.Factory],
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project,
            [block]);

        Assert.Empty(outcome.Blocks ?? []);
        SquadDegradationRecord record = Assert.Single(outcome.Degradations);
        Assert.Equal(FactoryHooksShadowing.DegradationCode, record.Code);
        Assert.Contains(".factory/settings.json", record.Details, StringComparison.Ordinal);
        Assert.StartsWith(FactoryHooksShadowing.UnparsableReason, record.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_UnparsableSettingsJson_DropsTheBlockAndNeverCreatesHooksJson()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(targetRoot);
        WriteFile(Path.Combine(targetRoot, ".factory/settings.json"), "{ // jsonc comment\n \"hooks\": {} }");

        SquadLifecycleResult result = await InstallAsync(targetRoot, fixture.Path);

        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));
        Assert.True(result.Receipt?.Blocks is null || result.Receipt.Blocks.Count == 0);
        Assert.False(File.Exists(Path.Combine(targetRoot, ".factory/hooks.json")));
        Assert.Contains(
            result.Degradations ?? [],
            d => d.Target == "factory" && d.Code == "arbiter-not-enforced");
    }

    [Fact]
    public async Task Update_SettingsHooksShadowFactoryHooks_DropsTheBlockAndNeverCreatesHooksJson()
    {
        // The update path has its own call to the shadowing check (review 20.1, e).
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(targetRoot);
        await InstallAsync(targetRoot, fixture.Path);
        File.Delete(Path.Combine(targetRoot, ".factory/hooks.json"));
        WriteFile(Path.Combine(targetRoot, ".factory/settings.json"), "{\"hooks\": {\"post_tool_use\": []}}");

        SquadLifecycleResult result = await UpdateAsync(targetRoot, fixture.Path);

        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));
        Assert.True(result.Receipt?.Blocks is null || result.Receipt.Blocks.Count == 0);
        Assert.False(File.Exists(Path.Combine(targetRoot, ".factory/hooks.json")));
        Assert.Contains(
            result.Degradations ?? [],
            d => d.Target == "factory" && d.Code == "arbiter-not-enforced");
    }

    [Fact]
    public async Task Update_UnparsableSettingsJson_DropsTheBlockAndNeverCreatesHooksJson()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(targetRoot);
        await InstallAsync(targetRoot, fixture.Path);
        File.Delete(Path.Combine(targetRoot, ".factory/hooks.json"));
        WriteFile(Path.Combine(targetRoot, ".factory/settings.json"), "{ // jsonc comment\n \"hooks\": {} }");

        SquadLifecycleResult result = await UpdateAsync(targetRoot, fixture.Path);

        Assert.True(result.Success, string.Join("; ", result.Errors ?? []));
        Assert.True(result.Receipt?.Blocks is null || result.Receipt.Blocks.Count == 0);
        Assert.False(File.Exists(Path.Combine(targetRoot, ".factory/hooks.json")));
        Assert.Contains(
            result.Degradations ?? [],
            d => d.Target == "factory" && d.Code == "arbiter-not-enforced");
    }

    [Fact]
    public async Task Reinstall_FactoryHooks_RegistersSubagentStopOnceAndRecordsItsEntry()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(targetRoot);
        SquadLifecycleResult installed = await InstallAsync(targetRoot, fixture.Path);
        Assert.True(installed.Success, string.Join("; ", installed.Errors ?? []));

        SquadLifecycleResult reinstalled = await UpdateAsync(targetRoot, fixture.Path);

        Assert.True(reinstalled.Success, string.Join("; ", reinstalled.Errors ?? []));
        string hooksJson = await File.ReadAllTextAsync(Path.Combine(targetRoot, ".factory/hooks.json"));
        // One command per event: PreToolUse, PostToolUse and SubagentStop. A reinstall must not add a copy.
        Assert.Equal(3, CountOccurrences(hooksJson, ExpectedCommand));
        Assert.Equal(1, CountOccurrences(hooksJson, "\"SubagentStop\""));
        SquadOwnedBlock block = Assert.Single(reinstalled.Receipt?.Blocks ?? []);
        Assert.Contains(block.Entries, entry => entry.Container == "/SubagentStop/0");
    }

    [Fact]
    public async Task Update_TwoContainerFactoryInstall_UpgradesToSubagentStop()
    {
        // An install from before this change wrote only PreToolUse and PostToolUse. The next
        // install must add the stop registration and record it, without touching the others.
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "project");
        Directory.CreateDirectory(targetRoot);
        SquadLifecycleResult older = await InstallAsync(targetRoot, fixture.Path, subagentStop: false);
        Assert.True(older.Success, string.Join("; ", older.Errors ?? []));
        string hooksPath = Path.Combine(targetRoot, ".factory/hooks.json");
        Assert.DoesNotContain("SubagentStop", await File.ReadAllTextAsync(hooksPath), StringComparison.Ordinal);

        SquadLifecycleResult upgraded = await UpdateAsync(targetRoot, fixture.Path);

        Assert.True(upgraded.Success, string.Join("; ", upgraded.Errors ?? []));
        string hooksJson = await File.ReadAllTextAsync(hooksPath);
        Assert.Equal(3, CountOccurrences(hooksJson, ExpectedCommand));
        Assert.Equal(1, CountOccurrences(hooksJson, "\"SubagentStop\""));
        SquadOwnedBlock block = Assert.Single(upgraded.Receipt?.Blocks ?? []);
        Assert.Contains(block.Entries, entry => entry.Container == "/SubagentStop/0");
    }

    [Fact]
    public async Task Uninstall_FactoryHooks_RemovesSquadSubagentStopAndKeepsTheUsersOwn()
    {
        using TempDirectory fixture = new();
        string targetRoot = Path.Combine(fixture.Path, "project");
        string hooksPath = Path.Combine(targetRoot, ".factory/hooks.json");
        WriteFile(hooksPath, """
            {"SubagentStop": [{"hooks": [{"type": "command", "command": "user-own-stop-notifier"}]}]}
            """);
        SquadLifecycleResult installed = await InstallAsync(targetRoot, fixture.Path);
        Assert.True(installed.Success, string.Join("; ", installed.Errors ?? []));

        SquadLifecycleResult removed = await UninstallAsync(targetRoot, fixture.Path);

        Assert.True(removed.Success, string.Join("; ", removed.Errors ?? []));
        string remaining = await File.ReadAllTextAsync(hooksPath);
        Assert.DoesNotContain(ExpectedCommand, remaining, StringComparison.Ordinal);
        Assert.Contains("user-own-stop-notifier", remaining, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string needle) =>
        text.Split(needle, StringSplitOptions.None).Length - 1;

    private async Task<SquadLifecycleResult> UninstallAsync(string targetRoot, string applicationData)
    {
        using FakeSquadReleaseSource releases = new();
        SquadStateStore store = new(new FakeSquadUserPaths(applicationData));
        SquadLifecycleService service = new(
            releases,
            new FactoryBlockStubRenderer(),
            store,
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        return await service.UninstallAsync(new SquadUninstallRequest(targetRoot, SquadDeploymentScope.Project));
    }

    private async Task<SquadLifecycleResult> InstallAsync(string targetRoot, string applicationData, bool subagentStop = true)
    {
        using FakeSquadReleaseSource releases = new();
        SquadStateStore store = new(new FakeSquadUserPaths(applicationData));
        SquadLifecycleService service = new(
            releases,
            new FactoryBlockStubRenderer(subagentStop),
            store,
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        return await service.InstallAsync(new SquadInstallRequest(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Factory],
            Version: "1.2.3",
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds)));
    }

    private async Task<SquadLifecycleResult> UpdateAsync(string targetRoot, string applicationData, bool subagentStop = true)
    {
        using FakeSquadReleaseSource releases = new();
        SquadStateStore store = new(new FakeSquadUserPaths(applicationData));
        SquadLifecycleService service = new(
            releases,
            new FactoryBlockStubRenderer(subagentStop),
            store,
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        return await service.UpdateAsync(new SquadUpdateRequest(
            TargetRoot: targetRoot,
            Scope: SquadDeploymentScope.Project,
            Targets: [SquadTarget.Factory],
            Version: "1.2.4",
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds)));
    }

    private static void WriteFile(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    /// <summary>A stand-in renderer that always emits the Factory block, isolating the
    /// lifecycle's shadowing decision from the renderer's own gating.</summary>
    /// <summary>
    /// Emits the Factory block as the real renderer does. <paramref name="includeSubagentStop"/>
    /// set to false reproduces the two-container file an older Squad wrote, for upgrade tests.
    /// </summary>
    private sealed class FactoryBlockStubRenderer(bool includeSubagentStop = true) : ISquadRenderer
    {
        public IReadOnlyCollection<SquadTarget> SupportedTargets { get; } = [SquadTarget.Factory];

        public Task<SquadRenderResult> RenderAsync(
            SquadRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SquadRenderResult(
                true,
                [],
                [],
                [],
                [],
                Blocks: [FactoryBlock()]));
        }

        private SquadRenderedBlock FactoryBlock()
        {
            List<SquadRenderedBlockEntry> entries =
            [
                new SquadRenderedBlockEntry("PreToolUse", MatcherGroup(ExpectedCommand)),
                new SquadRenderedBlockEntry("PostToolUse", MatcherGroup(ExpectedCommand)),
            ];
            if (includeSubagentStop)
            {
                entries.Add(new SquadRenderedBlockEntry("SubagentStop", MatcherGroup(ExpectedCommand, matcher: null)));
            }

            return new("factory", BlockPath, SquadHookBlockFormat.Factory, entries);
        }

        private static JsonObject MatcherGroup(string command, string? matcher = "Task")
        {
            JsonObject group = new();
            if (matcher is not null)
            {
                group["matcher"] = matcher;
            }

            group["hooks"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = HookTimeoutSeconds,
                });
            return group;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private readonly DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;
    }
}
