using KyberWeave.Contracts.Repositories;
using Xunit;

namespace KyberWeave.Tests.Host;

/// <summary>
/// The contract every <see cref="IProcessEnvironment" /> must satisfy. Run it once against the fake and once against
/// the adapter, so a test that relies on a directory or a platform fact cannot pass while the real host answers
/// something else.
/// </summary>
/// <remarks>
/// A derived class supplies the environment under test and the answers it should give. The adapter run reads those
/// answers from the running process; the fake run states them when it builds the fake.
/// </remarks>
public abstract class ProcessEnvironmentContractTests
{
    /// <summary>Creates the environment under test.</summary>
    /// <returns>The environment.</returns>
    protected abstract IProcessEnvironment CreateEnvironment();

    /// <summary>Gets the answers the environment under test is expected to give.</summary>
    protected abstract ProcessEnvironmentFacts Facts { get; }

    [Fact]
    public void ItReportsTheWellKnownDirectories()
    {
        IProcessEnvironment environment = CreateEnvironment();

        Assert.Equal(Facts.CurrentDirectory, environment.CurrentDirectory);
        Assert.Equal(Facts.HomeDirectory, environment.HomeDirectory);
        Assert.Equal(Facts.TempDirectory, environment.TempDirectory);
    }

    [Fact]
    public void ItReportsTheUserAndTheMachine() =>
        Assert.Equal(Facts.UserName, CreateEnvironment().UserName);

    [Fact]
    public void ItReportsTheMachineName() =>
        Assert.Equal(Facts.MachineName, CreateEnvironment().MachineName);

    [Fact]
    public void ItReportsAKnownOperatingSystemFamily()
    {
        OperatingSystemKind family = CreateEnvironment().OperatingSystem;

        Assert.Equal(Facts.OperatingSystem, family);
        Assert.True(Enum.IsDefined(family), $"{family} is not a known operating-system family.");
    }

    [Fact]
    public void ItReportsTheFileSystemsNameRule() =>
        Assert.Equal(Facts.IsFileSystemCaseInsensitive, CreateEnvironment().IsFileSystemCaseInsensitive);

    [Fact]
    public void ItReportsWhetherInputIsRedirected() =>
        Assert.Equal(Facts.IsInputRedirected, CreateEnvironment().IsInputRedirected);

    [Fact]
    public void ItReadsAVariableThatIsSet()
    {
        IProcessEnvironment environment = CreateEnvironment();

        Assert.Equal(Facts.KnownVariableValue, environment.GetVariable(Facts.KnownVariableName));
    }

    [Fact]
    public void ItAnswersNullForAVariableThatIsNotSet() =>
        Assert.Null(CreateEnvironment().GetVariable(Facts.AbsentVariableName));
}

/// <summary>The answers a <see cref="IProcessEnvironment" /> is expected to give in a contract run.</summary>
/// <param name="CurrentDirectory">The expected current directory.</param>
/// <param name="HomeDirectory">The expected home directory.</param>
/// <param name="TempDirectory">The expected temporary directory.</param>
/// <param name="UserName">The expected user name.</param>
/// <param name="MachineName">The expected machine name.</param>
/// <param name="OperatingSystem">The expected operating-system family.</param>
/// <param name="IsFileSystemCaseInsensitive">The expected file-system name rule.</param>
/// <param name="IsInputRedirected">The expected input-redirection state.</param>
/// <param name="KnownVariableName">A variable the environment has a value for.</param>
/// <param name="KnownVariableValue">The value that variable has.</param>
/// <param name="AbsentVariableName">A variable the environment does not have.</param>
public sealed record ProcessEnvironmentFacts(
    string CurrentDirectory,
    string HomeDirectory,
    string TempDirectory,
    string UserName,
    string MachineName,
    OperatingSystemKind OperatingSystem,
    bool IsFileSystemCaseInsensitive,
    bool IsInputRedirected,
    string KnownVariableName,
    string? KnownVariableValue,
    string AbsentVariableName);
