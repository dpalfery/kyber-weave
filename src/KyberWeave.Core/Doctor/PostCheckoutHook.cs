using KyberWeave.Cli.Commands.Squad.Infrastructure;

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

    /// <summary>
    ///     Reads the hook file and reports whether the marked block is present and current.
    /// </summary>
    /// <param name="repositoryRoot">The repository root whose shared hook is inspected.</param>
    /// <param name="executor">Runs git, which resolves the shared hooks directory.</param>
    /// <returns>The <c>githook.post-checkout</c> check, or null when there is nothing to report.</returns>
    public static DoctorCheck? Inspect(string repositoryRoot, IProcessExecutor executor) =>
        throw new NotImplementedException();

    /// <summary>
    ///     Installs or refreshes the marked block in the shared post-checkout hook.
    /// </summary>
    /// <param name="repositoryRoot">The repository root whose shared hook is written.</param>
    /// <param name="executor">Runs git, which resolves the shared hooks directory.</param>
    /// <returns>The <c>githook.post-checkout</c> check describing what was done, or null when there is nothing to report.</returns>
    public static DoctorCheck? Ensure(string repositoryRoot, IProcessExecutor executor) =>
        throw new NotImplementedException();
}

/// <summary>
///     Pure text edits to the post-checkout hook file. Holds no I/O, so every refusal and edit is testable on its own.
/// </summary>
public static class PostCheckoutHookText
{
    /// <summary>The marked block, including its begin and end markers, as written into the hook.</summary>
    /// <remarks>
    ///     Initialised from a method rather than a throwing getter, because CA1065 rejects a property getter that
    ///     throws <see cref="NotImplementedException"/>. The skeleton throws on first access instead.
    /// </remarks>
    public static string Block { get; } = Unimplemented();

    /// <summary>Throws the not-yet-implemented exception the skeleton members share.</summary>
    private static string Unimplemented() =>
        throw new NotImplementedException();

    /// <summary>
    ///     Computes the hook text after installing the block into <paramref name="existing"/>.
    /// </summary>
    /// <param name="existing">The current hook text, or null when the hook file does not exist.</param>
    /// <returns>The outcome and, when the file must change, the new text.</returns>
    public static HookTextEdit Install(string? existing) =>
        throw new NotImplementedException();

    /// <summary>
    ///     Computes the hook text after removing the marked block from <paramref name="existing"/>.
    /// </summary>
    /// <param name="existing">The current hook text.</param>
    /// <returns>The outcome and, when the file must change, the new text.</returns>
    public static HookTextEdit Remove(string existing) =>
        throw new NotImplementedException();
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
