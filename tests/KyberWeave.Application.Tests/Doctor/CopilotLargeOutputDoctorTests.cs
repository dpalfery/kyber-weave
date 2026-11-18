using KyberWeave.Core.Doctor;
using KyberWeave.Domain.Doctor;
using KyberWeave.Tests.FileSystem;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the advisory doctor check that recommends raising Copilot CLI's large-output limit: it applies only when
/// Copilot is in use, warns below 32000 bytes, and never fails.
/// </summary>
public sealed class CopilotLargeOutputDoctorTests
{
    private readonly InMemoryFileSystem _files = new();

    private string Repository => Path.Combine(_files.Root, "repo");

    private string Home => Path.Combine(_files.Root, "home");

    private static Func<string, string?> Env(string? value) =>
        name => name == CopilotLargeOutputProbe.VariableName ? value : null;

    private void UseCopilot() => _files.CreateDirectory(Path.Combine(Home, ".copilot"));

    private DoctorCheck Single(string? value)
    {
        IReadOnlyList<DoctorCheck> checks = CopilotLargeOutputProbe.Run(Repository, Home, Env(value), _files.DirectoryExists, _files.FileExists);
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
        Assert.Empty(CopilotLargeOutputProbe.Run(Repository, Home, Env(null), _files.DirectoryExists, _files.FileExists));
        Assert.Empty(CopilotLargeOutputProbe.Run(Repository, null, Env(null), _files.DirectoryExists, _files.FileExists));
    }

    [Fact]
    public void InstalledSquadCopilotTarget_CountsAsInUse()
    {
        _files.CreateDirectory(Path.Combine(Repository, ".github", "agents"));

        Assert.Single(CopilotLargeOutputProbe.Run(Repository, Home, Env(null), _files.DirectoryExists, _files.FileExists));
    }
}
