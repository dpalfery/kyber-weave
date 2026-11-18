using System.Diagnostics.CodeAnalysis;
using KyberWeave.Contracts.Host;
using KyberWeave.Data.Host;
using KyberWeave.Tests.ConsoleSeam;

namespace KyberWeave.Integration.Tests.Contracts;

/// <summary>Runs the console contract against the real adapter, which forwards to the process's own console.</summary>
public sealed class SystemConsoleContractTests : ConsoleContractTests
{
    [SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The field holds the process's own standard input reader, which this test puts back and must not close.")]
    private readonly TextReader _originalInput = Console.In;

    /// <inheritdoc />
    protected override IConsole CreateConsole() => new SystemConsole();

    /// <inheritdoc />
    protected override bool ExpectedInputRedirected => Console.IsInputRedirected;

    /// <inheritdoc />
    protected override void FeedInput(IConsole console, string text) => Console.SetIn(new StringReader(text));

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

    /// <inheritdoc />
    public override void Dispose()
    {
        Console.SetIn(_originalInput);
        base.Dispose();
    }
}
