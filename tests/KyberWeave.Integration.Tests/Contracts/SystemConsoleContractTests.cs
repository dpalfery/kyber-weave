using KyberWeave.Contracts.Host;
using KyberWeave.Data.Host;
using KyberWeave.Tests.ConsoleSeam;
using Xunit;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the console contract against the real adapter, which forwards to the process's own console.</summary>
public sealed class SystemConsoleContractTests : ConsoleContractTests
{
    /// <inheritdoc />
    protected override IConsole CreateConsole() => new SystemConsole();

    /// <inheritdoc />
    protected override bool ExpectedInputRedirected => Console.IsInputRedirected;

    /// <inheritdoc />
    protected override void FeedInput(IConsole console, string text) => ProcessConsoleCapture.FeedInput(text);

    /// <inheritdoc />
    protected override ConsoleWrites Capture(IConsole console, Action<IConsole> write)
    {
        // The shared console capture routes the process console into an asynchronous-local writer, so this run reads
        // what the adapter forwarded without taking a process-wide lock away from the tests running beside it.
        CapturedConsoleExecution<bool> captured = ProcessConsoleCapture.Run(() =>
        {
            write(console);
            return true;
        });

        return new ConsoleWrites(captured.Output, captured.ErrorOutput);
    }

    [Fact]
    public async Task KeepsTheInputOneReaderFedOutOfAnotherReader()
    {
        IConsole first = CreateConsole();
        IConsole second = CreateConsole();
        TaskCompletionSource bothFed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int fed = 0;

        async Task<string?> ReadFedLineAsync(IConsole console, string text)
        {
            FeedInput(console, text);
            if (Interlocked.Increment(ref fed) == 2)
            {
                bothFed.SetResult();
            }

            await bothFed.Task;
            return console.ReadLine();
        }

        Task<string?> firstReader = Task.Run(() => ReadFedLineAsync(first, "first-reader\n"));
        Task<string?> secondReader = Task.Run(() => ReadFedLineAsync(second, "second-reader\n"));

        string?[] lines = await Task.WhenAll(firstReader, secondReader);

        Assert.Equal("first-reader", lines[0]);
        Assert.Equal("second-reader", lines[1]);
    }
}
