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
    /// <param name="text">The input text. A carriage return, a line feed or the pair ends a line, as a real console does.</param>
    /// <returns>This fake, for chaining.</returns>
    public FakeConsole ScriptInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            if (current is not ('\r' or '\n'))
            {
                continue;
            }

            _input.Enqueue(text[start..index]);
            if (current == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
            }

            start = index + 1;
        }

        if (start < text.Length)
        {
            _input.Enqueue(text[start..]);
        }

        return this;
    }
}
