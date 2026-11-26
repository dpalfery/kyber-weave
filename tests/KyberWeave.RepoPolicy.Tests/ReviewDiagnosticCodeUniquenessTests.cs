using System.Text.RegularExpressions;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Holds the review diagnostics to one definition: a <c>KW-REVIEW-*</c> code is written as a string
/// literal in exactly one source file, so every command and service that reports it shares that value.
/// This test only reads source. It touches no product code.
/// </summary>
public sealed class ReviewDiagnosticCodeUniquenessTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static readonly Regex CodeLiteral = new(
        @"""KW-REVIEW-\d{3}""",
        RegexOptions.CultureInvariant);

    [Fact]
    public void EachReviewDiagnosticCodeLiteralAppearsExactlyOnce()
    {
        Dictionary<string, List<string>> sites = new(StringComparer.Ordinal);

        foreach (string relative in Corpus.EnumerateFiles("src", "*.cs"))
        {
            if (relative.Contains("/bin/", StringComparison.Ordinal) ||
                relative.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            string text = Corpus.ReadText(relative);
            foreach (Match match in CodeLiteral.Matches(text))
            {
                string code = match.Value.Trim('"');
                if (!sites.TryGetValue(code, out List<string>? found))
                {
                    sites[code] = found = [];
                }

                int line = text[..match.Index].Count(character => character == '\n') + 1;
                found.Add($"{relative}:{line}");
            }
        }

        string[] duplicated =
        [
            .. sites
                .Where(pair => pair.Value.Count > 1)
                .Select(pair => $"{pair.Key}: {string.Join(", ", pair.Value)}")
                .OrderBy(entry => entry, StringComparer.Ordinal)
        ];

        Assert.True(
            duplicated.Length == 0,
            "Each KW-REVIEW-* code must be written once under src; reuse ReviewDiagnosticCodes:\n"
            + string.Join("\n", duplicated));
    }
}
