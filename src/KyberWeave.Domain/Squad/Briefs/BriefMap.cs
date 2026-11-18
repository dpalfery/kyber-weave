namespace KyberWeave.Core.Squad.Briefs;

/// <summary>One clause of a condition: the fact and the values it accepts.</summary>
/// <param name="Fact">The fact name.</param>
/// <param name="Values">The values the fact may have for the clause to hold.</param>
public sealed record BriefClause(string Fact, IReadOnlyList<string> Values);

/// <summary>
/// A condition on the run's facts: every clause must hold. A clause on an unknown fact holds, so a fact
/// the machine could not resolve never removes instructions.
/// </summary>
/// <param name="Clauses">The clauses; none means the condition always holds.</param>
public sealed record BriefCondition(IReadOnlyList<BriefClause> Clauses)
{
    /// <summary>The condition with no clauses.</summary>
    public static BriefCondition Always { get; } = new([]);
}

/// <summary>One reference file in the map: its condition and the condition of each heading in it.</summary>
/// <param name="File">The authored file name, for example <c>plan-path.md</c>.</param>
/// <param name="When">The condition on the whole file.</param>
/// <param name="Sections">Heading to extra condition, keyed by the heading as the map spells it.</param>
public sealed record BriefFileRule(string File, BriefCondition When, IReadOnlyDictionary<string, BriefCondition> Sections);

/// <summary>The map from each agent's reference files and headings to the facts they apply to.</summary>
public sealed class BriefMap
{
    /// <summary>The schema value the map carries.</summary>
    public const string Schema = "kyber-squad.briefs/v1";

    private readonly IReadOnlyDictionary<string, IReadOnlyList<BriefFileRule>> _agents;

    /// <summary>Creates a map from the file rules of each agent.</summary>
    /// <param name="agents">Each agent's file rules in map order.</param>
    public BriefMap(IReadOnlyDictionary<string, IReadOnlyList<BriefFileRule>> agents)
    {
        _agents = agents;
    }

    /// <summary>The agents the map describes, in map order.</summary>
    public IReadOnlyCollection<string> Agents => _agents.Keys.ToArray();

    /// <summary>The file rules of <paramref name="agent"/> in map order, or null when the map does not know the agent.</summary>
    public IReadOnlyList<BriefFileRule>? FilesOf(string agent) =>
        _agents.TryGetValue(agent, out IReadOnlyList<BriefFileRule>? files) ? files : null;
}

/// <summary>The brief map is malformed, or a brief was asked for something it does not describe.</summary>
public sealed class BriefMapException : Exception
{
    /// <summary>Creates the exception with no message.</summary>
    public BriefMapException()
    {
    }

    /// <summary>Creates the exception with the message that names the problem.</summary>
    public BriefMapException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the message and the failure that caused it.</summary>
    public BriefMapException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
