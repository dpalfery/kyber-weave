using System.Text.Json;
using KyberDash.Data.Collector;
using Xunit;

namespace KyberDash.Data.Tests.Collector;

/// <summary>
/// Unit tests for <see cref="SkillExtraction"/> — the shared skill-attribute reader both the
/// ingest pipeline and the OTLP log mapper run, so the two ingestion paths cannot disagree on
/// which skills a record invoked.
/// </summary>
public class SkillExtractionTests
{
    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void ExtractInvoked_SkillAttributeString_ReturnsTheTrimmedName()
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["gen_ai.skill.name"] = Element("\"  deploy \""),
        };

        Assert.Equal(["deploy"], SkillExtraction.ExtractInvoked(attributes));
    }

    [Fact]
    public void ExtractInvoked_SkillAttributeArray_ReturnsEveryName()
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["gen_ai.skill.name"] = Element("""["deploy","review"]"""),
        };

        Assert.Equal(["deploy", "review"], SkillExtraction.ExtractInvoked(attributes));
    }

    [Fact]
    public void ExtractInvoked_JsonEncodedArrayString_ParsesEveryName()
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["skill.name"] = Element("\"[\\\"deploy\\\",\\\"review\\\"]\""),
        };

        Assert.Equal(["deploy", "review"], SkillExtraction.ExtractInvoked(attributes));
    }

    [Fact]
    public void ExtractInvoked_DeduplicatesAcrossAttributesAndSkipsDocuments()
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["gen_ai.skill.name"] = Element("\"deploy\""),
            ["kyber.skills.invoked"] = Element("[\"deploy\", \"{\\\"a\\\":1}\"]"),
        };

        Assert.Equal(["deploy"], SkillExtraction.ExtractInvoked(attributes));
    }

    [Fact]
    public void ExtractInvoked_SkillToolWithStringifiedArgumentsObject_ExtractsTheSkillProperty()
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["gen_ai.tool.name"] = Element("\"Skill\""),
            ["arguments"] = Element("\"{\\\"skill\\\":\\\"deploy\\\"}\""),
        };

        Assert.Equal(["deploy"], SkillExtraction.ExtractInvoked(attributes));
    }

    [Fact]
    public void ExtractInvoked_SkillToolWithObjectArguments_ExtractsTheSkillProperty()
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["tool.name"] = Element("\"Skill\""),
            ["input"] = JsonSerializer.SerializeToElement(new { skill = "deploy" }),
        };

        Assert.Equal(["deploy"], SkillExtraction.ExtractInvoked(attributes));
    }

    [Fact]
    public void ExtractInvoked_ResolvedToolNameWinsOverTheAttributes()
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["gen_ai.tool.name"] = Element("\"Bash\""),
            ["arguments"] = JsonSerializer.SerializeToElement(new { skill = "deploy" }),
        };

        Assert.Equal(["deploy"], SkillExtraction.ExtractInvoked(attributes, "Skill"));
    }

    [Fact]
    public void ExtractInvoked_FallsBackToNameProperty_WhenTheSkillPropertyIsBlank()
    {
        Dictionary<string, JsonElement> attributes = new(StringComparer.Ordinal)
        {
            ["gen_ai.tool.name"] = Element("\"Skill\""),
            ["arguments"] = JsonSerializer.SerializeToElement(new { skill = "  ", name = "review" }),
        };

        Assert.Equal(["review"], SkillExtraction.ExtractInvoked(attributes));
    }

    [Fact]
    public void ExtractInvoked_NullOrEmptyAttributes_ReturnsNoSkills()
    {
        Assert.Empty(SkillExtraction.ExtractInvoked(null));
        Assert.Empty(SkillExtraction.ExtractInvoked(new Dictionary<string, JsonElement>(StringComparer.Ordinal)));
    }
}
