using KyberWeave.Contracts.Host;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="IConsole"/>: it captures what was written and scripts the lines to read.</summary>
public sealed class FakeConsole : IConsole
{
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly Queue<string> _input = new();

    /// <summary>Creates a console whose input is, by default, an operator's terminal.</summary>
    /// <param name="isInputRedirected">Whether the console reports redirected input.</param>
    public FakeConsole(bool isInputRedirected = false)
    {
        IsInputRedirected = isInputRedirected;
    }

    /// <inheritdoc />
    public TextWriter Out => _output;

    /// <inheritdoc />
    public TextWriter Error => _error;

    /// <inheritdoc />
    public bool IsInputRedirected { get; set; }

    /// <summary>Gets everything written to <see cref="Out"/>.</summary>
    public string Output => _output.ToString();

    /// <summary>Gets everything written to <see cref="Error"/>.</summary>
    public string ErrorOutput => _error.ToString();

    /// <inheritdoc />
    public string? ReadLine() => _input.Count > 0 ? _input.Dequeue() : null;

    /// <summary>Supplies the lines <see cref="ReadLine"/> returns, in order, before it reports the end of input.</summary>
    /// <param name="text">The input text; each <c>\n</c> ends a line and a leading <c>\r</c> is dropped.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeConsole ScriptInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] lines = text.Split('\n');
        int count = lines.Length;
        if (count > 0 && lines[count - 1].Length == 0)
        {
            count--;
        }

        for (int index = 0; index < count; index++)
        {
            _input.Enqueue(lines[index].TrimEnd('\r'));
        }

        return this;
    }
}
