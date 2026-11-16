using KyberWeave.Core.Agents.Model;

namespace KyberWeave.Core.Agents.Parsing;

/// <summary>Result of attempting to load a single agent file.</summary>
/// <param name="Success">Whether the file parsed.</param>
/// <param name="Agent">The parsed agent, or null when it did not parse.</param>
/// <param name="Path">The file that was loaded.</param>
/// <param name="Error">Why the file did not parse, or null when it did.</param>
public sealed record AgentLoadResult(bool Success, AgentModel? Agent, string Path, string? Error);
