using System.Text;

namespace KyberWeave.Cli.Commands.Docs;

/// <summary>
/// Text that came from a file, made safe to write to an operator's terminal.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Spectre.Console.Markup.Escape(string)"/> answers a different question: it stops a
/// <c>[</c> being read as a markup tag. It does not touch a control character, and a terminal acts
/// on those directly — an escape sequence in a server name, in a declared command, or in a
/// harness's own configuration colours, moves or clears the operator's console. Every one of those
/// values is written by somebody else: <c>kyber-weave.yml</c> is whatever was cloned, and a harness
/// configuration belongs to the harness.
/// </para>
/// <para>
/// So every value on the rendering path that came out of a file goes through here. The manual
/// wiring snippet and a finding's hint are the same hazard reached from two places, and they are
/// the same call on purpose: a second copy of this would be a second sequence table to keep in
/// step, and the day they disagree is the day one of them stops stripping.
/// </para>
/// <para>
/// Length is part of the same bound. A value is written from a file this process does not control,
/// so without a cap a single declaration can push everything the operator needed to read off the
/// scrollback — and a truncated value is a better outcome than an unreadable one.
/// </para>
/// </remarks>
internal static class TerminalText
{
    /// <summary>
    /// The introducer of every escape sequence. Dropped along with the sequence it starts, because
    /// the sequences that matter to a terminal are the ones a file has no business writing.
    /// </summary>
    private const char Escape = '\u001b';

    /// <summary>How much of one line is kept: enough for a remedy command, short of a flood.</summary>
    private const int MaxLineLength = 512;

    /// <summary>
    /// How much of a block is kept. Larger than a line because a snippet is TOML or JSON an operator
    /// pastes, and a cap tight enough to trim a real entry would make the snippet useless.
    /// </summary>
    private const int MaxBlockLength = 4096;

    /// <summary>The marker that says the value was cut, so a truncated entry is not read as whole.</summary>
    private const string TruncationMarker = "…";

    /// <summary>
    /// One line of text: no escape sequences, no line breaks, capped.
    /// </summary>
    /// <remarks>
    /// A line break is replaced rather than kept because a hint is rendered as one line under the
    /// finding it belongs to: a value carrying a newline could otherwise forge a second finding
    /// line above the operator's next prompt.
    /// </remarks>
    internal static string Line(string? text) => Clean(text, MaxLineLength, keepLineBreaks: false);

    /// <summary>
    /// A block of text: no escape sequences, its own line structure kept, capped.
    /// </summary>
    /// <remarks>
    /// The line breaks are load-bearing here — a snippet's indentation and row separators are what
    /// make it pasteable — so only the control characters that address the terminal are removed.
    /// </remarks>
    internal static string Block(string? text) => Clean(text, MaxBlockLength, keepLineBreaks: true);

    private static string Clean(string? text, int cap, bool keepLineBreaks)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        StringBuilder cleaned = new(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];

            if (current == Escape)
            {
                index = SkipEscapeSequence(text, index);
                continue;
            }

            if (current is '\n' or '\r' or '\t' or '\u2028' or '\u2029')
            {
                cleaned.Append(keepLineBreaks ? current : ' ');
                continue;
            }

            // Every remaining control character, C0 or C1, is dropped: what a terminal does with
            // them is the operator's problem only because it is not ours to do.
            if (char.IsControl(current))
                continue;

            cleaned.Append(current);
        }

        return cleaned.Length <= cap
            ? cleaned.ToString()
            : cleaned.ToString(0, cap) + TruncationMarker;
    }

    /// <summary>
    /// The index of the last character of the escape sequence starting at
    /// <paramref name="escape"/>, so the caller's loop resumes after it.
    /// </summary>
    /// <remarks>
    /// The shapes are the ones a terminal recognises: a CSI sequence ends at its final byte, an
    /// OSC or DCS sequence ends at BEL or at the string terminator, and anything else is the escape
    /// followed by a single character. An unterminated sequence consumes the rest of the value,
    /// which is the safe reading — the remainder is not text the operator asked to see.
    /// </remarks>
    private static int SkipEscapeSequence(string text, int escape)
    {
        int cursor = escape + 1;
        if (cursor >= text.Length)
            return escape;

        char kind = text[cursor];
        if (kind == '[')
        {
            cursor++;
            while (cursor < text.Length && text[cursor] >= ' ' && text[cursor] <= '?')
                cursor++;

            return Math.Min(cursor, text.Length - 1);
        }

        if (kind is ']' or 'P' or 'X' or '^' or '_')
        {
            cursor++;
            while (cursor < text.Length && text[cursor] != '\a' && text[cursor] != Escape)
                cursor++;

            if (cursor + 1 < text.Length && text[cursor] == Escape && text[cursor + 1] == '\\')
                cursor++;

            return Math.Min(cursor, text.Length - 1);
        }

        return Math.Min(cursor, text.Length - 1);
    }
}
