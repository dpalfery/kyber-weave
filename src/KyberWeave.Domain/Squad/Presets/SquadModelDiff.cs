using KyberWeave.Core.Squad.Deployment;

namespace KyberWeave.Core.Squad.Model;

/// <summary>
///     A model diff item representing a changed model resolution for an agent.
/// </summary>
/// <param name="Agent">The agent canonical name.</param>
/// <param name="OldModel">The previously resolved model token, or <c>null</c> if inherited or unset.</param>
/// <param name="NewModel">The newly resolved model token, or <c>null</c> if inherited or unset.</param>
public sealed record SquadModelDiffItem(string Agent, string? OldModel, string? NewModel);
