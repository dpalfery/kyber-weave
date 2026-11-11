using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Cli.Commands.Squad.Infrastructure;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Doctor;

/// <summary>
///     Installs, inspects and removes the marked block that <c>doctor --ensure</c> keeps in the clone's shared git
///     post-checkout hook.
/// </summary>
/// <remarks>
///     A new worktree has no CodeGraph index, and nothing builds one until a run asks for it. The block starts
///     <c>kyber-weave doctor --post-checkout .</c> in the background on the first checkout of a worktree, so the index
///     is ready by the time an agent looks. The hook lives in the shared <c>.git/hooks</c> directory, which every
///     worktree of the clone uses, so one install covers them all.
/// </remarks>
public static class PostCheckoutHook
{
    /// <summary>The check id the hook status reports under. Agents branch on it.</summary>
    public const string CheckId = "githook.post-checkout";

    /// <summary>The git hook file name the block lives in.</summary>
    private const string HookName = "post-checkout";

    /// <summary>The prefix every problem reason carries, so a worker can tell a warning from a passing status.</summary>
    private const string WarningPrefix = "warning: ";

    /// <summary>The executable mode a new hook gets on Unix: readable and executable by everyone, writable by its owner.</summary>
    private const UnixFileMode ExecutableMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>The command a worker runs to repair a hook that is missing, out of date or unwanted.</summary>
    private const string EnsureHint = "run: kyber-weave doctor --ensure";

    /// <summary>
    ///     Reads the hook file and reports whether the marked block is present and current.
    /// </summary>
    /// <param name="repositoryRoot">The repository root whose shared hook is inspected.</param>
    /// <param name="executor">Runs git, which resolves the shared hooks directory.</param>
    /// <returns>The <c>githook.post-checkout</c> check, or null when there is nothing to report.</returns>
    public static DoctorCheck? Inspect(string repositoryRoot, IProcessExecutor executor) =>
        Apply(repositoryRoot, executor, write: false);

    /// <summary>
    ///     Installs or refreshes the marked block in the shared post-checkout hook, or removes it when the repository
    ///     sets <c>worktree-index-hook: false</c>.
    /// </summary>
    /// <param name="repositoryRoot">The repository root whose shared hook is written.</param>
    /// <param name="executor">Runs git, which resolves the shared hooks directory.</param>
    /// <returns>The <c>githook.post-checkout</c> check describing what was done, or null when there is nothing to report.</returns>
    public static DoctorCheck? Ensure(string repositoryRoot, IProcessExecutor executor) =>
        Apply(repositoryRoot, executor, write: true);

    /// <summary>The shared flow behind <see cref="Inspect"/> and <see cref="Ensure"/>: the same decisions, with writes only when asked.</summary>
    private static DoctorCheck? Apply(string root, IProcessExecutor executor, bool write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(executor);

        // A repository without governed docs has no index to keep, so it gets no hook and no status line.
        if (!DocsEnabled.IsEnabled(root, ReadText))
            return null;

        try
        {
            return ApplyToHook(root, executor, write);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Warn($"the post-checkout hook could not be read or written: {ex.Message}", null);
        }
    }

    private static DoctorCheck ApplyToHook(string root, IProcessExecutor executor, bool write)
    {
        (string? folder, DoctorCheck? problem) = ResolveHooksFolder(root, executor);
        if (problem is not null || folder is null)
            return problem ?? Warn("the git hooks folder could not be found", null);

        string hook = Path.Combine(folder, HookName);
        bool exists = File.Exists(hook);
        if (exists && !IsExecutable(hook))
            return Warn(
                "the post-checkout hook exists without the executable bit, so kyber-weave left it alone; " +
                "add the executable bit or remove the hook, then " + EnsureHint,
                null);

        string? existing = exists ? File.ReadAllText(hook) : null;

        // An unreadable config counts as on: a broken file must not be the thing that removes the index hook.
        bool wanted = KyberWeaveConfigLoader.TryLoad(root) is { Config: { } config } ? config.WorktreeIndexHook : true;

        return wanted
            ? InstallBlock(hook, existing, write)
            : RemoveBlock(hook, existing, write);
    }

    /// <summary>Makes the block present and current, or only reports whether it is.</summary>
    private static DoctorCheck InstallBlock(string hook, string? existing, bool write)
    {
        HookTextEdit edit = PostCheckoutHookText.Install(existing);

        switch (edit.Outcome)
        {
            case HookTextOutcome.Unchanged:
                return Pass("installed and current");

            case HookTextOutcome.RefusedNotPosixShell:
                return Warn(
                    "the post-checkout hook is not a POSIX shell script (sh, bash, dash, ksh or zsh), so kyber-weave " +
                    "left it alone; add the index block by hand or remove the hook",
                    null);

            case HookTextOutcome.RefusedNoShebang:
                return Warn(
                    "the post-checkout hook has no #! line, so kyber-weave left it alone; add a shell #! line " +
                    "or remove the hook, then " + EnsureHint,
                    null);

            case HookTextOutcome.RefusedCrlf:
                return Warn(
                    "the post-checkout hook uses CRLF line endings, so kyber-weave left it alone; convert it to LF " +
                    "then " + EnsureHint,
                    null);

            case HookTextOutcome.RefusedUnmatchedBlock:
                return Warn(
                    "the post-checkout hook has the start of the kyber-weave block but not its end, so kyber-weave " +
                    "left it alone; delete the partial block by hand, then " + EnsureHint,
                    null);
        }

        if (!write)
        {
            return Warn(
                edit.Outcome == HookTextOutcome.Created
                    ? "the post-checkout hook is missing its kyber-weave block; " + EnsureHint
                    : "the post-checkout hook's kyber-weave block is missing or out of date; " + EnsureHint,
                null);
        }

        string text = edit.Text ?? throw new InvalidOperationException("A written hook edit always carries its text.");
        Directory.CreateDirectory(Path.GetDirectoryName(hook) ?? throw new IOException($"'{hook}' has no folder."));
        File.WriteAllText(hook, text);

        if (edit.Outcome == HookTextOutcome.Created && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, ExecutableMode);
            return Pass("installed the post-checkout hook with the worktree index block");
        }

        return Pass(edit.Outcome == HookTextOutcome.Updated
            ? "installed: refreshed the kyber-weave block in the post-checkout hook"
            : "installed: added the kyber-weave block to the post-checkout hook");
    }

    /// <summary>Takes the block out when the repository has turned the hook off, keeping every other line.</summary>
    private static DoctorCheck RemoveBlock(string hook, string? existing, bool write)
    {
        if (existing is null)
            return Pass("not installed: worktree-index-hook is false");

        HookTextEdit edit = PostCheckoutHookText.Remove(existing);

        switch (edit.Outcome)
        {
            case HookTextOutcome.Unchanged:
                return Pass("not installed: worktree-index-hook is false");

            case HookTextOutcome.RefusedUnmatchedBlock:
                return Warn(
                    "the post-checkout hook has the start of the kyber-weave block but not its end; delete the " +
                    "partial block by hand, then " + EnsureHint,
                    null);
        }

        if (!write)
        {
            return Warn(
                "the post-checkout hook still has the kyber-weave block but worktree-index-hook is false; " + EnsureHint,
                null);
        }

        if (edit.Outcome == HookTextOutcome.DeleteFile)
        {
            File.Delete(hook);
            return Pass("removed the post-checkout hook, which held only the kyber-weave block; worktree-index-hook is false");
        }

        File.WriteAllText(hook, edit.Text ?? throw new InvalidOperationException("A removal that keeps the hook carries its text."));
        return Pass("removed the kyber-weave block from the post-checkout hook; worktree-index-hook is false");
    }

    /// <summary>
    ///     Finds the hooks folder git uses for this tree, refusing one a repository tracks.
    /// </summary>
    /// <remarks>
    ///     <c>git rev-parse --git-path hooks</c> already applies <c>core.hooksPath</c> and the shared-folder rule of a
    ///     linked worktree, so the answer is the folder git would run hooks from. A relative answer is relative to
    ///     the root. A hooks folder inside the working tree that git tracks belongs to the repository: writing there
    ///     would leave a change to commit that nobody asked for.
    /// </remarks>
    private static (string? Folder, DoctorCheck? Problem) ResolveHooksFolder(string root, IProcessExecutor executor)
    {
        ProcessResult? hooks = Git(root, executor, "rev-parse", "--git-path", "hooks");
        string answer = hooks is { ExitCode: 0 } ? hooks.Value.StandardOutput.Trim() : string.Empty;
        if (answer.Length == 0)
            return (null, Warn("git could not say where its hooks folder is", null));

        string folder = Path.GetFullPath(Path.Combine(root, answer));
        if (!IsInside(root, folder))
            return (folder, null);

        ProcessResult? configured = Git(root, executor, "config", "--get", "core.hooksPath");
        bool redirected = configured is { ExitCode: 0 } && configured.Value.StandardOutput.Trim().Length > 0;
        if (!redirected)
            return (folder, null);

        string relative = Path.GetRelativePath(Path.GetFullPath(root), folder).Replace('\\', '/');
        ProcessResult? tracked = Git(root, executor, "ls-files", "--", relative);
        if (tracked is null || tracked.Value.ExitCode != 0)
            return (null, Warn($"git could not say whether the hooks folder '{relative}' is tracked", null));

        if (tracked.Value.StandardOutput.Trim().Length > 0)
        {
            return (null, Warn(
                $"core.hooksPath points at '{relative}', which git tracks, so kyber-weave will not write into it; " +
                "point core.hooksPath at a folder outside the repository, or untrack it",
                null));
        }

        return (folder, null);
    }

    /// <summary>Whether <paramref name="path"/> is inside <paramref name="root"/>, by its relative path rather than a string prefix.</summary>
    private static bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }

    private static bool IsExecutable(string hook) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(hook) & UnixFileMode.UserExecute) != 0;

    /// <summary>Runs one git child in the root, or returns null when git could not be started or did not finish.</summary>
    private static ProcessResult? Git(string root, IProcessExecutor executor, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            return executor.Run(startInfo, string.Empty);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadText(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    private static DoctorCheck Pass(string reason) =>
        new(CheckId, true, reason, null);

    /// <summary>A hook problem: a passing check whose reason starts with <c>warning:</c>, so it never changes the exit code.</summary>
    private static DoctorCheck Warn(string message, string? hint) =>
        new(CheckId, true, WarningPrefix + message, hint);
}

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

        if (!TryStripOwnedLines(lines, out int firstBlock))
            return new HookTextEdit(HookTextOutcome.RefusedUnmatchedBlock, null);

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
        if (!TryStripOwnedLines(lines, out _))
            return new HookTextEdit(HookTextOutcome.RefusedUnmatchedBlock, null);

        if (lines.Count == before)
            return new HookTextEdit(HookTextOutcome.Unchanged, null);

        string remaining = string.Join('\n', lines);
        return IsOnlyShebangOrEmpty(remaining)
            ? new HookTextEdit(HookTextOutcome.DeleteFile, null)
            : new HookTextEdit(HookTextOutcome.Removed, remaining);
    }

    /// <summary>
    ///     Removes every owned block and every stray end marker from <paramref name="lines"/>.
    /// </summary>
    /// <remarks>
    ///     A marker is a whole line, never a fragment of one, so a hook that merely mentions the marker text inside its
    ///     own command is not mistaken for a block. Every copy of the block goes, so a hook that ended up with two is
    ///     left with one after the next install. An end marker with no begin marker before it is a leftover of ours.
    /// </remarks>
    /// <param name="lines">The hook's lines, edited in place.</param>
    /// <param name="firstBlock">The index the first removed block started at, or -1 when there was none.</param>
    /// <returns>False when a begin marker has no end marker after it, in which case nothing is safe to remove.</returns>
    private static bool TryStripOwnedLines(List<string> lines, out int firstBlock)
    {
        firstBlock = -1;
        for (int index = 0; index < lines.Count;)
        {
            if (lines[index] == EndMarker)
            {
                lines.RemoveAt(index);
                continue;
            }

            if (lines[index] != BeginMarker)
            {
                index++;
                continue;
            }

            int end = lines.IndexOf(EndMarker, index + 1);
            if (end < 0)
                return false;

            lines.RemoveRange(index, end - index + 1);
            if (firstBlock < 0)
                firstBlock = index;
        }

        return true;
    }

    /// <summary>
    ///     Whether the shebang names a POSIX shell, directly or through <c>/usr/bin/env</c>. Flags and
    ///     <c>NAME=value</c> assignments after <c>env</c> are skipped.
    /// </summary>
    private static bool IsPosixShell(string shebang)
    {
        string[] words = shebang[2..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return false;

        string interpreter = BaseName(words[0]);
        if (interpreter == "env")
        {
            string? named = words.Skip(1).FirstOrDefault(
                word => !word.StartsWith('-') && !word.Contains('=', StringComparison.Ordinal));
            if (named is null)
                return false;

            interpreter = BaseName(named);
        }

        return interpreter is "sh" or "bash" or "dash" or "ksh" or "zsh";
    }

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

    /// <summary>The hook has the block's begin marker but no end marker, so it was left alone rather than guessed at.</summary>
    RefusedUnmatchedBlock,
}
