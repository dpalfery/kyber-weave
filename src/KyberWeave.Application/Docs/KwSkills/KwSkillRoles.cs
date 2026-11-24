namespace KyberWeave.Core.Docs.KwSkills;

/// <summary>Maps an agent name to the role folder whose skills list that agent gets.</summary>
/// <remarks>
/// An agent named after a role reads that role's folder. The four review agents share the
/// <c>reviewer</c> folder, because a council seat is the reviewer role under another name; the table is
/// here, in one place, so adding a reviewing agent is one line.
/// </remarks>
internal static class KwSkillRoles
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["code-reviewer"] = "reviewer",
        ["plan-reviewer"] = "reviewer",
        ["review-lens"] = "reviewer",
        ["review-triage"] = "reviewer"
    };

    /// <summary>Gets the role folder for <paramref name="agent"/>, or null when it has no role skills.</summary>
    /// <param name="agent">The agent name, already trimmed.</param>
    /// <param name="roles">The role folders that exist: the declared roles and the shipped ones.</param>
    public static string? FolderFor(string agent, IReadOnlySet<string> roles)
    {
        if (roles.Contains(agent))
        {
            return agent;
        }

        return Aliases.TryGetValue(agent, out string? alias) && roles.Contains(alias) ? alias : null;
    }
}
