using JetBrains.Annotations;

namespace KyberWeave.Core.Skills.Parsing;

/// <summary>Raised when a SKILL.md cannot be parsed at all (malformed YAML, missing fences).</summary>
public sealed class SkillParseException : Exception
{
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
