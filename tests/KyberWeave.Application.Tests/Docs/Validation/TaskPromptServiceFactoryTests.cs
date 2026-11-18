using KyberWeave.Contracts.Repositories;
using KyberWeave.Core.Configuration;
using KyberWeave.Core.Docs.Validation;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Validation;

/// <summary>The task prompt factory resolves the repository root through the path seam before it opens the corpus.</summary>
public sealed class TaskPromptServiceFactoryTests
{
    [Theory]
    [InlineData("/kw/work", "/kw/work/repo")]
    [InlineData("/kw/other", "/kw/other/repo")]
    public void ARelativeRepositoryRootIsResolvedAgainstTheInjectedWorkingDirectory(string workingDirectory, string expectedRoot)
    {
        RecordingDocuments documents = new();
        TaskPromptServiceFactory factory = new(
            null!,
            null!,
            null!,
            null!,
            documents,
            new FakePathResolver(new InMemoryFileSystem()),
            new FakeProcessEnvironment(currentDirectory: workingDirectory));

        _ = Assert.Throws<InvalidOperationException>(() => factory.ForRepository("repo", KyberWeaveConfigDefaults.Product));

        Assert.Equal(expectedRoot, documents.Root);
    }

    private sealed class RecordingDocuments : IDocumentSourceFactory
    {
        public string? Root { get; private set; }

        public IDocumentSource Create(string repoRoot, OntologyConfig ontology)
        {
            Root = repoRoot;
            throw new InvalidOperationException("The corpus is not opened by this test.");
        }
    }
}
