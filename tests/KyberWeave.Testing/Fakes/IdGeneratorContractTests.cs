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
            Assert.True(seen.Add(name), $"The generator returned '{name}' twice.");
        }
    }

    /// <summary>The shape a token must have: letters, digits, hyphens and underscores, at least one character.</summary>
    /// <returns>The pattern.</returns>
    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex TokenPattern();
}
