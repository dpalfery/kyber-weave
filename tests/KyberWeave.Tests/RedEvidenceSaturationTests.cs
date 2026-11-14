using System.Globalization;
using KyberWeave.Core.Review;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Test-runner output is text from outside the process, so a count in it can be larger than an int. Every runner
/// format reads such a count as the largest int, and a sum that passes the largest int stops there; neither throws,
/// because a throw would turn one odd line of runner output into a crashed gate run.
/// </summary>
public sealed class RedEvidenceSaturationTests
{
    private const string TooBigForAnInt = "99999999999999999999";
    private const string Max = "2147483647";

    private static TestRunSummary? Read(string language, string output) =>
        RedLanguages.All.Single(candidate => candidate.Name == language).ReadTests(output);

    [Theory]
    [InlineData("C#", "Failed!  - Failed: {0}, Passed: 1, Skipped: 0, Total: 5", 5, int.MaxValue)]
    [InlineData("C#", "Failed!  - Failed: 1, Passed: 1, Skipped: 0, Total: {0}", int.MaxValue, 1)]
    [InlineData("C#", "total: {0}\nfailed: 2", int.MaxValue, 2)]
    [InlineData("C#", "total: 7\nfailed: {0}", 7, int.MaxValue)]
    [InlineData("Playwright", "  {0} failed\n  2 passed", int.MaxValue, int.MaxValue)]
    [InlineData("Playwright", "  1 failed\n  {0} passed", int.MaxValue, 1)]
    [InlineData("Python", "=== {0} failed, 2 passed in 0.5s ===", int.MaxValue, int.MaxValue)]
    [InlineData("Python", "=== 1 failed, {0} passed in 0.5s ===", int.MaxValue, 1)]
    [InlineData("Rust", "test result: FAILED. 3 passed; {0} failed; 0 ignored", int.MaxValue, int.MaxValue)]
    [InlineData("Rust", "test result: FAILED. {0} passed; 1 failed; 0 ignored", int.MaxValue, 1)]
    [InlineData("TypeScript", "Tests  {0} failed | 2 passed", int.MaxValue, int.MaxValue)]
    [InlineData("TypeScript", "Tests  1 failed | {0} passed", int.MaxValue, 1)]
    public void ACountTooLargeForAnIntReadsAsTheLargestIntInEveryRunnerFormat(string language, string template, int total, int failed)
    {
        string output = string.Format(CultureInfo.InvariantCulture, template, TooBigForAnInt);

        TestRunSummary? summary = Read(language, output);

        Assert.Equal(new TestRunSummary(total, failed), summary);
    }

    [Theory]
    [InlineData("C#", "Failed!  - Failed: 1, Passed: 1, Skipped: 0, Total: {0}\nFailed!  - Failed: 1, Passed: 1, Skipped: 0, Total: {0}")]
    [InlineData("Playwright", "  {0} failed\n  {0} passed")]
    [InlineData("Python", "=== {0} failed, {0} passed in 0.5s ===")]
    [InlineData("Rust", "test result: FAILED. {0} passed; {0} failed; 0 ignored\ntest result: FAILED. {0} passed; {0} failed; 0 ignored")]
    public void CountsThatSumPastTheLargestIntStopAtTheLargestIntInEveryRunnerFormat(string language, string template)
    {
        string output = string.Format(CultureInfo.InvariantCulture, template, Max);

        TestRunSummary? summary = Read(language, output);

        Assert.NotNull(summary);
        Assert.Equal(int.MaxValue, summary.Total);
    }

    [Fact]
    public void TheGoRunnerCountsLinesSoNoCountInItsOutputCanOverflow()
    {
        TestRunSummary? summary = Read("Go", "--- FAIL: TestA (0.00s)\n--- PASS: TestB (0.00s)\nFAIL\n");

        Assert.Equal(new TestRunSummary(2, 1), summary);
    }
}
