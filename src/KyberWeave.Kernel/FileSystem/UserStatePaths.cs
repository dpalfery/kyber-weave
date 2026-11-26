namespace KyberWeave.Core.FileSystem;

/// <summary>
/// The <c>.kyber-weave</c> state locations under an operator's home or a harness's global root: the one place the
/// folder name and the layout beneath it are written down.
/// </summary>
/// <remarks>
/// A caller names the state it wants, not the path it is kept at, so a store and its reader cannot spell the same
/// location differently. The folder name is exposed on its own for the repository-scope locations that share it.
/// </remarks>
public static class UserStatePaths
{
    /// <summary>The state folder's own name, shared by the user-level and repository-level layouts.</summary>
    public const string DirectoryName = ".kyber-weave";

    /// <summary>The state folder inside <paramref name="containingDirectory" />.</summary>
    /// <param name="containingDirectory">The home directory, repository root, or harness global root that holds it.</param>
    /// <returns>The absolute state directory path.</returns>
    public static string StateDirectory(string containingDirectory) => Path.Combine(containingDirectory, DirectoryName);

    /// <summary>The Arbiter state folder inside a user-level or harness-global root.</summary>
    /// <param name="containingDirectory">The home directory or harness global root that holds the state folder.</param>
    /// <returns>The absolute directory path.</returns>
    public static string ArbiterDirectory(string containingDirectory) => Path.Combine(StateDirectory(containingDirectory), "arbiter");

    /// <summary>The per-project Arbiter stores under a home directory.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string ArbiterProjectsDirectory(string home) => Path.Combine(ArbiterDirectory(home), "projects");

    /// <summary>The Factory worker session ledger's directory.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string WorkerSessionsDirectory(string home) => Path.Combine(ArbiterDirectory(home), "sessions");

    /// <summary>The worker budget files' directory.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string WorkerBudgetsDirectory(string home) => Path.Combine(ArbiterDirectory(home), "budgets");

    /// <summary>The Squad state folder under a home directory.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string SquadDirectory(string home) => Path.Combine(StateDirectory(home), "squad");

    /// <summary>The Squad global state folder.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string SquadGlobalDirectory(string home) => Path.Combine(SquadDirectory(home), "global");

    /// <summary>The Squad global lock file.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute file path.</returns>
    public static string SquadLockFile(string home) => Path.Combine(SquadGlobalDirectory(home), "squad.lock.yml");

    /// <summary>The user-level Squad profile snapshots.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string SquadProfilesDirectory(string home) => Path.Combine(SquadDirectory(home), "profiles");

    /// <summary>The user-level Squad model presets.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string SquadPresetsDirectory(string home) => Path.Combine(SquadDirectory(home), "presets");

    /// <summary>The user-level Squad preset selections.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string SquadSelectionsDirectory(string home) => Path.Combine(SquadDirectory(home), "selections");

    /// <summary>The user-level Squad instances.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string SquadInstancesDirectory(string home) => Path.Combine(SquadDirectory(home), "instances");

    /// <summary>The user-level Squad instance state.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string SquadInstanceStateDirectory(string home) => Path.Combine(SquadDirectory(home), "instance-state");

    /// <summary>The installed Kyber-Weave skills.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string KwSkillsDirectory(string home) => Path.Combine(StateDirectory(home), "kw-skills");

    /// <summary>The Copilot system-prompt file.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute file path.</returns>
    public static string CopilotPromptFile(string home) => Path.Combine(StateDirectory(home), "copilot", "system-prompt.yaml");

    /// <summary>The per-user host configuration file.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute file path.</returns>
    public static string HostConfigFile(string home) => Path.Combine(StateDirectory(home), "kyber-weave.yml");

    /// <summary>The per-user hooks configuration file.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute file path.</returns>
    public static string HooksConfigFile(string home) => Path.Combine(StateDirectory(home), "hooks.yml");

    /// <summary>The records one <c>run start</c> keeps per branch.</summary>
    /// <param name="home">The operator's home directory.</param>
    /// <returns>The absolute directory path.</returns>
    public static string RunsDirectory(string home) => Path.Combine(StateDirectory(home), "runs");
}
