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
public sealed record RedLanguage(
    string Name,
    Regex Diagnostic,
    IReadOnlyDictionary<string, Regex> MissingSymbolCodes,
    Func<string, TestRunSummary?> ReadTests,
    Func<string, IReadOnlyList<string>> FailedTests,
    Func<string, string?> Assertion);

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
            DotnetAssertion),
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
            ScriptAssertion),
    ];

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

    /// <summary>Judges the runs of one RED task's gates.</summary>
    /// <param name="gates">Every gate that ran, in order.</param>
    /// <param name="ownFiles">The files the task changed; missing-symbol errors must be in these.</param>
    public static RedOutcome Evaluate(IReadOnlyList<RedGateObservation> gates, IReadOnlyList<string> ownFiles)
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
            if (gate.ExitCode == 0)
            {
                if (RedLanguages.All.Select(language => language.ReadTests(gate.Output)).FirstOrDefault(run => run is not null) is { Total: > 0, Failed: 0 })
                {
                    testsPassed = true;
                    filter ??= FilterOf(gate.Command);
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

            TestRunSummary? run = RedLanguages.All.Select(language => language.ReadTests(gate.Output)).FirstOrDefault(summary => summary is not null);
            if (run is { Failed: > 0 })
            {
                foreach (RedLanguage language in RedLanguages.All)
                {
                    failing.AddRange(language.FailedTests(gate.Output).Where(name => !failing.Contains(name, StringComparer.Ordinal)));
                    assertion ??= language.Assertion(gate.Output);
                }

                accepted[gate.Id] = $"{run.Failed} of {run.Total} tests failed";
                filter = FilterOf(gate.Command);
                continue;
            }

            rejected[gate.Id] = $"it exited {gate.ExitCode} with no compiler error and no failing test, so the failure is not RED";
        }

        if (rejected.Count > 0)
        {
            return Fail(
                "a gate failed for a reason that is not RED evidence: " + string.Join(" | ", rejected.Select(entry => $"{entry.Key}: {entry.Value}")),
                accepted,
                rejected);
        }

        if (symbols.Count == 0 && failing.Count == 0 && accepted.Count == 0)
        {
            return Fail(
                testsPassed
                    ? "every test passed, so the tests prove nothing is missing: make a test assert behaviour the code does not have yet."
                    : "no gate ran failing tests or failed on a missing symbol, so there is no RED evidence: the declared gates ran no tests.",
                accepted,
                rejected);
        }

        string line = symbols.Count > 0
            ? $"RED_EVIDENCE: {filter ?? "build"} — compile: missing {string.Join(", ", symbols)}"
            : $"RED_EVIDENCE: {filter ?? "tests"} — {(failing.Count > 0 ? string.Join(", ", failing.Take(10)) : "failing tests")} — {assertion ?? "assertion failed"}";
        return new RedOutcome(true, string.Empty, line, symbols, failing, accepted, rejected);
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
