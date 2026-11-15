namespace KyberWeave.Core.Review;

/// <summary>
/// One gate's run in a few lines a model can act on: pass or fail, the test totals, and for a failure each failing test
/// with the first line of its error, or each compile error, then where the full output is. The full output is never
/// printed: a harness spills a long tool result to a file, and the worker then spends turns reading it back.
/// </summary>
/// <remarks>
/// The totals, failing test names and compile errors come from <see cref="RedLanguages"/> and
/// <see cref="RedClassifier.ParseErrors"/>, the same readers the failing-run classifier uses, so the short form and the
/// evidence never disagree about what failed.
/// </remarks>
public static class TaskTestDigest
{
    /// <summary>The most failing tests or compile errors listed for one gate; the rest are counted.</summary>
    public const int MaxItems = 10;

    /// <summary>The longest line printed; a longer one is cut and marked.</summary>
    public const int MaxLineLength = 200;

    private const int LinesSearchedAfterName = 8;

    /// <summary>The short form of one gate's run.</summary>
    /// <param name="gateId">The gate's id.</param>
    /// <param name="passed">Whether the gate passed.</param>
    /// <param name="output">What the gate's command printed, standard output then standard error.</param>
    /// <param name="logPath">Where the full output was written, or null when it was not.</param>
    public static IReadOnlyList<string> Lines(string gateId, bool passed, string output, string? logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gateId);
        ArgumentNullException.ThrowIfNull(output);

        List<string> lines = [];
        IReadOnlyList<CompileError> errors = passed ? [] : RedClassifier.ParseErrors(output);
        RedLanguage? runner = RedLanguages.All.FirstOrDefault(language => language.ReadTests(output) is not null);
        TestRunSummary? run = runner?.ReadTests(output);

        if (errors.Count > 0)
        {
            lines.Add($"{gateId}: FAIL — the build failed with {errors.Count} compile error{(errors.Count == 1 ? string.Empty : "s")}, so no test ran");
            lines.AddRange(errors.Take(MaxItems).Select(error => Item($"{error.File}: {error.Code} {error.Message}")));
            AddRemainder(lines, errors.Count);
        }
        else if (passed && (run is { Total: 0 } || RedLanguages.All.Any(language => language.ZeroTests(0, output))))
        {
            lines.Add($"{gateId}: NO TESTS RAN — its filter matched no test, so nothing is verified; check the test class names");
        }
        else if (run is not null && runner is not null)
        {
            lines.Add($"{gateId}: {(passed ? "PASS" : "FAIL")} — tests: total={run.Total} failed={run.Failed} passed={run.Total - run.Failed}");
            IReadOnlyList<string> failing = passed ? [] : runner.FailedTests(output);
            string[] outputLines = output.Split('\n');
            foreach (string name in failing.Take(MaxItems))
                lines.Add(Item(FirstErrorLine(outputLines, name) is { } error ? $"{name} — {error}" : name));
            AddRemainder(lines, failing.Count);
        }
        else if (!passed)
        {
            lines.Add($"{gateId}: FAIL — no test summary or compile error in its output; its last lines:");
            lines.AddRange(output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3).Select(Item));
        }
        else
        {
            lines.Add($"{gateId}: PASS");
        }

        if (logPath is not null)
            lines.Add(Item("full output: " + logPath));

        return lines;
    }

    /// <summary>
    /// The first line after the one naming <paramref name="name"/> that says what went wrong: runners print the test's
    /// name, then a label such as <c>Error Message:</c>, then the assertion.
    /// </summary>
    internal static string? FirstErrorLine(IReadOnlyList<string> outputLines, string name)
    {
        for (int index = 0; index < outputLines.Count; index++)
        {
            if (!NamesTest(outputLines[index], name))
                continue;

            for (int next = index + 1; next < outputLines.Count && next <= index + LinesSearchedAfterName; next++)
            {
                string line = WithoutRunnerPrefix(outputLines[next].Trim());
                if (line.Length == 0 || IsLabel(line))
                    continue;

                // Another test's result line or a stack frame: this mention of the name has no error after it, so the
                // next mention is tried (xUnit names a test once in its own log line and again in the result line).
                if (line.StartsWith("Failed ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Passed ", StringComparison.OrdinalIgnoreCase)
                    || line.StartsWith("at ", StringComparison.Ordinal) || line.EndsWith("[FAIL]", StringComparison.Ordinal))
                    break;

                return line;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="line"/> names <paramref name="name"/> itself, not a longer name that starts with it.</summary>
    private static bool NamesTest(string line, string name)
    {
        for (int at = line.IndexOf(name, StringComparison.Ordinal); at >= 0; at = line.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            int end = at + name.Length;
            if (end == line.Length || !(char.IsLetterOrDigit(line[end]) || line[end] == '_'))
                return true;
        }

        return false;
    }

    private static string WithoutRunnerPrefix(string line)
    {
        if (!line.StartsWith("[xUnit.net", StringComparison.Ordinal))
            return line;

        int end = line.IndexOf(']', StringComparison.Ordinal);
        return end < 0 ? line : line[(end + 1)..].Trim();
    }

    // Microsoft.Testing.Platform prints "from <assembly> (<framework>)" under a failed test's name, before its error.
    private static bool IsLabel(string line) =>
        line is "Error Message:" or "Stack Trace:" or "Standard Output Messages:" or "Output:"
        || (line.StartsWith("from ", StringComparison.Ordinal) && line.EndsWith(')'));

    private static void AddRemainder(List<string> lines, int count)
    {
        if (count > MaxItems)
            lines.Add(Item($"and {count - MaxItems} more"));
    }

    private static string Item(string text)
    {
        string line = "  " + text.Trim();
        return line.Length <= MaxLineLength ? line : line[..(MaxLineLength - 1)] + "…";
    }
}
