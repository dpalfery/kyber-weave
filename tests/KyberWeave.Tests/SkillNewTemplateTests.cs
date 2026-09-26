using KyberWeave.Cli.Commands.Skills;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// <c>skill new --template</c> selects the scaffold named on the command line, whatever case
/// it is written in, and falls back to the blank scaffold for anything else.
/// </summary>
public sealed class SkillNewTemplateTests
{
    /// <summary>Each named template produces its own description, in any case.</summary>
    [Theory]
    [InlineData("sop", "the same compliant way every time")]
    [InlineData("SOP", "the same compliant way every time")]
    [InlineData("runbook", "operational task with defined steps")]
    [InlineData("reference", "as a reference manual for")]
    [InlineData("Checklist", "checklist so required validations are never skipped")]
    public void NamedTemplateIsSelected(string template, string expected)
    {
        string body = NewCommand.Template(template, "order-refund", includeLicense: false, includeMetadata: false);

        Assert.Contains(expected, body, StringComparison.Ordinal);
    }

    /// <summary>An unknown or blank template name gets the blank scaffold.</summary>
    [Theory]
    [InlineData("blank")]
    [InlineData("unknown")]
    public void UnknownTemplateFallsBackToBlank(string template)
    {
        string body = NewCommand.Template(template, "order-refund", includeLicense: false, includeMetadata: false);

        Assert.Contains("Replace this with a specific, routable description.", body, StringComparison.Ordinal);
    }
}
