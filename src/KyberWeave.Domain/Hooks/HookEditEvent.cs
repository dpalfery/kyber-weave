namespace KyberWeave.Domain.Hooks;

/// <summary>A file an agent just edited, as reported by a harness.</summary>
/// <param name="FilePath">The edited file, absolute or relative to <paramref name="Cwd"/>.</param>
/// <param name="Cwd">The agent's working directory.</param>
/// <param name="ProjectDirectory">The session's project directory from the harness, or null when it names none; the edited file and its repository root must lie inside it, or inside <paramref name="Cwd"/> when it is null.</param>
public sealed record HookEditEvent(string FilePath, string Cwd, string? ProjectDirectory = null);
