using System.Collections;
using System.Diagnostics;
using KyberWeave.Core.Processes;
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

        public ProcessResult RunFastLoop()
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
                  *tests/KyberWeave.Tests/bin/*)
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

            string fastLoopSource = Path.Combine(KyberWeaveTestPaths.ToolRoot, "scripts", "fast-loop.sh");
            string fastLoopDest = Path.Combine(Root, "scripts", "fast-loop.sh");
            Directory.CreateDirectory(Path.GetDirectoryName(fastLoopDest)!);
            File.Copy(fastLoopSource, fastLoopDest, true);
            MakeExecutable(fastLoopDest);

            string classListDest = Path.Combine(Root, "scripts", "fast-loop-classes.txt");
            File.WriteAllText(classListDest, "KyberWeave.Tests.FastLoopScriptTests\n");

            string cliBinDir = Path.Combine(Root, "src", "KyberWeave.Cli", "bin", "Release", "net10.0");
            Directory.CreateDirectory(cliBinDir);
            File.WriteAllText(Path.Combine(cliBinDir, "kyber-weave.dll"), "stub");
            File.WriteAllText(Path.Combine(cliBinDir, "KyberWeave.Cli.dll"), "stub");

            string testBinDir = Path.Combine(Root, "tests", "KyberWeave.Tests", "bin", "Release", "net10.0");
            Directory.CreateDirectory(testBinDir);
            File.WriteAllText(Path.Combine(testBinDir, "KyberWeave.Tests.dll"), "stub");

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
