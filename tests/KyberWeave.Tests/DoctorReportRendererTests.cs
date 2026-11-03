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
}
