using System.Text.RegularExpressions;
using KyberWeave.Contracts.Repositories;
using Xunit;

namespace KyberWeave.Tests.Host;

/// <summary>
/// The contract every <see cref="IIdGenerator" /> must satisfy. Run it once against the fake and once against the
/// adapter, so a test that pins the value it observes still proves the real generator's guarantees.
/// </summary>
public abstract partial class IdGeneratorContractTests
{
    private const int SampleSize = 100;

    /// <summary>Creates the generator under test.</summary>
    /// <returns>The generator.</returns>
    protected abstract IIdGenerator CreateGenerator();

    [Fact]
    public void EveryGuidIsANewOne()
    {
        IIdGenerator generator = CreateGenerator();
        HashSet<Guid> seen = [];
        for (int index = 0; index < SampleSize; index++)
        {
            Guid value = generator.NewGuid();

            Assert.NotEqual(Guid.Empty, value);
            Assert.True(seen.Add(value), $"The generator returned {value} twice.");
        }
    }

    [Fact]
    public void EveryTokenIsNewAndSafeInAFileName()
    {
        IIdGenerator generator = CreateGenerator();
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 0; index < SampleSize; index++)
        {
            string token = generator.NewRandomToken();

            Assert.Matches(TokenPattern(), token);
            Assert.True(seen.Add(token), $"The generator returned '{token}' twice.");
        }
    }

    [Fact]
    public void EveryRandomFileNameIsANewBareName()
    {
        IIdGenerator generator = CreateGenerator();
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 0; index < SampleSize; index++)
        {
            string name = generator.NewRandomFileName();

            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(name, Path.GetFileName(name));
            Assert.DoesNotContain('/', name);
            Assert.DoesNotContain('\\', name);
            Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
            Assert.True(seen.Add(name), $"The generator returned '{name}' twice.");
        }
    }

    [Fact]
    public void EverySortableIdLeadsWithTheUtcTicksAndIsNewForTheSameInstant()
    {
        IIdGenerator generator = CreateGenerator();
        DateTimeOffset at = new(2026, 11, 18, 9, 30, 0, TimeSpan.FromHours(-8));
        string expectedPrefix = at.UtcTicks.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + "-";
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 0; index < SampleSize; index++)
        {
            string id = generator.NewSortableId(at);

            Assert.Matches(SortableIdPattern(), id);
            Assert.StartsWith(expectedPrefix, id, StringComparison.Ordinal);
            Assert.True(seen.Add(id), $"The generator returned '{id}' twice.");
        }
    }

    [Fact]
    public void SortableIdsOrderChronologically()
    {
        IIdGenerator generator = CreateGenerator();
        DateTimeOffset start = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
        List<string> ids = [];
        for (int index = 0; index < 20; index++)
        {
            ids.Add(generator.NewSortableId(start.AddTicks(index * 1000L)));
        }

        Assert.Equal(ids, ids.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>The shape a sortable id must have: sixteen hex digits, a hyphen, eight hex characters.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex("^[0-9a-f]{16}-[0-9a-f]{8}$")]
    private static partial Regex SortableIdPattern();

    /// <summary>The shape a token must have: letters, digits, hyphens and underscores, at least one character.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex TokenPattern();
}
