using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace KyberWeave.Tests;

/// <summary>
/// Static reads of <c>.github/workflows/ci.yml</c> that pin the process-leftover wiring of the three jobs
/// that run test or build processes.
/// </summary>
/// <remarks>
/// <para>
/// Each of <c>ts-gates</c>, <c>dash-e2e</c>, and <c>build-test</c> must record its start epoch as the first step,
/// so the leftover check can tell this job's processes from an earlier run's. Each must end with an
/// <c>if: always()</c> step that runs <c>scripts/proc-guard.sh check-leftovers</c> over the workspace since that epoch,
/// so a leftover fails the job even when the test step failed first. <c>build-test</c> also shuts the .NET build servers
/// down before that check, since they outlive every <c>dotnet build</c> by design.
/// </para>
/// <para>
/// Without these pins a later workflow edit could drop the always-step, and a fork-bomb like the one that crashed three
/// Macs would pass CI green. This suite reads the file only. It starts no process.
/// </para>
/// </remarks>
public sealed class CiLeftoverStepTests
{
    private const string EpochRecordPattern = @"(?<name>[A-Za-z_]\w*)=\$\(date \+%s\)";

    private const string LeftoverCommandPattern =
        @"scripts/proc-guard\.sh\s+check-leftovers\s+--root\s+""\$GITHUB_WORKSPACE""\s+--since\s+";

    /// <summary>
    /// The first step of each job writes the job-start epoch to <c>$GITHUB_ENV</c>, before any checkout, install, or test
    /// step runs.
    /// </summary>
    /// <remarks>
    /// The epoch must be taken before the work starts. A later epoch would drop processes the job itself started, so
    /// the leftover check would miss them.
    /// </remarks>
    [Theory]
    [InlineData("ts-gates")]
    [InlineData("dash-e2e")]
    [InlineData("build-test")]
    public void JobRecordsStartEpochAsItsFirstStep(string jobName)
    {
        List<Step> steps = StepsOf(jobName);

        Assert.True(steps.Count > 0, $"{jobName} has no steps.");
        Assert.True(
            EpochVariableOf(steps[0]) is not null,
            $"{jobName} step 1 ('{steps[0].Name}') must write a start epoch to $GITHUB_ENV, " +
            "e.g. echo \"NAME=$(date +%s)\" >> \"$GITHUB_ENV\".");
    }

    /// <summary>
    /// The last step of each job is an <c>if: always()</c> step that runs the leftover check over the workspace since the
    /// job-start epoch.
    /// </summary>
    /// <remarks>
    /// <c>always()</c> is what makes the check run after a failed test step. A check that runs only on success would be
    /// silent on exactly the runs that leave processes behind.
    /// </remarks>
    [Theory]
    [InlineData("ts-gates")]
    [InlineData("dash-e2e")]
    [InlineData("build-test")]
    public void JobEndsWithAlwaysLeftoverCheckSinceItsStartEpoch(string jobName)
    {
        List<Step> steps = StepsOf(jobName);
        string? epochVariable = EpochVariableOf(steps[0]);
        Assert.True(epochVariable is not null,
            $"{jobName} step 1 does not record a start epoch, so no check can name one.");

        Step last = steps[^1];
        Assert.True(
            last.If == "always()",
            $"{jobName} last step ('{last.Name}') must carry 'if: always()', found '{last.If ?? "(none)"}'.");

        string pattern = LeftoverCommandPattern + "\"\\$\\{?" + Regex.Escape(epochVariable!) + "\\}?\"";
        Assert.True(
            last.Run is not null && Regex.IsMatch(last.Run, pattern),
            $"{jobName} last step ('{last.Name}') must run " +
            $"scripts/proc-guard.sh check-leftovers --root \"$GITHUB_WORKSPACE\" --since \"${epochVariable}\". " +
            $"Found: '{last.Run ?? "(no run)"}'.");
    }

    /// <summary>
    /// <c>build-test</c> runs <c>dotnet build-server shutdown</c> before its leftover check.
    /// </summary>
    /// <remarks>
    /// The build servers (VBCSCompiler and MSBuild node reuse) outlive every build by design. Left running, they are
    /// reported as leftovers from every .NET job, and the check would fail on a clean build.
    /// </remarks>
    [Fact]
    public void BuildTestShutsDownBuildServersBeforeTheLeftoverCheck()
    {
        List<Step> steps = StepsOf("build-test");
        int shutdownIndex = steps.FindIndex(step =>
            step.Run is not null && step.Run.Contains("dotnet build-server shutdown", StringComparison.Ordinal));
        int checkIndex = steps.Count - 1;

        Assert.True(shutdownIndex >= 0, "build-test has no step running 'dotnet build-server shutdown'.");
        Assert.True(
            shutdownIndex < checkIndex,
            $"build-test runs 'dotnet build-server shutdown' at step {shutdownIndex + 1}, which is not before the leftover check at step {checkIndex + 1}.");
    }

    /// <summary>
    /// <c>dash-e2e</c> invokes Playwright with <c>--config dash/playwright.isolated.config.ts</c>, and that file exists.
    /// </summary>
    /// <remarks>
    /// The default <c>dash/playwright.config.ts</c> boots a webServer against the live host. Running CI without the isolated
    /// config would start a dashboard that the leftover check then has to account for, and a machine's live dashboard would
    /// be the thing being tested. The config file must exist, or the step fails on a missing path rather than on a test.
    /// </remarks>
    [Fact]
    public void DashE2eRunsPlaywrightWithTheIsolatedConfig()
    {
        List<Step> steps = StepsOf("dash-e2e");
        List<Step> playwrightSteps = steps.Where(step =>
            step.Run is not null && step.Run.Contains("playwright test", StringComparison.Ordinal)).ToList();

        Assert.True(playwrightSteps.Count > 0, "dash-e2e has no step running 'playwright test'.");
        Assert.True(
            playwrightSteps.All(step =>
                step.Run!.Contains("--config dash/playwright.isolated.config.ts", StringComparison.Ordinal)),
            "dash-e2e must pass '--config dash/playwright.isolated.config.ts' to every Playwright invocation. Found: " +
            string.Join(" | ", playwrightSteps.Select(step => step.Run)));
    }

    /// <summary>
    /// The isolated Playwright config that <c>dash-e2e</c> names is present in the repository.
    /// </summary>
    /// <remarks>
    /// A workflow that names a config file which is not checked in fails on the missing path, and the failure says
    /// nothing about the tests.
    /// </remarks>
    [Fact]
    public void IsolatedPlaywrightConfigExistsInTheRepository()
    {
        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, "dash", "playwright.isolated.config.ts");

        Assert.True(File.Exists(path), $"The isolated Playwright config named by dash-e2e is missing: {path}");
    }

    private static List<Step> StepsOf(string jobName)
    {
        YamlMappingNode jobs = (YamlMappingNode)Child(Root(), "jobs")!;
        YamlMappingNode job = (YamlMappingNode?)Child(jobs, jobName)
                              ?? throw new InvalidOperationException($"ci.yml has no job named '{jobName}'.");

        YamlSequenceNode stepNodes = (YamlSequenceNode?)Child(job, "steps")
                                     ?? throw new InvalidOperationException(
                                         $"ci.yml job '{jobName}' has no steps sequence.");

        List<Step> steps = [];
        foreach (YamlNode node in stepNodes.Children)
        {
            YamlMappingNode step = (YamlMappingNode)node;
            steps.Add(new Step(
                Scalar(step, "name"),
                Scalar(step, "if"),
                Scalar(step, "run")));
        }

        return steps;
    }

    private static string? EpochVariableOf(Step step)
    {
        if (step.Run is null || !step.Run.Contains("GITHUB_ENV", StringComparison.Ordinal))
            return null;

        Match match = Regex.Match(step.Run, EpochRecordPattern);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static YamlMappingNode Root()
    {
        string path = Path.Combine(KyberWeaveTestPaths.ToolRoot, ".github", "workflows", "ci.yml");
        using StreamReader reader = new(path);
        YamlStream stream = new();
        stream.Load(reader);

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlNode? Child(YamlMappingNode map, string key)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> pair in map.Children)
        {
            if (pair.Key is YamlScalarNode scalar && scalar.Value == key)
                return pair.Value;
        }

        return null;
    }

    private static string? Scalar(YamlMappingNode map, string key)
    {
        return (Child(map, key) as YamlScalarNode)?.Value;
    }

    private sealed record Step(string? Name, string? If, string? Run);
}
