using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using KyberWeave.Core.Processes;

namespace KyberWeave.Tests.Fakes;

/// <summary>
///     Pre-seeds baseline git repository templates once per test-assembly run and copies them into
///     each test's isolated temporary directory in &lt;2ms with 0 child processes during per-test setup.
/// </summary>
/// <remarks>
///     Spawning child git processes (<c>git init</c>, <c>git add</c>, <c>git commit</c>) on every test
///     method dominates runner overhead on macOS and Linux. Pre-seeding genuine <c>.git</c> trees and
///     copying them into isolated scratchroots gives every test real git behavior (untracked files,
///     diffs, staging, commit graphs) with no mock divergence and zero OS child process overhead at setup.
/// </remarks>
public static class TestGitRepositoryTemplate
{
    private static readonly string TemplateRoot =
        Path.Combine(Path.GetTempPath(), "kyber-weave-git-templates-" + Guid.NewGuid().ToString("N"));

    private static readonly ConcurrentDictionary<string, Lazy<string>> Templates = new(StringComparer.Ordinal);

    static TestGitRepositoryTemplate()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(TemplateRoot)) Directory.Delete(TemplateRoot, true);
            }
            catch
            {
                // Best-effort cleanup on process termination.
            }
        };
    }

    /// <summary>
    ///     Creates an isolated copy of the pre-seeded <c>ReviewGatesAgentCommandTests</c> repository
    ///     carrying <c>src/a.cs</c>, <c>src/gone.cs</c>, <c>notes/readme.md</c>, and initial commit.
    /// </summary>
    public static void CreateReviewGatesAgentRepository(string destinationPath)
    {
        CreateRepositoryCopy("review-gates-agent", destinationPath, InitializeReviewGatesAgentTemplate);
    }

    /// <summary>
    ///     Creates an isolated copy of the pre-seeded <c>ReviewGatesCouncilCommandTests</c> repository
    ///     carrying <c>src/a.cs</c>, <c>notes/readme.md</c>, and initial commit.
    /// </summary>
    public static void CreateReviewGatesCouncilRepository(string destinationPath)
    {
        CreateRepositoryCopy("review-gates-council", destinationPath, InitializeReviewGatesCouncilTemplate);
    }

    /// <summary>
    ///     Creates an isolated copy of the pre-seeded <c>DocsInitReviewGatesTests</c> repository
    ///     initialized from the specified gate catalogue fixture family clean directory and host technologies.
    /// </summary>
    public static void CreateDocsInitFixtureRepository(
        string family,
        IReadOnlyList<string> technologies,
        string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentNullException.ThrowIfNull(technologies);

        string templateKey = $"docs-init-fixture:{family}:{string.Join(",", technologies)}";
        CreateRepositoryCopy(templateKey, destinationPath,
            dir => InitializeDocsInitFixtureTemplate(dir, family, technologies));
    }

    /// <summary>
    ///     Creates an isolated copy of an empty pre-seeded <c>DocsInitReviewGatesTests</c> repository
    ///     carrying only the written host configuration and initial commit.
    /// </summary>
    public static void CreateDocsInitEmptyRepository(
        IReadOnlyList<string> technologies,
        string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(technologies);

        string templateKey = $"docs-init-empty:{string.Join(",", technologies)}";
        CreateRepositoryCopy(templateKey, destinationPath, dir => InitializeDocsInitEmptyTemplate(dir, technologies));
    }

    /// <summary>
    ///     Gets or creates a pre-seeded git template identified by <paramref name="templateKey" /> and copies
    ///     the full working tree (including <c>.git</c>) into <paramref name="destinationPath" />.
    /// </summary>
    public static void CreateRepositoryCopy(
        string templateKey,
        string destinationPath,
        Action<string> initialize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(initialize);

        Lazy<string> lazyTemplate = Templates.GetOrAdd(
            templateKey,
            key => new Lazy<string>(() =>
            {
                string dir = Path.Combine(TemplateRoot, key.Replace(':', '_').Replace('/', '_'));
                Directory.CreateDirectory(dir);
                initialize(dir);
                return dir;
            }));

        string templateDir = lazyTemplate.Value;
        CopyDirectory(templateDir, destinationPath);
    }

    /// <summary>
    ///     Recursively copies a directory tree adhering to safe filesystem enumeration standards.
    /// </summary>
    public static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        DirectoryInfo sourceInfo = new(sourceDir);

        foreach (FileSystemInfo entry in sourceInfo.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0
        }))
        {
            if (entry.LinkTarget is not null) continue;

            string targetPath = Path.Combine(destinationDir, entry.Name);
            if (entry is DirectoryInfo subDir)
            {
                CopyDirectory(subDir.FullName, targetPath);
            }
            else if (entry is FileInfo file)
            {
                file.CopyTo(targetPath, true);
                try
                {
                    File.SetAttributes(targetPath, FileAttributes.Normal);
                }
                catch
                {
                    // Best-effort attribute normalization on POSIX / Windows.
                }
            }
        }
    }

    private static void InitializeReviewGatesAgentTemplate(string repo)
    {
        RunGit(repo, "init", "--quiet");
        WriteFile(repo, "src/a.cs", "// a\n");
        WriteFile(repo, "src/gone.cs", "// gone\n");
        WriteFile(repo, "notes/readme.md", "# readme\n");
        RunGit(repo, "add", "-A");
        RunGit(
            repo,
            "-c", "user.name=t2r",
            "-c", "user.email=t2r@example.invalid",
            "-c", "commit.gpgsign=false",
            "commit", "--quiet", "--allow-empty", "-m", "initial");
        AppendInfoExclude(repo, ".kyber-weave/\n");
    }

    private static void InitializeReviewGatesCouncilTemplate(string repo)
    {
        RunGit(repo, "init", "--quiet");
        WriteFile(repo, "src/a.cs", "// a\n");
        WriteFile(repo, "notes/readme.md", "# readme\n");
        RunGit(repo, "add", "-A");
        RunGit(
            repo,
            "-c", "user.name=task3",
            "-c", "user.email=task3@example.invalid",
            "-c", "commit.gpgsign=false",
            "commit", "--quiet", "--allow-empty", "-m", "initial");
        AppendInfoExclude(repo, ".kyber-weave/\n");
    }

    private static void InitializeDocsInitFixtureTemplate(
        string repo,
        string family,
        IReadOnlyList<string> technologies)
    {
        string cleanPath = Path.Combine(
            KyberWeaveTestPaths.ToolRoot, "tests", "fixtures", "gate-catalogue", family, "clean");
        CopyDirectory(cleanPath, repo);
        WriteHostConfig(repo, technologies);
        RunGit(repo, "init", "--quiet");
        RunGit(repo, "add", "-A");
        RunGit(
            repo,
            "-c", "user.name=t2r",
            "-c", "user.email=t2r@example.invalid",
            "-c", "commit.gpgsign=false",
            "commit", "--quiet", "-m", "fixture");
    }

    private static void InitializeDocsInitEmptyTemplate(
        string repo,
        IReadOnlyList<string> technologies)
    {
        WriteHostConfig(repo, technologies);
        RunGit(repo, "init", "--quiet");
        RunGit(repo, "add", "-A");
        RunGit(
            repo,
            "-c", "user.name=t2r",
            "-c", "user.email=t2r@example.invalid",
            "-c", "commit.gpgsign=false",
            "commit", "--quiet", "-m", "fixture");
    }

    private static void WriteHostConfig(string repo, IReadOnlyList<string> technologies)
    {
        StringBuilder yaml = new();
        yaml.AppendLine("ontology:");
        yaml.AppendLine("  docs-root: docs");
        if (technologies.Count == 0)
        {
            yaml.AppendLine("  technologies: []");
        }
        else
        {
            yaml.AppendLine("  technologies:");
            foreach (string technology in technologies) yaml.Append("    - ").AppendLine(technology);
        }

        string directory = Path.Combine(repo, ".kyber-weave");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "kyber-weave.yml"), yaml.ToString());
    }

    private static void WriteFile(string repo, string relativePath, string content)
    {
        string path = Path.Combine(repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void RunGit(string repo, params string[] arguments)
    {
        ProcessStartInfo startInfo = new("git")
        {
            WorkingDirectory = repo,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed in '{repo}': {result.StandardError}");
    }

    private static void AppendInfoExclude(string repo, string entry)
    {
        string infoExcludePath = Path.Combine(repo, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(infoExcludePath)!);
        File.AppendAllText(infoExcludePath, entry);
    }
}
