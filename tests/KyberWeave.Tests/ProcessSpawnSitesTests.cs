using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Static audit of every place the .NET test project and the product code under <c>src/</c> start a
/// child process. An unbounded test spawn once fork-bombed node (536 processes) and crashed three developer
/// Macs, so each spawn site must appear in Fixtures/spawn-sites-allowlist.json with a non-empty
/// <c>bound</c> that says what stops it (a timeout, a kill in finally, a self-exiting
/// command, or a fake). A site is keyed by its repository-relative file (prefixed with its scan root,
/// <c>tests/KyberWeave.Tests/</c> or <c>src/</c>) and normalized line text with an occurrence
/// count, so unrelated edits above a site do not invalidate its entry. Unlisted sites fail
/// with root-prefixed file:line, and entries that no longer match a site fail as stale.
/// This test only reads source. It starts no process.
/// </summary>
public sealed class ProcessSpawnSitesTests
{
    private const string AllowListFileName = "spawn-sites-allowlist.json";
    private const string ThisFileName = "ProcessSpawnSitesTests.cs";

    // Scan roots relative to the repository root. The test project is scanned for C# and shell
    // sources; product code under src/ is scanned for C# only. bin and obj are skipped under each root.
    private static readonly (string Root, string[] Extensions)[] ScanRoots =
    [
        ("tests/KyberWeave.Tests", [".cs", ".sh"]),
        ("src", [".cs"])
    ];

    // Type names may be namespace-qualified (System.Diagnostics.Process), so an optional
    // "Namespace." prefix is allowed before each one. ProcessRunner.RunInteractive is matched
    // without a call paren: SquadRunCommand hands it over as a delegate, and the spawn happens
    // when that delegate is invoked, so the reference itself is the site.
    private static readonly Regex CSharpSpawn = new(
        @"(?<start>\b(?:\w+\.)*Process\.Start\s*\()" +
        @"|(?<run>\bProcessRunner\.Run\s*\()" +
        @"|(?<interactive>\bProcessRunner\.RunInteractive\b)" +
        @"|(?<proc>\bnew\s+(?:\w+\.)*Process\b)" +
        @"|(?<info>\bnew\s+(?:\w+\.)*ProcessStartInfo\s*\(|\bProcessStartInfo\s+\w+\s*=\s*new\s*\()" +
        @"|(?<build>\bBuildStartInfo\s*\()",
        RegexOptions.CultureInvariant);

    // A command word in command position: line start, after a separator, or after "$(".
    // The word must be followed by whitespace, end of line, or ")", so assignments such as
    // name="..." are not commands.
    private static readonly Regex ShellSpawn = new(
        @"(?:^|[;&|(]|\bthen\b|\bdo\b|\belse\b)\s*(?:exec\s+)?(?<name>[A-Za-z_][\w./-]*)(?=\s|$|\))",
        RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ShellBuiltins = new(StringComparer.Ordinal)
    {
        "set", "printf", "echo", "local", "return", "exit", "cd", "read", "export", "shift",
        "true", "false", "test", "if", "elif", "fi", "for", "while", "until", "do", "done",
        "case", "esac", "then", "else", "unset", "trap", "wait", "eval", "declare", "readonly",
        "source", ".", ":"
    };

    [Fact]
    public void EveryProcessSpawnSiteIsAllowListedWithinItsCount()
    {
        string repoRoot = RepoRoot();
        IReadOnlyList<SpawnSite> sites = ScanSpawnSites(repoRoot);
        Dictionary<(string File, string Text), int> allowed = AllowedCounts(LoadAllowList(repoRoot));

        List<string> unlisted = [];
        foreach (IGrouping<(string File, string Text), SpawnSite> group in sites.GroupBy(s => (s.File, s.Text)))
        {
            int allowedCount = allowed.GetValueOrDefault(group.Key);
            foreach (SpawnSite site in group.Skip(allowedCount))
                unlisted.Add($"{site.File}:{site.Line} [{site.Kind}] {site.Text}");
        }

        Assert.True(
            unlisted.Count == 0,
            $"{unlisted.Count} process spawn site(s) under tests/KyberWeave.Tests or src/ are not on " +
            $"Fixtures/{AllowListFileName} with a bound. Add an entry keyed by the root-prefixed path " +
            "(tests/KyberWeave.Tests/... or src/...) stating what bounds each one (timeout, kill in finally, " +
            "self-exit, or fake), or remove the spawn:\n" +
            string.Join("\n", unlisted));
    }

    [Fact]
    public void EveryAllowListEntryHasABoundAndStillMatchesASpawnSite()
    {
        string repoRoot = RepoRoot();
        IReadOnlyList<SpawnSite> sites = ScanSpawnSites(repoRoot);
        Dictionary<(string File, string Text), int> found =
            sites.GroupBy(s => (s.File, s.Text)).ToDictionary(g => g.Key, g => g.Count());

        List<string> violations = [];
        HashSet<(string File, string Text)> seen = [];
        foreach (AllowListEntry entry in LoadAllowList(repoRoot))
        {
            string label = $"{entry.File} \"{entry.Text}\"";
            if (!seen.Add((entry.File, entry.Text)))
                violations.Add($"{label} is listed more than once");

            if (string.IsNullOrWhiteSpace(entry.Bound))
                violations.Add($"{label} has no bound");

            if (entry.Count < 1)
                violations.Add($"{label} must list a count of at least 1");

            int actual = found.GetValueOrDefault((entry.File, entry.Text));
            if (actual < entry.Count)
                violations.Add($"{label} is stale: lists {entry.Count}, source has {actual}");
        }

        Assert.True(
            violations.Count == 0,
            $"{violations.Count} allow-list problem(s) in Fixtures/{AllowListFileName}:\n" +
            string.Join("\n", violations));
    }

    [Theory]
    [InlineData("        using Process process = Process.Start(startInfo)", "Process.Start")]
    [InlineData("        ProcessResult result = ProcessRunner.Run(startInfo, string.Empty);", "ProcessRunner.Run")]
    [InlineData("        ProcessStartInfo startInfo = new(\"git\")", "ProcessStartInfo")]
    [InlineData("        ProcessStartInfo startInfo = new ProcessStartInfo(\"/bin/sh\")", "ProcessStartInfo")]
    [InlineData("        ProcessStartInfo startInfo = SquadHarnessLauncher.BuildStartInfo(", "BuildStartInfo")]
    [InlineData("        using Process process = new Process { StartInfo = startInfo };", "new Process")]
    [InlineData("        using Process process = new Process();", "new Process")]
    [InlineData("        using System.Diagnostics.Process process = new System.Diagnostics.Process();", "new Process")]
    [InlineData("        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo);", "Process.Start")]
    [InlineData("        ProcessStartInfo startInfo = new System.Diagnostics.ProcessStartInfo(\"git\")", "ProcessStartInfo")]
    [InlineData("        Func<ProcessStartInfo, int> runner = _runner ?? ProcessRunner.RunInteractive;", "ProcessRunner.RunInteractive")]
    [InlineData("        return ProcessRunner.RunInteractive(startInfo);", "ProcessRunner.RunInteractive")]
    [InlineData("        // Func<ProcessStartInfo, int> runner = ProcessRunner.RunInteractive;", null)]
    [InlineData("        // ProcessRunner.Run(startInfo, string.Empty);", null)]
    [InlineData("    /// <c>ProcessRunner.Run</c> copies exactly this environment.", null)]
    [InlineData("        return ProcessRunner.ReadToEnd(process);", null)]
    [InlineData("        ProcessStartInfo call = Assert.Single(executor.Calls);", null)]
    public void ScannerClassifiesCSharpInvocationsAndIgnoresCommentsAndReads(string line, string? expectedKind)
    {
        Assert.Equal(expectedKind, ClassifyCSharp(line));
    }

    [Theory]
    [InlineData("user=\"$(id -un)\"", "shell:id")]
    [InlineData("  node server.js &", "shell:node")]
    [InlineData("printf '%s %s\\n' 2147483011 1 \"${user}\" node \"node fixture-worker-a\"", null)]
    [InlineData("set -euo pipefail", null)]
    [InlineData("# node fixture-worker-a", null)]
    public void ScannerClassifiesShellCommandsAndIgnoresDataAndComments(string line, string? expectedKind)
    {
        Assert.Equal(expectedKind, ClassifyShell(line));
    }

    private static string RepoRoot()
    {
        return KyberWeaveTestPaths.ToolRoot;
    }

    private static IReadOnlyList<SpawnSite> ScanSpawnSites(string repoRoot)
    {
        List<SpawnSite> sites = [];
        foreach ((string root, string[] extensions) in ScanRoots)
        {
            string absoluteRoot = Path.Combine(repoRoot, root);
            IEnumerable<string> files = Directory.EnumerateFiles(absoluteRoot, "*", SearchOption.AllDirectories)
                .Where(path => IsScannedSource(absoluteRoot, path, extensions))
                .OrderBy(path => path, StringComparer.Ordinal);

            foreach (string path in files)
            {
                string relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
                bool csharp = path.EndsWith(".cs", StringComparison.Ordinal);
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string? kind = csharp ? ClassifyCSharp(lines[i]) : ClassifyShell(lines[i]);
                    if (kind is not null)
                        sites.Add(new SpawnSite(relative, i + 1, kind, Normalize(lines[i])));
                }
            }
        }

        return sites;
    }

    private static bool IsScannedSource(string scanRoot, string path, string[] extensions)
    {
        if (!extensions.Contains(Path.GetExtension(path), StringComparer.Ordinal))
            return false;

        if (Path.GetFileName(path) == ThisFileName)
            return false;

        string relative = Path.GetRelativePath(scanRoot, path);
        return !relative.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj");
    }

    private static string? ClassifyCSharp(string line)
    {
        string code = StripCSharpLineComment(line);
        Match match = CSharpSpawn.Match(code);
        if (!match.Success)
            return null;

        if (match.Groups["start"].Success)
            return "Process.Start";

        if (match.Groups["run"].Success)
            return "ProcessRunner.Run";

        if (match.Groups["interactive"].Success)
            return "ProcessRunner.RunInteractive";

        if (match.Groups["proc"].Success)
            return "new Process";

        if (match.Groups["build"].Success)
            return "BuildStartInfo";

        return "ProcessStartInfo";
    }

    private static string? ClassifyShell(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#')
            return null;

        foreach (Match match in ShellSpawn.Matches(line))
        {
            string name = match.Groups["name"].Value;
            if (!ShellBuiltins.Contains(name))
                return $"shell:{name}";
        }

        return null;
    }

    private static string StripCSharpLineComment(string line)
    {
        string trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("/*", StringComparison.Ordinal)
            || trimmed.StartsWith('*'))
            return string.Empty;

        int comment = line.IndexOf("//", StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static string Normalize(string line)
    {
        return Regex.Replace(line.Trim(), @"\s+", " ");
    }

    private static List<AllowListEntry> LoadAllowList(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "tests", "KyberWeave.Tests", "Fixtures", AllowListFileName);
        Assert.True(File.Exists(path), $"Missing spawn-site allow-list: {path}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        List<AllowListEntry> entries = [];
        if (!document.RootElement.TryGetProperty("entries", out JsonElement items))
            return entries;

        foreach (JsonElement item in items.EnumerateArray())
        {
            entries.Add(new AllowListEntry(
                item.GetProperty("file").GetString() ?? string.Empty,
                item.GetProperty("text").GetString() ?? string.Empty,
                item.GetProperty("count").GetInt32(),
                item.TryGetProperty("bound", out JsonElement bound)
                    ? bound.GetString() ?? string.Empty
                    : string.Empty));
        }

        return entries;
    }

    private static Dictionary<(string File, string Text), int> AllowedCounts(IEnumerable<AllowListEntry> entries)
    {
        Dictionary<(string File, string Text), int> counts = [];
        foreach (AllowListEntry entry in entries)
            counts[(entry.File, entry.Text)] = counts.GetValueOrDefault((entry.File, entry.Text)) + entry.Count;

        return counts;
    }

    private sealed record SpawnSite(string File, int Line, string Kind, string Text);

    private sealed record AllowListEntry(string File, string Text, int Count, string Bound);
}
