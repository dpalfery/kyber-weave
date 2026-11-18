using KyberWeave.Arbiter.Hooks;
using KyberWeave.Core.Configuration;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// A global-scope hook whose project configuration is malformed fails closed and names the cause, instead of falling back
/// to the user's configuration. Regression test for Kilo review comments 5 and 6 on PR #527; the whole hook command is
/// run in the integration project.
/// </summary>
public sealed class ArbiterHookMalformedProjectConfigTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Items5And6_GlobalScope_MalformedProjectConfig_FailsClosedWithKwArbHook001()
    {
        _host.Write(UserHostConfigPath.Of(_host.HomeDirectory), "arbiter:\n  enabled: true\n");
        string project = _host.InWork("project");
        _host.WriteRepoConfig("invalid: yaml: : content", project);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            _host.LoadHostConfig(project, HookScope.Global, _host.HomeDirectory));

        Assert.Contains("KW-ARB-HOOK-001", ex.Message, StringComparison.Ordinal);
        Assert.Contains("failed to load project config", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
