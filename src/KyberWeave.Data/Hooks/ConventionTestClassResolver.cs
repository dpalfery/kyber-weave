using System.Text.RegularExpressions;
using KyberWeave.Contracts.Repositories;
using KyberWeave.Domain.Hooks;

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
        string fileName = testClass + ".cs";
        foreach (string file in fileSystem.EnumerateFiles(repositoryRoot, "*.cs", recursive: true)
            .Where(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            string? name;
            try
            {
                name = QualifiedName(fileSystem.ReadAllText(file), testClass);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
            {
                continue;
            }

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

        string first = found.Select(f => f.Project).Order(StringComparer.Ordinal).First();
        List<string> others = [.. found.Select(f => f.Project).Where(p => p != first).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(repositoryRoot, p).Replace('\\', '/'))];
        return new TestClassResolution(
            [.. found.Where(f => f.Project == first).Select(f => f.Class).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            Path.GetRelativePath(repositoryRoot, first).Replace('\\', '/'),
            others.Count == 0 ? null : $"test class {testClass} also exists in {string.Join(", ", others)}; those projects were not run.");
    }

    private static string? QualifiedName(string source, string className)
    {
        string code = BlockComment().Replace(source, string.Empty);
        if (!Regex.IsMatch(code, $@"^[ \t]*(?:\[[^\]\r\n]*\][ \t]*)*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|unsafe|file|new)[ \t]+)*class[ \t]+{Regex.Escape(className)}\b", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            return null;
        }

        Match ns = NamespacePattern().Match(code);
        return ns.Success ? $"{ns.Groups[1].Value}.{className}" : className;
    }

    private string? NearestProject(string? directory, string root)
    {
        while (directory is not null && PathContainment.IsWithin(root, directory))
        {
            string? project = fileSystem.EnumerateFiles(directory, "*.csproj", recursive: false).Order(StringComparer.Ordinal).FirstOrDefault();
            if (project is not null)
            {
                return project;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NamespacePattern();
}
