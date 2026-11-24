using System.Text;
using KyberWeave.Contracts.Services;

namespace KyberWeave.Core.Docs.KwSkills;

/// <summary>
/// The words that list KW skills for a worker. The task prompt's <c>kw-skills</c> part and the coding-standard answer
/// both take their text from here, so the two cannot disagree about what a skill is or how to load it (D25).
/// </summary>
public static class KwSkillListText
{
    /// <summary>The first line of the list: how a worker loads one of these skills.</summary>
    public const string LoadInstruction =
        "These are Kyber-Weave skills, not harness skills. When a skill's description fits your work, load it by " +
        "calling the kw-docs load_skill tool with its name; never load one through your harness's own skill tool.";

    /// <summary>Renders the list: the instruction line, then one <c>- name: description</c> line per skill.</summary>
    /// <param name="entries">The skills to list, in the order they are to be read.</param>
    /// <returns>The list's text, with no trailing newline.</returns>
    public static string Render(IReadOnlyList<KwSkillEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        StringBuilder text = new(LoadInstruction);
        foreach (KwSkillEntry entry in entries)
        {
            text.Append("\n- ").Append(entry.Name).Append(": ").Append(Neutralise(entry.Description));
        }

        return text.ToString();
    }

    /// <summary>
    /// A description is repository-authored free text that lands in a model prompt one line per skill inside
    /// one <c>kw-skills</c> tag: a newline would forge a list entry and the tag's own markers would close or
    /// reopen the block, so whitespace runs collapse to one space and the markers are defused.
    /// </summary>
    private static string Neutralise(string description)
    {
        string collapsed = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed
            .Replace("<kw-skills>", "(kw-skills)", StringComparison.OrdinalIgnoreCase)
            .Replace("</kw-skills>", "(/kw-skills)", StringComparison.OrdinalIgnoreCase);
    }
}
