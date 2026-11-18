using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Contract for task fix1, F1 (Phase 1 review fixes, council review 10.1): a stdin read or host construction that
/// faults allows and logs, never exit-1-with-empty-stdout.
/// </summary>
public sealed class ArbiterFailClosedHostTests
{
    // F1: a throwing stdin read must block, never exit-1-with-empty-stdout.
    [Fact]
    public async Task Fix1_ThrowingStdinRead_AllowsAndLogs()
    {
        using ThrowingReader stdin = new();
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", "claude", "--caller", "conductor"], stdin, stdout, stderr);

        // ADR 0072: a host that cannot read the event cannot decide, so it allows and logs.
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains(HookCommand.FailClosedCode, stderr.ToString(), StringComparison.Ordinal);
    }

    // F1: a throwing host construction allows too, with the code on stderr and exit 0.
    [Fact]
    public async Task Fix1_ThrowingConstruction_AllowsAndLogs()
    {
        using StringReader stdin = new("{}");
        using StringWriter stdout = new();
        using StringWriter stderr = new();

        int exit = await Composition.DispatchAsync(
            ["hook", "--harness", "claude", "--caller", "conductor"],
            stdin,
            stdout,
            stderr,
            _ => throw new InvalidOperationException("registry exploded"));

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Contains(HookCommand.FailClosedCode, stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("registry exploded", stderr.ToString(), StringComparison.Ordinal);
    }

    private sealed class ThrowingReader : TextReader
    {
        public override Task<string> ReadToEndAsync() =>
            throw new InvalidOperationException("stdin exploded");
    }
}
