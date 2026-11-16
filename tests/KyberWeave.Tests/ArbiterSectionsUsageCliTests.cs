using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Data.Arbiter;
using KyberWeave.Core.Arbiter.Plans;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Cli.Commands.Arbiter;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Usage errors on <c>arbiter sections</c>. A missing or empty <c>--path</c> is refused with the sibling commands' usage
/// error (exit 2, one line) rather than an exception that escapes the command.
/// </summary>
public sealed class ArbiterSectionsUsageCliTests
{
    [Fact]
    public void ASectionsRunWithoutAPathIsAUsageErrorNotAnException()
    {
        ArbiterSectionsCommand command = ArbiterTestServices.Command<ArbiterSectionsCommand>();

        CapturedConsoleExecution<int> execution = ProcessConsoleCapture.Run(() => command.Execute(
            null!,
            new ArbiterSectionsSettings
            {
                Task = "Change the install steps.",
            }));

        Assert.Equal(2, execution.Result);
        Assert.Contains("kyber-weave arbiter: error: --path is required.", execution.Output, StringComparison.Ordinal);
    }
}
