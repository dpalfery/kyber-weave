using System.Text.Json;
using Xunit;
using KyberWeave.Cli.Commands.Doctor;
using KyberWeave.Core.Doctor;

namespace KyberWeave.Tests;

/// <summary>
/// Pins the D9 line contract of <see cref="DoctorReportRenderer"/>: one <c>FAIL &lt;check-id&gt; &lt;reason&gt;</c>
/// line per failure, each followed by exactly one indented <c>fix:</c> line.
/// </summary>
/// <remarks>
/// The reason and the fix are text the doctor did not author — the reason can carry a child's first output line.
/// A line break inside either would split one failing check into what a reader or a script counts as two, so the
/// renderer is where the contract is enforced rather than every producer.
/// </remarks>
public sealed class DoctorReportRendererTests
{
    /// <summary>A reason and a fix that both contain line breaks stay on one line each.</summary>
    [Fact]
    public void Write_ReasonAndFixWithLineBreaks_KeepsOneLinePerCheck()
    {
        DoctorReport report = new(
            "/repo",
            DoctorMode.Check,
            1,
            [new DoctorCheck("mcp.docs_explore", false, "first line\nsecond line", "kyber-weave update\r\nkyber-weave doctor")]);

        CapturedConsoleExecution<object?> execution = ProcessConsoleCapture.Run<object?>(() =>
        {
            DoctorReportRenderer.Write(report, asJson: false);
            return null;
        });

        string[] lines = execution.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("FAIL mcp.docs_explore first line second line", lines[0]);
        Assert.Equal("  fix: kyber-weave update kyber-weave doctor", lines[1]);
    }

    /// <summary>A passing check with no reason or fix still renders a single, clean line.</summary>
    [Fact]
    public void Write_PassingCheckWithNoDetail_RendersOneLine()
    {
        DoctorReport report = new(
            "/repo",
            DoctorMode.Check,
            0,
            [new DoctorCheck("codegraph.binary", true, "found on PATH", null)]);

        CapturedConsoleExecution<object?> execution = ProcessConsoleCapture.Run<object?>(() =>
        {
            DoctorReportRenderer.Write(report, asJson: false);
            return null;
        });

        Assert.Equal("PASS codegraph.binary found on PATH", execution.Output.Trim());
    }

    /// <summary>
    /// The JSON report's <c>mode</c> field names the mode the run actually used, for every mode
    /// there is.
    /// </summary>
    /// <remarks>
    /// A report that names a mode it did not run is a report that lies about what it did, and this
    /// is the one thing a doctor report exists to prevent. The field was a two-way ternary, so an
    /// <c>--ensure</c> run rendered as <c>"check"</c> and a JSON consumer could not tell a run that
    /// wrote the index from one that promised not to. Pin each mode by name: a new mode needs a row
    /// here rather than inheriting another mode's name.
    /// </remarks>
    [Theory]
    [InlineData(DoctorMode.Check, "check")]
    [InlineData(DoctorMode.Provision, "provision")]
    [InlineData(DoctorMode.Ensure, "ensure")]
    public void TheJsonReportNamesTheModeTheRunUsed(DoctorMode mode, string expectedName)
    {
        DoctorReport report = new("/repo", mode, 0, []);

        CapturedConsoleExecution<object?> execution = ProcessConsoleCapture.Run<object?>(() =>
        {
            DoctorReportRenderer.Write(report, asJson: true);
            return null;
        });

        using JsonDocument json = JsonDocument.Parse(execution.Output);
        Assert.Equal(expectedName, json.RootElement.GetProperty("mode").GetString());
    }
}
