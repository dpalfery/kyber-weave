using KyberWeave.Contracts.Host;
using Xunit;

namespace KyberWeave.Tests.ConsoleSeam;

/// <summary>
/// The contract every <see cref="IConsole"/> must satisfy. Run it once against the fake and once against the real
/// adapter, so a unit test can trust what the fake reports.
/// </summary>
/// <remarks>
/// A derived class supplies the console under test and three hooks: how to feed input, how to capture what was written,
/// and whether this host reports redirected input. The adapter's hooks redirect the real console for the length of one
/// call; the fake's read its own writers.
/// </remarks>
public abstract class ConsoleContractTests : IDisposable
{
    /// <summary>Creates the console under test.</summary>
    /// <returns>The console seam. The suite may create more than one instance.</returns>
    protected abstract IConsole CreateConsole();

    /// <summary>The value <see cref="IConsole.IsInputRedirected"/> reports on this host, or as this fake was configured.</summary>
    protected abstract bool ExpectedInputRedirected { get; }

    /// <summary>Makes the console's next reads return the supplied lines.</summary>
    /// <param name="console">The console under test.</param>
    /// <param name="text">The input text, one line per <c>\n</c>.</param>
    protected abstract void FeedInput(IConsole console, string text);

    /// <summary>Runs a write against the console and reports what reached each writer.</summary>
    /// <param name="console">The console under test.</param>
    /// <param name="write">The writes to perform.</param>
    /// <returns>The text captured from <see cref="IConsole.Out"/> and <see cref="IConsole.Error"/>.</returns>
    protected abstract ConsoleWrites Capture(IConsole console, Action<IConsole> write);

    /// <summary>Releases anything the hooks redirected.</summary>
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void KeepsOutputAndDiagnosticsApart()
    {
        IConsole console = CreateConsole();

        ConsoleWrites writes = Capture(console, target =>
        {
            target.Out.Write("a result");
            target.Out.Flush();
            target.Error.Write("a warning");
            target.Error.Flush();
        });

        Assert.Equal("a result", writes.Output);
        Assert.Equal("a warning", writes.Error);
    }

    [Fact]
    public void ProvidesTwoWritersThatAreNotTheSameOne()
    {
        IConsole console = CreateConsole();

        Assert.NotNull(console.Out);
        Assert.NotNull(console.Error);
        Assert.NotSame(console.Out, console.Error);
    }

    [Fact]
    public void ReportsWhetherInputIsRedirected()
    {
        IConsole console = CreateConsole();

        Assert.Equal(ExpectedInputRedirected, console.IsInputRedirected);
    }

    [Fact]
    public void ReadsTheSuppliedInputOneLineAtATime()
    {
        IConsole console = CreateConsole();
        FeedInput(console, "first\nsecond\n");

        Assert.Equal("first", console.ReadLine());
        Assert.Equal("second", console.ReadLine());
    }

    [Fact]
    public void ReportsTheEndOfInputAsNull()
    {
        IConsole console = CreateConsole();
        FeedInput(console, "only\n");

        Assert.Equal("only", console.ReadLine());
        Assert.Null(console.ReadLine());
    }

    [Fact]
    public void ReadsLinesEndedByEveryKindOfLineBreak()
    {
        IConsole console = CreateConsole();
        FeedInput(console, "carriage\rnewline\nboth\r\n");

        Assert.Equal("carriage", console.ReadLine());
        Assert.Equal("newline", console.ReadLine());
        Assert.Equal("both", console.ReadLine());
        Assert.Null(console.ReadLine());
    }
}

/// <summary>What reached each of the console's two writers.</summary>
/// <param name="Output">The text written to <see cref="IConsole.Out"/>.</param>
/// <param name="Error">The text written to <see cref="IConsole.Error"/>.</param>
public sealed record ConsoleWrites(string Output, string Error);
