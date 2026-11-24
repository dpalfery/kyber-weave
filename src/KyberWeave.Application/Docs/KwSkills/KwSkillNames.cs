namespace KyberWeave.Core.Docs.KwSkills;

/// <summary>The spelling every skill name must have.</summary>
internal static class KwSkillNames
{
    /// <summary>True for lowercase letters and digits in hyphen-separated, non-empty groups: <c>^[a-z0-9]+(-[a-z0-9]+)*$</c>.</summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Split('-').All(part => part.Length > 0 && part.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9')));
}
