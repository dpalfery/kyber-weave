using JetBrains.Annotations;

namespace KyberWeave.Core.Skills.Parsing;

/// <summary>Raised when a SKILL.md cannot be parsed at all (malformed YAML, missing fences).</summary>
public sealed class SkillParseException : Exception
{
    /// <summary>
    /// Required by <see cref="Exception"/> serialization and by CA1032. ReSharper reports
    /// it unused because nothing in this assembly constructs it by name; [UsedImplicitly]
    /// is how those two analyzers are told they are both right.
    /// </summary>
    [UsedImplicitly]
    public SkillParseException()
    {
    }

    public SkillParseException(string message)
        : base(message)
    {
    }

    public SkillParseException(string message, Exception? inner)
        : base(message, inner)
    {
    }
}
