using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Data.Configuration;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Fakes;
using Xunit;
using KyberWeave.Application.Update;

namespace KyberWeave.Application.Tests.Update;

/// <summary>
/// The post-update refresh of the user-level Arbiter hook files: it rewrites each global target's shim and updates the
/// receipt's digest for it. Regression test for Kilo review comments 13 and 14 on PR #527, over an in-memory home.
/// </summary>
public sealed class ArbiterGlobalHookRefreshTests
{
    private readonly InMemorySquadState _state = new();

    [Fact]
    public async Task Items13And14_ArbiterHookRefresher_FileShimTargetsAndReceiptUpdate()
    {
        string home = _state.UserPaths.HomeDirectory;
        FileUserHostConfigStore userConfig = new(_state.Files);
        userConfig.WriteGlobalTargets(home, ["kilo", "opencode", "pi", "copilot", "cursor"]);

        SquadGlobalRoots roots = new(_ => null, home);
        string kiloHook = Path.Combine(roots.ResolveGlobalRoot(SquadTarget.Kilo), "plugin", "kyber-arbiter.ts");
        string opencodeHook = Path.Combine(roots.ResolveGlobalRoot(SquadTarget.OpenCode), "plugins", "kyber-arbiter.ts");
        string piHook = Path.Combine(roots.ResolveGlobalRoot(SquadTarget.Pi), "extensions", "kyber-arbiter.ts");
        string copilotHook = Path.Combine(roots.ResolveGlobalRoot(SquadTarget.Copilot), "hooks", "kyber-arbiter.json");
        string cursorHook = Path.Combine(roots.ResolveGlobalRoot(SquadTarget.Cursor), "hooks.json");

        Write(kiloHook, "// old-kilo-shim");
        Write(opencodeHook, "// old-opencode-shim");
        Write(piHook, "// old-pi-shim");
        Write(copilotHook, "{\"version\": 1, \"hooks\": {}}");
        Write(cursorHook, "{\"hooks\": {\"preToolUse\": [{\"command\": \"kyber-weave-arbiter hook --harness cursor --timeout 1\", \"matcher\": \"Task\"}], \"postToolUse\": []}}");

        string oldSha = new('a', 64);
        string oldDigest = new('b', 64);
        SquadReceipt receipt = new(
            SquadStateSchemas.ReceiptSchemaV3,
            SquadDeploymentScope.Global,
            home,
            _state.Time.GetUtcNow(),
            [],
            [
                new SquadOwnedFile("plugin/kyber-arbiter.ts", oldSha, "kilo", false),
                new SquadOwnedFile("plugins/kyber-arbiter.ts", oldSha, "opencode", false),
                new SquadOwnedFile("extensions/kyber-arbiter.ts", oldSha, "pi", false),
                new SquadOwnedFile("hooks/kyber-arbiter.json", oldSha, "copilot", false),
            ])
        {
            Blocks =
            [
                new SquadOwnedBlock("hooks.json", "cursor", false,
                [
                    new SquadOwnedBlockEntry("/hooks/preToolUse/0", oldDigest)
                ])
            ]
        };
        Write(_state.Store.ResolveReceiptPath(home, SquadDeploymentScope.Global), _state.Store.SerializeReceipt(receipt));

        List<string> log = [];
        ArbiterHookRefresher refresher = new(
            new FakeProcessEnvironment(homeDirectory: home, readVariable: name => name == "HOME" ? home : null),
            userConfig,
            new FakeKyberWeaveConfigRepository(),
            new FakeSquadLifecycleService(_state.Store),
            _state.Store,
            _state.Files,
            new FakeMcpWiringService(),
            log.Add);

        ArbiterHookRefreshResult refreshResult = await refresher.RefreshAsync([]);
        Assert.Equal(5, refreshResult.Refreshed);

        Assert.Contains("kilo", _state.Files.ReadAllText(kiloHook), StringComparison.Ordinal);
        Assert.Contains("opencode", _state.Files.ReadAllText(opencodeHook), StringComparison.Ordinal);
        Assert.Contains("pi", _state.Files.ReadAllText(piHook), StringComparison.Ordinal);
        Assert.Contains("copilot", _state.Files.ReadAllText(copilotHook), StringComparison.Ordinal);
        Assert.Contains("cursor", _state.Files.ReadAllText(cursorHook), StringComparison.Ordinal);

        SquadReceipt? updatedReceipt = _state.Store.ReadReceipt(home, SquadDeploymentScope.Global);
        Assert.NotNull(updatedReceipt);

        SquadOwnedBlock cursorBlock = Assert.Single(updatedReceipt.Blocks, b => b.Target == "cursor");
        Assert.NotEqual(oldDigest, cursorBlock.Entries[0].Sha256);

        SquadOwnedFile kiloFile = Assert.Single(updatedReceipt.Files, f => f.Target == "kilo");
        Assert.NotEqual(oldSha, kiloFile.Sha256);
    }

    private void Write(string path, string content)
    {
        _state.Files.CreateDirectory(Path.GetDirectoryName(path)!);
        _state.Files.WriteAllText(path, content);
    }
}
