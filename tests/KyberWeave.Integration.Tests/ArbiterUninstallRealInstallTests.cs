using KyberWeave.Data.Configuration;
using KyberWeave.Testing.Squad;
using KyberWeave.Tests.Configuration;
using KyberWeave.Cli.Commands.Arbiter;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Tests.Arbiter;
using Microsoft.Extensions.DependencyInjection;
using KyberWeave.Core.Arbiter.Credentials;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Integration.Tests;

/// <summary>
/// <c>arbiter uninstall</c> after a real <c>arbiter install</c>: the install writes the owned hook block and the receipt,
/// the uninstall removes exactly that block and the receipt, keeps the operator's foreign bytes, and unregisters the
/// target. This needs the real deployment transaction and receipt file; the command's own decisions are unit tests in
/// <c>KyberWeave.Cli.Tests.ArbiterUninstallCommandInMemoryTests</c>.
/// </summary>
public sealed class ArbiterUninstallRealInstallTests : IDisposable
{
    private const string ClaudeForeign = """
        {
          "model": "sonnet",
          "permissions": { "allow": ["Bash"] },
          "hooks": {
            "PreToolUse": [
              {
                "matcher": "^Read$",
                "hooks": [
                  { "type": "command", "command": "user-owned read hook" }
                ]
              }
            ],
            "PostToolUse": [
              {
                "matcher": "^Task$",
                "hooks": [
                  { "type": "command", "command": "user-owned task hook" }
                ]
              }
            ]
          },
          "custom": { "keep": "this stays" },
          "user": "keep me"
        }
        """;

    private readonly TempDirectory _home = new();
    private readonly FakeCredentialStore _store = new();
    private readonly ArbiterTestHost _host;

    public ArbiterUninstallRealInstallTests()
    {
        _host = ArbiterTestHost.Create(RegisterFakes);
    }

    public void Dispose()
    {
        _host.Dispose();
        _home.Dispose();
    }

    // The commands resolve the injected home and store, never the real home or keychain.
    private void RegisterFakes(IServiceCollection services)
    {
        services.AddSingleton<IProcessEnvironment>(new FakeProcessEnvironment(() => _home.Path));
        services.AddSingleton<ICredentialStore>(_store);
    }

    /// <summary>
    /// The default global uninstall removes the real Claude hook block from the user home, cleans
    /// the global receipt, keeps the foreign config bytes, and unregisters the target from the
    /// user-scoped global target ledger. The historical test invented a non-existent root key, so it
    /// only proved the wrong thing and never exercised the real install path.
    /// </summary>
    [Fact]
    public void Uninstall_DefaultScope_GlobalRealInstall_RemovesOwnedClaudeBlock_CleansState_AndPreservesForeignBytes()
    {
        string home = _home.Path;
        string settingsPath = Path.Combine(home, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, ClaudeForeign);

        ArbiterInstallCommand install = _host.Command<ArbiterInstallCommand>();
        CommandExecution installExecution = Capture(() => install.Execute(
            null!,
            new ArbiterInstallSettings
            {
                Path = home,
                Target = "claude",
                Provider = "none",
                Global = true,
            }));

        Assert.Equal(0, installExecution.ExitCode);
        string installed = File.ReadAllText(settingsPath);
        Assert.Contains("kyber-weave-arbiter hook --harness claude", installed, StringComparison.Ordinal);
        Assert.Contains("user-owned read hook", installed, StringComparison.Ordinal);

        FakeSquadUserPaths userPaths = new(home);
        SquadStateStore stateStore = RealSquadState.Store(userPaths);
        string receiptPath = stateStore.ResolveReceiptPath(home, SquadDeploymentScope.Global);
        Assert.True(File.Exists(receiptPath), "the real global install must write a global receipt");

        ArbiterUninstallCommand uninstall = _host.Command<ArbiterUninstallCommand>();
        CommandExecution execution = Capture(() => uninstall.Execute(
            null!,
            new ArbiterUninstallSettings { Path = home, Target = "claude" }));

        Assert.Equal(0, execution.ExitCode);
        string after = File.ReadAllText(settingsPath);
        Assert.DoesNotContain("kyber-weave-arbiter hook --harness claude", after, StringComparison.Ordinal);
        Assert.Contains("user-owned read hook", after, StringComparison.Ordinal);
        Assert.Contains("custom", after, StringComparison.Ordinal);
        Assert.Contains("keep me", after, StringComparison.Ordinal);
        Assert.DoesNotContain(RealUserHostConfigStore.Create().ReadGlobalTargets(home), target =>
            string.Equals(target, "claude", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(receiptPath), "the global receipt must be removed after uninstall");
        Assert.Null(stateStore.ReadReceipt(home, SquadDeploymentScope.Global));

        Assert.Contains("this stays", after, StringComparison.Ordinal);
        Assert.Contains("keep me", after, StringComparison.Ordinal);
    }

    private static CommandExecution Capture(Func<int> execute)
    {
        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(execute);
        return new CommandExecution(execution.Result, execution.Output);
    }

    private sealed record CommandExecution(int ExitCode, string Output);
}
