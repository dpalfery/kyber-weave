using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Skills.Authoring;
using Xunit;

namespace KyberWeave.Tests;

public class FailOnPolicyTests
{
    private static DiagnosticReport ReportWith(params Severity[] severities)
    {
        DiagnosticReport report = new();
        foreach (Severity severity in severities)
            report.Add(new Diagnostic("KW-TEST-001", severity, "finding", "subject", "file"));
        return report;
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("CRITICAL")]
    public void Critical_TripsOnlyOnCritical(string failOn)
    {
        Assert.Equal(1, FailOnPolicy.ExitCode(ReportWith(Severity.Critical), failOn));
        Assert.Equal(0, FailOnPolicy.ExitCode(ReportWith(Severity.Error, Severity.Warning, Severity.Info), failOn));
        Assert.Equal(0, FailOnPolicy.ExitCode(ReportWith(), failOn));
    }

    [Theory]
    [InlineData("error")]
    [InlineData("ERROR")]
    [InlineData("Error")]
    public void Error_TripsOnErrorAndCriticalButNotWarning(string failOn)
    {
        Assert.Equal(1, FailOnPolicy.ExitCode(ReportWith(Severity.Error), failOn));
        Assert.Equal(1, FailOnPolicy.ExitCode(ReportWith(Severity.Critical), failOn));
        Assert.Equal(0, FailOnPolicy.ExitCode(ReportWith(Severity.Warning, Severity.Info), failOn));
    }

    [Theory]
    [InlineData("warning")]
    [InlineData("WARNING")]
    [InlineData("Warning")]
    public void Warning_TripsOnWarningErrorAndCritical(string failOn)
    {
        Assert.Equal(1, FailOnPolicy.ExitCode(ReportWith(Severity.Warning), failOn));
        Assert.Equal(1, FailOnPolicy.ExitCode(ReportWith(Severity.Error), failOn));
        Assert.Equal(1, FailOnPolicy.ExitCode(ReportWith(Severity.Critical), failOn));
        Assert.Equal(0, FailOnPolicy.ExitCode(ReportWith(Severity.Info), failOn));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    public void UnknownValue_FallsBackToTheCriticalThreshold(string failOn)
    {
        Assert.Equal(1, FailOnPolicy.ExitCode(ReportWith(Severity.Critical), failOn));
        Assert.Equal(0, FailOnPolicy.ExitCode(ReportWith(Severity.Error, Severity.Warning), failOn));
    }
}
