namespace KyberWeave.Core.Squad.Briefs;

/// <summary>Reads a fact of <see cref="BriefFacts"/> by name.</summary>
public static class BriefFactsExtensions
{
    /// <summary>The value of the named fact, or null when it is unknown.</summary>
    /// <param name="facts">The facts.</param>
    /// <param name="fact">The fact name.</param>
    /// <returns>The value, or null.</returns>
    public static string? Get(this BriefFacts facts, string fact)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return fact switch
        {
            "path" => facts.Path,
            "autonomy" => facts.Autonomy,
            "mode" => facts.Mode,
            _ => null,
        };
    }
}
