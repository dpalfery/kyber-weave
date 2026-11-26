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
    public void ChangedMarkdownUnderSrcTauri_RunsBothRustChecksAndDocsValidate()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();

        string readmePath = Path.Combine(fixture.Root, "dash", "tray", "src-tauri", "README.md");
        File.AppendAllText(readmePath, "\n## Rust Crate Documentation Changes\n");

        _ = fixture.RunFastLoop();
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
    public void TouchedNeverRunsIntegrationClassesAndSaysCiRunsThem()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();

        fixture.WriteProductSource();
        fixture.WriteUnitTestClass("TouchedUnitTests");
        fixture.WriteIntegrationTestClasses(21);

        ProcessResult result = fixture.RunFastLoop("--touched");
        string log = fixture.GetCallLog();

        Assert.DoesNotContain("running the fast tier instead", result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        Assert.Contains("integration classes touched", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("integration tests (touched)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("-class KyberWeave.Application.Tests.TouchedUnitTests", log, StringComparison.Ordinal);
        Assert.DoesNotContain("KyberWeave.Integration.Tests.TouchedCase", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// A filter that names no unit test fails the loop even when the repository-policy run reports tests:
    /// the unit total is read from the unit logs, not from a combined log the policy run can satisfy.
    /// </summary>
    [Fact]
    public void FilterThatMatchesNoUnitTests_FailsEvenWhenPolicyTestsReportATotal()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();
        fixture.ReplaceDotnetStub(
            """
            #!/bin/sh
            echo "dotnet $*" >> "$CALL_LOG"
            case "$*" in
              *tests/KyberWeave.Application.Tests/bin/*)
                echo "Total: 0"
                ;;
              *tests/KyberWeave.RepoPolicy.Tests/bin/*)
                echo "Total: 1, Passed: 1"
                ;;
            esac
            exit 0
            """);

        ProcessResult result = fixture.RunFastLoop("--class", "KyberWeave.Application.Tests.DoesNotExistTests");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no unit tests ran; the filter matched nothing", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("KyberWeave.Application.Tests.DoesNotExistTests", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Fast tier clean", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// The touched routing reads a namespace that starts after leading whitespace, so the class it names is the
    /// one the runner can match rather than the fallback namespace.
    /// </summary>
    [Fact]
    public void TouchedReadsAnIndentedNamespaceAndNamesTheTestClass()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();

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

        ProcessResult result = fixture.RunFastLoop("--touched");
        string log = fixture.GetCallLog();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("-class KyberWeave.Application.Tests.Review.ReviewServiceSeamTests", log, StringComparison.Ordinal);
        Assert.DoesNotContain("-class KyberWeave.Tests.ReviewServiceSeamTests", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Editing markdown inside <c>dash/tray/ui/</c> must trigger documentation validation.
    /// </summary>
    [Fact]
    public void ChangedMarkdownUnderTrayUi_RunsDocsValidate()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();

        string nodeModulesDir = Path.Combine(fixture.Root, "dash", "tray", "ui", "node_modules");
        Directory.CreateDirectory(nodeModulesDir);
        File.WriteAllText(Path.Combine(nodeModulesDir, ".keep"), string.Empty);

        string readmePath = Path.Combine(fixture.Root, "dash", "tray", "ui", "README.md");
        File.AppendAllText(readmePath, "\n## Tray UI Documentation Changes\n");

        _ = fixture.RunFastLoop();
        string log = fixture.GetCallLog();

        Assert.Contains("docs validate .", log, StringComparison.Ordinal);
    }

    /// <summary>
    /// Editing tray UI files when <c>dash/tray/ui/node_modules</c> does not exist must exit 1,
    /// provide the remediation command <c>npm --prefix dash/tray/ui ci</c>, and not print <c>Fast tier clean.</c>.
    /// </summary>
    [Fact]
    public void ChangedTrayUiWithoutNodeModules_ExitsOneWithCiHintAndDoesNotPrintClean()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();

        string nodeModulesDir = Path.Combine(fixture.Root, "dash", "tray", "ui", "node_modules");
        if (Directory.Exists(nodeModulesDir))
        {
            Directory.Delete(nodeModulesDir, true);
        }

        string appPath = Path.Combine(fixture.Root, "dash", "tray", "ui", "src", "App.tsx");
        File.AppendAllText(appPath, "\nexport const AppExtension = 42;\n");

        ProcessResult result = fixture.RunFastLoop();
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
    public void ChangedScript_RunsTheRepositoryPolicyTests()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();

        File.WriteAllText(Path.Combine(fixture.Root, "scripts", "new-tool.sh"), "#!/bin/sh\nexit 0\n");

        ProcessResult result = fixture.RunFastLoop();
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
    public void ChangedProductCSharpOnly_RunsOnlyThePolicySourceScans()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();

        fixture.WriteProductSource();

        ProcessResult result = fixture.RunFastLoop();
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
    public void PolicySourceScansThatRunNoTests_FailTheLoop()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();
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

        ProcessResult result = fixture.RunFastLoop();

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("the policy source scans ran no tests", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("Fast tier clean", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A unit-project failure does not stop the repository-policy run: both run, both statuses are
    /// reported, and the loop still fails.
    /// </summary>
    [Fact]
    public void UnitProjectFailure_StillRunsThePolicyTestsAndFailsTheLoop()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();
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

        File.WriteAllText(Path.Combine(fixture.Root, "scripts", "new-tool.sh"), "#!/bin/sh\nexit 0\n");

        ProcessResult result = fixture.RunFastLoop();
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
    public void UnitProjectFailureOnACSharpOnlyChange_StillRunsTheSourceScans()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();
        fixture.ReplaceDotnetStub(FailingUnitStub(policyReportsTests: true));
        fixture.WriteProductSource();

        ProcessResult result = fixture.RunFastLoop();
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
    public void EmptySourceScanRunWithAUnitFailure_ReportsBoth()
    {
        SkipIfBashUnavailable();
        using FastLoopFixture fixture = new();
        fixture.ReplaceDotnetStub(FailingUnitStub(policyReportsTests: false));
        fixture.WriteProductSource();

        ProcessResult result = fixture.RunFastLoop();

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

        public FastLoopFixture()
        {
            Root = Path.Combine(_temp.Path, "repo");
            BinDir = Path.Combine(_temp.Path, "fake-bin");
            CallLogPath = Path.Combine(_temp.Path, "call_log.txt");

            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(BinDir);

            CreateStubs();
            InitializeRepo();
        }

        public ProcessResult RunFastLoop() => RunFastLoop([]);

        public ProcessResult RunFastLoop(params string[] arguments)
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

            return ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(30));
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

        private void InitializeRepo()
        {
            RunGit(Root, "init", "--quiet");
            RunGit(Root, "config", "user.name", "TestUser");
            RunGit(Root, "config", "user.email", "test@example.com");
            RunGit(Root, "config", "commit.gpgsign", "false");

            string fastLoopDest = Path.Combine(Root, "scripts", "fast-loop.sh");
            Directory.CreateDirectory(Path.GetDirectoryName(fastLoopDest)!);
            File.WriteAllText(fastLoopDest, Corpus.ReadText("scripts/fast-loop.sh").ReplaceLineEndings("\n"));
            MakeExecutable(fastLoopDest);

            string cliBinDir = Path.Combine(Root, "src", "KyberWeave.Cli", "bin", "Release", "net10.0");
            Directory.CreateDirectory(cliBinDir);
            File.WriteAllText(Path.Combine(cliBinDir, "kyber-weave.dll"), "stub");
            File.WriteAllText(Path.Combine(cliBinDir, "KyberWeave.Cli.dll"), "stub");

            foreach (string project in new[] { "Application", "Hooks", "Kernel", "Mcp", "Arbiter", "Cli", "Data", "Architecture", "Content", "RepoPolicy" })
            {
                string testBinDir = Path.Combine(Root, "tests", $"KyberWeave.{project}.Tests", "bin", "Release", "net10.0");
                Directory.CreateDirectory(testBinDir);
                File.WriteAllText(Path.Combine(testBinDir, $"KyberWeave.{project}.Tests.dll"), "stub");
            }

            File.WriteAllText(Path.Combine(Root, "KyberWeave.sln"), "# stub sln\n");

            WriteTrackedFile("dash/tray/src-tauri/README.md", "# Tray Rust\n");
            WriteTrackedFile("dash/tray/ui/README.md", "# Tray UI\n");
            WriteTrackedFile("dash/tray/ui/src/App.tsx", "// App\n");
            WriteTrackedFile("dash/package.json", "{}\n");

            string dashNodeModules = Path.Combine(Root, "dash", "node_modules");
            Directory.CreateDirectory(dashNodeModules);
            File.WriteAllText(Path.Combine(dashNodeModules, ".keep"), string.Empty);

            RunGit(Root, "add", "-A");
            RunGit(Root, "commit", "--quiet", "--allow-empty", "-m", "initial tree");
        }

        private void WriteTrackedFile(string relativePath, string content)
        {
            string fullPath = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
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

        private static void RunGit(string repoDir, params string[] arguments)
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

            ProcessResult result = ProcessRunner.Run(startInfo, string.Empty, TimeSpan.FromSeconds(30));
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
