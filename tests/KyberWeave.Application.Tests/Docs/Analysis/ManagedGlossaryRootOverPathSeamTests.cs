using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Analysis.Glossary;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Analysis;

/// <summary>
/// The managed glossary resolves a caller's repository root and its own file path through the path seam, against the
/// base directory the environment names.
/// </summary>
/// <remarks>
/// The glossary file lives under <c>/kw/work/repo</c> while the root argument is the relative spelling <c>repo</c>, so
/// only the injected working directory can find it. A found file that is not a valid glossary is refused; a file the
/// glossary does not find reads as an empty glossary, which is how the two cases are told apart.
/// </remarks>
public sealed class ManagedGlossaryRootOverPathSeamTests
{
    private const string WorkingDirectory = "/kw/work";
    private const string OtherDirectory = "/kw/other";
    private const string RelativeRoot = "repo";

    private readonly InMemoryFileSystem _disk = new();

    private ManagedGlossary Glossary(string workingDirectory) =>
        new(
            RelativeRoot,
            KyberWeaveConfigDefaults.Product,
            TimeProvider.System,
            new InMemoryRepositoryFileStore(_disk),
            new FakePathResolver(_disk),
            new FakeProcessEnvironment(currentDirectory: workingDirectory));

    [Fact]
    public void ARelativeRepositoryRootIsResolvedAgainstTheInjectedWorkingDirectory()
    {
        string glossaryPath = KyberWeaveConfigDefaults.Product.DocsAnalysis.ResolveGlossaryPath(KyberWeaveConfigDefaults.Product.Ontology);
        _disk.CreateDirectory(Path.GetDirectoryName($"{WorkingDirectory}/{RelativeRoot}/{glossaryPath}")!);
        _disk.WriteAllText($"{WorkingDirectory}/{RelativeRoot}/{glossaryPath}", "not a glossary\n");

        Assert.Throws<InvalidDataException>(() => Glossary(WorkingDirectory).Load());

        // The counter-case proves the load ran against the injected directory at all: from another working directory
        // the same relative root names no glossary, so the load finds nothing to refuse.
        Assert.Empty(Glossary(OtherDirectory).Load().Terms);
    }

    [Fact]
    public void AGlossaryPathThatLeavesTheRepositoryIsRefused()
    {
        KyberWeaveConfig escaping = new()
        {
            Arbiter = KyberWeaveConfigDefaults.Product.Arbiter,
            DocsAnalysis = new DocsAnalysisConfig { GlossaryPath = "../outside/glossary.md" }
        };

        ArgumentException refusal = Assert.Throws<ArgumentException>(() => new ManagedGlossary(
            RelativeRoot,
            escaping,
            TimeProvider.System,
            new InMemoryRepositoryFileStore(_disk),
            new FakePathResolver(_disk),
            new FakeProcessEnvironment(currentDirectory: WorkingDirectory)));

        Assert.Contains("inside the repository", refusal.Message, StringComparison.Ordinal);
    }
}
