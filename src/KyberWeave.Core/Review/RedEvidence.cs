using System.Globalization;
using System.Text.RegularExpressions;

namespace KyberWeave.Core.Review;

/// <summary>One task gate's run, as the RED classifier reads it.</summary>
/// <param name="Id">The gate's id.</param>
/// <param name="Command">The command the gate ran, joined for display.</param>
/// <param name="ExitCode">The process exit code.</param>
/// <param name="Output">Standard output followed by standard error.</param>
public sealed record RedGateObservation(string Id, string Command, int ExitCode, string Output);

/// <summary>One compiler error found in a gate's output.</summary>
/// <param name="File">The file the compiler named, as it printed it; empty when the error names none.</param>
/// <param name="Code">The compiler's diagnostic code, such as <c>CS0246</c>.</param>
/// <param name="Message">The compiler's message.</param>
/// <param name="Symbol">The missing symbol the message names, or null when the code is not a missing-symbol code.</param>
public sealed record CompileError(string File, string Code, string Message, string? Symbol);

/// <summary>What the tests of one gate's run did.</summary>
/// <param name="Total">Tests that ran.</param>
/// <param name="Failed">Tests that failed.</param>
public sealed record TestRunSummary(int Total, int Failed);

/// <summary>
/// How one language's toolchain says a symbol is missing and how its test runner reports a run. A language is data:
/// supporting another means adding one record to <see cref="RedLanguages.All"/>, with no change to the classifier.
/// </summary>
/// <param name="Name">The language, for messages.</param>
/// <param name="Diagnostic">Matches one compiler error line; named groups <c>file</c>, <c>code</c> and <c>message</c>.</param>
/// <param name="MissingSymbolCodes">
/// The diagnostic codes that mean "this symbol does not exist", each with a pattern over the message whose group
/// <c>symbol</c> names the symbol and whose optional group <c>owner</c> names the type or module it was looked up on.
/// Signature mismatches (wrong argument count or type) are not here: the symbol exists.
/// </param>
/// <param name="ReadTests">Reads a test run's totals from output, or null when no test run is reported.</param>
/// <param name="FailedTests">The names of the tests the output reports as failed.</param>
/// <param name="Assertion">The first line of the output that says what an assertion expected, or null.</param>
/// <param name="CommandMarker">Matches a gate command that runs this language's tests, so a later test gate can be skipped when the build it needs is known broken.</param>
/// <param name="ZeroTests">Whether an exit code and output say the runner found no tests to run.</param>
/// <param name="Infrastructure">Output patterns that say the run broke before or around the tests (a server that did not start, a crashed host), each with the plain-words reason.</param>
public sealed record RedLanguage(
    string Name,
    Regex Diagnostic,
    IReadOnlyDictionary<string, Regex> MissingSymbolCodes,
    Func<string, TestRunSummary?> ReadTests,
    Func<string, IReadOnlyList<string>> FailedTests,
    Func<string, string?> Assertion,
    Regex CommandMarker,
    Func<int, string, bool> ZeroTests,
    IReadOnlyList<(Regex Pattern, string Reason)> Infrastructure);

/// <summary>The languages the RED classifier knows.</summary>
public static partial class RedLanguages
{
    /// <summary>C# (Roslyn) and TypeScript (tsc); add a record here for another language.</summary>
    public static IReadOnlyList<RedLanguage> All { get; } =
    [
        new RedLanguage(
            "C#",
            CSharpDiagnostic(),
            new Dictionary<string, Regex>(StringComparer.Ordinal)
            {
                // The name 'X' does not exist in the current context
                ["CS0103"] = QuotedName(),
                // 'T' does not contain a definition for 'X'
                ["CS0117"] = OwnerAndMember(),
                // The type or namespace name 'X' could not be found
                ["CS0246"] = QuotedName(),
                // 'T' does not contain a definition for 'X' and no accessible extension method 'X' ...
                ["CS1061"] = OwnerAndMember(),
                // The type or namespace name 'X' does not exist in the namespace 'N'
                ["CS0234"] = QuotedName(),
                // The type name 'X' does not exist in the type 'T'
                ["CS0426"] = QuotedName(),
                // The type or namespace name 'X' could not be found in the global namespace
                ["CS0400"] = QuotedName(),
            },
            ReadDotnetTests,
            DotnetFailedTests,
            DotnetAssertion,
            DotnetCommand(),
            // Microsoft.Testing.Platform exits 8 when zero tests ran; VSTest prints that no test matched the filter.
            (exit, output) => exit == 8 || output.Contains("Zero tests ran", StringComparison.Ordinal)
                              || output.Contains("No test matches the given testcase filter", StringComparison.Ordinal)
                              || output.Contains("No test is available in", StringComparison.Ordinal),
            [
                (Pattern(@"Testhost process .* exited with error|The active test run was aborted|Test host process crashed"), "the test host crashed before the tests finished"),
                (Pattern(@"Microsoft\.DotNet\.Cli\.Commands\.Test\.TestApplication"), "the dotnet test runner failed without running a test (a build that cannot produce a test assembly, or a runner crash)"),
            ]),
        new RedLanguage(
            "TypeScript",
            TypeScriptDiagnostic(),
            new Dictionary<string, Regex>(StringComparer.Ordinal)
            {
                // Cannot find name 'X'.
                ["TS2304"] = QuotedName(),
                // Property 'X' does not exist on type 'T'.
                ["TS2339"] = PropertyOnType(),
                // Module '"m"' has no exported member 'X'.
                ["TS2305"] = ExportedMember(),
                // Cannot find module 'm' or its corresponding type declarations.
                ["TS2307"] = QuotedName(),
            },
            ReadScriptTests,
            ScriptFailedTests,
            ScriptAssertion,
            Pattern(@"\b(?:vitest|jest|mocha|ava|tap)\b|\b(?:npm|pnpm|yarn|bun)\b.*\btest\b"),
            (exit, output) => output.Contains("No test files found", StringComparison.Ordinal)
                              || output.Contains("No tests found", StringComparison.Ordinal),
            []),
        new RedLanguage(
            "Playwright",
            NeverMatches(),
            new Dictionary<string, Regex>(StringComparer.Ordinal),
            ReadPlaywrightTests,
            PlaywrightFailedTests,
            PlaywrightAssertion,
            Pattern(@"\bplaywright\b"),
            (exit, output) => output.Contains("No tests found", StringComparison.Ordinal),
            [
                (Pattern(@"Timed out waiting \d+ms from config\.webServer"), "the web server the tests need did not start in time"),
                (Pattern(@"is already used, make sure that nothing is running on the port"), "the web server's port is already in use"),
                (Pattern(@"ECONNREFUSED|net::ERR_CONNECTION_REFUSED"), "the application under test refused connections"),
                (Pattern(@"Executable doesn't exist at|browserType\.launch"), "the browser could not be launched (not installed?)"),
            ]),
        new RedLanguage(
            "Python",
            NeverMatches(),
            new Dictionary<string, Regex>(StringComparer.Ordinal),
            ReadPytestTests,
            PytestFailedTests,
            PytestAssertion,
            Pattern(@"\bpytest\b|\bpy\.test\b"),
            (exit, output) => exit == 5 || output.Contains("no tests ran", StringComparison.Ordinal),
            [(Pattern(@"^ERROR collecting |\d+ errors? in "), "pytest could not collect the tests (an import or syntax error in the test module)")]),
        new RedLanguage(
            "Rust",
            NeverMatches(),
            new Dictionary<string, Regex>(StringComparer.Ordinal),
            ReadCargoTests,
            CargoFailedTests,
            CargoAssertion,
            Pattern(@"\bcargo\b.*\btest\b"),
            (exit, output) => output.Contains("running 0 tests", StringComparison.Ordinal) && !output.Contains("FAILED", StringComparison.Ordinal),
            [(Pattern(@"error(?:\[E\d+\])?: could not compile"), "cargo could not compile the tests")]),
        new RedLanguage(
            "Go",
            GoDiagnostic(),
            new Dictionary<string, Regex>(StringComparer.Ordinal) { ["undefined"] = GoSymbol() },
            ReadGoTests,
            GoFailedTests,
            GoAssertion,
            Pattern(@"\bgo\b.*\btest\b"),
            (exit, output) => output.Contains("no test files", StringComparison.Ordinal) || output.Contains("[no tests to run]", StringComparison.Ordinal),
            []),
    ];

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"\bdotnet\b.*\btest\b|\bxunit\b|\bnunit\b", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetCommand();

    [GeneratedRegex(@"(?!)", RegexOptions.CultureInvariant)]
    private static partial Regex NeverMatches();

    [GeneratedRegex(@"^\s*(?<file>[^\s:]+\.go):\d+(?::\d+)?: (?<code>undefined): (?<message>.+?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex GoDiagnostic();

    [GeneratedRegex(@"^(?<symbol>\S+)$", RegexOptions.CultureInvariant)]
    private static partial Regex GoSymbol();

    [GeneratedRegex(@"^\s*(?<n>\d+) failed\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PlaywrightFailedCount();

    [GeneratedRegex(@"^\s*(?<n>\d+) (?:passed|skipped|flaky|did not run)\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PlaywrightOtherCount();

    [GeneratedRegex(@"^\s*\d+\) \[?[\w -]*\]? ?› (?<name>.+?)[\s─]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PlaywrightFailedLine();

    [GeneratedRegex(@"^\s*(?<line>(?:Error: )?expect\(.*|Error: expect.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PlaywrightAssertionLine();

    [GeneratedRegex(@"^=+ (?<summary>.*\b(?:passed|failed|error|errors)\b.*) in [\d.]+s.*=+$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PytestSummary();

    [GeneratedRegex(@"^FAILED (?<name>\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PytestFailedLine();

    [GeneratedRegex(@"^E\s+(?<line>(?:assert|AssertionError).*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PytestAssertionLine();

    [GeneratedRegex(@"test result: (?:ok|FAILED)\. (?<passed>\d+) passed; (?<failed>\d+) failed;", RegexOptions.CultureInvariant)]
    private static partial Regex CargoResult();

    [GeneratedRegex(@"^test (?<name>\S+) \.\.\. FAILED", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CargoFailedLine();

    [GeneratedRegex(@"^(?<line>assertion `?(?:left|right|failed).*|.*panicked at .*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CargoAssertionLine();

    [GeneratedRegex(@"^\s*--- FAIL: (?<name>\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex GoFailLine();

    [GeneratedRegex(@"^\s*--- PASS: (?<name>\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex GoPassLine();

    [GeneratedRegex(@"^\s*(?<line>[\w./-]+_test\.go:\d+: .+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex GoAssertionLine();

    [GeneratedRegex(@"^\s*(?<file>.+?)\(\d+,\d+(?:,\d+,\d+)?\): error (?<code>CS\d+): (?<message>.*?)(?: \[[^\]]+\])?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CSharpDiagnostic();

    [GeneratedRegex(@"^\s*(?:(?<file>[^\s(][^():]*?)\(\d+,\d+\): error|(?<file>[^\s:][^:]*?):\d+:\d+ - error) (?<code>TS\d+): (?<message>.*?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TypeScriptDiagnostic();

    [GeneratedRegex(@"'(?<symbol>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedName();

    [GeneratedRegex(@"'(?<owner>[^']+)' does not contain a definition for '(?<symbol>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerAndMember();

    [GeneratedRegex(@"Property '(?<symbol>[^']+)' does not exist on type '(?<owner>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyOnType();

    [GeneratedRegex(@"has no exported member (?:named )?'(?<symbol>[^']+)'", RegexOptions.CultureInvariant)]
    private static partial Regex ExportedMember();

    [GeneratedRegex(@"^\s*total:\s*(?<n>\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RunnerTotal();

    [GeneratedRegex(@"^\s*failed:\s*(?<n>\d+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex RunnerFailed();

    [GeneratedRegex(@"(?:Passed|Failed)!\s+-\s+Failed:\s*(?<failed>\d+),\s*Passed:\s*\d+,\s*Skipped:\s*\d+,\s*Total:\s*(?<total>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VsTestSummary();

    [GeneratedRegex(@"^\s*(?:failed|Failed)\s+(?<name>[\w.<>,`+\[\]-]+)(?:\s|\()", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DotnetFailedLine();

    [GeneratedRegex(@"^\s*(?<line>(?:Assert\.\w+\(\)[^\n]*|Expected:[^\n]*|Shouldly[^\n]*))$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex DotnetAssertionLine();

    [GeneratedRegex(@"^\s*Tests:?\s+(?<rest>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptTestsLine();

    [GeneratedRegex(@"(?<n>\d+) failed", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptFailedCount();

    [GeneratedRegex(@"(?<n>\d+) (?:passed|total)", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptPassedCount();

    [GeneratedRegex(@"^\s*(?:FAIL|✗|×)\s+(?<name>.+?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptFailedLine();

    [GeneratedRegex(@"^\s*(?<line>(?:AssertionError|expect\(|Expected)[^\n]*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptAssertionLine();

    private static TestRunSummary? ReadDotnetTests(string output)
    {
        int total = 0;
        int failed = 0;
        bool ran = false;
        foreach (Match match in VsTestSummary().Matches(output))
        {
            ran = true;
            total += Number(match.Groups["total"]);
            failed += Number(match.Groups["failed"]);
        }

        if (!ran && RunnerTotal().Match(output) is { Success: true } runnerTotal)
        {
            ran = true;
            total = Number(runnerTotal.Groups["n"]);
            failed = RunnerFailed().Match(output) is { Success: true } runnerFailed ? Number(runnerFailed.Groups["n"]) : 0;
        }

        return ran ? new TestRunSummary(total, failed) : null;
    }

    private static List<string> DotnetFailedTests(string output) =>
        [.. DotnetFailedLine().Matches(output).Select(match => match.Groups["name"].Value).Where(name => name.Contains('.', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)];

    private static string? DotnetAssertion(string output) =>
        DotnetAssertionLine().Match(output) is { Success: true } match ? match.Groups["line"].Value.Trim() : null;

    private static TestRunSummary? ReadScriptTests(string output)
    {
        if (ScriptTestsLine().Match(output) is not { Success: true } line)
        {
            return null;
        }

        string rest = line.Groups["rest"].Value;
        int failed = ScriptFailedCount().Match(rest) is { Success: true } failedMatch ? Number(failedMatch.Groups["n"]) : 0;
        int passed = ScriptPassedCount().Matches(rest).Select(match => Number(match.Groups["n"])).DefaultIfEmpty(0).Max();
        return new TestRunSummary(Math.Max(passed, failed), failed);
    }

    private static List<string> ScriptFailedTests(string output) =>
        [.. ScriptFailedLine().Matches(output).Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal)];

    private static string? ScriptAssertion(string output) =>
        ScriptAssertionLine().Match(output) is { Success: true } match ? match.Groups["line"].Value.Trim() : null;

    private static TestRunSummary? ReadPlaywrightTests(string output)
    {
        MatchCollection failed = PlaywrightFailedCount().Matches(output);
        MatchCollection other = PlaywrightOtherCount().Matches(output);
        if (failed.Count == 0 && other.Count == 0)
        {
            return null;
        }

        int failedCount = failed.Sum(match => Number(match.Groups["n"]));
        return new TestRunSummary(failedCount + other.Sum(match => Number(match.Groups["n"])), failedCount);
    }

    private static List<string> PlaywrightFailedTests(string output) =>
        [.. PlaywrightFailedLine().Matches(output).Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal)];

    private static string? PlaywrightAssertion(string output) =>
        PlaywrightAssertionLine().Match(output) is { Success: true } match ? match.Groups["line"].Value.Trim() : null;

    private static TestRunSummary? ReadPytestTests(string output)
    {
        if (PytestSummary().Match(output) is not { Success: true } summary)
        {
            return null;
        }

        string text = summary.Groups["summary"].Value;
        int failed = Count(text, "failed");
        int passed = Count(text, "passed");
        return new TestRunSummary(failed + passed + Count(text, "error"), failed);

        static int Count(string line, string word) =>
            Regex.Match(line, @"(?<n>\d+) " + word, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) is { Success: true } match
                ? int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture)
                : 0;
    }

    private static List<string> PytestFailedTests(string output) =>
        [.. PytestFailedLine().Matches(output).Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal)];

    private static string? PytestAssertion(string output) =>
        PytestAssertionLine().Match(output) is { Success: true } match ? match.Groups["line"].Value.Trim() : null;

    private static TestRunSummary? ReadCargoTests(string output)
    {
        MatchCollection results = CargoResult().Matches(output);
        if (results.Count == 0)
        {
            return null;
        }

        int failed = results.Sum(match => Number(match.Groups["failed"]));
        return new TestRunSummary(failed + results.Sum(match => Number(match.Groups["passed"])), failed);
    }

    private static List<string> CargoFailedTests(string output) =>
        [.. CargoFailedLine().Matches(output).Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal)];

    private static string? CargoAssertion(string output) =>
        CargoAssertionLine().Match(output) is { Success: true } match ? match.Groups["line"].Value.Trim() : null;

    private static TestRunSummary? ReadGoTests(string output)
    {
        int failed = GoFailLine().Count(output);
        int passed = GoPassLine().Count(output);
        bool packageResult = Regex.IsMatch(output, @"^(?:ok|FAIL)\s+\S+", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return failed + passed > 0 ? new TestRunSummary(failed + passed, failed) : packageResult && failed == 0 && !output.Contains("[build failed]", StringComparison.Ordinal) ? new TestRunSummary(1, 0) : null;
    }

    private static List<string> GoFailedTests(string output) =>
        [.. GoFailLine().Matches(output).Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal)];

    private static string? GoAssertion(string output) =>
        GoAssertionLine().Match(output) is { Success: true } match ? match.Groups["line"].Value.Trim() : null;

    private static int Number(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);
}

/// <summary>What the RED classifier decided about a set of task gate runs.</summary>
/// <param name="Passed">Whether the runs are valid RED evidence.</param>
/// <param name="Reason">Why not, as one sentence an agent can act on; empty when <paramref name="Passed"/>.</param>
/// <param name="EvidenceLine">The <c>RED_EVIDENCE:</c> line to paste into the digest; empty when not passed.</param>
/// <param name="MissingSymbols">The missing symbols accepted, in order of first appearance.</param>
/// <param name="FailingTests">The failing tests accepted.</param>
/// <param name="AcceptedGates">The gates whose failure was accepted as RED, with the reason each was accepted.</param>
/// <param name="RejectedGates">The gates that decided against RED, with the reason each did.</param>
public sealed record RedOutcome(
    bool Passed,
    string Reason,
    string EvidenceLine,
    IReadOnlyList<string> MissingSymbols,
    IReadOnlyList<string> FailingTests,
    IReadOnlyDictionary<string, string> AcceptedGates,
    IReadOnlyDictionary<string, string> RejectedGates);

/// <summary>
/// Decides from a task's gate runs whether they are valid RED evidence for a compiled language: tests that ran and
/// failed, or a build that failed only because the tests name symbols that do not exist yet. It reads compiler
/// diagnostics by code, so the judgment does not depend on a worker's prose.
/// </summary>
public static partial class RedClassifier
{
    private const int ExamplesShown = 3;

    /// <summary>The compiler errors in <paramref name="output"/>, de-duplicated (a multi-target build prints each twice).</summary>
    public static IReadOnlyList<CompileError> ParseErrors(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        List<CompileError> errors = [];
        foreach (RedLanguage language in RedLanguages.All)
        {
            foreach (Match match in language.Diagnostic.Matches(output))
            {
                string code = match.Groups["code"].Value;
                string message = match.Groups["message"].Value;
                CompileError error = new(match.Groups["file"].Value.Trim(), code, message, MissingSymbol(language, code, message));
                if (!errors.Contains(error))
                {
                    errors.Add(error);
                }
            }
        }

        return errors;
    }

    /// <summary>Whether a compiler-named <paramref name="file"/> is one of <paramref name="ownFiles"/> (repository-relative).</summary>
    public static bool IsOwnFile(string file, IReadOnlyList<string> ownFiles)
    {
        string normalized = file.Replace('\\', '/');
        return ownFiles.Any(own =>
        {
            string path = own.Replace('\\', '/').TrimStart('/');
            return normalized.Equals(path, StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// The missing symbols a failed gate's build is stopped by when every error is a missing-symbol error and none is in
    /// <paramref name="ownFiles"/>: sibling tests reference code no task has created. Null in every other case.
    /// </summary>
    public static IReadOnlyList<string>? SiblingSymbols(RedGateObservation gate, IReadOnlyList<string> ownFiles)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(ownFiles);
        if (gate.ExitCode == 0)
        {
            return null;
        }

        IReadOnlyList<CompileError> errors = ParseErrors(gate.Output);
        if (errors.Count == 0 || HasUnparsedError(gate.Output, errors)
            || errors.Any(error => error.Symbol is null || IsOwnFile(error.File, ownFiles)))
        {
            return null;
        }

        return [.. errors.Select(error => error.Symbol!).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Whether a gate command runs tests, so it cannot run once the build it needs is known to be broken.</summary>
    public static bool IsTestCommand(string command) =>
        RedLanguages.All.Any(language => language.CommandMarker.IsMatch(command));

    /// <summary>The reason recorded for a test gate skipped after a build accepted as RED on missing symbols.</summary>
    public static string BuildSkipReason(IReadOnlyList<string> symbols) =>
        $"not run — RED build (missing symbols {string.Join(", ", symbols)})";

    /// <summary>The missing symbols when <paramref name="gate"/> is a failed gate the classifier accepts as a compile-form RED, else null.</summary>
    public static IReadOnlyList<string>? AcceptedMissingSymbols(RedGateObservation gate, IReadOnlyList<string> ownFiles)
    {
        ArgumentNullException.ThrowIfNull(gate);
        RedOutcome outcome = Evaluate([gate], ownFiles);
        return outcome.Passed && outcome.MissingSymbols.Count > 0 ? outcome.MissingSymbols : null;
    }

    /// <summary>Judges the runs of one RED task's gates.</summary>
    /// <param name="gates">Every gate that ran, in order.</param>
    /// <param name="ownFiles">The files the task changed; missing-symbol errors must be in these.</param>
    /// <param name="repositoryRoot">The repository root, used to name the project a changed file belongs to; null names none.</param>
    public static RedOutcome Evaluate(IReadOnlyList<RedGateObservation> gates, IReadOnlyList<string> ownFiles, string? repositoryRoot = null)
    {
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(ownFiles);

        List<string> symbols = [];
        List<string> failing = [];
        string? assertion = null;
        Dictionary<string, string> accepted = new(StringComparer.Ordinal);
        Dictionary<string, string> rejected = new(StringComparer.Ordinal);
        bool testsPassed = false;
        string? filter = null;

        foreach (RedGateObservation gate in gates)
        {
            TestRunSummary? run = RedLanguages.All.Select(language => language.ReadTests(gate.Output)).FirstOrDefault(summary => summary is not null);
            if (gate.ExitCode == 0)
            {
                if (run is { Total: > 0, Failed: 0 })
                {
                    testsPassed = true;
                    filter ??= FilterOf(gate.Command);
                }
                else if (ZeroTests(gate))
                {
                    rejected[gate.Id] = ZeroTestsReason(gate, ownFiles, repositoryRoot);
                }

                continue;
            }

            IReadOnlyList<CompileError> errors = ParseErrors(gate.Output);
            if (errors.Count > 0 || HasUnparsedError(gate.Output, errors))
            {
                List<CompileError> other = [.. errors.Where(error => error.Symbol is null || !IsOwnFile(error.File, ownFiles))];
                if (other.Count > 0 || HasUnparsedError(gate.Output, errors))
                {
                    rejected[gate.Id] = other.Count > 0
                        ? "the build failed for a reason other than a missing symbol in this task's files: "
                          + string.Join("; ", other.Take(ExamplesShown).Select(Describe))
                          + (other.Count > ExamplesShown ? $"; and {other.Count - ExamplesShown} more" : string.Empty)
                        : "the build printed an error that names no source file, which is not a missing symbol";
                    continue;
                }

                symbols.AddRange(errors.Select(error => error.Symbol!).Where(symbol => !symbols.Contains(symbol, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal));
                accepted[gate.Id] = "compile failed only on missing symbols: " + string.Join(", ", errors.Select(error => error.Symbol!).Distinct(StringComparer.Ordinal));
                filter ??= FilterOf(gate.Command);
                continue;
            }

            if (run is { Failed: > 0, Total: > 0 })
            {
                // The runner whose summary matched names the tests and the assertion; another runner's patterns could match noise.
                RedLanguage runner = RedLanguages.All.First(language => language.ReadTests(gate.Output) is not null);
                failing.AddRange(runner.FailedTests(gate.Output).Where(name => !failing.Contains(name, StringComparer.Ordinal)));
                assertion ??= runner.Assertion(gate.Output);

                accepted[gate.Id] = $"tests ran and failed: {run.Failed} of {run.Total}";
                filter = FilterOf(gate.Command);
                continue;
            }

            if (ZeroTests(gate) || run is { Total: 0 })
            {
                rejected[gate.Id] = ZeroTestsReason(gate, ownFiles, repositoryRoot);
                continue;
            }

            string? infrastructure = RedLanguages.All.SelectMany(language => language.Infrastructure).FirstOrDefault(entry => entry.Pattern.IsMatch(gate.Output)).Reason;
            rejected[gate.Id] = infrastructure is not null
                ? $"'{gate.Command}' exited {gate.ExitCode} and no test failed: {infrastructure}. That is an infrastructure failure, not RED"
                : $"'{gate.Command}' exited {gate.ExitCode} with no compiler error and no failing test in its output, so the failure is not RED evidence";
        }

        if (rejected.Count > 0)
        {
            return Fail(string.Join(" | ", rejected.Select(entry => $"gate '{entry.Key}': {entry.Value}")), accepted, rejected);
        }

        if (symbols.Count == 0 && failing.Count == 0 && accepted.Count == 0)
        {
            return Fail(
                testsPassed
                    ? "every test passed, so the tests prove nothing is missing: make a test assert behaviour the code does not have yet"
                    : "no gate ran failing tests or failed on a missing symbol, so there is no RED evidence: the declared gates ran no tests",
                accepted,
                rejected);
        }

        string line = symbols.Count > 0
            ? $"RED_EVIDENCE: {filter ?? "build"} — compile: missing {string.Join(", ", symbols)}"
            : $"RED_EVIDENCE: {filter ?? "tests"} — {(failing.Count > 0 ? string.Join(", ", failing.Take(10)) : "failing tests")} — {assertion ?? "assertion failed"}";
        return new RedOutcome(true, string.Empty, line, symbols, failing, accepted, rejected);
    }

    private static bool ZeroTests(RedGateObservation gate) =>
        RedLanguages.All.Any(language => language.ZeroTests(gate.ExitCode, gate.Output));

    private static string ZeroTestsReason(RedGateObservation gate, IReadOnlyList<string> ownFiles, string? repositoryRoot)
    {
        string filter = FilterOf(gate.Command);
        string text = $"'{gate.Command}' ran zero tests: nothing matched the filter or project it uses ('{filter}'), so no test failed and there is no RED";
        foreach (string file in ownFiles)
        {
            if (ProjectOf(file, repositoryRoot) is { } project)
            {
                return text + $". The host's review.task-gates for this agent does not cover {project}, which holds {file}: "
                       + "declare a task gate for that project under review.task-gates (or point this gate's filter at it), then run again";
            }
        }

        return text + ". Check that the gate's project and filter select the tests this task wrote, or declare a task gate for the project that holds them under review.task-gates";
    }

    /// <summary>The nearest project file above <paramref name="file"/> (a .csproj, package.json, pyproject.toml, Cargo.toml or go.mod), repository-relative, or null.</summary>
    public static string? ProjectOf(string file, string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return null;
        }

        string root = Path.GetFullPath(repositoryRoot);
        string? directory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, file)));
        while (directory is not null && directory.StartsWith(root, StringComparison.Ordinal))
        {
            if (Directory.Exists(directory))
            {
                string? found = Directory.EnumerateFiles(directory)
                    .FirstOrDefault(path => Path.GetExtension(path) is ".csproj" or ".fsproj" or ".vbproj"
                                            || Path.GetFileName(path) is "package.json" or "pyproject.toml" or "Cargo.toml" or "go.mod");
                if (found is not null)
                {
                    return Path.GetRelativePath(root, found).Replace('\\', '/');
                }
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static RedOutcome Fail(string reason, Dictionary<string, string> accepted, Dictionary<string, string> rejected) =>
        new(false, reason, string.Empty, [], [], accepted, rejected);

    private static string Describe(CompileError error) =>
        error.Symbol is not null && error.File.Length > 0
            ? $"{error.Code} for '{error.Symbol}' in {error.File}, which this task did not change"
            : $"{error.Code} {error.Message}".Trim();

    private static string? MissingSymbol(RedLanguage language, string code, string message)
    {
        if (!language.MissingSymbolCodes.TryGetValue(code, out Regex? pattern) || pattern.Match(message) is not { Success: true } match)
        {
            return null;
        }

        string symbol = match.Groups["symbol"].Value;
        return match.Groups["owner"].Success ? match.Groups["owner"].Value + "." + symbol : symbol;
    }

    [GeneratedRegex(@"^\s*(?:[^\s(][^()]*\(\d+,\d+\): )?error (?!CS\d+:|TS\d+:)[A-Za-z]+\d+:", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex OtherErrorLine();

    /// <summary>Whether the output prints an error the language patterns did not read (a build-system or restore error).</summary>
    private static bool HasUnparsedError(string output, IReadOnlyList<CompileError> parsed) =>
        OtherErrorLine().IsMatch(output) || (parsed.Count == 0 && output.Contains(": error ", StringComparison.Ordinal));

    private static string FilterOf(string command)
    {
        string[] words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < words.Length - 1; index++)
        {
            if (words[index] is "--filter" or "-t" or "--testNamePattern" or "-k")
            {
                return words[index + 1];
            }
        }

        return command;
    }
}
