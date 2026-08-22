using KyberWeave.Cli.Commands.Review;
using KyberWeave.Core.CodeGraph;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Review;
using Xunit;
using YamlDotNet.Core;

namespace KyberWeave.Tests;

/// <summary>
/// The duplicates gate is only useful if it never merges two bodies that differ. Recall is a
/// tuning question and can be argued about; a false cluster is a finding the council reports
/// against code that is fine, so these tests pin precision first and threshold behaviour
/// second. Every case is a source fixture on disk plus a synthetic node set — no CodeGraph
/// index and no sqlite3.
/// </summary>
public class ReviewDuplicatesTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        _temp.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string Body = """
        public int Total(IReadOnlyList<int> values)
        {
            int sum = 0;
            foreach (int value in values)
                sum += value;
            return sum;
        }
        """;

    private void Write(string relativePath, string content)
    {
        string full = Path.Combine(_temp.Path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private int LineCount(string relativePath) =>
        File.ReadAllLines(Path.Combine(_temp.Path, relativePath)).Length;

    private static CodeGraphNode Method(string name, string file, int startLine, int endLine) =>
        Node("method", name, file, startLine, endLine);

    private static CodeGraphNode Node(string kind, string name, string file, int startLine, int endLine) =>
        new($"id-{kind}-{name}-{startLine}", kind, name, $"N.{name}", file, "csharp", startLine, endLine);

    private DuplicateReport Detect(IReadOnlyList<CodeGraphNode> nodes, int minimumLines = 4) =>
        DuplicateDetector.Detect(
            _temp.Path,
            nodes,
            new ReviewDuplicates(minimumLines),
            ".codegraph/codegraph.db",
            indexModifiedUtc: null);

    [Fact]
    public void TwoIdenticalBodiesInDifferentFilesFormOneCluster()
    {
        Write("A.cs", Body);
        Write("B.cs", Body);

        DuplicateReport report = Detect([Method("Total", "A.cs", 1, 7), Method("Total", "B.cs", 1, 7)]);

        DuplicateCluster cluster = Assert.Single(report.Clusters);
        Assert.Equal(2, cluster.Members.Count);
        Assert.Equal(["A.cs", "B.cs"], cluster.Members.Select(m => m.File));
    }

    [Fact]
    public void DifferentNamesOverTheSameBodyStillCluster()
    {
        // The signature line is dropped on purpose: a body copied under a new name is the
        // duplication this gate exists to find, and keeping the signature would hide it.
        Write("A.cs", Body);
        Write("B.cs", Body.Replace("Total", "Sum", StringComparison.Ordinal));

        DuplicateReport report = Detect([Method("Total", "A.cs", 1, 7), Method("Sum", "B.cs", 1, 7)]);

        DuplicateCluster cluster = Assert.Single(report.Clusters);
        Assert.Equal(["Sum", "Total"], cluster.Members.Select(m => m.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("        // a comment nobody reads")]
    [InlineData("")]
    [InlineData("        /// <summary>Adds them up.</summary>")]
    [InlineData("        /* and a block one */")]
    public void CommentAndBlankLineDifferencesDoNotPreventClustering(string inserted)
    {
        Write("A.cs", Body);
        string[] lines = Body.Split('\n');
        Write("B.cs", string.Join('\n', [lines[0], inserted, .. lines[1..]]));

        DuplicateReport report = Detect(
            [Method("Total", "A.cs", 1, LineCount("A.cs")), Method("Total", "B.cs", 1, LineCount("B.cs"))]);

        Assert.Single(report.Clusters);
    }

    [Fact]
    public void IndentationAndInteriorSpacingDifferencesDoNotPreventClustering()
    {
        Write("A.cs", Body);
        Write("B.cs", Body.Replace("sum += value;", "    sum  +=  value;", StringComparison.Ordinal));

        DuplicateReport report = Detect([Method("Total", "A.cs", 1, 7), Method("Total", "B.cs", 1, 7)]);

        Assert.Single(report.Clusters);
    }

    [Fact]
    public void AGenuinelyDifferentBodyDoesNotCluster()
    {
        Write("A.cs", Body);
        Write("B.cs", Body.Replace("sum += value;", "sum -= value;", StringComparison.Ordinal));

        DuplicateReport report = Detect([Method("Total", "A.cs", 1, 7), Method("Total", "B.cs", 1, 7)]);

        Assert.Empty(report.Clusters);
    }

    [Fact]
    public void ATrailingCommentIsTreatedAsADifference()
    {
        // Deliberate, and the reason is in DuplicateDetector's remarks: stripping trailing
        // comments safely means knowing whether '//' sits inside a string literal. Missing a
        // cluster costs recall; merging two bodies that differ costs trust.
        Write("A.cs", Body);
        Write("B.cs", Body.Replace("sum += value;", "sum += value; // accumulate", StringComparison.Ordinal));

        DuplicateReport report = Detect([Method("Total", "A.cs", 1, 7), Method("Total", "B.cs", 1, 7)]);

        Assert.Empty(report.Clusters);
    }

    [Fact]
    public void BodiesBelowTheThresholdAreNotCompared()
    {
        // Normalize drops the signature and brace lines, so this counts as one statement
        // (`return 1;`) — below the default threshold of four, and identical everywhere.
        const string small = """
            public int One()
            {
                return 1;
            }
            """;
        Write("A.cs", small);
        Write("B.cs", small);

        DuplicateReport report = Detect([Method("One", "A.cs", 1, 4), Method("One", "B.cs", 1, 4)]);

        Assert.Empty(report.Clusters);
        Assert.Equal(0, report.SymbolsConsidered);
    }

    [Fact]
    public void LoweringTheThresholdBringsShorterBodiesIntoScope()
    {
        // After Normalize drops the signature and braces, two statements remain — enough
        // for a threshold of two, still short of the default of four.
        const string small = """
            public int One()
            {
                int x = 1;
                return x;
            }
            """;
        Write("A.cs", small);
        Write("B.cs", small);
        CodeGraphNode[] nodes = [Method("One", "A.cs", 1, 5), Method("One", "B.cs", 1, 5)];

        Assert.Empty(Detect(nodes).Clusters);
        Assert.Single(Detect(nodes, minimumLines: 2).Clusters);
    }

    [Fact]
    public void ASymbolWhoseFileTheTreeLacksIsCountedAsUnreadableRatherThanSkippedSilently()
    {
        Write("A.cs", Body);

        DuplicateReport report = Detect([Method("Total", "A.cs", 1, 7), Method("Total", "Gone.cs", 1, 7)]);

        Assert.Equal(1, report.SymbolsUnreadable);
        Assert.Empty(report.Clusters);
    }

    [Fact]
    public void ASpanRunningPastTheEndOfTheFileIsUnreadableRatherThanTruncated()
    {
        // Index staleness in its most common form: the file shrank after it was indexed.
        // Hashing the truncated remainder would produce a body that never existed.
        Write("A.cs", Body);

        DuplicateReport report = Detect([Method("Total", "A.cs", 1, 400)]);

        Assert.Equal(1, report.SymbolsUnreadable);
    }

    [Fact]
    public void ClusterIdsAreStableAcrossRunsAndIndependentOfOrdering()
    {
        Write("A.cs", Body);
        Write("B.cs", Body);
        CodeGraphNode a = Method("Total", "A.cs", 1, 7);
        CodeGraphNode b = Method("Total", "B.cs", 1, 7);

        string forward = Assert.Single(Detect([a, b]).Clusters).Id;
        string reversed = Assert.Single(Detect([b, a]).Clusters).Id;

        Assert.Equal(forward, reversed);
        Assert.StartsWith("dup-", forward, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectDoesNotClusterAComparableKindWithAMatchingNonComparableBody()
    {
        // Detect, not the caller, owns ComparableKinds. A mixed list used to cluster
        // a method with a class that happened to share a span.
        Write("A.cs", Body);
        Write("B.cs", Body);

        DuplicateReport report = Detect(
            [Method("Total", "A.cs", 1, 7), Node("class", "Holder", "B.cs", 1, 7)]);

        Assert.Empty(report.Clusters);
        Assert.Equal(1, report.SymbolsConsidered);
    }

    [Fact]
    public void TwoIdenticalNonComparableKindsDoNotFormACluster()
    {
        Write("A.cs", Body);
        Write("B.cs", Body);

        DuplicateReport report = Detect(
            [Node("class", "A", "A.cs", 1, 7), Node("class", "B", "B.cs", 1, 7)]);

        Assert.Empty(report.Clusters);
        Assert.Equal(0, report.SymbolsConsidered);
    }

    [Fact]
    public void FunctionAndMethodKindsAreBothCompared()
    {
        Write("A.cs", Body);
        Write("B.cs", Body);

        DuplicateReport report = Detect(
            [Method("Total", "A.cs", 1, 7), Node("function", "Total", "B.cs", 1, 7)]);

        Assert.Single(report.Clusters);
        Assert.Equal(2, report.SymbolsConsidered);
    }

    [Fact]
    public void TheCommandDrivesDetectionThroughTheEnumeratorPort()
    {
        Write("A.cs", Body);
        Write("B.cs", Body);
        FakeCodeGraphResolver graph = FakeCodeGraphResolver.WithNodes(
            Method("Total", "A.cs", 1, 7),
            Method("Sum", "B.cs", 1, 7));

        DuplicateReport report = ReviewDuplicatesCommand.Analyze(
            _temp.Path, graph, graph, ReviewDuplicates.Default);

        Assert.True(report.IndexAvailable);
        Assert.Single(report.Clusters);
    }

    [Fact]
    public void TheCommandReportsAnUnavailableIndexRatherThanEnumeratingNothing()
    {
        // "No index" and "index with nothing in it" must not read the same downstream: the
        // duplicate-implementation lens skips on the first and reports clean on the second.
        UnavailableGraph graph = new();

        DuplicateReport report = ReviewDuplicatesCommand.Analyze(
            _temp.Path, graph, graph, ReviewDuplicates.Default);

        Assert.False(report.IndexAvailable);
        Assert.Equal(graph.UnavailableReason, report.UnavailableReason);
    }

    private sealed class UnavailableGraph : ICodeGraphResolver, ICodeGraphSymbolEnumerator
    {
        public bool IsAvailable => false;
        public string UnavailableReason => "No CodeGraph index at .codegraph/codegraph.db.";
        public string DatabasePath => ".codegraph/codegraph.db";
        public IReadOnlyList<CodeGraphNode> ResolveSymbol(string name) => [];
        public IReadOnlyList<CodeGraphNode> ResolveRoute(string route) => [];
        public bool HasFilesUnder(string relativePathPrefix) => false;
        public IReadOnlyList<string> CandidateNames(string like) => [];
        public IReadOnlyList<string> AllRoutes() => [];

        public IReadOnlyList<CodeGraphNode> NodesOfKind(string kind) =>
            throw new InvalidOperationException("Analyze must not enumerate an unavailable index.");
    }

    [Fact]
    public void AMissingIndexProducesAnUnavailableReportRatherThanThrowing()
    {
        DuplicateReport report = DuplicateDetector.Unavailable(
            ".codegraph/codegraph.db",
            "No CodeGraph index at .codegraph/codegraph.db.",
            ReviewDuplicates.Default);

        Assert.False(report.IndexAvailable);
        Assert.Contains("No CodeGraph index", report.UnavailableReason, StringComparison.Ordinal);
        Assert.Empty(report.Clusters);
    }

    [Fact]
    public void TheReportRoundTripsThroughJson()
    {
        Write("A.cs", Body);
        Write("B.cs", Body);
        DuplicateReport report = Detect([Method("Total", "A.cs", 1, 7), Method("Total", "B.cs", 1, 7)]);

        DuplicateReport read = ReviewJson.ReadDuplicates(ReviewJson.Write(report));

        Assert.Equal(DuplicateReport.CurrentSchema, read.Schema);
        Assert.Equal(report.Clusters[0].Id, read.Clusters[0].Id);
        Assert.Equal(2, read.Clusters[0].Members.Count);
    }

    [Fact]
    public void AHostThresholdBelowTwoIsRejectedRatherThanSilentlyReplaced()
    {
        // Normalize drops the signature, so a one-line body is a single statement and would
        // match everywhere. A host that asks for one is told, not given a different number.
        YamlException ex = Assert.ThrowsAny<YamlException>(() =>
            ReviewConfigLoader.Merge(
                ReviewConfig.ProductDefaults,
                new ReviewYamlSection { Duplicates = new ReviewDuplicatesYaml { MinimumLines = 1 } }));

        Assert.Contains("greater than one", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentDuplicatesThresholdKeepsTheProductDefault()
    {
        ReviewConfig omitted = ReviewConfigLoader.Merge(ReviewConfig.ProductDefaults, new ReviewYamlSection());
        ReviewConfig declaredWithoutValue = ReviewConfigLoader.Merge(
            ReviewConfig.ProductDefaults,
            new ReviewYamlSection { Duplicates = new ReviewDuplicatesYaml() });

        Assert.Equal(ReviewDuplicates.Default.MinimumLines, omitted.Duplicates.MinimumLines);
        Assert.Equal(ReviewDuplicates.Default.MinimumLines, declaredWithoutValue.Duplicates.MinimumLines);
    }

    [Fact]
    public void AHostThresholdIsHonouredWhenItIsInRange()
    {
        ReviewConfig config = ReviewConfigLoader.Merge(
            ReviewConfig.ProductDefaults,
            new ReviewYamlSection { Duplicates = new ReviewDuplicatesYaml { MinimumLines = 9 } });

        Assert.Equal(9, config.Duplicates.MinimumLines);
    }
}
