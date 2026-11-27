using System.ComponentModel;
using System.Diagnostics;
using KyberWeave.Contracts.Paths;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Doctor;
using KyberWeave.Core.Processes;
using KyberWeave.Domain.Doctor;
using KyberWeave.Kernel.FileSystem;

namespace KyberWeave.Application.Doctor;

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
public sealed class PostCheckoutHookInstaller(
    IGitHookFiles hooks,
    IDoctorFileSystem files,
    IRepositoryToolSettings settings,
    IPathResolver paths,
    IProcessEnvironment environment)
{
    /// <summary>The check id the hook status reports under. Agents branch on it.</summary>
    public const string CheckId = "githook.post-checkout";

    /// <summary>The git hook file name the block lives in.</summary>
    private const string HookName = "post-checkout";

    /// <summary>The prefix every problem reason carries, so a worker can tell a warning from a passing status.</summary>
    private const string WarningPrefix = "warning: ";

    /// <summary>The command a worker runs to repair a hook that is missing, out of date or unwanted.</summary>
    private const string EnsureHint = "run: kyber-weave doctor --ensure";

    /// <summary>
    ///     Reads the hook file and reports whether the marked block is present and current.
    /// </summary>
    /// <param name="repositoryRoot">The repository root whose shared hook is inspected.</param>
    /// <param name="executor">Runs git, which resolves the shared hooks directory.</param>
    /// <returns>The <c>githook.post-checkout</c> check, or null when there is nothing to report.</returns>
    public DoctorCheck? Inspect(string repositoryRoot, IProcessExecutor executor) =>
        Apply(repositoryRoot, executor, write: false);

    /// <summary>
    ///     Installs or refreshes the marked block in the shared post-checkout hook, or removes it when the repository
    ///     sets <c>worktree-index-hook: false</c>.
    /// </summary>
    /// <param name="repositoryRoot">The repository root whose shared hook is written.</param>
    /// <param name="executor">Runs git, which resolves the shared hooks directory.</param>
    /// <returns>The <c>githook.post-checkout</c> check describing what was done, or null when there is nothing to report.</returns>
    public DoctorCheck? Ensure(string repositoryRoot, IProcessExecutor executor) =>
        Apply(repositoryRoot, executor, write: true);

    /// <summary>The shared flow behind <see cref="Inspect"/> and <see cref="Ensure"/>: the same decisions, with writes only when asked.</summary>
    private DoctorCheck? Apply(string root, IProcessExecutor executor, bool write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(executor);

        // A repository without governed docs has no index to keep, so it gets no hook and no status line.
        if (!DocsEnabled.IsEnabled(root, ReadText, files.DirectoryExists))
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

    private DoctorCheck ApplyToHook(string root, IProcessExecutor executor, bool write)
    {
        (string? folder, DoctorCheck? problem) = ResolveHooksFolder(root, executor);
        if (problem is not null || folder is null)
            return problem ?? Warn("the git hooks folder could not be found", null);

        string hook = Path.Combine(folder, HookName);
        bool exists = hooks.Exists(hook);
        if (exists && !hooks.IsExecutable(hook))
            return Warn(
                "the post-checkout hook exists without the executable bit, so kyber-weave left it alone; " +
                "add the executable bit or remove the hook, then " + EnsureHint,
                null);

        string? existing = exists ? hooks.ReadAllText(hook) : null;

        // An unreadable config counts as on: a broken file must not be the thing that removes the index hook.
        bool wanted = settings.Read(root).WorktreeIndexHook;

        return wanted
            ? InstallBlock(hook, existing, write)
            : RemoveBlock(hook, existing, write);
    }

    /// <summary>Makes the block present and current, or only reports whether it is.</summary>
    private DoctorCheck InstallBlock(string hook, string? existing, bool write)
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
        hooks.Write(hook, text, makeExecutable: edit.Outcome == HookTextOutcome.Created);

        if (edit.Outcome == HookTextOutcome.Created && !OperatingSystem.IsWindows())
        {
            return Pass("installed the post-checkout hook with the worktree index block");
        }

        return Pass(edit.Outcome == HookTextOutcome.Updated
            ? "installed: refreshed the kyber-weave block in the post-checkout hook"
            : "installed: added the kyber-weave block to the post-checkout hook");
    }

    /// <summary>Takes the block out when the repository has turned the hook off, keeping every other line.</summary>
    private DoctorCheck RemoveBlock(string hook, string? existing, bool write)
    {
        if (existing is null)
            return Pass("not installed: worktree-index-hook is false");

        HookTextEdit edit = PostCheckoutHookText.Remove(existing);

        switch (edit.Outcome)
        {
            case HookTextOutcome.Unchanged:
                return Pass("not installed: worktree-index-hook is false");
        }

        if (!write)
        {
            return Warn(
                "the post-checkout hook still has the kyber-weave block but worktree-index-hook is false; " + EnsureHint,
                null);
        }

        if (edit.Outcome == HookTextOutcome.DeleteFile)
        {
            hooks.Delete(hook);
            return Pass("removed the post-checkout hook, which held only the kyber-weave block; worktree-index-hook is false");
        }

        hooks.Write(hook, edit.Text ?? throw new InvalidOperationException("A removal that keeps the hook carries its text."), makeExecutable: false);
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
    private (string? Folder, DoctorCheck? Problem) ResolveHooksFolder(string root, IProcessExecutor executor)
    {
        ProcessResult? hooks = Git(root, executor, "rev-parse", "--git-path", "hooks");
        string answer = hooks is { ExitCode: 0 } ? hooks.Value.StandardOutput.Trim() : string.Empty;
        if (answer.Length == 0)
            return (null, Warn("git could not say where its hooks folder is", null));

        string folder = paths.GetFullPath(Path.Combine(root, answer), environment.CurrentDirectory);
        if (!IsInside(root, folder))
            return (folder, null);

        ProcessResult? configured = Git(root, executor, "config", "--get", "core.hooksPath");
        bool redirected = configured is { ExitCode: 0 } && configured.Value.StandardOutput.Trim().Length > 0;
        if (!redirected)
            return (folder, null);

        string relative = Path.GetRelativePath(paths.GetFullPath(root, environment.CurrentDirectory), folder).Replace('\\', '/');
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
    private bool IsInside(string root, string path)
    {
        string relative = Path.GetRelativePath(paths.GetFullPath(root, environment.CurrentDirectory), path);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }

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

    private string? ReadText(string path) =>
        files.FileExists(path) ? files.ReadAllText(path) : null;

    private static DoctorCheck Pass(string reason) =>
        new(CheckId, true, reason, null);

    /// <summary>A hook problem: a passing check whose reason starts with <c>warning:</c>, so it never changes the exit code.</summary>
    private static DoctorCheck Warn(string message, string? hint) =>
        new(CheckId, true, WarningPrefix + message, hint);
}
