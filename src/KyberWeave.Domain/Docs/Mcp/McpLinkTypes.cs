namespace KyberWeave.Core.Docs.Mcp;

/// <summary>Which segment of a configuration path stopped the guard, and why.</summary>
public enum McpLinkObstruction
{
    /// <summary>Every segment is a real file or directory, so the path may be read or written.</summary>
    None,

    /// <summary>A segment is a symbolic link, so the bytes would land where the link points.</summary>
    Link,

    /// <summary>A segment could not be inspected, so "absent" cannot be told from "denied".</summary>
    Denied
}

/// <summary>
/// The first segment of a configuration path the guard will not read through.
/// </summary>
/// <param name="Obstruction">Which of the two reasons applies, or none.</param>
/// <param name="Segment">
/// The repository-relative segment, with <c>/</c> separators, or null when nothing stopped the walk.
/// </param>
public readonly record struct McpLinkProbe(McpLinkObstruction Obstruction, string? Segment);
