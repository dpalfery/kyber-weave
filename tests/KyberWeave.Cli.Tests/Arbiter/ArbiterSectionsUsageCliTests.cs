using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests.Arbiter;

/// <summary>
/// Usage errors on <c>arbiter sections</c>. A missing or empty <c>--path</c> is refused with the sibling commands' usage
/// error (exit 2, one line) rather than an exception that escapes the command.
/// </summary>
public sealed class ArbiterSectionsUsageCliTests : IDisposable
{
    private readonly InMemoryArbiterHost _host = InMemoryArbiterHost.Create();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void ASectionsRunWithoutAPathIsAUsageErrorNotAnException()
    {
        ArbiterSectionsCommand command = _host.Command<ArbiterSectionsCommand>();

        ArbiterCommandRun execution = _host.Run(() => command.Execute(
            null!,
            new ArbiterSectionsSettings
            {
                Task = "Change the install steps.",
            }));

        Assert.Equal(2, execution.ExitCode);
        Assert.Contains("kyber-weave arbiter: error: --path is required.", execution.Output, StringComparison.Ordinal);
    }
}
