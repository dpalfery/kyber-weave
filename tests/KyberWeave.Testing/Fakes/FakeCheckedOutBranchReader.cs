using KyberWeave.Contracts.Repositories;

namespace KyberWeave.Tests.Fakes;

/// <summary>The one fake for <see cref="ICheckedOutBranchReader"/>: answers the branch a test names for a checkout, and null for any other.</summary>
public sealed class FakeCheckedOutBranchReader : ICheckedOutBranchReader
{
    /// <summary>Gets the branch each checkout reports, by checkout directory.</summary>
    public Dictionary<string, string?> Branches { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string? Read(string workingDirectory) => Branches.GetValueOrDefault(workingDirectory);
}
