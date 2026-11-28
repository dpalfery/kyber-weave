using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace KyberWeave.Tests;

/// <summary>
/// A console that reports whether its output is a terminal, which <see cref="TestConsole"/> never does, so a test can
/// reach the branch a person at a terminal takes.
/// </summary>
public sealed class TerminalTestConsole : IAnsiConsole, IDisposable
{
    private readonly TestConsole _inner = new();

    /// <summary>Creates the console.</summary>
    /// <param name="isTerminal">Whether the output is a terminal.</param>
    /// <param name="width">The width the console reports; 0 stands for a console that cannot measure itself.</param>
    public TerminalTestConsole(bool isTerminal, int width)
    {
        Profile = new Profile(new TerminalOutput(_inner.Profile.Out.Writer, isTerminal, width), _inner.Profile.Capabilities, Encoding.UTF8)
        {
            Width = width > 0 ? width : 80
        };
        Width = width;
    }

    /// <summary>The width the output reports.</summary>
    public int Width { get; }

    /// <summary>Everything written to the console.</summary>
    public string Output => _inner.Output;

    /// <inheritdoc />
    public Profile Profile { get; }

    /// <inheritdoc />
    public IAnsiConsoleCursor Cursor => _inner.Cursor;

    /// <inheritdoc />
    public IAnsiConsoleInput Input => _inner.Input;

    /// <inheritdoc />
    public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;

    /// <inheritdoc />
    public RenderPipeline Pipeline => _inner.Pipeline;

    /// <inheritdoc />
    public void Clear(bool home) => _inner.Clear(home);

    /// <inheritdoc />
    public void Write(IRenderable renderable) => _inner.Write(renderable);

    /// <inheritdoc />
    public void WriteAnsi(Action<AnsiWriter> action) => _inner.WriteAnsi(action);

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();

    private sealed class TerminalOutput(TextWriter writer, bool isTerminal, int width) : IAnsiConsoleOutput
    {
        public TextWriter Writer => writer;

        public bool IsTerminal => isTerminal;

        public int Width => width;

        public int Height => 24;

        public void SetEncoding(Encoding encoding)
        {
        }
    }
}
