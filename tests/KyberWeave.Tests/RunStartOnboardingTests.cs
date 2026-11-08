using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>run start</c> checks the <c>onboarding</c> setting before it checks anything else: unset stops the run and
/// names the skill that sets it, and a recorded answer lets the run continue.
/// </summary>
/// <remarks>
/// <para>
/// The setting answers one question — has this repository been asked which gates and standards it wants — and an
/// unasked repository runs with defaults nobody reviewed. D6 makes that a stop rather than a warning, so the run
/// cannot proceed into work whose checks were never chosen. It is a hard stop in autonomous mode too, where the
/// run stops without asking and leaves the setting as it found it; a run that asked would break "ask the user
/// nothing", and one that wrote an answer would record a decision nobody made.
/// </para>
/// <para>
/// Every case here writes a host configuration, because the setting only exists in a configured repository. The
/// cases run the real CLI through <see cref="RunStartProbe"/>, which builds a real clone with a CodeGraph stub on
/// <c>PATH</c>, so a stop is observed at the same surface the conductor sees.
/// </para>
/// </remarks>
public sealed class RunStartOnboardingTests : IDisposable
{
    private const string OnboardingSkill = "kyber-docs-onboarding-and-assessment";

    private const string Branch = "claude/fix/tighten-the-gate";

    private readonly RunStartProbe _probe = RunStartProbe.Create();

    public void Dispose()
    {
        _probe.Dispose();
    }

    /// <summary>
    /// A configured repository with no <c>onboarding</c> value stops the run, names the skill that sets it, and
    /// creates no branch.
    /// </summary>
    /// <remarks>
    /// Creating the branch first would leave a checkout on a branch the run never started, and the next attempt
    /// would find it. The message has to name the skill: a stop that says only "onboarding is required" leaves the
    /// operator to find the verb.
    /// </remarks>
    [Fact]
    public void AnUnsetOnboardingSettingStopsTheRunAndNamesTheSkill()
    {
        _probe.WriteHostConfig("ontology:\n  docs-root: docs\n");

        ProbeResult result = StartRun();

        Assert.True(result.ExitCode != 0, $"A run with onboarding unset exited 0. {result.Output}");
        Assert.Contains(OnboardingSkill, result.Output, StringComparison.Ordinal);
        Assert.False(
            _probe.BranchExists(Branch),
            "A run stopped at onboarding created its branch, so the stop happened after the work began.");
    }

    /// <summary>
    /// The onboarding stop comes before every other run-start check, so a second failure cannot mask it.
    /// </summary>
    /// <remarks>
    /// The competing failure here is a fetch that cannot reach its remote, which is the run start's tool check.
    /// With onboarding evaluated first, the operator is told to onboard rather than told about a remote that may
    /// be fine once someone has answered the question.
    /// </remarks>
    [Fact]
    public void TheOnboardingStopIsReportedBeforeAToolFailure()
    {
        _probe.WriteHostConfig("ontology:\n  docs-root: docs\n");
        _probe.BreakOrigin();

        ProbeResult result = StartRun();

        Assert.True(result.ExitCode != 0, $"A run with onboarding unset exited 0. {result.Output}");
        Assert.Contains(OnboardingSkill, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("git fetch origin --prune", result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A repository that recorded <c>done</c> runs on to its normal checks instead of stopping at onboarding.
    /// </summary>
    /// <remarks>
    /// This is the other half of the rule and it passes before the setting exists, which is the point: the stop
    /// must not become a stop for a repository that already answered. Its value is preventing a check that
    /// refuses everything.
    /// </remarks>
    [Fact]
    public void ARecordedDoneValueLetsTheRunContinue()
    {
        _probe.WriteHostConfig("ontology:\n  docs-root: docs\nonboarding: done\n");

        ProbeResult result = StartRun();

        Assert.True(result.ExitCode == 0, $"A run in an onboarded repository exited {result.ExitCode}. {result.Output}");
        Assert.DoesNotContain(OnboardingSkill, result.Output, StringComparison.Ordinal);
        Assert.True(_probe.BranchExists(Branch), "A ready run created no branch. " + result.Output);
    }

    /// <summary>
    /// <c>declined</c> behaves as <c>done</c> does: the run continues and the question is not asked again.
    /// </summary>
    /// <remarks>
    /// A user who says no has answered. Treating the refusal as "not onboarded" would ask on every start, which
    /// is the behaviour the value exists to end.
    /// </remarks>
    [Fact]
    public void ARecordedDeclinedValueLetsTheRunContinueWithoutAskingAgain()
    {
        _probe.WriteHostConfig("ontology:\n  docs-root: docs\nonboarding: declined\n");

        ProbeResult result = StartRun();

        Assert.True(result.ExitCode == 0, $"A run in a declined repository exited {result.ExitCode}. {result.Output}");
        Assert.DoesNotContain(OnboardingSkill, result.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Under <c>autonomy: autonomous</c> an unset onboarding value stops the run without asking, and the setting
    /// is left exactly as it was found.
    /// </summary>
    /// <remarks>
    /// Autonomous mode asks the user nothing, so it cannot offer the onboarding step. It also cannot decide for
    /// the user: writing <c>declined</c> on their behalf would record a refusal nobody made and hide the gap from
    /// every later run.
    /// </remarks>
    [Fact]
    public void AnAutonomousRunStopsWithoutAnsweringTheQuestionForTheUser()
    {
        const string config = "ontology:\n  docs-root: docs\nautonomy: autonomous\n";
        _probe.WriteHostConfig(config);

        ProbeResult result = StartRun();

        Assert.True(result.ExitCode != 0, $"An autonomous run with onboarding unset exited 0. {result.Output}");
        Assert.Contains(OnboardingSkill, result.Output, StringComparison.Ordinal);
        Assert.Equal(
            config,
            File.ReadAllText(Path.Combine(_probe.Root, ".kyber-weave", "kyber-weave.yml")));
    }

    private ProbeResult StartRun()
    {
        return _probe.RunStart("--harness", "claude", "--type", "fix", "--slug", "tighten-the-gate");
    }
}
