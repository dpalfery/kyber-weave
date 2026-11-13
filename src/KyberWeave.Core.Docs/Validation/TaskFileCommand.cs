using KyberWeave.Core.Processes;

namespace KyberWeave.Core.Docs.Validation;

/// <summary>
/// The one command that writes a task file, spelled for a finding's hint so the reader can paste it.
/// </summary>
internal static class TaskFileCommand
{
    /// <summary>The command that regenerates the task file of the plan or specification task list at <paramref name="artifactPath"/>.</summary>
    /// <param name="artifactPath">The repository-relative path of the plan, or of the specification's <c>tasks.md</c>.</param>
    public static string Write(string artifactPath) => "`" + Spell(artifactPath) + " --write`";

    /// <summary>The same command with <c>--check</c>, which changes nothing.</summary>
    /// <param name="artifactPath">The repository-relative path of the plan, or of the specification's <c>tasks.md</c>.</param>
    public static string Check(string artifactPath) => "`" + Spell(artifactPath) + " --check`";

    /// <summary>The command name and path, without a mode flag.</summary>
    /// <param name="artifactPath">The repository-relative path of the plan, or of the specification's <c>tasks.md</c>.</param>
    public static string Spell(string artifactPath) =>
        "kyber-weave docs tasks " + QuoteIfNeeded(artifactPath);

    // A plain path is printed bare so it reads and pastes the same everywhere; only a path a shell
    // would interpret is quoted.
    private static string QuoteIfNeeded(string path) =>
        path.All(character => char.IsAsciiLetterOrDigit(character) || character is '/' or '.' or '-' or '_')
            ? path
            : ShellQuote.Quote(path);
}
