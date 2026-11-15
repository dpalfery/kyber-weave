using KyberWeave.Core.Doctor;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the advisory doctor check that recommends raising Copilot CLI's large-output limit: it applies only when
/// Copilot is in use, warns below 32000 bytes, and never fails.
/// </summary>
public sealed class CopilotLargeOutputDoctorTests : IDisposable
{
    private readonly TempDirectory _repository = new();
    private readonly TempDirectory _home = new();

    public void Dispose()
    {
        _home.Dispose();
        _repository.Dispose();
    }

    private static Func<string, string?> Env(string? value) =>
        name => name == CopilotLargeOutputProbe.VariableName ? value : null;

    private void UseCopilot() => Directory.CreateDirectory(Path.Combine(_home.Path, ".copilot"));

    private DoctorCheck Single(string? value)
    {
        IReadOnlyList<DoctorCheck> checks = CopilotLargeOutputProbe.Run(_repository.Path, _home.Path, Env(value));
        return Assert.Single(checks);
    }

    [Fact]
    public void Unset_WarnsWithBothFixLines_AndStillPasses()
    {
        UseCopilot();

        DoctorCheck check = Single(null);

        Assert.Equal("copilot.large-output-limit", check.Id);
        Assert.True(check.Passed);
        Assert.StartsWith("warning: ", check.Reason, StringComparison.Ordinal);
        Assert.Contains("export COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES=32000", check.Reason, StringComparison.Ordinal);
        Assert.Contains("$env:COPILOT_LARGE_OUTPUT_THRESHOLD_BYTES = \"32000\"", check.Reason, StringComparison.Ordinal);
        Assert.Contains("code index", check.Reason, StringComparison.Ordinal);
        Assert.Contains("starts from", check.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("20480")]
    [InlineData("31999")]
    [InlineData("0")]
    public void BelowThreshold_Warns(string value)
    {
        UseCopilot();

        DoctorCheck check = Single(value);

        Assert.True(check.Passed);
        Assert.StartsWith("warning: ", check.Reason, StringComparison.Ordinal);
        Assert.Contains($"is {value}", check.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("32000")]
    [InlineData("60000")]
    [InlineData(" 32000 ")]
    public void SufficientLimit_PassesWithoutWarning(string value)
    {
        UseCopilot();

        DoctorCheck check = Single(value);

        Assert.True(check.Passed);
        Assert.DoesNotContain("warning", check.Reason, StringComparison.Ordinal);
        Assert.Null(check.Fix);
    }

    [Theory]
    [InlineData("lots")]
    [InlineData("-5")]
    [InlineData("32k")]
    [InlineData("")]
    public void NonNumeric_WarnsAndPasses(string value)
    {
        UseCopilot();

        DoctorCheck check = Single(value);

        Assert.True(check.Passed);
        Assert.StartsWith("warning: ", check.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CopilotNotInUse_ReportsNothing()
    {
        Assert.Empty(CopilotLargeOutputProbe.Run(_repository.Path, _home.Path, Env(null)));
        Assert.Empty(CopilotLargeOutputProbe.Run(_repository.Path, null, Env(null)));
    }

    [Fact]
    public void InstalledSquadCopilotTarget_CountsAsInUse()
    {
        Directory.CreateDirectory(Path.Combine(_repository.Path, ".github", "agents"));

        Assert.Single(CopilotLargeOutputProbe.Run(_repository.Path, _home.Path, Env(null)));
    }
}
