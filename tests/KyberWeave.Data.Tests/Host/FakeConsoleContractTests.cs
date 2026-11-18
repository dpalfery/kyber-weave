using KyberWeave.Contracts.Host;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.ConsoleSeam;

/// <summary>Runs the console contract against the fake, so unit tests can trust what it captures and reads.</summary>
public sealed class FakeConsoleContractTests : ConsoleContractTests
{
    /// <inheritdoc />
    protected override IConsole CreateConsole() => new FakeConsole();

    /// <inheritdoc />
    protected override bool ExpectedInputRedirected => false;

    /// <inheritdoc />
    protected override void FeedInput(IConsole console, string text) =>
        ((FakeConsole)console).ScriptInput(text);

    /// <inheritdoc />
    protected override ConsoleWrites Capture(IConsole console, Action<IConsole> write)
    {
        FakeConsole fake = (FakeConsole)console;
        write(fake);
        return new ConsoleWrites(fake.Output, fake.ErrorOutput);
    }
}
