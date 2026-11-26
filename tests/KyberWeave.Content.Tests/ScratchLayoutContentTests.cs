using System.Text.RegularExpressions;
using KyberWeave.Core;
using KyberWeave.Tests.Corpus;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The prompts the squad ships name the run's scratch folder, and the code builds every path inside it from one
/// constant. A prompt that spells the folder differently sends a worker to a folder the tools never read.
/// </summary>
public sealed class ScratchLayoutContentTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private const string ProductRoot = "products/kyber-squad";

    private static readonly Regex ScratchMention =
        new(@"\.[A-Za-z0-9_-]*scratch[A-Za-z0-9_-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void EveryScratchFolderThePromptsMentionIsTheOneTheCodeUses()
    {
        List<(string Path, string Mention)> mentions = [];
        foreach (string path in Corpus.EnumerateFiles(ProductRoot, "*.md"))
        {
            foreach (Match match in ScratchMention.Matches(Corpus.ReadText(path)))
                mentions.Add((path, match.Value));
        }

        Assert.NotEmpty(mentions);
        Assert.All(mentions, mention => Assert.Equal(ScratchLayout.Root, mention.Mention));
    }
}
