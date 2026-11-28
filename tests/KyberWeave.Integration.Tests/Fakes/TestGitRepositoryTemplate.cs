using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using KyberWeave.Core.Processes;
using KyberWeave.Core.Review;
using KyberWeave.Tests.Corpus;

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
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    internal static readonly string TemplateRoot =
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
    ///     Creates an isolated copy of the pre-seeded <c>ReviewSuppressionsCommandTests</c> repository
    ///     carrying <c>src/a.cs</c> with a committed suppression that gives no reason, <c>src/a.csproj</c>,
    ///     <c>notes/readme.md</c>, and the initial commit.
    /// </summary>
    /// <remarks>
    ///     The suppression has to be in history rather than in the working diff: the scan judges the
    ///     lines a change adds, so a pre-existing suppression can only be shown to be ignored when it
    ///     was committed before the change under test.
    /// </remarks>
    public static void CreateSuppressionScanRepository(string destinationPath)
    {
        CreateRepositoryCopy("suppression-scan", destinationPath, InitializeSuppressionScanTemplate);
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
    ///     The archived plan a <c>RunDeliveryTests</c> run leaves behind. It is the text those tests read back as the
    ///     pull request's changes, low-confidence decisions and unverified items.
    /// </summary>
    private const string RunDeliveryPlanText = """
        ---
        id: plans/widget-export
        title: Export widgets as CSV from the list page
        doc-type: plan
        status: archived
        component: Widget API
        autonomy: autonomous
        ---

        # Export widgets as CSV from the list page

        ## Decisions

        | ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
        |---|---|---|---|---|
        | D1 | Which delimiter? | A comma, with RFC 4180 quoting. Semicolons were considered. | Spreadsheet default. | ANSWERED: owner requirement |
        | D2 | Cap the export size? | Yes, at 50,000 rows, with a message past it. | No source names a limit. | autonomous: low-confidence, no authoritative source |
        | D3 | Stream or buffer? | Stream the rows. | Memory stays flat. | autonomous: recommendation accepted (2026-11-13) |
        | AP1 | Approve and execute. | Approved. | Conductor. | autonomous: recommendation accepted (2026-11-13) |

        ## Tasks

        ### T1: RED for the CSV writer
        ### T2: GREEN for the CSV writer
        ### T3: Export button on the list page

        ## Completion summary

        - NOT VERIFIED: the export in a real browser — no browser in the run environment.
        """;

    /// <summary>The github.com URL the seed clone records, which every copy keeps as its fetch URL.</summary>
    private const string SeedFetchUrl = "https://github.com/acme/widgets.git";

    /// <summary>
    ///     The push URL every delivery repository records, <c>../remote.git</c>, which is the bare remote beside it.
    /// </summary>
    /// <remarks>
    ///     Relative on purpose: git resolves it against the directory it runs in, which the deliver step always makes
    ///     the run's own checkout. The copy therefore reaches the remote copied next to it and no other, and a copy
    ///     needs no git command at all to become independent of the template.
    /// </remarks>
    private const string SeedPushUrl = "../remote.git";

    /// <summary>
    ///     Creates the <c>ReviewCycleTests</c> repository: the council baseline already on its <c>feature</c> branch,
    ///     with the three gate configuration written but not committed.
    /// </summary>
    public static void CreateReviewCycleRepository(string destinationPath)
    {
        CreateRepositoryCopy("review-cycle", destinationPath, dir =>
        {
            InitializeReviewGatesCouncilTemplate(dir);
            SeedReviewCycleBranch(dir);
        });
    }

    /// <summary>
    ///     Creates the <c>ReviewGatesCycleTests</c> repository: the same council baseline, already on its
    ///     <c>feature</c> branch, with the three gate configuration written but not committed.
    /// </summary>
    public static void CreateReviewGatesCycleRepository(string destinationPath)
    {
        CreateRepositoryCopy("review-gates-cycle", destinationPath, dir =>
        {
            InitializeReviewGatesCouncilTemplate(dir);
            SeedReviewCycleBranch(dir);
        });
    }

    /// <summary>
    ///     Creates the <c>PostCheckoutGitFixture</c> ungoverned repository: an empty <c>git init</c> with nothing
    ///     committed, exactly as the fixture's own <c>git init</c> left it.
    /// </summary>
    public static void CreatePostCheckoutEmptyRepository(string destinationPath)
    {
        CreateRepositoryCopy("post-checkout-empty", destinationPath, InitializePostCheckoutEmptyTemplate);
    }

    /// <summary>
    ///     Creates the <c>PostCheckoutGitFixture</c> governed repository: only the <c>.kyber-weave</c> folder and its
    ///     docs, neither committed nor staged, with the fixture's own configuration text.
    /// </summary>
    /// <param name="destinationPath">The repository path the copy is written to.</param>
    /// <param name="config">The <c>kyber-weave.yml</c> text the test wants.</param>
    public static void CreatePostCheckoutGovernedRepository(string destinationPath, string config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(config);

        CreateRepositoryCopy("post-checkout-governed:" + config, destinationPath,
            dir => InitializePostCheckoutGovernedTemplate(dir, config));
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

        CopyDirectory(EnsureTemplate(templateKey, initialize), destinationPath);
    }

    /// <summary>
    ///     Gets or creates the pre-seeded template directory identified by <paramref name="templateKey" /> without
    ///     copying it. One initialization failure evicts the entry and deletes the partial directory, so a later
    ///     request retries.
    /// </summary>
    private static string EnsureTemplate(string templateKey, Action<string> initialize)
    {
        Lazy<string> lazyTemplate = Templates.GetOrAdd(
            templateKey,
            key => new Lazy<string>(() =>
            {
                string dir = GetTemplateDirectoryPath(key);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }

                Directory.CreateDirectory(dir);
                initialize(dir);
                return dir;
            }));

        try
        {
            return lazyTemplate.Value;
        }
        catch
        {
            Templates.TryRemove(KeyValuePair.Create(templateKey, lazyTemplate));
            TryDeleteDirectory(GetTemplateDirectoryPath(templateKey));
            throw;
        }
    }

    /// <summary>Deletes a template directory, which may already be gone or gone by now, without failing the caller.</summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup: the process-exit hook removes the template root anyway.
        }
    }

    private static string GetTemplateDirectoryPath(string key)
    {
        string sanitized = key.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
        return Path.Combine(TemplateRoot, $"{sanitized}_{hash}");
    }

    /// <summary>
    ///     Recursively copies a directory tree adhering to safe filesystem enumeration standards.
    /// </summary>
    /// <param name="sourceDir">The directory tree to copy.</param>
    /// <param name="destinationDir">The directory the tree is copied into, created when absent.</param>
    /// <param name="skipRelativePath">
    ///     A repository-relative path to leave out, or null to copy everything. A caller passes one to drop a file
    ///     whose recorded paths no longer hold, such as the template's own <c>.git/config</c>.
    /// </param>
    public static void CopyDirectory(string sourceDir, string destinationDir, string? skipRelativePath = null)
    {
        string skip = skipRelativePath?.Replace('\\', '/').Trim('/') ?? string.Empty;
        CopyDirectory(sourceDir, destinationDir, skip, Path.GetFullPath(sourceDir));
    }

    /// <param name="skip">The entry to leave out, relative to <paramref name="rootDir" />; empty to copy everything.</param>
    /// <param name="rootDir">The directory <paramref name="skip" /> is relative to, so a nested call keeps the whole path.</param>
    private static void CopyDirectory(string sourceDir, string destinationDir, string skip, string rootDir)
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
            if (skip.Length > 0 && string.Equals(
                    Path.GetRelativePath(rootDir, entry.FullName).Replace('\\', '/'),
                    skip,
                    StringComparison.Ordinal))
            {
                continue;
            }

            string targetPath = Path.Combine(destinationDir, entry.Name);
            if (entry is DirectoryInfo subDir)
            {
                CopyDirectory(subDir.FullName, targetPath, skip, rootDir);
            }
            else if (entry is FileInfo file)
            {
                try
                {
                    file.CopyTo(targetPath, true);
                }
                catch (IOException) when (IsTransientGitLock(file.FullName) && !File.Exists(file.FullName))
                {
                    // Git background maintenance can remove a transient lock between the
                    // directory enumeration and the copy. The template does not need it.
                    continue;
                }

                try
                {
                    File.SetAttributes(targetPath, FileAttributes.Normal);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort attribute normalization on POSIX / Windows.
                }
            }
        }
    }

    private static void InitializeReviewGatesAgentTemplate(string repo)
    {
        RunGit(repo, "init", "--quiet");
        ConfigureTemplateRepository(repo);
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

    private static void InitializeSuppressionScanTemplate(string repo)
    {
        RunGit(repo, "init", "--quiet");
        ConfigureTemplateRepository(repo);
        WriteFile(repo, "src/a.cs", "// a\n#pragma warning disable CS0168\n");
        WriteFile(repo, "src/a.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
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
        ConfigureTemplateRepository(repo);
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

    /// <summary>
    ///     Writes the <c>RunDeliveryTests</c> history once, into a directory holding the bare <c>remote.git</c> at the
    ///     base commit and the <c>run-delivery-model</c> checkout cloned from it on the run branch. The class copies
    ///     this seed per test, and copies it again to derive the seed's archived variants.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The checkout is a real clone because only a clone derives the remote-tracking refs a run needs:
    ///         <c>origin/main</c> on the same commit as the remote's own <c>main</c>, and <c>origin/HEAD</c> naming it.
    ///         <c>origin/main</c> therefore differs from <c>HEAD</c> by exactly the run branch's commits, which is the
    ///         measured change.
    ///     </para>
    ///     <para>
    ///         The checkout is clean: the run's work, the active plan and the evidence in an ignored scratch folder are
    ///         all committed. The archive move is the one change a test makes, so the deliver step has exactly that
    ///         to commit or find committed.
    ///     </para>
    /// </remarks>
    internal static async Task InitializeRunDeliverySeedTemplate(string seed)
    {
        const string branch = "claude/feat/widget-export";
        const string activePlan = "docs/plans/2026-11-13-widget-export.md";

        string remote = Path.Combine(seed, "remote.git");
        string model = Path.Combine(seed, "run-delivery-model");
        Directory.CreateDirectory(seed);

        RunGit(seed, "init", "--quiet", "--bare", remote);
        RunGit(seed, "clone", "--quiet", remote, model);
        GitIdentity(model);
        ConfigureTemplateRepository(model);
        WriteFile(model, "README.md", "# Widgets\n");
        WriteFile(model, "docs/plans/README.md", "# Plans\n");
        WriteFile(model, "docs/archive/plans/.keep", string.Empty);
        RunGit(model, "add", "-A");
        Commit(model, "chore: the widget list");
        RunGit(model, "push", "--quiet", "origin", "HEAD:refs/heads/main");
        // The clone already put the initial branch (main) on the commit it cloned, tracking origin/main.
        RunGit(model, "remote", "set-head", "origin", "main");
        // Record the github.com URL the deliver step reads as owner/name, and a push URL relative to the checkout, so a
        // copy of the seed needs no rewrite.
        RunGit(model, "remote", "set-url", "origin", SeedFetchUrl);
        RunGit(model, "remote", "set-url", "--push", "origin", SeedPushUrl);
        RunGit(model, "checkout", "--quiet", "-b", branch);
        WriteFile(model, ".kyber-weave/.keep", string.Empty);
        WriteFile(model, "src/Export.cs", "// export\n");
        // The plan is committed as an active plan. Moving it into the archive is the change the deliver step commits
        // or finds committed, so the seed stops short of it and each test makes that move itself.
        WriteFile(model, activePlan, RunDeliveryPlanText);
        RunGit(model, "add", "-A");
        Commit(model, "feat: export widgets as CSV");
        await WriteRunEvidence(model, branch);
    }

    /// <summary>
    ///     Writes one task's passing gate report and an approving review cycle with one minor finding, the evidence a
    ///     completed run leaves in its scratch folder. The scratch folder is ignored so it never reaches the pushed
    ///     branch, which is what the deliver step expects of a real run.
    /// </summary>
    private static async Task WriteRunEvidence(string model, string branch)
    {
        WriteFile(model, ".gitignore", ".agent_scratchpad/\n");
        RunGit(model, "add", ".gitignore");
        Commit(model, "chore: ignore scratch");
        WriteFile(model, ".agent_scratchpad/T3/task-gate.json", ReviewJson.Write(new GateReport(GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "passed"), new GateResult("test", true, 0, "passed"), new GateResult("format", true, 0, "passed")])));
        string cycle = Path.Combine(model, ".agent_scratchpad", "review", "review", "cycle-1");
        Directory.CreateDirectory(cycle);
        await File.WriteAllTextAsync(Path.Combine(cycle, "gates.json"), ReviewJson.Write(new GateReport(GateReport.CurrentSchema,
            [new GateResult("build", true, 0, "passed"), new GateResult("test", true, 0, "passed")])));
        await File.WriteAllTextAsync(Path.Combine(cycle, "findings.json"), ReviewJson.Write(new FindingsReport(FindingsReport.CurrentSchema,
            [new ReviewFinding("naming/vague", "naming", ReviewSeverity.Minor, 8, "src/Export.cs", 1, "// export",
                "The file name does not say it writes CSV.", "Read the file.", "A reader looks for the CSV writer elsewhere.", "Rename it CsvExport.cs.")],
            ["src/Export.cs"], 1)));
        ReviewTestServices.Cycles.WriteIdentity(cycle, new ReviewCycleIdentity(
            branch,
            await Output(model, "merge-base", "origin/main", "HEAD"),
            await Output(model, "rev-parse", "HEAD")));
    }

    /// <summary>
    ///     Takes the council baseline onto the <c>feature</c> branch and writes the gate configuration those tests read,
    ///     both done once here rather than per test.
    /// </summary>
    /// <remarks>
    ///     The configuration stays uncommitted: it is edited after the run starts, and nothing under test reads the
    ///     gate list from history.
    /// </remarks>
    private static void SeedReviewCycleBranch(string repo)
    {
        RunGit(repo, "checkout", "--quiet", "-b", "feature");
        WriteFile(repo, ".kyber-weave/kyber-weave.yml", ReviewCycleGateConfiguration);
    }

    /// <summary>The three <c>review.gates</c> entries the cycle tests rely on: one per changed kind, and one that always runs.</summary>
    private const string ReviewCycleGateConfiguration = """
        review:
          gates:
            - id: build
              include: ["**/*.cs"]
              run: [git, --version]
            - id: docs
              include: ["**/*.md"]
              run: [git, --version]
            - id: always
              run: [git, --version]
        """;

    private static void InitializePostCheckoutEmptyTemplate(string repo)
    {
        RunGit(repo, "init", "--quiet");
    }

    private static void InitializePostCheckoutGovernedTemplate(string repo, string config)
    {
        InitializePostCheckoutEmptyTemplate(repo);
        WriteFile(repo, ".kyber-weave/kyber-weave.yml", config);
        WriteFile(repo, "docs/README.md", "# Docs\n");
    }

    /// <summary>Pins the commit identity a template repository commits under.</summary>
    private static void GitIdentity(string repo)
    {
        RunGit(repo, "config", "user.name", "t");
        RunGit(repo, "config", "user.email", "t@example.com");
    }

    /// <summary>Commits everything already staged, refusing nothing because the checkout has nothing to commit.</summary>
    internal static void Commit(string repo, string message)
    {
        RunGit(repo, "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", message);
    }

    private static void InitializeDocsInitFixtureTemplate(
        string repo,
        string family,
        IReadOnlyList<string> technologies)
    {
        string cleanPath = Path.Combine(
            Corpus.Root, "tests", "fixtures", "gate-catalogue", family, "clean");
        CopyDirectory(cleanPath, repo);
        WriteHostConfig(repo, technologies);
        RunGit(repo, "init", "--quiet");
        ConfigureTemplateRepository(repo);
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
        ConfigureTemplateRepository(repo);
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

    /// <summary>
    ///     Whether a path is a transient git lock file. Only these may vanish during a template
    ///     copy; a missing tracked file is real corruption and must still fail the copy.
    /// </summary>
    internal static bool IsTransientGitLock(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/.git/", StringComparison.Ordinal) &&
               normalized.EndsWith(".lock", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Stops the template repository from starting background auto-gc or maintenance, whose
    ///     transient lock files can disappear while a test copies the template.
    /// </summary>
    private static void ConfigureTemplateRepository(string repo)
    {
        RunGit(repo, "config", "gc.auto", "0");
        RunGit(repo, "config", "maintenance.auto", "false");
    }

    /// <summary>
    ///     Runs git in <paramref name="repo" /> and throws git's own stderr when it fails. The directory need not be a
    ///     repository: seeding runs <c>git init</c> and <c>git clone</c> inside a plain template directory.
    /// </summary>
    internal static void RunGit(string repo, params string[] arguments) =>
        _ = Output(repo, arguments).GetAwaiter().GetResult();

    /// <summary>Runs git as <see cref="RunGit" /> does and returns its standard output, trimmed.</summary>
    private static async Task<string> Output(string repo, params string[] arguments)
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

        ProcessResult result = await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(60));
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed in '{repo}': {result.StandardError}");
        return result.StandardOutput.Trim();
    }

    private static void AppendInfoExclude(string repo, string entry)
    {
        string infoExcludePath = Path.Combine(repo, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(infoExcludePath)!);
        File.AppendAllText(infoExcludePath, entry);
    }
}
