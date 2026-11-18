namespace KyberWeave.Cli.Update;

/// <summary>Starts a child that shares this console and waits for it, bounded.</summary>
/// <remarks>
/// The process seam's role views capture a child's output, so none of them can run a step that writes to the user's
/// console. This is the one update-side place that does.
/// </remarks>
internal interface IConsoleProcessLauncher
{
    /// <summary>Starts a child that inherits this console (or has its output discarded) and waits at most <paramref name="timeout"/> for it.</summary>
    /// <param name="fileName">The program to start.</param>
    /// <param name="arguments">The program's arguments.</param>
    /// <param name="timeout">How long to wait before the child's whole process tree is stopped.</param>
    /// <param name="discardOutput">True to drain and drop the child's output instead of sharing the console with it.</param>
    /// <returns>The child's exit code.</returns>
    /// <exception cref="InvalidOperationException">The process could not be started.</exception>
    /// <exception cref="TimeoutException">The child outlasted <paramref name="timeout"/> and its process tree was stopped.</exception>
    int Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, bool discardOutput = false);
}
