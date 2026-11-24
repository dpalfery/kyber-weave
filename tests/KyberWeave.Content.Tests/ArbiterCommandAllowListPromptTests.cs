using System.Text.Json;
using KyberWeave.Arbiter;
using KyberWeave.Arbiter.Hooks;
using KyberWeave.Cli.Commands.Docs;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Contracts.Services;
using KyberWeave.Core.Arbiter;
using KyberWeave.Core.Arbiter.Stores;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Parsing;
using KyberWeave.Core.Docs.Search;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Data.Arbiter;
using KyberWeave.Data.Hooks;
using KyberWeave.Data.Paths;
using KyberWeave.Tests.Arbiter;
using KyberWeave.Tests.Corpus;
using KyberWeave.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// The command forms a technology's Arbiter rules file allows are also stated in the technology's rules part of the
/// task prompt, built from the shipped standards folder over the documents in memory.
/// </summary>
public sealed class ArbiterCommandAllowListPromptTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    [Fact]
    public void TheListIsInThePromptRulesPart()
    {
        AssertListedInTheRulesPart("azure", ["list*", "show*"]);
        AssertListedInTheRulesPart("aws", ["describe-*", "list-*", "get-*", "ls"]);
    }

    private static void AssertListedInTheRulesPart(string technology, string[] forms)
    {
        string rules = RulesPartOfThePrompt(technology);
        foreach (string form in forms)
        {
            Assert.Contains(form, rules, StringComparison.Ordinal);
        }
    }

    /// <summary>The <c>technology-rules</c> part of a prompt built for <paramref name="technology"/> from the shipped folder.</summary>
    private static string RulesPartOfThePrompt(string technology)
    {
        using DocFixture fixture = DocFixture.CreateInMemory();
        fixture.Write("6-Docs/standards/shared-rules.md", ShippedStandardsFile("shared-rules.md"));
        foreach (string name in new[] { $"{technology}.standards.md", $"{technology}.rules.md", $"{technology}.skills.md" })
        {
            fixture.Write($"6-Docs/standards/{technology}/{name}", ShippedStandardsFile($"{technology}/{name}"));
        }

        KyberWeaveConfig config = KyberWeaveConfigAssembler.FromYaml(
            $"ontology:\n  docs-root: 6-Docs\n  technologies: [{technology}]\n");
        IServiceProvider provider = DocsMemoryServicesD.Services(fixture);
        DocumentIndex index = DocumentIndex.Build(
            fixture.Loader(config.Ontology).Load(), FakeCodeGraphResolver.WithSymbols());
        TaskPromptService service = new(
            provider.GetRequiredService<IPlanTaskService>(),
            provider.GetRequiredService<IPacketHostFacts>(),
            provider.GetRequiredService<IWritableFileSystem>(),
            index.StandardsFor,
            provider.GetRequiredService<IProcessEnvironment>(),
            FixedKwSkillCatalog.Empty);

        TaskPromptResult result = service.Build(new TaskPromptRequest(fixture.Root, TaskId: null, Technology: technology), config);

        Assert.True(result.Built, result.Refusal);
        const string openTag = "<technology-rules>";
        int open = result.Prompt.IndexOf(openTag, StringComparison.Ordinal);
        int close = result.Prompt.IndexOf("</technology-rules>", StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, "The prompt carries a technology-rules part.");
        return result.Prompt[(open + openTag.Length)..close];
    }

    private static string ShippedStandardsFile(string relativePath) =>
        Corpus.ReadText($"products/kyber-squad/standards/{relativePath}");
}
