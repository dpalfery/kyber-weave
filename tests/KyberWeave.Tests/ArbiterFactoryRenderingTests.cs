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
    public async Task RenderAsync_EnabledAtProjectScope_HoldsTopLevelPreAndPostTaskGroups()
    {
        SquadRenderResult result = await RenderAsync(
            new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            SquadDeploymentScope.Project);

        SquadRenderedBlock block = Assert.Single(result.Blocks ?? []);

        // F11: the event names are the top-level keys of .factory/hooks.json (no `hooks`
        // wrapper), so each block entry is one matcher group keyed by its event container.
        Assert.Equal(
            ["PreToolUse", "PostToolUse"],
            block.Entries.Select(entry => entry.Container).ToArray());

        foreach (SquadRenderedBlockEntry entry in block.Entries)
        {
            JsonObject group = Assert.IsType<JsonObject>(entry.Entry);
            Assert.Equal(
                ["matcher", "hooks"],
                group.Select(property => property.Key).ToArray());
            Assert.Equal("Task", group["matcher"]!.GetValue<string>());

            JsonArray hooks = Assert.IsType<JsonArray>(group["hooks"]);
            JsonObject hook = Assert.IsType<JsonObject>(Assert.Single(hooks));
            Assert.Equal("command", hook["type"]!.GetValue<string>());
            Assert.Equal(ExpectedCommand, hook["command"]!.GetValue<string>());
            Assert.Equal(HookTimeoutSeconds, hook["timeout"]!.GetValue<int>());
        }
    }

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
    [InlineData("global")]
    public async Task RenderAsync_WhenNotRendered_EmitsNoBlock(string mode)
    {
        SquadArbiterWiring? wiring = mode switch
        {
            "disabled" => new SquadArbiterWiring(Enabled: false, HookTimeoutSeconds: HookTimeoutSeconds),
            "global" => new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds),
            _ => null,
        };
        SquadDeploymentScope scope = string.Equals(mode, "global", StringComparison.Ordinal)
            ? SquadDeploymentScope.Global
            : SquadDeploymentScope.Project;

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

    private async Task<SquadLifecycleResult> InstallAsync(string targetRoot, string applicationData)
    {
        using FakeSquadReleaseSource releases = new();
        SquadStateStore store = new(new FakeSquadUserPaths(applicationData));
        SquadLifecycleService service = new(
            releases,
            new FactoryBlockStubRenderer(),
            store,
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        return await service.InstallAsync(new SquadInstallRequest(
            targetRoot,
            SquadDeploymentScope.Project,
            [SquadTarget.Factory],
            Version: "1.2.3",
            Arbiter: new SquadArbiterWiring(Enabled: true, HookTimeoutSeconds: HookTimeoutSeconds)));
    }

    private async Task<SquadLifecycleResult> UpdateAsync(string targetRoot, string applicationData)
    {
        using FakeSquadReleaseSource releases = new();
        SquadStateStore store = new(new FakeSquadUserPaths(applicationData));
        SquadLifecycleService service = new(
            releases,
            new FactoryBlockStubRenderer(),
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
    private sealed class FactoryBlockStubRenderer : ISquadRenderer
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

        private static SquadRenderedBlock FactoryBlock() =>
            new(
                "factory",
                BlockPath,
                SquadHookBlockFormat.Factory,
                [new SquadRenderedBlockEntry("PreToolUse", MatcherGroup(ExpectedCommand))]);

        private static JsonObject MatcherGroup(string command) =>
            new()
            {
                ["matcher"] = "Task",
                ["hooks"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = command,
                        ["timeout"] = HookTimeoutSeconds,
                    }),
            };
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private readonly DateTimeOffset _value = value;

        public override DateTimeOffset GetUtcNow() => _value;
    }
}
