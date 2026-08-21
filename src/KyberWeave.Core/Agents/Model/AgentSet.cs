namespace KyberWeave.Core.Agents.Model;

/// <summary>
/// A collection of agents discovered across all coding harness directories in a project.
/// </summary>
public sealed class AgentSet(IEnumerable<AgentModel> agents)
{
    private readonly List<AgentModel> _agents = agents.ToList();

    public IReadOnlyList<AgentModel> Agents => _agents;
    public int Count => _agents.Count;

    /// <summary>
    /// Returns all unique role names found across any harness.
    /// </summary>
    public IEnumerable<string> GetAllRoleNames() =>
        _agents.Select(a => a.RoleName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r);

    /// <summary>
    /// Gets agents grouped by role name and harness kind.
    /// </summary>
    public IReadOnlyDictionary<string, Dictionary<HarnessKind, AgentModel>> GetRoleHarnessMatrix()
    {
        Dictionary<string, Dictionary<HarnessKind, AgentModel>> matrix = new Dictionary<string, Dictionary<HarnessKind, AgentModel>>(StringComparer.OrdinalIgnoreCase);

        foreach (AgentModel agent in _agents)
        {
            if (!matrix.TryGetValue(agent.RoleName, out Dictionary<HarnessKind, AgentModel>? harnessMap))
            {
                harnessMap = new Dictionary<HarnessKind, AgentModel>();
                matrix[agent.RoleName] = harnessMap;
            }

            harnessMap[agent.Harness] = agent;
        }

        return matrix;
    }
}
