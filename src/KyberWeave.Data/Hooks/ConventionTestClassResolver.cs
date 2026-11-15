using System.Text.RegularExpressions;
using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Data.Hooks;

/// <summary>Maps <c>Foo.cs</c> to a <c>FooTests</c> class found in <c>FooTests.cs</c>, and checks the class exists.</summary>
internal sealed partial class ConventionTestClassResolver(IFileSystem fileSystem) : ITestClassResolver
{
    public TestClassResolution Resolve(string repositoryRoot, string relativePath)
    {
        if (!relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return TestClassResolution.None;
        }

        string stem = Path.GetFileNameWithoutExtension(relativePath);
        string testClass = stem.EndsWith("Tests", StringComparison.Ordinal) ? stem : stem + "Tests";

        List<(string Class, string Project)> found = [];
        foreach (string file in fileSystem.EnumerateFiles(repositoryRoot, testClass + ".cs", recursive: true).Order(StringComparer.Ordinal))
        {
            string? name = QualifiedName(fileSystem.ReadAllText(file), testClass);
            string? project = name is null ? null : NearestProject(Path.GetDirectoryName(file), repositoryRoot);
            if (name is not null && project is not null)
            {
                found.Add((name, project));
            }
        }

        if (found.Count == 0)
        {
            return TestClassResolution.None;
        }

        string first = found[0].Project;
        return new TestClassResolution(
            [.. found.Where(f => f.Project == first).Select(f => f.Class).Distinct(StringComparer.Ordinal)],
            Path.GetRelativePath(repositoryRoot, first).Replace('\\', '/'));
    }

    private static string? QualifiedName(string source, string className)
    {
        if (!Regex.IsMatch(source, $@"\bclass\s+{Regex.Escape(className)}\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            return null;
        }

        Match ns = NamespacePattern().Match(source);
        return ns.Success ? $"{ns.Groups[1].Value}.{className}" : className;
    }

    private string? NearestProject(string? directory, string root)
    {
        while (directory is not null && directory.StartsWith(root, StringComparison.Ordinal))
        {
            string? project = fileSystem.EnumerateFiles(directory, "*.csproj", recursive: false).FirstOrDefault();
            if (project is not null)
            {
                return project;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();
}
