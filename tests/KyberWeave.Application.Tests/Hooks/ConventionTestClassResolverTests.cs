using KyberWeave.Contracts.Repositories;
using KyberWeave.Data.Hooks;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Application.Tests.Hooks;

/// <summary>The convention resolver over an in-memory file system: no disk, no temp directory.</summary>
public sealed class ConventionTestClassResolverTests
{
    private const string Root = "/work/repo";

    private readonly InMemoryFileSystem _files = new();

    private void Add(string relative, string content)
    {
        string path = Root + "/" + relative;
        _files.CreateDirectory(Path.GetDirectoryName(path)!);
        _files.WriteAllText(path, content);
    }

    private TestClassResolution Resolve(string relativePath) =>
        new ConventionTestClassResolver(_files).Resolve(Root, relativePath);

    [Fact]
    public void ACoveringClassIsNamedWithItsNamespaceAndNearestProject()
    {
        Add("tests/App.Tests/App.Tests.csproj", "<Project />");
        Add("tests/App.Tests/FooTests.cs", "namespace App.Tests;\npublic sealed class FooTests {}");

        TestClassResolution result = Resolve("src/App/Foo.cs");

        Assert.Equal(["App.Tests.FooTests"], result.Classes);
        Assert.Equal("tests/App.Tests/App.Tests.csproj", result.ProjectPath);
        Assert.Null(result.Note);
    }

    [Fact]
    public void AFileThatIsNotCSharpHasNoCoveringClass()
    {
        Add("tests/App.Tests/FooTests.cs", "namespace App.Tests;\npublic sealed class FooTests {}");

        Assert.Equal(TestClassResolution.None.Classes, Resolve("docs/Foo.md").Classes);
    }

    [Fact]
    public void AClassNamedOnlyInACommentIsNotACoveringClass()
    {
        Add("tests/App.Tests/App.Tests.csproj", "<Project />");
        Add("tests/App.Tests/FooTests.cs", "/*\n class FooTests {}\n*/\npublic sealed class Other {}");

        Assert.Empty(Resolve("src/App/Foo.cs").Classes);
    }

    [Fact]
    public void ASameNamedClassInTwoProjectsKeepsTheFirstAndNamesTheOther()
    {
        Add("tests/B.Tests/B.Tests.csproj", "<Project />");
        Add("tests/B.Tests/FooTests.cs", "namespace B.Tests;\npublic sealed class FooTests {}");
        Add("tests/A.Tests/A.Tests.csproj", "<Project />");
        Add("tests/A.Tests/FooTests.cs", "namespace A.Tests;\npublic sealed class FooTests {}");

        TestClassResolution result = Resolve("src/App/Foo.cs");

        Assert.Equal("tests/A.Tests/A.Tests.csproj", result.ProjectPath);
        Assert.Contains("tests/B.Tests/B.Tests.csproj", result.Note, StringComparison.Ordinal);
    }
}
