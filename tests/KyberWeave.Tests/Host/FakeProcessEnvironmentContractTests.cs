using KyberWeave.Contracts.Repositories;
using KyberWeave.Tests.Fakes;
using Xunit;

namespace KyberWeave.Tests.Host;

/// <summary>Runs the environment contract against the configurable fake, with facts the fake states itself.</summary>
public sealed class FakeProcessEnvironmentContractTests : ProcessEnvironmentContractTests
{
    private const string KnownVariable = "KW_CONTRACT_KNOWN";
    private const string KnownValue = "known-value";
    private const string AbsentVariable = "KW_CONTRACT_ABSENT";

    /// <inheritdoc />
    protected override IProcessEnvironment CreateEnvironment() => new FakeProcessEnvironment(
        currentDirectory: "/fake/work",
        homeDirectory: "/fake/home",
        tempDirectory: "/fake/temp",
        userName: "fake-user",
        machineName: "fake-machine",
        operatingSystem: OperatingSystemKind.MacOS,
        isFileSystemCaseInsensitive: true,
        isInputRedirected: false,
        readVariable: name => name == KnownVariable ? KnownValue : null);

    /// <inheritdoc />
    protected override ProcessEnvironmentFacts Facts => new(
        CurrentDirectory: "/fake/work",
        HomeDirectory: "/fake/home",
        TempDirectory: "/fake/temp",
        UserName: "fake-user",
        MachineName: "fake-machine",
        OperatingSystem: OperatingSystemKind.MacOS,
        IsFileSystemCaseInsensitive: true,
        IsInputRedirected: false,
        KnownVariableName: KnownVariable,
        KnownVariableValue: KnownValue,
        AbsentVariableName: AbsentVariable);
}

/// <summary>Runs the identifier contract against the deterministic fake.</summary>
public sealed class FakeIdGeneratorContractTests : IdGeneratorContractTests
{
    /// <inheritdoc />
    protected override IIdGenerator CreateGenerator() => new FakeIdGenerator();
}
