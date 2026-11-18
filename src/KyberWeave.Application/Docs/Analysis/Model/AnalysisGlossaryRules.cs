using KyberWeave.Core.Docs.Analysis.Claims;

namespace KyberWeave.Core.Docs.Analysis.Model;

/// <summary>Glossary lookups over the data an <see cref="AnalysisGlossary"/> holds.</summary>
public static class AnalysisGlossaryRules
{
    /// <summary>True when each occurrence maps to exactly one approved sense.</summary>
    /// <param name="glossary">The approved senses to match against.</param>
    /// <param name="term">The term as it appears in the document.</param>
    /// <param name="claims">Every occurrence of the term in the document.</param>
    public static bool Covers(this AnalysisGlossary glossary, string term, IReadOnlyList<Claim> claims)
    {
        ArgumentNullException.ThrowIfNull(glossary);
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        ArgumentNullException.ThrowIfNull(claims);

        ApprovedGlossarySense[] senses = glossary.Senses
            .Where(sense => StringComparer.OrdinalIgnoreCase.Equals(sense.Term, term))
            .ToArray();
        if (senses.Length == 0) return false;

        return claims.All(claim => senses.Count(sense => AppliesTo(sense, claim)) == 1);
    }

    private static bool AppliesTo(ApprovedGlossarySense sense, Claim claim) =>
        sense.Scopes.Any(scope =>
            scope.StartsWith("component:", StringComparison.Ordinal)
                ? StringComparer.OrdinalIgnoreCase.Equals(scope["component:".Length..], claim.Component)
                : scope.StartsWith("code-ref:", StringComparison.Ordinal)
                  && claim.CodeRefs.Contains(scope["code-ref:".Length..], StringComparer.Ordinal));
}
