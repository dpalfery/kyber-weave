namespace KyberWeave.Core.Arbiter.Facts;

/// <summary>A bounded window of a repository file, around one line.</summary>
/// <param name="Path">The repository-relative path the excerpt was read from.</param>
/// <param name="StartLine">The 1-based first line of the window.</param>
/// <param name="EndLine">The 1-based last line of the window that was read.</param>
/// <param name="Text">The window's lines joined with newlines, bounded by the excerpt character limit.</param>
/// <param name="Truncated">True when a line or the window was cut short to stay within bounds.</param>
public sealed record FileExcerpt(string Path, int StartLine, int EndLine, string Text, bool Truncated);
