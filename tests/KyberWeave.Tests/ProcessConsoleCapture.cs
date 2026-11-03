using System.Diagnostics.CodeAnalysis;
using System.Text;
using JetBrains.Annotations;
using KyberWeave.Cli.Rendering;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace KyberWeave.Tests;

/// <summary>
/// Routes console and Spectre.Console output to asynchronous-local string writers without
/// process-wide locking, allowing concurrent CLI test execution without output cross-talk or width interference.
/// </summary>
internal static class ProcessConsoleCapture
{
    private static readonly Lock InitLock = new();
    private static readonly AsyncLocal<StringWriter?> CurrentWriter = new();
    private static readonly AsyncLocal<IAnsiConsole?> CurrentAnsiConsole = new();
    private static bool _installed;

    static ProcessConsoleCapture()
    {
        EnsureInstalled();
    }

    /// <summary>
    /// Ensures the global console and Spectre router are installed once per process.
    /// </summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "Process-lifetime console router stays active for the lifetime of the test host.")]
    public static void EnsureInstalled()
    {
        if (_installed)
        {
            return;
        }

        lock (InitLock)
        {
            if (_installed)
            {
                return;
            }

            TextWriter originalOut = Console.Out;
            IAnsiConsole originalAnsiConsole = AnsiConsole.Console;
            Console.SetOut(new AsyncLocalTextWriter(originalOut));
            AnsiConsole.Console = new AsyncLocalAnsiConsole(originalAnsiConsole);
            _installed = true;
        }
    }

    /// <summary>
    /// Runs <paramref name="execute"/> once, before returning, with console output captured
    /// in an isolated asynchronous-local context.
    /// </summary>
    public static CapturedConsoleExecution<T> Run<T>([InstantHandle] Func<T> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        EnsureInstalled();

        using StringWriter writer = new();
        IAnsiConsole capture = CreateCaptureConsole(writer);

        StringWriter? priorWriter = CurrentWriter.Value;
        IAnsiConsole? priorAnsiConsole = CurrentAnsiConsole.Value;

        CurrentWriter.Value = writer;
        CurrentAnsiConsole.Value = capture;
        try
        {
            T result = execute();
            return new CapturedConsoleExecution<T>(result, writer.ToString());
        }
        finally
        {
            CurrentAnsiConsole.Value = priorAnsiConsole;
            CurrentWriter.Value = priorWriter;
        }
    }

    /// <summary>
    /// Runs <paramref name="execute"/> asynchronously with console output captured in an
    /// isolated asynchronous-local context.
    /// </summary>
    public static async Task<CapturedConsoleExecution<T>> RunAsync<T>([InstantHandle] Func<Task<T>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        EnsureInstalled();

        using StringWriter writer = new();
        IAnsiConsole capture = CreateCaptureConsole(writer);

        StringWriter? priorWriter = CurrentWriter.Value;
        IAnsiConsole? priorAnsiConsole = CurrentAnsiConsole.Value;

        CurrentWriter.Value = writer;
        CurrentAnsiConsole.Value = capture;
        try
        {
            T result = await execute().ConfigureAwait(false);
            return new CapturedConsoleExecution<T>(result, writer.ToString());
        }
        finally
        {
            CurrentAnsiConsole.Value = priorAnsiConsole;
            CurrentWriter.Value = priorWriter;
        }
    }

    private static IAnsiConsole CreateCaptureConsole(StringWriter writer)
    {
        IAnsiConsole capture = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });
        ConsoleWidth.EnsureUsable(capture);
        return capture;
    }

    private sealed class AsyncLocalTextWriter : TextWriter
    {
        [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
            Justification = "Process-lifetime fallback console is owned by the process, not this wrapper.")]
        private readonly TextWriter _fallback;

        public AsyncLocalTextWriter(TextWriter fallback)
        {
            _fallback = fallback;
        }

        private TextWriter Active => CurrentWriter.Value ?? _fallback;

        public override Encoding Encoding => Active.Encoding;
        public override IFormatProvider FormatProvider => Active.FormatProvider;

        public override void Flush() => Active.Flush();
        public override Task FlushAsync() => Active.FlushAsync();

        public override void Write(char value) => Active.Write(value);
        public override void Write(char[]? buffer) => Active.Write(buffer);
        public override void Write(char[] buffer, int index, int count) => Active.Write(buffer, index, count);
        public override void Write(ReadOnlySpan<char> buffer) => Active.Write(buffer);
        public override void Write(string? value) => Active.Write(value);
        public override void Write(StringBuilder? value) => Active.Write(value);

        public override void WriteLine() => Active.WriteLine();
        public override void WriteLine(char value) => Active.WriteLine(value);
        public override void WriteLine(char[]? buffer) => Active.WriteLine(buffer);
        public override void WriteLine(char[] buffer, int index, int count) => Active.WriteLine(buffer, index, count);
        public override void WriteLine(ReadOnlySpan<char> buffer) => Active.WriteLine(buffer);
        public override void WriteLine(string? value) => Active.WriteLine(value);
        public override void WriteLine(StringBuilder? value) => Active.WriteLine(value);

        public override Task WriteAsync(char value) => Active.WriteAsync(value);
        public override Task WriteAsync(char[] buffer, int index, int count) => Active.WriteAsync(buffer, index, count);
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            Active.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(string? value) => Active.WriteAsync(value);
        public override Task WriteAsync(StringBuilder? value, CancellationToken cancellationToken = default) =>
            Active.WriteAsync(value, cancellationToken);

        public override Task WriteLineAsync() => Active.WriteLineAsync();
        public override Task WriteLineAsync(char value) => Active.WriteLineAsync(value);
        public override Task WriteLineAsync(char[] buffer, int index, int count) => Active.WriteLineAsync(buffer, index, count);
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            Active.WriteLineAsync(buffer, cancellationToken);
        public override Task WriteLineAsync(string? value) => Active.WriteLineAsync(value);
        public override Task WriteLineAsync(StringBuilder? value, CancellationToken cancellationToken = default) =>
            Active.WriteLineAsync(value, cancellationToken);
    }

    private sealed class AsyncLocalAnsiConsole : IAnsiConsole
    {
        private readonly IAnsiConsole _fallback;

        public AsyncLocalAnsiConsole(IAnsiConsole fallback)
        {
            _fallback = fallback;
        }

        private IAnsiConsole Active => CurrentAnsiConsole.Value ?? _fallback;

        public Profile Profile => Active.Profile;
        public IAnsiConsoleCursor Cursor => Active.Cursor;
        public IAnsiConsoleInput Input => Active.Input;
        public IExclusivityMode ExclusivityMode => Active.ExclusivityMode;
        public RenderPipeline Pipeline => Active.Pipeline;

        public void Clear(bool home) => Active.Clear(home);
        public void Write(IRenderable renderable) => Active.Write(renderable);
        public void WriteAnsi(Action<AnsiWriter> action) => Active.WriteAnsi(action);
    }
}

internal sealed record CapturedConsoleExecution<T>(T Result, string Output);
