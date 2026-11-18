namespace KyberWeave.Domain.Doctor;

/// <summary>
///     Pure text edits to the post-checkout hook file. Holds no I/O, so every refusal and edit is testable on its own.
/// </summary>
public static class PostCheckoutHookText
{
    /// <summary>The line that opens the marked block. Matched as text, so an edit finds the block it wrote earlier.</summary>
    private const string BeginMarker = "# >>> kyber-weave worktree index >>>";

    /// <summary>The line that closes the marked block.</summary>
    private const string EndMarker = "# <<< kyber-weave worktree index <<<";

    /// <summary>The marked block, including its begin and end markers, as written into the hook.</summary>
    /// <remarks>
    ///     The block must never call <c>exit</c>: it sits directly after the owner's shebang, and a bare exit would end
    ///     the hook before the owner's own body ran. Under <c>set -e</c> and <c>set -u</c> every expansion is guarded and
    ///     every command is either a test or backgrounded, so a failing check cannot stop the hook early.
    /// </remarks>
    public const string Block =
        BeginMarker + "\n" +
        "# Managed by 'kyber-weave doctor --ensure'. Set 'worktree-index-hook: false' in .kyber-weave/kyber-weave.yml to remove it.\n" +
        "# On the first checkout of a new worktree, starts its CodeGraph index in the background so it is ready when an agent looks.\n" +
        "case \"${1:-}\" in\n" +
        "    '') ;;\n" +
        "    *[!0]*) ;;\n" +
        "    *)\n" +
        "        if [ \"${3:-}\" = \"1\" ] && command -v kyber-weave >/dev/null 2>&1; then\n" +
        "            (\n" +
        "                unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_PREFIX GIT_COMMON_DIR\n" +
        "                gitdir=$(git rev-parse --absolute-git-dir 2>/dev/null) || gitdir=\n" +
        "                if [ -n \"$gitdir\" ]; then log=\"$gitdir/kyber-weave-index.log\"; else log=/dev/null; fi\n" +
        "                kyber-weave doctor --post-checkout . </dev/null >>\"$log\" 2>&1 &\n" +
        "            ) || :\n" +
        "        fi\n" +
        "        ;;\n" +
        "esac\n" +
        EndMarker + "\n";

    /// <summary>
    ///     Computes the hook text after installing the block into <paramref name="existing"/>.
    /// </summary>
    /// <param name="existing">The current hook text, or null when the hook file does not exist.</param>
    /// <returns>The outcome and, when the file must change, the new text.</returns>
    /// <remarks>
    ///     The block goes directly after the shebang line, so it runs before the owner's own body. A hook that already
    ///     holds the block has it replaced in place, and the text around it is kept byte for byte.
    /// </remarks>
    public static HookTextEdit Install(string? existing)
    {
        // An empty file carries nothing to keep, so it is written the same way as a missing one.
        if (string.IsNullOrWhiteSpace(existing))
            return new HookTextEdit(HookTextOutcome.Created, "#!/bin/sh\n" + Block);

        // CRLF is refused first: a shebang with a carriage return would not name its interpreter, and an edit
        // through it would leave mixed line endings that git and the shell read differently.
        if (existing.Contains("\r\n", StringComparison.Ordinal))
            return new HookTextEdit(HookTextOutcome.RefusedCrlf, null);

        List<string> lines = [.. existing.Split('\n')];
        if (!lines[0].StartsWith("#!", StringComparison.Ordinal))
            return new HookTextEdit(HookTextOutcome.RefusedNoShebang, null);

        if (!IsPosixShell(lines[0]))
            return new HookTextEdit(HookTextOutcome.RefusedNotPosixShell, null);

        StripOwnedLines(lines, out int firstBlock);

        // A one-line hook has no line break after its shebang; the block needs one before it and one after.
        if (lines.Count == 1)
            lines.Add(string.Empty);

        // A block already there keeps its place; a new one goes directly after the shebang, before the owner's body.
        lines.InsertRange(firstBlock < 0 ? 1 : firstBlock, Block[..^1].Split('\n'));
        string updated = string.Join('\n', lines);
        if (string.Equals(updated, existing, StringComparison.Ordinal))
            return new HookTextEdit(HookTextOutcome.Unchanged, null);

        return new HookTextEdit(firstBlock < 0 ? HookTextOutcome.Inserted : HookTextOutcome.Updated, updated);
    }

    /// <summary>
    ///     Computes the hook text after removing the marked block from <paramref name="existing"/>.
    /// </summary>
    /// <param name="existing">The current hook text.</param>
    /// <returns>The outcome and, when the file must change, the new text.</returns>
    public static HookTextEdit Remove(string existing)
    {
        ArgumentNullException.ThrowIfNull(existing);

        List<string> lines = [.. existing.Split('\n')];
        int before = lines.Count;
        StripOwnedLines(lines, out _);
        if (lines.Count == before)
            return new HookTextEdit(HookTextOutcome.Unchanged, null);

        string remaining = string.Join('\n', lines);
        return IsOnlyShebangOrEmpty(remaining)
            ? new HookTextEdit(HookTextOutcome.DeleteFile, null)
            : new HookTextEdit(HookTextOutcome.Removed, remaining);
    }

    /// <summary>
    ///     Removes every owned block and every stray marker from <paramref name="lines"/>.
    /// </summary>
    /// <remarks>
    ///     A marker is a whole line, never a fragment of one, so a hook that merely mentions the marker text inside its
    ///     own command is not mistaken for a block; whitespace after it is ignored, because an editor that pads or
    ///     trims line ends must not make the block look absent, and whitespace before it is not, because an indented
    ///     copy is the owner's own text. Every copy of the block goes, so a hook that ended up with
    ///     two is left with one after the next install. An end marker with no begin marker before it is a leftover of
    ///     ours. So is a begin marker with no end marker after it: it goes together with the lines after it that are
    ///     still the block's own, and anything else is the owner's and stays.
    /// </remarks>
    /// <param name="lines">The hook's lines, edited in place.</param>
    /// <param name="firstBlock">The index the first removed block started at, or -1 when there was none.</param>
    private static void StripOwnedLines(List<string> lines, out int firstBlock)
    {
        string[] block = Block[..^1].Split('\n');
        firstBlock = -1;
        for (int index = 0; index < lines.Count;)
        {
            if (IsMarker(lines[index], EndMarker))
            {
                lines.RemoveAt(index);
                continue;
            }

            if (!IsMarker(lines[index], BeginMarker))
            {
                index++;
                continue;
            }

            int end = lines.FindIndex(index + 1, line => IsMarker(line, EndMarker));
            int nextBegin = lines.FindIndex(index + 1, line => IsMarker(line, BeginMarker));
            int count;
            if (end >= 0 && (nextBegin < 0 || end < nextBegin))
            {
                count = end - index + 1;
            }
            else
            {
                // No end marker belongs to this begin marker. Only the lines that still read as the block are ours.
                count = 1;
                while (count < block.Length - 1
                       && index + count < lines.Count
                       && lines[index + count].TrimEnd() == block[count])
                {
                    count++;
                }
            }

            lines.RemoveRange(index, count);
            if (firstBlock < 0)
                firstBlock = index;
        }
    }

    // Only trailing whitespace is ignored. An indented line that reads like a marker sits inside the owner's own
    // code, a here-document for one, and the block is always written at the start of a line.
    private static bool IsMarker(string line, string marker) =>
        line.AsSpan().TrimEnd().SequenceEqual(marker);

    /// <summary>
    ///     Whether the shebang names a POSIX shell, directly or through <c>/usr/bin/env</c>. After <c>env</c>, flags,
    ///     the argument of a flag that takes one, and <c>NAME=value</c> assignments are skipped.
    /// </summary>
    private static bool IsPosixShell(string shebang)
    {
        string[] words = shebang[2..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return false;

        string interpreter = BaseName(words[0]);
        if (interpreter != "env")
            return IsShellName(interpreter);

        for (int index = 1; index < words.Length; index++)
        {
            string word = words[index];
            // These flags take their value as the next word. Short flags can be bundled, with the one that takes a
            // value last, as in -iu NAME. -S and the signal flags carry their value in the same word, if at all.
            bool takesNextWord = word is "--unset" or "--chdir" or "--argv0"
                || (word.Length > 1 && word[0] == '-' && word[1] != '-' && word[^1] is 'u' or 'C' or 'a');
            if (takesNextWord)
            {
                index++;
                continue;
            }

            if (word.StartsWith('-') || word.Contains('=', StringComparison.Ordinal))
                continue;

            return IsShellName(BaseName(word));
        }

        return false;
    }

    private static bool IsShellName(string name) =>
        name is "sh" or "bash" or "dash" or "ksh" or "zsh";

    /// <summary>The program name without its folder, and without the <c>.exe</c> Git for Windows writes.</summary>
    private static string BaseName(string path)
    {
        string name = path[(path.LastIndexOf('/') + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Whether nothing remains but a shebang line, so the hook carries no behaviour of its own.</summary>
    private static bool IsOnlyShebangOrEmpty(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length == 0 || (trimmed.StartsWith("#!", StringComparison.Ordinal) && !trimmed.Contains('\n', StringComparison.Ordinal));
    }
}

/// <summary>The result of a pure hook text edit.</summary>
/// <param name="Outcome">What the edit did, or why it refused.</param>
/// <param name="Text">The new hook text, or null when the file is unchanged or must be deleted.</param>
public sealed record HookTextEdit(HookTextOutcome Outcome, string? Text);

/// <summary>What a hook text edit did, or why it refused to touch the hook.</summary>
public enum HookTextOutcome
{
    /// <summary>The hook file did not exist and now holds the block.</summary>
    Created,

    /// <summary>The block was added to a hook that had other content.</summary>
    Inserted,

    /// <summary>An older block was replaced with the current one.</summary>
    Updated,

    /// <summary>The block was already current, so nothing changed.</summary>
    Unchanged,

    /// <summary>The block was removed and other content remains.</summary>
    Removed,

    /// <summary>The block was removed and nothing else remained, so the hook file should be deleted.</summary>
    DeleteFile,

    /// <summary>The hook is not a POSIX shell script, so it was left alone.</summary>
    RefusedNotPosixShell,

    /// <summary>The hook has no shebang line, so it was left alone.</summary>
    RefusedNoShebang,

    /// <summary>The hook uses CRLF line endings, so it was left alone.</summary>
    RefusedCrlf,
}
