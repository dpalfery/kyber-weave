using Spectre.Console;
using Spectre.Console.Rendering;

namespace KyberWeave.Cli.Composition;

/// <summary>Hands every console member to the process-wide console the composition root names.</summary>
/// <param name="current">Reads the console to delegate to, on every member.</param>
/// <remarks>
/// The console is resolved once per command, but the console a process writes to can change after startup: the test
/// host routes output per asynchronous context by replacing <see cref="AnsiConsole.Console"/>. Reading the target on
/// each member keeps a command bound to whatever console is current, instead of snapshotting the one present when the
/// command was built.
/// </remarks>
internal sealed class ForwardingAnsiConsole(Func<IAnsiConsole> current) : IAnsiConsole
{
    private IAnsiConsole Target => current();

    /// <inheritdoc />
    public Profile Profile => Target.Profile;

    /// <inheritdoc />
    public IAnsiConsoleCursor Cursor => Target.Cursor;

    /// <inheritdoc />
    public IAnsiConsoleInput Input => Target.Input;

    /// <inheritdoc />
    public IExclusivityMode ExclusivityMode => Target.ExclusivityMode;

    /// <inheritdoc />
    public RenderPipeline Pipeline => Target.Pipeline;

    /// <inheritdoc />
    public void Clear(bool home) => Target.Clear(home);

    /// <inheritdoc />
    public void Write(IRenderable renderable) => Target.Write(renderable);

    /// <inheritdoc />
    public void WriteAnsi(Action<AnsiWriter> action) => Target.WriteAnsi(action);
}
