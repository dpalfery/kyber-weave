using KyberWeave.Core.Docs.Delivery;
using KyberWeave.Tests.Fakes;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Docs.Delivery;

/// <summary>The pull request description reads its standard template and the host's template through the seams.</summary>
public sealed class PullRequestDescriptionTests
{
    private const string Root = "/kw/repo";
    private const string ResourceName = "Delivery.pull-request-description.md";

    private readonly InMemoryFileSystem _disk = new();

    private InMemoryRepositoryFileStore Store => new(_disk);

    [Fact]
    public void TheStandardSectionsComeFromTheInjectedTemplateResource()
    {
        FakeEmbeddedResources resources = new FakeEmbeddedResources()
            .Add(ResourceName, "## Overview\n\n{{summary}}\n\n## Edits\n\n{{changes}}\n");
        PullRequestContent content = new() { Summary = "Lists now export.", Changes = ["CSV writer"] };

        string body = PullRequestDescription.Render(resources, content);

        Assert.Contains("## Overview\n\nLists now export.", body, StringComparison.Ordinal);
        Assert.Contains("## Edits\n\n- CSV writer", body, StringComparison.Ordinal);
        Assert.DoesNotContain("## Summary", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ARenderWithNoTemplateResourceFailsInsteadOfFallingBackToAnotherSource()
    {
        _ = Assert.ThrowsAny<Exception>(
            () => PullRequestDescription.Render(new FakeEmbeddedResources(), new PullRequestContent { Summary = "x" }));
    }

    [Fact]
    public void AHostTemplateIsFoundByFileNameIgnoringCaseInTheFirstFolderThatHoldsOne()
    {
        _disk.CreateDirectory($"{Root}/.github");
        _disk.WriteAllText($"{Root}/.github/Pull_Request_Template.md", "## Why\n");

        PullRequestDescription.HostTemplate read = PullRequestDescription.ReadHostTemplate(Store, Root, Store.ReadAllTextBounded);

        Assert.Equal("## Why\n", read.Text);
        Assert.Empty(read.Unreadable);
    }

    [Fact]
    public void ARepositoryWithNoHostTemplateReadsAsNone()
    {
        _disk.CreateDirectory($"{Root}/.github");
        _disk.WriteAllText($"{Root}/.github/CODEOWNERS", "* @owner\n");

        Assert.Null(PullRequestDescription.ReadHostTemplate(Store, Root));
    }

    [Fact]
    public void AnUnreadableHostTemplateIsNamedAndALaterOneIsUsed()
    {
        _disk.CreateDirectory($"{Root}/.github");
        _disk.WriteAllText($"{Root}/pull_request_template.md", "## Summary\n");
        _disk.WriteAllText($"{Root}/.github/pull_request_template.md", "## Why\n");

        PullRequestDescription.HostTemplate read = PullRequestDescription.ReadHostTemplate(
            Store,
            Root,
            path => Path.GetDirectoryName(path) == Root ? throw new IOException("denied") : Store.ReadAllTextBounded(path));

        Assert.Equal("## Why\n", read.Text);
        Assert.Equal(["pull_request_template.md"], read.Unreadable);
    }

    [Fact]
    public void AFolderThatCannotBeListedIsNamedAsUnreadable()
    {
        _disk.CreateDirectory($"{Root}/docs");
        _disk.WriteAllText($"{Root}/docs/pull_request_template.md", "## Summary\n");

        PullRequestDescription.HostTemplate read = PullRequestDescription.ReadHostTemplate(
            Store,
            Root,
            Store.ReadAllTextBounded,
            folder => Path.GetFileName(folder) == "docs" ? throw new UnauthorizedAccessException("denied") : Store.GetFiles(folder));

        Assert.Null(read.Text);
        Assert.Equal(["docs/pull_request_template.md"], read.Unreadable);
    }
}
