using System.Reflection;
using KyberWeave.Core.Docs.Validation;
using Xunit;

namespace KyberWeave.Tests;

/// <summary>
/// Every task-file rule id the validator can emit is documented in the rule reference, so a
/// finding an author receives always has a published meaning and repair.
/// </summary>
public sealed class TaskFileRuleReferenceTests
{
    private static string RuleReferencePath =>
        Path.Combine(KyberWeaveTestPaths.ToolRoot, "docs", "ci-pipelines", "rule-reference.md");

    [Fact]
    public void EveryTaskFileRuleIdIsDocumentedInTheRuleReference()
    {
        string reference = File.ReadAllText(RuleReferencePath);

        string[] ids = [.. typeof(PlanTaskFileRules)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(value => value.StartsWith("KW-DOC-TASKS-", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)];

        Assert.NotEmpty(ids);
        string[] missing = [.. ids.Where(id => !reference.Contains(id, StringComparison.Ordinal))];
        Assert.True(
            missing.Length == 0,
            "These task-file rule ids are emitted by code and missing from docs/ci-pipelines/rule-reference.md: "
            + string.Join(", ", missing));
    }
}
