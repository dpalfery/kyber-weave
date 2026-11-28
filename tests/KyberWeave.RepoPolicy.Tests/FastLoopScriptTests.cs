using System.Collections;
using System.Diagnostics;
using KyberWeave.Core.Processes;
using KyberWeave.Tests.Corpus;
using Xunit;
using Xunit.Sdk;

namespace KyberWeave.Tests;

/// <summary>
/// Behavioral regression tests for the local fast inner-loop runner (<c>scripts/fast-loop.sh</c>).
/// </summary>
/// <remarks>
/// Fast-loop answers "did this edit break something" in seconds by inspecting changed workspace paths
/// and triggering targeted checks. These tests guard component routing: editing markdown documentation
/// anywhere under <c>dash/</c> (including nested crates and UI packages) must invoke <c>docs validate .</c>,
/// and editing tray UI files when dependencies are uninstalled must exit 1 with a remediation hint.
/// </remarks>
public sealed class FastLoopScriptTests
{
    private static readonly ICorpusSource Corpus = CheckoutCorpusSource.Checkout;

    private static void SkipIfBashUnavailable()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/bash"))
        {
            throw SkipException.ForSkip("Shell script tests require /bin/bash on a POSIX environment.");
        }
    }

    /// <summary>
    /// Editing markdown inside <c>dash/tray/src-tauri/</c> must trigger both Rust crate checks
    /// and documentation validation.
    /// </summary>
    [Fact]
    public async Task ChangedMarkdownUnderSrcTauri_RunsBothRustChecksAndDocsValidate()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();

        string readmePath = Path.Combine(fixture.Root, "dash", "tray", "src-tauri", "README.md");
        await File.AppendAllTextAsync(readmePath, "\n## Rust Crate Documentation Changes\n");

        _ = await fixture.RunFastLoopAsync();
        string log = fixture.GetCallLog();

        Assert.Contains("cargo fmt --check", log, StringComparison.Ordinal);
        Assert.Contains("cargo test", log, StringComparison.Ordinal);
        Assert.Contains("docs validate .", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A developer loop never runs the integration project (requirement 7.3): <c>--touched</c> runs the touched
    /// unit classes, says that CI runs the touched integration classes, and does not start them.
    /// </summary>
    [Fact]
    public async Task TouchedNeverRunsIntegrationClassesAndSaysCiRunsThem()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();

        fixture.WriteProductSource();
        fixture.WriteUnitTestClass("TouchedUnitTests");
        fixture.WriteIntegrationTestClasses(21);

        ProcessResult result = await fixture.RunFastLoopAsync("--touched");
        string log = fixture.GetCallLog();

        Assert.DoesNotContain("running the fast tier instead", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        Assert.Contains("integration classes touched", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("integration tests (touched)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-class KyberWeave.Application.Tests.TouchedUnitTests", log, StringComparison.Ordinal);
        Assert.DoesNotContain("KyberWeave.Integration.Tests.TouchedCase", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A filter that names no test in either project fails the loop: the unit total and the repository-policy total
    /// are counted together for an explicit <c>--class</c> / <c>--method</c>, so a run that matched nothing cannot
    /// read as clean.
    /// </summary>
    [Fact]
    public async Task FilterThatMatchesNoTestInEitherProject_FailsTheLoop()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();
        fixture.ReplaceDotnetStub(
            """
            #!/bin/sh
            echo "dotnet $*" >> "$CALL_LOG"
            case "$*" in
              *tests/KyberWeave.Application.Tests/bin/*)
                echo "Total: 0"
                ;;
              *tests/KyberWeave.RepoPolicy.Tests/bin/*)
                echo "Total: 0"
                ;;
            esac
            exit 0
            """);

        ProcessResult result = await fixture.RunFastLoopAsync("--class", "KyberWeave.Application.Tests.DoesNotExistTests");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no unit or repository-policy tests ran; the filter matched nothing", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("KyberWeave.Application.Tests.DoesNotExistTests", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Fast tier clean", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// An explicit filter may select only repository-policy tests. The unit projects then report no total, and the
    /// policy run's total is what proves the filter matched.
    /// </summary>
    [Fact]
    public async Task FilterThatSelectsOnlyPolicyTests_Passes()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();
        fixture.ReplaceDotnetStub(
            """
            #!/bin/sh
            echo "dotnet $*" >> "$CALL_LOG"
            case "$*" in
              *tests/KyberWeave.RepoPolicy.Tests/bin/*)
                echo "Total: 1, Passed: 1"
                ;;
              *tests/KyberWeave.*.Tests/bin/*)
                echo "Total: 0"
                ;;
            esac
            exit 0
            """);

        ProcessResult result = await fixture.RunFastLoopAsync("--class", "KyberWeave.Tests.ProcessSpawnSitesTests");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("KyberWeave.RepoPolicy.Tests.dll -class KyberWeave.Tests.ProcessSpawnSitesTests", fixture.GetCallLog(), StringComparison.Ordinal);
        Assert.DoesNotContain("the filter matched nothing", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The touched routing reads a namespace that starts after leading whitespace, so the class it names is the
    /// one the runner can match rather than the fallback namespace.
    /// </summary>
    [Fact]
    public async Task TouchedReadsAnIndentedNamespaceAndNamesTheTestClass()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();

        fixture.WriteProductSource("ReviewCycleService");
        fixture.WriteChangedTestFile(
            "KyberWeave.Application.Tests",
            "Review/ReviewServiceSeamTests.cs",
            """
              namespace KyberWeave.Application.Tests.Review;

            public sealed class ReviewServiceSeamTests
            {
                private ReviewCycleService? _service;
            }
            """);

        ProcessResult result = await fixture.RunFastLoopAsync("--touched");
        string log = fixture.GetCallLog();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("-class KyberWeave.Application.Tests.Review.ReviewServiceSeamTests", log, StringComparison.Ordinal);
        Assert.DoesNotContain("-class KyberWeave.Tests.ReviewServiceSeamTests", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Editing markdown inside <c>dash/tray/ui/</c> must trigger documentation validation.
    /// </summary>
    [Fact]
    public async Task ChangedMarkdownUnderTrayUi_RunsDocsValidate()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();

        string nodeModulesDir = Path.Combine(fixture.Root, "dash", "tray", "ui", "node_modules");
        Directory.CreateDirectory(nodeModulesDir);
        await File.WriteAllTextAsync(Path.Combine(nodeModulesDir, ".keep"), string.Empty);

        string readmePath = Path.Combine(fixture.Root, "dash", "tray", "ui", "README.md");
        await File.AppendAllTextAsync(readmePath, "\n## Tray UI Documentation Changes\n");

        _ = await fixture.RunFastLoopAsync();
        string log = fixture.GetCallLog();

        Assert.Contains("docs validate .", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Editing tray UI files when <c>dash/tray/ui/node_modules</c> does not exist must exit 1,
    /// provide the remediation command <c>npm --prefix dash/tray/ui ci</c>, and not print <c>Fast tier clean.</c>.
    /// </summary>
    [Fact]
    public async Task ChangedTrayUiWithoutNodeModules_ExitsOneWithCiHintAndDoesNotPrintClean()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();

        string nodeModulesDir = Path.Combine(fixture.Root, "dash", "tray", "ui", "node_modules");
        if (Directory.Exists(nodeModulesDir))
        {
            Directory.Delete(nodeModulesDir, true);
        }

        string appPath = Path.Combine(fixture.Root, "dash", "tray", "ui", "src", "App.tsx");
        await File.AppendAllTextAsync(appPath, "\nexport const AppExtension = 42;\n");

        ProcessResult result = await fixture.RunFastLoopAsync();
        string combinedOutput = result.StandardOutput + Environment.NewLine + result.StandardError;

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("npm --prefix dash/tray/ui ci", combinedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Fast tier clean", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A change to anything but C# source (here a script) runs the repository-policy tests as well
    /// as the unit projects.
    /// </summary>
    [Fact]
    public async Task ChangedScript_RunsTheRepositoryPolicyTests()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();

        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "scripts", "new-tool.sh"), "#!/bin/sh\nexit 0\n");

        ProcessResult result = await fixture.RunFastLoopAsync();
        string log = fixture.GetCallLog();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("KyberWeave.Application.Tests.dll -trait- Category=Scale", log, StringComparison.Ordinal);
        Assert.Contains("KyberWeave.RepoPolicy.Tests.dll -trait- Category=Scale", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A C#-only change to product code runs the unit projects and, of the repository-policy tests, only
    /// the ones that read source text: the spawn-site audit, the upstream-name scan and the domain-purity scan.
    /// </summary>
    [Fact]
    public async Task ChangedProductCSharpOnly_RunsOnlyThePolicySourceScans()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();

        fixture.WriteProductSource();

        ProcessResult result = await fixture.RunFastLoopAsync();
        string log = fixture.GetCallLog();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("KyberWeave.Application.Tests.dll -trait- Category=Scale", log, StringComparison.Ordinal);
        Assert.Contains(
            "KyberWeave.RepoPolicy.Tests.dll -class KyberWeave.Tests.ProcessSpawnSitesTests " +
            "-class KyberWeave.Tests.UpstreamSeveranceTests",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain("KyberWeave.RepoPolicy.Tests.dll -trait-", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// The source-scan run is checked on its own, so a scan run that matched no tests fails the loop even
    /// though the unit projects ran: a renamed scan class cannot pass as clean.
    /// </summary>
    [Fact]
    public async Task PolicySourceScansThatRunNoTests_FailTheLoop()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();
        fixture.ReplaceDotnetStub(
            """
            #!/bin/sh
            echo "dotnet $*" >> "$CALL_LOG"
            case "$*" in
              *tests/KyberWeave.Application.Tests/bin/*)
                echo "Total: 1, Passed: 1"
                ;;
            esac
            exit 0
            """);

        fixture.WriteProductSource();

        ProcessResult result = await fixture.RunFastLoopAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("the policy source scans ran no tests", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Fast tier clean", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A unit-project failure does not stop the repository-policy run: both run, both statuses are
    /// reported, and the loop still fails.
    /// </summary>
    [Fact]
    public async Task UnitProjectFailure_StillRunsThePolicyTestsAndFailsTheLoop()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();
        fixture.ReplaceDotnetStub(
            """
            #!/bin/sh
            echo "dotnet $*" >> "$CALL_LOG"
            case "$*" in
              *tests/KyberWeave.Application.Tests/bin/*)
                echo "Total: 1, Failed: 1"
                exit 1
                ;;
              *tests/KyberWeave.RepoPolicy.Tests/bin/*)
                echo "Total: 1, Passed: 1"
                ;;
            esac
            exit 0
            """);

        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "scripts", "new-tool.sh"), "#!/bin/sh\nexit 0\n");

        ProcessResult result = await fixture.RunFastLoopAsync();
        string log = fixture.GetCallLog();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("KyberWeave.RepoPolicy.Tests.dll", log, StringComparison.Ordinal);
        Assert.Contains(
            "tests failed: unit projects exit 1, repository-policy tests exit 0",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Fast tier clean", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same holds on the C#-only path: a failing unit project does not stop the policy source scans,
    /// and both statuses are reported.
    /// </summary>
    [Fact]
    public async Task UnitProjectFailureOnACSharpOnlyChange_StillRunsTheSourceScans()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();
        fixture.ReplaceDotnetStub(FailingUnitStub(policyReportsTests: true));
        fixture.WriteProductSource();

        ProcessResult result = await fixture.RunFastLoopAsync();
        string log = fixture.GetCallLog();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("KyberWeave.RepoPolicy.Tests.dll -class KyberWeave.Tests.ProcessSpawnSitesTests", log, StringComparison.Ordinal);
        Assert.Contains(
            "tests failed: unit projects exit 1, repository-policy tests exit 0",
            result.StandardError,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A source-scan run that matched nothing does not hide a unit failure: both are reported before
    /// the loop exits.
    /// </summary>
    [Fact]
    public async Task EmptySourceScanRunWithAUnitFailure_ReportsBoth()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = await FastLoopFixture.CreateAsync();
        fixture.ReplaceDotnetStub(FailingUnitStub(policyReportsTests: false));
        fixture.WriteProductSource();

        ProcessResult result = await fixture.RunFastLoopAsync();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("the policy source scans ran no tests", result.StandardError, StringComparison.Ordinal);
        Assert.Contains(
            "tests failed: unit projects exit 1, repository-policy tests exit 1",
            result.StandardError,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>dotnet</c> stub whose unit test run fails, and whose policy run reports one passing test or none.
    /// </summary>
    private static string FailingUnitStub(bool policyReportsTests) =>
        $$"""
        #!/bin/sh
        echo "dotnet $*" >> "$CALL_LOG"
        case "$*" in
          *tests/KyberWeave.Application.Tests/bin/*)
            echo "Total: 1, Failed: 1"
            exit 1
            ;;
          *tests/KyberWeave.RepoPolicy.Tests/bin/*)
            {{(policyReportsTests ? "echo \"Total: 1, Passed: 1\"" : ":")}}
            ;;
        esac
        exit 0
        """;

    /// <summary>
    /// The source scans the fast loop always runs are named by class in the script, so a renamed class
    /// would silently match nothing. Each name must be a test class in this project.
    /// </summary>
    [Fact]
    public void PolicySourceScansNameTestClassesThatExist()
    {
        string script = Corpus.ReadText("scripts/fast-loop.sh");
        // Read to the closing parenthesis, not the end of the line, so a wrapped array still parses.
        int start = script.IndexOf("POLICY_SOURCE_SCANS=(", StringComparison.Ordinal);
        Assert.True(start >= 0, "scripts/fast-loop.sh no longer declares POLICY_SOURCE_SCANS.");
        int end = script.IndexOf(')', start);
        Assert.True(end > start, "POLICY_SOURCE_SCANS has no closing parenthesis.");
        string declaration = script[start..(end + 1)];
        string[] classes = System.Text.RegularExpressions.Regex.Matches(declaration, @"-class ([\w.]+)")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.Equal(4, classes.Length);
        Assert.All(classes, name => Assert.NotNull(typeof(FastLoopScriptTests).Assembly.GetType(name)));
    }

    /// <summary>
    /// Disposable temporary git repository fixture configured with stub toolchain executables.
    /// </summary>
    private sealed class FastLoopFixture : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public string Root { get; }

        public string BinDir { get; }

        public string CallLogPath { get; }

        /// <summary>Creates the fixture and waits for its temporary git repository to be committed.</summary>
        public static async Task<FastLoopFixture> CreateAsync()
        {
            FastLoopFixture fixture = new();
            await fixture.InitializeAsync();
            return fixture;
        }

        private FastLoopFixture()
        {
            Root = Path.Combine(_temp.Path, "repo");
            BinDir = Path.Combine(_temp.Path, "fake-bin");
            CallLogPath = Path.Combine(_temp.Path, "call_log.txt");

            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(BinDir);

            CreateStubs();
        }

        /// <summary>Initializes the fixture's temporary git repository and commits its initial tree.</summary>
        public Task InitializeAsync() => InitializeRepoAsync();

        public async Task<ProcessResult> RunFastLoopAsync(params string[] arguments)
        {
            ProcessStartInfo startInfo = new("/bin/bash")
            {
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("scripts/fast-loop.sh");

            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    startInfo.Environment[key] = value;
                }
            }

            string originalPath = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
            startInfo.Environment["PATH"] = $"{BinDir}:{originalPath}";
            startInfo.Environment["CALL_LOG"] = CallLogPath;

            return await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(30));
        }

        /// <summary>Writes one product C# file, so the change is C#-only.</summary>
        public void WriteProductSource()
        {
            string source = Path.Combine(Root, "src", "KyberWeave.Core", "Example.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "namespace Example;\n");
        }

        /// <summary>Writes one product C# file declaring <paramref name="typeName"/>, so a test can reference it.</summary>
        public void WriteProductSource(string typeName)
        {
            string source = Path.Combine(Root, "src", "KyberWeave.Core", "Example.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(
                source,
                $"namespace Example;\n\npublic sealed class {typeName}\n{{\n}}\n");
        }

        /// <summary>Writes one changed test file at a path relative to its project.</summary>
        public void WriteChangedTestFile(string project, string relativePath, string content)
        {
            string path = Path.Combine(Root, "tests", project, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.ReplaceLineEndings("\n") + "\n");
        }

        /// <summary>Writes one changed unit-test file declaring <paramref name="className"/>.</summary>
        public void WriteUnitTestClass(string className)
        {
            WriteChangedTestClass("KyberWeave.Application.Tests", className);
        }

        /// <summary>Writes <paramref name="count"/> changed integration-test files, one class each.</summary>
        public void WriteIntegrationTestClasses(int count)
        {
            for (int index = 1; index <= count; index++)
                WriteChangedTestClass("KyberWeave.Integration.Tests", $"TouchedCase{index:00}Tests");
        }

        private void WriteChangedTestClass(string project, string className)
        {
            string path = Path.Combine(Root, "tests", project, className + ".cs");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                $"namespace {project};\n\npublic sealed class {className}\n{{\n}}\n");
        }

        /// <summary>Replaces the <c>dotnet</c> stub, for a test that needs a different runner reply.</summary>
        public void ReplaceDotnetStub(string script) => WriteExecutableStub(Path.Combine(BinDir, "dotnet"), script);

        public string GetCallLog()
        {
            return File.Exists(CallLogPath) ? File.ReadAllText(CallLogPath) : string.Empty;
        }

        private void CreateStubs()
        {
            WriteExecutableStub(
                Path.Combine(BinDir, "dotnet"),
                """
                #!/bin/sh
                echo "dotnet $*" >> "$CALL_LOG"
                case "$*" in
                  *tests/KyberWeave.*.Tests/bin/*)
                    echo "Total: 1, Passed: 1"
                    ;;
                esac
                exit 0
                """);

            WriteExecutableStub(
                Path.Combine(BinDir, "npm"),
                """
                #!/bin/sh
                echo "npm $*" >> "$CALL_LOG"
                exit 0
                """);

            WriteExecutableStub(
                Path.Combine(BinDir, "cargo"),
                """
                #!/bin/sh
                echo "cargo $*" >> "$CALL_LOG"
                exit 0
                """);
        }

        private async Task InitializeRepoAsync()
        {
            await RunGitAsync(Root, "init", "--quiet");
            await RunGitAsync(Root, "config", "user.name", "TestUser");
            await RunGitAsync(Root, "config", "user.email", "test@example.com");
            await RunGitAsync(Root, "config", "commit.gpgsign", "false");

            string fastLoopDest = Path.Combine(Root, "scripts", "fast-loop.sh");
            Directory.CreateDirectory(Path.GetDirectoryName(fastLoopDest)!);
            await File.WriteAllTextAsync(fastLoopDest, Corpus.ReadText("scripts/fast-loop.sh").ReplaceLineEndings("\n"));
            MakeExecutable(fastLoopDest);

            string cliBinDir = Path.Combine(Root, "src", "KyberWeave.Cli", "bin", "Release", "net10.0");
            Directory.CreateDirectory(cliBinDir);
            await File.WriteAllTextAsync(Path.Combine(cliBinDir, "kyber-weave.dll"), "stub");
            await File.WriteAllTextAsync(Path.Combine(cliBinDir, "KyberWeave.Cli.dll"), "stub");

            foreach (string project in new[] { "Application", "Hooks", "Kernel", "Mcp", "Arbiter", "Cli", "Data", "Architecture", "Content", "RepoPolicy" })
            {
                string testBinDir = Path.Combine(Root, "tests", $"KyberWeave.{project}.Tests", "bin", "Release", "net10.0");
                Directory.CreateDirectory(testBinDir);
                await File.WriteAllTextAsync(Path.Combine(testBinDir, $"KyberWeave.{project}.Tests.dll"), "stub");
            }

            await File.WriteAllTextAsync(Path.Combine(Root, "KyberWeave.sln"), "# stub sln\n");

            await WriteTrackedFileAsync("dash/tray/src-tauri/README.md", "# Tray Rust\n");
            await WriteTrackedFileAsync("dash/tray/ui/README.md", "# Tray UI\n");
            await WriteTrackedFileAsync("dash/tray/ui/src/App.tsx", "// App\n");
            await WriteTrackedFileAsync("dash/package.json", "{}\n");

            string dashNodeModules = Path.Combine(Root, "dash", "node_modules");
            Directory.CreateDirectory(dashNodeModules);
            await File.WriteAllTextAsync(Path.Combine(dashNodeModules, ".keep"), string.Empty);

            await RunGitAsync(Root, "add", "-A");
            await RunGitAsync(Root, "commit", "--quiet", "--allow-empty", "-m", "initial tree");
        }

        private async Task WriteTrackedFileAsync(string relativePath, string content)
        {
            string fullPath = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, content);
        }

        private static void WriteExecutableStub(string path, string script)
        {
            File.WriteAllText(path, script.Replace("\r\n", "\n", StringComparison.Ordinal));
            MakeExecutable(path);
        }

        private static void MakeExecutable(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }

        private static async Task RunGitAsync(string repoDir, params string[] arguments)
        {
            ProcessStartInfo startInfo = new("git")
            {
                WorkingDirectory = repoDir,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            ProcessResult result = await ProcessRunner.RunAsync(startInfo, string.Empty, TimeSpan.FromSeconds(30));
            Assert.True(
                result.ExitCode == 0,
                $"git {string.Join(' ', arguments)} failed (exit {result.ExitCode}): {result.StandardError}");
        }

        public void Dispose()
        {
            _temp.Dispose();
        }
    }
}
