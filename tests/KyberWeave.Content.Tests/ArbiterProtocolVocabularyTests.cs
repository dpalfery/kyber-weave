using System.Text.RegularExpressions;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Facts;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Ties the protocol vocabulary the shipped prompts and rule files write to the constants the code reads:
/// dispatch header names, Arbiter trigger names and return status words.
/// </summary>
public sealed partial class ArbiterProtocolVocabularyTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string SquadRoot = "products/kyber-squad";

    /// <summary>
    /// The routing markers prompts describe beside the header block that are not header names. The classifier
    /// reads them, but they are not part of the closed header set the block parser accepts.
    /// </summary>
    private static readonly string[] NonHeaderRoutingMarkers =
    [
        "INTAKE",
        "FEATURE",
        "PHASE",
        "FINDINGS",
        "FINALIZE",
        "STATUS",
    ];

    [Fact]
    public void EveryDispatchHeaderAConductorPromptWritesIsADeclaredHeader()
    {
        HashSet<string> declared = [.. DispatchHeaders.Names, .. DispatchHeaders.Markers];
        List<string> undeclared = [];

        foreach (string path in MarkdownFiles())
        {
            foreach (string line in ReadLines(path))
            {
                if (!line.Contains(DispatchHeaders.Marker, StringComparison.Ordinal))
                    continue;

                foreach (Match match in BacktickedHeaderRegex().Matches(line))
                {
                    string name = match.Groups["name"].Value;
                    if (NonHeaderRoutingMarkers.Contains(name, StringComparer.Ordinal))
                        continue;
                    if (!declared.Contains(name))
                        undeclared.Add($"{path}: {name}");
                }
            }
        }

        Assert.True(
            undeclared.Count == 0,
            "Prompts write dispatch headers the Arbiter does not declare: " + string.Join(", ", undeclared));
    }

    [Fact]
    public void EveryRuleTriggerIsAKnownArbiterTrigger()
    {
        List<string> unknown = [];

        foreach (string path in RuleFiles())
        {
            foreach (string line in ReadLines(path))
            {
                string trimmed = line.TrimStart();
                if (!trimmed.StartsWith("trigger:", StringComparison.Ordinal))
                    continue;

                string value = trimmed["trigger:".Length..];
                int comment = value.IndexOf('#', StringComparison.Ordinal);
                if (comment >= 0)
                    value = value[..comment];

                foreach (string trigger in TriggerNames(value))
                {
                    if (!ArbiterTriggers.All.Contains(trigger, StringComparer.Ordinal))
                        unknown.Add($"{path}: {trigger}");
                }
            }
        }

        Assert.True(
            unknown.Count == 0,
            "Rule files declare triggers the Arbiter does not know: " + string.Join(", ", unknown));
    }

    [Fact]
    public void EveryParsedReturnStatusAPromptWritesIsSpelledTheWayTheCodeSpellsIt()
    {
        List<string> mismatched = [];

        foreach (string path in MarkdownFiles())
        {
            foreach (string line in ReadLines(path))
            {
                foreach (Match match in StatusRegex().Matches(line))
                {
                    string written = match.Groups["word"].Value;
                    string? canonical = ReturnStatus.All.FirstOrDefault(
                        status => string.Equals(status, written, StringComparison.OrdinalIgnoreCase));
                    if (canonical is not null && !string.Equals(canonical, written, StringComparison.Ordinal))
                        mismatched.Add($"{path}: {written} (code spells it {canonical})");
                }
            }
        }

        Assert.True(
            mismatched.Count == 0,
            "Prompts spell a parsed return status differently from the code: " + string.Join(", ", mismatched));
    }

    private static IEnumerable<string> TriggerNames(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.StartsWith('['))
            trimmed = trimmed.Trim('[', ']');

        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.Trim('"', '\''))
            .Where(name => name.Length > 0);
    }

    private static IEnumerable<string> MarkdownFiles() =>
        Corpus.EnumerateFiles(SquadRoot, "*.md").Order(StringComparer.Ordinal);

    private static IEnumerable<string> RuleFiles() =>
        Corpus.EnumerateFiles(SquadRoot, "*.yml")
            .Where(path => path.EndsWith(".arbiter-rules.yml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

    private static string[] ReadLines(string path) =>
        Corpus.ReadText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    [GeneratedRegex(@"`(?<name>[A-Z][A-Z0-9_-]*):")]
    private static partial Regex BacktickedHeaderRegex();

    [GeneratedRegex(@"STATUS:\s*(?<word>[A-Z][A-Z0-9_]*)")]
    private static partial Regex StatusRegex();
}
