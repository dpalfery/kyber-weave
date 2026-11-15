namespace KyberWeave.Domain.Hooks;

/// <summary>A file an agent just edited, as reported by a harness.</summary>
/// <param name="FilePath">The edited file, absolute or relative to <paramref name="Cwd"/>.</param>
/// <param name="Cwd">The agent's working directory.</param>
/// <param name="ToolName">The editing tool, when the harness names it.</param>
public sealed record HookEditEvent(string FilePath, string Cwd, string? ToolName);
