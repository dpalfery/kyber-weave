using KyberWeave.Core.Configuration;
using KyberWeave.Core.Diagnostics;
using KyberWeave.Core.Review;

namespace KyberWeave.Contracts.Repositories;

/// <summary>Reads the host's severity overrides from the review guidance file its registry declares.</summary>
public interface ISeverityOverrideSource
{
    /// <summary>The review guidance file the registry declares (default <c>REVIEW.md</c>), or null when it is missing.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="configReg">The host's registry of configuration files.</param>
    string? GuidancePath(string root, ConfigRegConfig configReg);

    /// <summary>The overrides of the declared guidance file; <see cref="SeverityOverrides.None"/> when it is missing or unreadable.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="configReg">The host's registry of configuration files.</param>
    SeverityOverrides Load(string root, ConfigRegConfig configReg);

    /// <summary>The problems in the declared guidance file's section, for <c>docs validate</c>; empty without a file.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="configReg">The host's registry of configuration files.</param>
    DiagnosticReport Validate(string root, ConfigRegConfig configReg);
}
