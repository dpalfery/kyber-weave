using System.Text;
using KyberWeave.Cli.Commands.Squad;
using KyberWeave.Core.Squad.Deployment;
using KyberWeave.Core.Squad.Model;
using KyberWeave.Core.Squad.Rendering;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the one always-on source of worker text (decision D27, task T47): the Discovery order, the
/// plan-read ban, the full-suite ban and the batched-read rule live in
/// <c>products/kyber-squad/shared/worker-baseline.md</c> and reach each worker's render once.
/// </summary>
/// <remarks>
/// <para>
/// "Once" is the assertion that matters, so every case counts occurrences rather than asserting
/// presence. Agent-file text is re-sent on every round trip of every worker, and a render carrying the
/// same paragraph twice is the defect this slice exists to remove — a presence check passes for a
/// render that carries it ten times.
/// </para>
/// <para>
/// The target rows come from <see cref="SquadCommandComposition.CreateRenderers"/>, the same list the
/// shared renderer contract suite uses, so a renderer that ships later is held to this contract the
/// day it ships rather than whenever someone remembers it. The tag is
/// <c>&lt;worker-baseline&gt;</c>, following the <c>&lt;response-preferences&gt;</c> block the
/// deployment injector already replaces: it is what makes the paragraph findable and countable in a
/// rendered body.
/// </para>
/// </remarks>
public sealed class SquadWorkerBaselineInjectionTests
{
    private const string BaselineOpenTag = "<worker-baseline";

    /// <summary>The worker whose rendered body every harness carries, as an agent file or a role skill.</summary>
    private const string SampleWorker = "csharp-dev";

    private static readonly string ProductRoot =
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "products", "kyber-squad");

    private static readonly string BaselineSourcePath = Path.Combine(ProductRoot, "shared", "worker-baseline.md");

    /// <summary>
    /// The rules the one source carries, each named by the vocabulary its rule already uses so the
    /// assertion reads as the rule rather than as a sample of prose.
    /// </summary>
    private static readonly string[] BaselineRulePhrases =
    [
        "codegraph_explore",
        "<plan-index>",
        "full test suite",
        "one round trip"
    ];

    public static TheoryData<SquadTarget> ShippedTargets() =>
        new(SquadCommandComposition.CreateRenderers().SelectMany(renderer => renderer.SupportedTargets));

    /// <summary>
    /// The single source exists and holds every rule the baseline is for. One file is the whole point:
    /// text copied into each agent file is the text this slice removes.
    /// </summary>
    [Fact]
    public void TheBaselineTextLivesInOneSharedSourceFile()
    {
        Assert.True(
            File.Exists(BaselineSourcePath),
            $"The shared baseline must live at '{BaselineSourcePath}', the one source every worker render injects.");

        string baseline = File.ReadAllText(BaselineSourcePath);
        foreach (string phrase in BaselineRulePhrases)
        {
            Assert.True(
                baseline.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                $"The shared baseline must carry the rule that names '{phrase}'.");
        }
    }

    /// <summary>
    /// Every harness's render of a worker carries the baseline exactly once, and no rendered file in
    /// the deployment carries a second copy.
    /// </summary>
    [Theory]
    [MemberData(nameof(ShippedTargets))]
    public async Task EachWorkerRenderCarriesTheBaselineExactlyOnce(SquadTarget target)
    {
        SquadRenderResult result = await TestSquadCorpus.GetProjectRenderAsync(target);
        Assert.True(result.Success, string.Join("; ", result.Errors));

        (string RelativePath, string Content)[] files =
        [
            .. result.Files.Select(file =>
                (file.RelativePath, Content: Encoding.UTF8.GetString(file.Content.Span)))
        ];

        (string RelativePath, string Content)[] workerFiles =
        [
            .. files.Where(file => file.RelativePath.Contains(SampleWorker, StringComparison.Ordinal))
        ];
        Assert.True(
            workerFiles.Length > 0,
            $"The {target} render carries no file for '{SampleWorker}', so this case would prove nothing.");

        (string RelativePath, string Content) carrier = Assert.Single(
            workerFiles, file => Occurrences(file.Content, BaselineOpenTag) == 1);

        Assert.All(workerFiles, file => Assert.True(
            Occurrences(file.Content, BaselineOpenTag) <= 1,
            $"'{file.RelativePath}' carries the shared baseline more than once. It reaches a worker from one " +
            "source, once per render."));

        Assert.All(files, file => Assert.True(
            Occurrences(file.Content, BaselineOpenTag) <= 1,
            $"'{file.RelativePath}' carries the shared baseline more than once."));

        foreach (string phrase in BaselineRulePhrases)
        {
            Assert.True(
                carrier.Content.Contains(phrase, StringComparison.OrdinalIgnoreCase),
                $"'{carrier.RelativePath}' carries the baseline block without the rule that names '{phrase}'.");
        }
    }

    private static int Occurrences(string content, string value)
    {
        int count = 0;
        int index = content.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = content.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }

    [Fact]
    public void NestedBlockOpenThrowsSquadRenderValidationException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "kw-test-baseline-" + Guid.NewGuid().ToString("N"));
        string sharedDir = Path.Combine(tempDir, "shared");
        Directory.CreateDirectory(sharedDir);
        try
        {
            string baselinePath = Path.Combine(sharedDir, "worker-baseline.md");
            File.WriteAllText(
                baselinePath,
                "<worker-baseline>\n<worker-baseline>\ntext\n</worker-baseline>");

            Assert.Throws<SquadRenderValidationException>(() => SquadWorkerBaselineInjector.ReadBlock(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void UnclosedBlockInInstructionBodyIsReplacedWithoutDuplicateBlock()
    {
        SquadAgent sample = TestSquadCorpus.CanonicalSource.Agents.First(a => a.Invocation == SquadInvocation.Subagent);
        SquadSource source = TestSquadCorpus.CanonicalSource with
        {
            Agents = [sample with { InstructionBody = "body <worker-baseline> unclosed" }]
        };

        string block = "<worker-baseline>\nclean\n</worker-baseline>";
        SquadSource applied = SquadWorkerBaselineInjector.Apply(source, block);

        string body = applied.Agents[0].InstructionBody;
        Assert.Equal(1, Occurrences(body, "<worker-baseline>"));
        Assert.Equal(1, Occurrences(body, "</worker-baseline>"));
        Assert.StartsWith("body", body, StringComparison.Ordinal);
        Assert.DoesNotContain("unclosed", body, StringComparison.Ordinal);
    }
}
